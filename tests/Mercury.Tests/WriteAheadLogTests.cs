using System;
using System.IO;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for WriteAheadLog - crash-safe durability layer.
/// </summary>
public class WriteAheadLogTests : IDisposable
{
    private readonly string _testPath;
    private WriteAheadLog? _wal;

    public WriteAheadLogTests()
    {
        _testPath = Path.Combine(Path.GetTempPath(), $"wal_test_{Guid.NewGuid():N}.log");
    }

    public void Dispose()
    {
        _wal?.Dispose();
        if (File.Exists(_testPath))
            File.Delete(_testPath);
    }

    private WriteAheadLog CreateWal(long sizeThreshold = 16 * 1024 * 1024, int timeSeconds = 60)
    {
        _wal?.Dispose();
        _wal = new WriteAheadLog(_testPath, sizeThreshold, timeSeconds);
        return _wal;
    }

    #region LogRecord Tests

    [Fact]
    public void LogRecord_CreateAdd_SetsCorrectFields()
    {
        var now = DateTimeOffset.UtcNow;
        var record = LogRecord.CreateAdd(1, 2, 3, now, DateTimeOffset.MaxValue);

        Assert.Equal(LogOperation.Add, record.Operation);
        Assert.Equal(1, record.SubjectId);
        Assert.Equal(2, record.PredicateId);
        Assert.Equal(3, record.ObjectId);
        Assert.Equal(now.UtcTicks, record.ValidFromTicks);
        Assert.Equal(DateTimeOffset.MaxValue.UtcTicks, record.ValidToTicks);
    }

    [Fact]
    public void LogRecord_CreateDelete_SetsCorrectFields()
    {
        var from = DateTimeOffset.UtcNow;
        var to = from.AddDays(1);
        var record = LogRecord.CreateDelete(10, 20, 30, from, to);

        Assert.Equal(LogOperation.Delete, record.Operation);
        Assert.Equal(10, record.SubjectId);
        Assert.Equal(20, record.PredicateId);
        Assert.Equal(30, record.ObjectId);
    }

