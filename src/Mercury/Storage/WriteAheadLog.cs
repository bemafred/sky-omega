using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkyOmega.Mercury.Runtime.Buffers;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// Write-Ahead Log for crash-safe triple storage.
///
/// Design:
/// - Append-only log file with fixed-size records
/// - fsync after each write (or batch commit)
/// - Recovery replays uncommitted entries after last checkpoint
/// - Hybrid checkpointing: size-based (16MB) OR time-based (60s)
/// </summary>
/// <remarks>
/// <para><strong>INTERNAL USE ONLY:</strong> This class is internal because it is an
/// implementation detail of <see cref="QuadStore"/>. WAL operations are managed
/// automatically by the storage layer for crash safety.</para>
/// </remarks>
internal sealed class WriteAheadLog : IDisposable
{
    public const int RecordSize = 80; // Fixed size for predictable I/O (v2: +TransactionTimeTicks)
    public const long DefaultCheckpointSizeThreshold = 16 * 1024 * 1024; // 16MB
    public const int DefaultCheckpointTimeSeconds = 60;

    private readonly FileStream _logFile;
    private readonly string _logPath;
    private readonly byte[] _writeBuffer;
    private readonly IBufferManager _bufferManager;
    private long _currentTxId;
    private long _lastCheckpointTxId;
    private long _lastCheckpointTime;
    private long _lastCheckpointPosition; // Cached position after last checkpoint for O(1) lookup
    private readonly long _checkpointSizeThreshold;
    private readonly int _checkpointTimeSeconds;
    private bool _disposed;

    public long CurrentTxId => _currentTxId;
    public long LastCheckpointTxId => _lastCheckpointTxId;
    public long LogSize => _logFile.Length;

    public WriteAheadLog(string logPath,
        long checkpointSizeThreshold = DefaultCheckpointSizeThreshold,
        int checkpointTimeSeconds = DefaultCheckpointTimeSeconds)
        : this(logPath, checkpointSizeThreshold, checkpointTimeSeconds, null) { }

    public WriteAheadLog(string logPath,
        long checkpointSizeThreshold,
        int checkpointTimeSeconds,
        IBufferManager? bufferManager)
    {
        _logPath = logPath;
        _checkpointSizeThreshold = checkpointSizeThreshold;
        _checkpointTimeSeconds = checkpointTimeSeconds;
        _bufferManager = bufferManager ?? PooledBufferManager.Shared;
        _writeBuffer = new byte[RecordSize];

        var exists = File.Exists(logPath);
        _logFile = new FileStream(
            logPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough); // Ensure durability

        if (exists && _logFile.Length > 0)
        {
            // Recovery: find last checkpoint and current TxId
            RecoverState();
        }
        else
        {
            _currentTxId = 0;
            _lastCheckpointTxId = 0;
            _lastCheckpointTime = Environment.TickCount64;
            _lastCheckpointPosition = 0; // No checkpoint yet
        }
    }

    /// <summary>
    /// Append a log record for a triple operation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(LogRecord record)
    {
        record.TxId = ++_currentTxId;
        record.Checksum = record.ComputeChecksum();

        record.WriteTo(_writeBuffer);
        _logFile.Write(_writeBuffer);
        _logFile.Flush(flushToDisk: true); // fsync
    }

    /// <summary>
    /// Begin a batch transaction. Writes a BeginTx marker and returns the TxId for this batch.
    /// </summary>
    public long BeginBatch()
    {
        var batchTxId = _currentTxId + 1;

        var record = new LogRecord
        {
            TxId = batchTxId,
            Operation = LogOperation.BeginTx,
            TransactionTimeTicks = 0
        };
        record.Checksum = record.ComputeChecksum();

        record.WriteTo(_writeBuffer);
        _logFile.Write(_writeBuffer);
        // No fsync — will be fsynced at CommitBatch

        return batchTxId;
    }

    /// <summary>
    /// Append a record as part of a batch (no fsync yet).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendBatch(LogRecord record, long batchTxId)
    {
        record.TxId = batchTxId;
        record.Checksum = record.ComputeChecksum();

        record.WriteTo(_writeBuffer);
        _logFile.Write(_writeBuffer);
    }

    /// <summary>
    /// Commit a batch transaction. Writes a CommitTx marker, then fsyncs.
    /// </summary>
    public void CommitBatch(long batchTxId)
    {
        var record = new LogRecord
        {
            TxId = batchTxId,
            Operation = LogOperation.CommitTx,
            TransactionTimeTicks = 0
        };
        record.Checksum = record.ComputeChecksum();

        record.WriteTo(_writeBuffer);
        _logFile.Write(_writeBuffer);

        _currentTxId = batchTxId;
        _logFile.Flush(flushToDisk: true); // fsync — CommitTx is now durable
    }

    /// <summary>
    /// Write a checkpoint marker and truncate the log.
    /// Call this after flushing all indexes to disk.
    /// </summary>
    public void Checkpoint()
    {
        // Write checkpoint record
        var record = new LogRecord
        {
            TxId = _currentTxId,
            Operation = LogOperation.Checkpoint,
            SubjectId = 0,
            PredicateId = 0,
            ObjectId = 0,
            ValidFromTicks = 0,
            ValidToTicks = 0,
            TransactionTimeTicks = 0
        };
        record.Checksum = record.ComputeChecksum();

        record.WriteTo(_writeBuffer);
        _logFile.Write(_writeBuffer);
        _logFile.Flush(flushToDisk: true);

        _lastCheckpointTxId = _currentTxId;
        _lastCheckpointTime = Environment.TickCount64;

        // Truncate log: keep only the checkpoint record
        // This reclaims disk space while preserving TxId for recovery
        TruncateLog();
    }

    /// <summary>
    /// Truncate the log file, keeping only the last checkpoint record.
    /// This reclaims disk space after all records have been applied to indexes.
    /// </summary>
    private void TruncateLog()
    {
        // Read the checkpoint record we just wrote (it's at the end)
        var checkpointPosition = _logFile.Length - RecordSize;
        if (checkpointPosition < 0)
            return;

        var buffer = _bufferManager.Rent<byte>(RecordSize).Array!;
        try
        {
            _logFile.Position = checkpointPosition;
            if (_logFile.Read(buffer, 0, RecordSize) != RecordSize)
                return;

            var checkpointRecord = LogRecord.ReadFrom(buffer);
            if (checkpointRecord.Operation != LogOperation.Checkpoint || !checkpointRecord.IsValid())
                return;

            // Write checkpoint record at the beginning of the file
            _logFile.Position = 0;
            _logFile.Write(buffer, 0, RecordSize);
            _logFile.Flush(flushToDisk: true);

            // Truncate the file to just the checkpoint record
            _logFile.SetLength(RecordSize);
            _logFile.Position = RecordSize; // Ready for new appends
            _lastCheckpointPosition = RecordSize; // Cache position for O(1) lookup
        }
        finally
        {
            _bufferManager.Return(buffer);
        }
    }

    /// <summary>
    /// Check if checkpoint is needed based on size or time thresholds.
    /// </summary>
    public bool ShouldCheckpoint()
    {
        if (_logFile.Length - GetCheckpointPosition() > _checkpointSizeThreshold)
            return true;

        var elapsedMs = Environment.TickCount64 - _lastCheckpointTime;
        if (elapsedMs > _checkpointTimeSeconds * 1000)
            return true;

        return false;
    }

    /// <summary>
    /// Enumerate all uncommitted records for recovery.
    /// </summary>
    public LogRecordEnumerator GetUncommittedRecords()
    {
        return new LogRecordEnumerator(_logFile, _lastCheckpointTxId);
    }