    [Fact]
    public void LogRecord_Checksum_ValidAfterCompute()
    {
        var record = LogRecord.CreateAdd(1, 2, 3, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
        record.TxId = 42;
        record.Checksum = record.ComputeChecksum();

        Assert.True(record.IsValid());
    }

    [Fact]
    public void LogRecord_Checksum_InvalidAfterModification()
    {
        var record = LogRecord.CreateAdd(1, 2, 3, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
        record.TxId = 42;
        record.Checksum = record.ComputeChecksum();

        // Modify a field
        record.SubjectId = 999;

        Assert.False(record.IsValid());
    }

    [Fact]
    public void LogRecord_WriteAndRead_Roundtrip()
    {
        var original = LogRecord.CreateAdd(100, 200, 300, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
        original.TxId = 1;
        original.Checksum = original.ComputeChecksum();

        var buffer = new byte[WriteAheadLog.RecordSize];
        original.WriteTo(buffer);

        var restored = LogRecord.ReadFrom(buffer);

        Assert.Equal(original.TxId, restored.TxId);
        Assert.Equal(original.Operation, restored.Operation);
        Assert.Equal(original.SubjectId, restored.SubjectId);
        Assert.Equal(original.PredicateId, restored.PredicateId);
        Assert.Equal(original.ObjectId, restored.ObjectId);
        Assert.Equal(original.ValidFromTicks, restored.ValidFromTicks);
        Assert.Equal(original.ValidToTicks, restored.ValidToTicks);
        Assert.Equal(original.Checksum, restored.Checksum);
        Assert.True(restored.IsValid());
    }

    #endregion

    #region Basic Append

    [Fact]
    public void Append_SingleRecord_IncreasesTxId()
    {
        var wal = CreateWal();
        Assert.Equal(0, wal.CurrentTxId);

        var record = LogRecord.CreateAdd(1, 2, 3, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
        wal.Append(record);

        Assert.Equal(1, wal.CurrentTxId);
    }

    [Fact]
    public void Append_MultipleRecords_IncrementsTxId()
    {
        var wal = CreateWal();

        for (int i = 1; i <= 10; i++)
        {
            var record = LogRecord.CreateAdd(i, i, i, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
            wal.Append(record);
            Assert.Equal(i, wal.CurrentTxId);
        }
    }

    [Fact]
    public void Append_Record_IncreasesLogSize()
    {
        var wal = CreateWal();
        var initialSize = wal.LogSize;

        var record = LogRecord.CreateAdd(1, 2, 3, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
        wal.Append(record);

        Assert.Equal(initialSize + WriteAheadLog.RecordSize, wal.LogSize);
    }

    #endregion

    #region Batch Operations

    [Fact]
    public void BeginBatch_ReturnsNextTxId()
    {
        var wal = CreateWal();
        var record1 = LogRecord.CreateAdd(1, 2, 3, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
        wal.Append(record1);

        var batchTxId = wal.BeginBatch();

        Assert.Equal(2, batchTxId);
    }

    [Fact]
    public void AppendBatch_DoesNotUpdateCurrentTxId()
    {
        var wal = CreateWal();
        var batchTxId = wal.BeginBatch();
        var record = LogRecord.CreateAdd(1, 2, 3, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);

        wal.AppendBatch(record, batchTxId);

        Assert.Equal(0, wal.CurrentTxId); // Not updated until commit
    }

    [Fact]
    public void CommitBatch_UpdatesCurrentTxId()
    {
        var wal = CreateWal();
        var batchTxId = wal.BeginBatch();
        var record = LogRecord.CreateAdd(1, 2, 3, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
        wal.AppendBatch(record, batchTxId);

        wal.CommitBatch(batchTxId);

        Assert.Equal(batchTxId, wal.CurrentTxId);
    }

    [Fact]
    public void Batch_MultipleRecords_AllWritten()
    {
        var wal = CreateWal();
        var batchTxId = wal.BeginBatch();

        for (int i = 0; i < 100; i++)
        {
            var record = LogRecord.CreateAdd(i, i, i, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
            wal.AppendBatch(record, batchTxId);
        }

        wal.CommitBatch(batchTxId);

        Assert.Equal(100 * WriteAheadLog.RecordSize, wal.LogSize);
    }

    #endregion

    #region Checkpoint

    [Fact]
    public void Checkpoint_UpdatesLastCheckpointTxId()
    {
        var wal = CreateWal();
        var record = LogRecord.CreateAdd(1, 2, 3, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
        wal.Append(record);

        Assert.Equal(0, wal.LastCheckpointTxId);

        wal.Checkpoint();

        Assert.Equal(1, wal.LastCheckpointTxId);
    }

    [Fact]
    public void Checkpoint_WritesCheckpointRecord()
    {
        var wal = CreateWal();
        var record = LogRecord.CreateAdd(1, 2, 3, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
        wal.Append(record);

        var sizeBeforeCheckpoint = wal.LogSize;
        wal.Checkpoint();

        Assert.Equal(sizeBeforeCheckpoint + WriteAheadLog.RecordSize, wal.LogSize);
    }

    [Fact]
    public void ShouldCheckpoint_SizeThresholdExceeded_ReturnsTrue()
    {
        // Use small threshold for testing
        var wal = CreateWal(sizeThreshold: WriteAheadLog.RecordSize * 5);

        for (int i = 0; i < 10; i++)
        {
            var record = LogRecord.CreateAdd(i, i, i, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
            wal.Append(record);
        }

        Assert.True(wal.ShouldCheckpoint());
    }

    [Fact]
    public void ShouldCheckpoint_BelowThreshold_ReturnsFalse()
    {
        var wal = CreateWal(); // Default 16MB threshold

        var record = LogRecord.CreateAdd(1, 2, 3, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
        wal.Append(record);

        Assert.False(wal.ShouldCheckpoint());
    }

    #endregion

    #region Recovery

    [Fact]
    public void Recovery_ReopenWal_RestoresTxId()
    {
        // First session
        using (var wal1 = new WriteAheadLog(_testPath))
        {
            for (int i = 0; i < 5; i++)
            {
                var record = LogRecord.CreateAdd(i, i, i, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
                wal1.Append(record);
            }
        }

        // Second session
        using (var wal2 = new WriteAheadLog(_testPath))
        {
            Assert.Equal(5, wal2.CurrentTxId);
        }
    }

    [Fact]
    public void Recovery_ReopenWal_RestoresCheckpointTxId()
    {
        // First session
        using (var wal1 = new WriteAheadLog(_testPath))
        {
            for (int i = 0; i < 3; i++)
            {
                var record = LogRecord.CreateAdd(i, i, i, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
                wal1.Append(record);
            }
            wal1.Checkpoint();

            var record2 = LogRecord.CreateAdd(99, 99, 99, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
            wal1.Append(record2);
        }

        // Second session
        using (var wal2 = new WriteAheadLog(_testPath))
        {
            Assert.Equal(3, wal2.LastCheckpointTxId);
            Assert.Equal(4, wal2.CurrentTxId);
        }
    }

    [Fact]
    public void GetUncommittedRecords_ReturnsRecordsAfterCheckpoint()
    {
        using (var wal = new WriteAheadLog(_testPath))
        {
            // Write some records
            for (int i = 0; i < 3; i++)
            {
                var record = LogRecord.CreateAdd(i, i, i, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
                wal.Append(record);
            }
            wal.Checkpoint();

            // Write more records after checkpoint
            for (int i = 10; i < 15; i++)
            {
                var record = LogRecord.CreateAdd(i, i, i, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
                wal.Append(record);
            }
        }

        // Reopen and check uncommitted records
        using (var wal2 = new WriteAheadLog(_testPath))
        {
            var enumerator = wal2.GetUncommittedRecords();
            var uncommitted = new List<LogRecord>();

            while (enumerator.MoveNext())
            {
                uncommitted.Add(enumerator.Current);
            }

            Assert.Equal(5, uncommitted.Count);
            Assert.All(uncommitted, r => Assert.True(r.SubjectId >= 10));
        }
    }

    [Fact]
    public void GetUncommittedRecords_NoCheckpoint_ReturnsAllRecords()
    {
        using (var wal = new WriteAheadLog(_testPath))
        {
            for (int i = 0; i < 5; i++)
            {
                var record = LogRecord.CreateAdd(i, i, i, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
                wal.Append(record);
            }
        }

        using (var wal2 = new WriteAheadLog(_testPath))
        {
            var enumerator = wal2.GetUncommittedRecords();
            var count = 0;

            while (enumerator.MoveNext())
            {
                count++;
            }

            Assert.Equal(5, count);
        }
    }

    [Fact]
    public void GetUncommittedRecords_AfterCheckpointAll_ReturnsEmpty()
    {
        using (var wal = new WriteAheadLog(_testPath))
        {
            for (int i = 0; i < 3; i++)
            {
                var record = LogRecord.CreateAdd(i, i, i, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
                wal.Append(record);
            }
            wal.Checkpoint();
        }

        using (var wal2 = new WriteAheadLog(_testPath))
        {
            var enumerator = wal2.GetUncommittedRecords();

            Assert.False(enumerator.MoveNext());
        }
    }

    #endregion

    #region Corruption Handling

    [Fact]
    public void Recovery_CorruptedRecord_TruncatesLog()
    {
        // First session: write valid records
        using (var wal1 = new WriteAheadLog(_testPath))
        {
            for (int i = 0; i < 3; i++)
            {
                var record = LogRecord.CreateAdd(i, i, i, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
                wal1.Append(record);
            }
        }

        // Corrupt the last record
        using (var fs = new FileStream(_testPath, FileMode.Open, FileAccess.ReadWrite))
        {
            // Corrupt the last record's checksum
            fs.Position = fs.Length - 8; // Last 8 bytes are checksum
            fs.WriteByte(0xFF);
            fs.WriteByte(0xFF);
        }

        // Second session: should truncate corrupted record
        using (var wal2 = new WriteAheadLog(_testPath))
        {
            // Should have 2 valid records (third was corrupted and truncated)
            Assert.Equal(2, wal2.CurrentTxId);
        }
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void NewWal_EmptyFile_InitializesCorrectly()
    {
        var wal = CreateWal();

        Assert.Equal(0, wal.CurrentTxId);
        Assert.Equal(0, wal.LastCheckpointTxId);
        Assert.Equal(0, wal.LogSize);
    }

    [Fact]
    public void Append_LargeAtomIds_Preserved()
    {
        var wal = CreateWal();
        var largeId = long.MaxValue - 1;
        var record = LogRecord.CreateAdd(largeId, largeId, largeId, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);

        wal.Append(record);
        wal.Dispose();

        using var wal2 = new WriteAheadLog(_testPath);
        var enumerator = wal2.GetUncommittedRecords();
        Assert.True(enumerator.MoveNext());
        Assert.Equal(largeId, enumerator.Current.SubjectId);
    }

    #endregion
}