    /// <summary>
    /// Resets the WAL to empty state. All records are discarded.
    /// File is truncated in place - cheaper than delete + recreate.
    /// </summary>
    /// <remarks>
    /// Must be called from QuadStore.Clear() which holds the write lock.
    /// </remarks>
    public void Clear()
    {
        _logFile.SetLength(0);
        _logFile.Flush(flushToDisk: true);
        _logFile.Position = 0;

        _currentTxId = 0;
        _lastCheckpointTxId = 0;
        _lastCheckpointPosition = 0;
        _lastCheckpointTime = Environment.TickCount64;
    }

    /// <summary>
    /// Get the file position of the last checkpoint.
    /// Returns the position immediately after the checkpoint record (where new records start).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long GetCheckpointPosition()
    {
        return _lastCheckpointPosition;
    }

    private void RecoverState()
    {
        var buffer = _bufferManager.Rent<byte>(RecordSize).Array!;
        try
        {
            _logFile.Position = 0;
            _currentTxId = 0;
            _lastCheckpointTxId = 0;
            _lastCheckpointPosition = 0;

            // Track which TxIds have BeginTx/CommitTx markers to distinguish
            // committed batches from uncommitted ones and single writes.
            HashSet<long>? batchTxIds = null;
            HashSet<long>? committedTxIds = null;

            while (_logFile.Read(buffer, 0, RecordSize) == RecordSize)
            {
                var record = LogRecord.ReadFrom(buffer);
                if (!record.IsValid())
                {
                    // Corrupted record - truncate log here
                    _logFile.SetLength(_logFile.Position - RecordSize);
                    break;
                }

                if (record.Operation == LogOperation.Checkpoint)
                {
                    _lastCheckpointTxId = record.TxId;
                    _lastCheckpointPosition = _logFile.Position;
                }
                else if (record.Operation == LogOperation.BeginTx)
                {
                    batchTxIds ??= new HashSet<long>();
                    batchTxIds.Add(record.TxId);
                }
                else if (record.Operation == LogOperation.CommitTx)
                {
                    committedTxIds ??= new HashSet<long>();
                    committedTxIds.Add(record.TxId);
                }
            }

            // _currentTxId = highest committed TxId or highest single-write TxId.
            // Re-scan to find it (records are small, file is bounded by checkpoint truncation).
            _logFile.Position = 0;
            long highestCommittedOrSingle = 0;

            while (_logFile.Read(buffer, 0, RecordSize) == RecordSize)
            {
                var record = LogRecord.ReadFrom(buffer);
                if (!record.IsValid())
                    break;

                if (record.Operation == LogOperation.Checkpoint)
                    continue;

                // Skip BeginTx/CommitTx metadata records
                if (record.Operation == LogOperation.BeginTx || record.Operation == LogOperation.CommitTx)
                    continue;

                // A record is committed if:
                // 1. Its TxId has a CommitTx marker (committed batch), OR
                // 2. Its TxId has NO BeginTx marker (single write, already fsynced)
                bool isCommitted = committedTxIds != null && committedTxIds.Contains(record.TxId);
                bool isSingleWrite = batchTxIds == null || !batchTxIds.Contains(record.TxId);

                if ((isCommitted || isSingleWrite) && record.TxId > highestCommittedOrSingle)
                    highestCommittedOrSingle = record.TxId;
            }

            _currentTxId = Math.Max(highestCommittedOrSingle, _lastCheckpointTxId);
            _lastCheckpointTime = Environment.TickCount64;
            _logFile.Position = _logFile.Length; // Ready for appends
        }
        finally
        {
            _bufferManager.Return(buffer);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _logFile.Flush(flushToDisk: true);
            _logFile.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Log operation types.
/// </summary>
internal enum LogOperation : byte
{
    Add = 1,
    Delete = 2,
    BeginTx = 3,
    CommitTx = 4,
    Checkpoint = 255
}

/// <summary>
/// Fixed-size WAL record (80 bytes, v2).
///
/// Layout:
/// [0-7]   TxId (8 bytes)
/// [8]     Operation (1 byte)
/// [9-15]  Reserved (7 bytes)
/// [16-23] GraphId (8 bytes) - 0 = default graph
/// [24-31] SubjectId (8 bytes)
/// [32-39] PredicateId (8 bytes)
/// [40-47] ObjectId (8 bytes)
/// [48-55] ValidFromTicks (8 bytes)
/// [56-63] ValidToTicks (8 bytes)
/// [64-71] TransactionTimeTicks (8 bytes) - when the system recorded the fact
/// [72-79] Checksum (8 bytes)
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 80)]
internal struct LogRecord
{
    [FieldOffset(0)] public long TxId;
    [FieldOffset(8)] public LogOperation Operation;
    // [9-15] reserved
    [FieldOffset(16)] public long GraphId;
    [FieldOffset(24)] public long SubjectId;
    [FieldOffset(32)] public long PredicateId;
    [FieldOffset(40)] public long ObjectId;
    [FieldOffset(48)] public long ValidFromTicks;
    [FieldOffset(56)] public long ValidToTicks;
    [FieldOffset(64)] public long TransactionTimeTicks;
    [FieldOffset(72)] public long Checksum;

    /// <summary>
    /// Create a log record for an Add operation.
    /// </summary>
    public static LogRecord CreateAdd(long subjectId, long predicateId, long objectId,
        DateTimeOffset validFrom, DateTimeOffset validTo, long graphId = 0,
        long transactionTimeTicks = 0)
    {
        return new LogRecord
        {
            Operation = LogOperation.Add,
            GraphId = graphId,
            SubjectId = subjectId,
            PredicateId = predicateId,
            ObjectId = objectId,
            ValidFromTicks = validFrom.UtcTicks,
            ValidToTicks = validTo.UtcTicks,
            TransactionTimeTicks = transactionTimeTicks
        };
    }

    /// <summary>
    /// Create a log record for a Delete operation.
    /// </summary>
    public static LogRecord CreateDelete(long subjectId, long predicateId, long objectId,
        DateTimeOffset validFrom, DateTimeOffset validTo, long graphId = 0,
        long transactionTimeTicks = 0)
    {
        return new LogRecord
        {
            Operation = LogOperation.Delete,
            GraphId = graphId,
            SubjectId = subjectId,
            PredicateId = predicateId,
            ObjectId = objectId,
            ValidFromTicks = validFrom.UtcTicks,
            ValidToTicks = validTo.UtcTicks,
            TransactionTimeTicks = transactionTimeTicks
        };
    }

    /// <summary>
    /// Compute checksum for integrity validation.
    /// Uses a simple XOR-based hash for speed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly long ComputeChecksum()
    {
        // XOR all fields together with a prime multiplier
        const long prime = unchecked((long)0x9E3779B97F4A7C15UL); // Golden ratio prime
        long hash = TxId;
        hash ^= (long)Operation * prime;
        hash ^= GraphId * prime;
        hash ^= SubjectId * prime;
        hash ^= PredicateId * prime;
        hash ^= ObjectId * prime;
        hash ^= ValidFromTicks * prime;
        hash ^= ValidToTicks * prime;
        hash ^= TransactionTimeTicks * prime;
        return hash;
    }

    /// <summary>
    /// Validate the record's checksum.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IsValid()
    {
        return Checksum == ComputeChecksum();
    }

    /// <summary>
    /// Write record to a buffer.
    /// </summary>
    public readonly void WriteTo(Span<byte> buffer)
    {
        BinaryPrimitives.WriteInt64LittleEndian(buffer[0..8], TxId);
        buffer[8] = (byte)Operation;
        buffer[9..16].Clear(); // Reserved
        BinaryPrimitives.WriteInt64LittleEndian(buffer[16..24], GraphId);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[24..32], SubjectId);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[32..40], PredicateId);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[40..48], ObjectId);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[48..56], ValidFromTicks);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[56..64], ValidToTicks);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[64..72], TransactionTimeTicks);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[72..80], Checksum);
    }

    /// <summary>
    /// Read record from a buffer.
    /// </summary>
    public static LogRecord ReadFrom(ReadOnlySpan<byte> buffer)
    {
        return new LogRecord
        {
            TxId = BinaryPrimitives.ReadInt64LittleEndian(buffer[0..8]),
            Operation = (LogOperation)buffer[8],
            GraphId = BinaryPrimitives.ReadInt64LittleEndian(buffer[16..24]),
            SubjectId = BinaryPrimitives.ReadInt64LittleEndian(buffer[24..32]),
            PredicateId = BinaryPrimitives.ReadInt64LittleEndian(buffer[32..40]),
            ObjectId = BinaryPrimitives.ReadInt64LittleEndian(buffer[40..48]),
            ValidFromTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer[48..56]),
            ValidToTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer[56..64]),
            TransactionTimeTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer[64..72]),
            Checksum = BinaryPrimitives.ReadInt64LittleEndian(buffer[72..80])
        };
    }
}

/// <summary>
/// Enumerator for replaying committed WAL records during recovery.
/// Two-pass: first collects committed/batch TxId sets, then yields only committed Add/Delete records.
/// Call Dispose() when done to return buffer to pool.
/// </summary>
internal ref struct LogRecordEnumerator
{
    private readonly FileStream _logFile;
    private readonly long _afterTxId;
    private byte[]? _buffer;
    private LogRecord _current;
    private readonly HashSet<long>? _committedTxIds;
    private readonly HashSet<long>? _batchTxIds;

    public LogRecordEnumerator(FileStream logFile, long afterTxId)
    {
        _logFile = logFile;
        _afterTxId = afterTxId;
        _buffer = PooledBufferManager.Shared.Rent<byte>(WriteAheadLog.RecordSize).Array!;
        _current = default;

        // First pass: collect committed and batch TxId sets
        _committedTxIds = null;
        _batchTxIds = null;
        _logFile.Position = 0;

        while (_logFile.Read(_buffer, 0, WriteAheadLog.RecordSize) == WriteAheadLog.RecordSize)
        {
            var record = LogRecord.ReadFrom(_buffer);
            if (!record.IsValid())
                break;

            if (record.Operation == LogOperation.BeginTx)
            {
                _batchTxIds ??= new HashSet<long>();
                _batchTxIds.Add(record.TxId);
            }
            else if (record.Operation == LogOperation.CommitTx)
            {
                _committedTxIds ??= new HashSet<long>();
                _committedTxIds.Add(record.TxId);
            }
        }

        // Position at start for second pass
        _logFile.Position = 0;
    }

    public readonly LogRecord Current => _current;

    public bool MoveNext()
    {
        if (_buffer == null)
            return false;

        while (_logFile.Read(_buffer, 0, WriteAheadLog.RecordSize) == WriteAheadLog.RecordSize)
        {
            _current = LogRecord.ReadFrom(_buffer);

            // Skip invalid records
            if (!_current.IsValid())
                continue;

            // Skip metadata records
            if (_current.Operation == LogOperation.Checkpoint ||
                _current.Operation == LogOperation.BeginTx ||
                _current.Operation == LogOperation.CommitTx)
                continue;

            // Skip already-applied records
            if (_current.TxId <= _afterTxId)
                continue;

            // A record is replayable if:
            // 1. Its TxId has a CommitTx marker (committed batch), OR
            // 2. Its TxId has NO BeginTx marker (single write, already fsynced by Append)
            bool isCommittedBatch = _committedTxIds != null && _committedTxIds.Contains(_current.TxId);
            bool isSingleWrite = _batchTxIds == null || !_batchTxIds.Contains(_current.TxId);

            if (isCommittedBatch || isSingleWrite)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Return the pooled buffer. Call this when done iterating.
    /// </summary>
    public void Dispose()
    {
        if (_buffer != null)
        {
            PooledBufferManager.Shared.Return(_buffer);
            _buffer = null;
        }
    }
}
