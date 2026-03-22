using System;
using System.IO;
using System.Text;
using System.Threading;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Diagnostics;
using SkyOmega.Mercury.Runtime.Buffers;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// RDF quad store with multiple indexes for optimal query patterns.
/// Uses Write-Ahead Logging (WAL) for crash safety.
/// Thread-safe: multiple concurrent readers, single writer.
///
/// Indexes:
/// 1. GSPO: Graph-Subject-Predicate-Object-Time (primary)
/// 2. GPOS: Graph-Predicate-Object-Subject-Time (predicate queries)
/// 3. GOSP: Graph-Object-Subject-Predicate-Time (object queries)
/// 4. TGSP: Time-Graph-Subject-Predicate-Object (temporal range scans)
/// </summary>
public sealed class QuadStore : IDisposable
{
    private readonly QuadIndex _gspoIndex; // Primary index: S→Primary, P→Secondary, O→Tertiary
    private readonly QuadIndex _gposIndex; // Predicate-first: P→Primary, O→Secondary, S→Tertiary
    private readonly QuadIndex _gospIndex; // Object-first: O→Primary, S→Secondary, P→Tertiary
    private readonly QuadIndex _tgspIndex; // Time-first: S→Primary, P→Secondary, O→Tertiary
    private readonly TrigramIndex _trigramIndex;

    private readonly AtomStore _atoms;
    private readonly WriteAheadLog _wal;
    private readonly string _baseDirectory;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly ILogger _logger;
    private readonly IBufferManager _bufferManager;
    private readonly long _minimumFreeDiskSpace;
    private readonly StatisticsStore _statistics = new();
    private long _activeBatchTxId = -1;
    private List<(LogRecord Record, string Graph, string Subject, string Predicate, string Object)>? _batchBuffer;
    private bool _disposed;
    private bool _diskSpaceLow; // Set when disk space drops below threshold

    /// <summary>
    /// Creates a new QuadStore at the specified directory.
    /// </summary>
    /// <param name="baseDirectory">Directory for store files.</param>
    public QuadStore(string baseDirectory) : this(baseDirectory, null, null, null) { }

    /// <summary>
    /// Creates a new QuadStore at the specified directory with logging.
    /// </summary>
    /// <param name="baseDirectory">Directory for store files.</param>
    /// <param name="logger">Logger for diagnostics (null for no logging).</param>
    public QuadStore(string baseDirectory, ILogger? logger) : this(baseDirectory, logger, null, null) { }

    /// <summary>
    /// Creates a new QuadStore at the specified directory with logging and buffer management.
    /// </summary>
    /// <param name="baseDirectory">Directory for store files.</param>
    /// <param name="logger">Logger for diagnostics (null for no logging).</param>
    /// <param name="bufferManager">Buffer manager for allocations (null for default pooled manager).</param>
    public QuadStore(string baseDirectory, ILogger? logger, IBufferManager? bufferManager)
        : this(baseDirectory, logger, bufferManager, null) { }

    /// <summary>
    /// Creates a new QuadStore at the specified directory with full configuration.
    /// </summary>
    /// <param name="baseDirectory">Directory for store files.</param>
    /// <param name="logger">Logger for diagnostics (null for no logging).</param>
    /// <param name="bufferManager">Buffer manager for allocations (null for default pooled manager).</param>
    /// <param name="storageOptions">Storage options including disk space limits (null for defaults).</param>
    public QuadStore(string baseDirectory, ILogger? logger, IBufferManager? bufferManager, StorageOptions? storageOptions)
    {
        _baseDirectory = baseDirectory;
        _logger = logger ?? NullLogger.Instance;
        _bufferManager = bufferManager ?? PooledBufferManager.Shared;
        _minimumFreeDiskSpace = (storageOptions ?? StorageOptions.Default).MinimumFreeDiskSpace;

        if (!Directory.Exists(baseDirectory))
            Directory.CreateDirectory(baseDirectory);

        var gspoPath = Path.Combine(baseDirectory, "gspo.tdb");
        var gposPath = Path.Combine(baseDirectory, "gpos.tdb");
        var gospPath = Path.Combine(baseDirectory, "gosp.tdb");
        var tgspPath = Path.Combine(baseDirectory, "tgsp.tdb");
        var atomPath = Path.Combine(baseDirectory, "atoms");
        var walPath = Path.Combine(baseDirectory, "wal.log");

        var options = storageOptions ?? StorageOptions.Default;

        // Create shared atom store for all indexes
        _atoms = new AtomStore(atomPath, _bufferManager, options.MaxAtomSize,
            options.AtomDataInitialSizeBytes, options.AtomOffsetInitialCapacity);

        // Create WAL for durability
        _wal = new WriteAheadLog(walPath, WriteAheadLog.DefaultCheckpointSizeThreshold,
            WriteAheadLog.DefaultCheckpointTimeSeconds, _bufferManager);

        // Create indexes with shared atom store
        // Entity-first indexes sort by Graph → dimensions → time
        // TGSP uses time-first sort for O(log N + k) temporal range queries
        _gspoIndex = new QuadIndex(gspoPath, _atoms, options.IndexInitialSizeBytes, QuadIndex.KeySortOrder.EntityFirst);
        _gposIndex = new QuadIndex(gposPath, _atoms, options.IndexInitialSizeBytes, QuadIndex.KeySortOrder.EntityFirst);
        _gospIndex = new QuadIndex(gospPath, _atoms, options.IndexInitialSizeBytes, QuadIndex.KeySortOrder.EntityFirst);
        _tgspIndex = new QuadIndex(tgspPath, _atoms, options.IndexInitialSizeBytes, QuadIndex.KeySortOrder.TimeFirst);

        // Create trigram index for full-text search
        var trigramPath = Path.Combine(baseDirectory, "trigram");
        _trigramIndex = new TrigramIndex(trigramPath, _bufferManager);

        _logger.Info("Opening store at {0}".AsSpan(), baseDirectory);

        // Recover any uncommitted transactions
        Recover();
    }

    /// <summary>
    /// Access to predicate statistics for query optimization.
    /// Thread-safe: returns immutable snapshots via copy-on-write.
    /// </summary>
    public StatisticsStore Statistics => _statistics;

    /// <summary>
    /// Access to atom store for looking up atom IDs.
    /// Required by QueryPlanner for cardinality estimation.
    /// </summary>
    /// <remarks>
    /// Internal: AtomStore requires external synchronization via this QuadStore's
    /// read/write locks. Direct access is only safe within Mercury internals.
    /// </remarks>
    internal AtomStore Atoms => _atoms;

    /// <summary>
    /// Returns true if disk space is below the configured minimum threshold.
    /// When true, write operations will fail until space is freed.
    /// </summary>
    public bool IsDiskSpaceLow => _diskSpaceLow;

    /// <summary>
    /// Throws if disk space was detected as low after a previous operation.
    /// Call this at the start of any write operation.
    /// </summary>
    private void ThrowIfDiskSpaceLow()
    {
        if (_diskSpaceLow)
        {
            var available = DiskSpaceChecker.GetAvailableSpace(_baseDirectory);
            if (available >= _minimumFreeDiskSpace)
            {
                // Space has been freed, clear the flag
                _diskSpaceLow = false;
            }
            else
            {
                throw new InsufficientDiskSpaceException(
                    _baseDirectory,
                    0, // No specific request, just refusing new writes
                    available,
                    _minimumFreeDiskSpace);
            }
        }
    }

    /// <summary>
    /// Checks disk space after a write operation and sets the low-space flag if needed.
    /// The current operation has already succeeded; this prevents the NEXT operation.
    /// </summary>
    private void CheckDiskSpaceAfterWrite()
    {
        if (_minimumFreeDiskSpace <= 0)
            return;

        var available = DiskSpaceChecker.GetAvailableSpace(_baseDirectory);
        if (available >= 0 && available < _minimumFreeDiskSpace)
        {
            _diskSpaceLow = true;
            _logger.Warning($"Disk space low: {available} bytes available, minimum is {_minimumFreeDiskSpace} bytes. Further writes will be refused.".AsSpan());
        }
    }

    /// <summary>
    /// Add a temporal quad to all indexes with WAL durability.
    /// Thread-safe: acquires write lock.
    /// </summary>
    public void Add(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        ReadOnlySpan<char> graph = default)
    {
        ThrowIfDisposed();
        ThrowIfDiskSpaceLow();
        _lock.EnterWriteLock();
        try
        {
            // 1. Intern atoms first (AtomStore is append-only, naturally durable)
            var graphId = graph.IsEmpty ? 0 : _atoms.Intern(graph);
            var subjectId = _atoms.Intern(subject);
            var predicateId = _atoms.Intern(predicate);
            var objectId = _atoms.Intern(@object);
            var transactionTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 2. Write to WAL (fsync ensures durability)
            var record = LogRecord.CreateAdd(subjectId, predicateId, objectId, validFrom, validTo, graphId,
                transactionTimeTicks: transactionTime);
            _wal.Append(record);

            // 3. Apply to indexes
            ApplyToIndexes(subject, predicate, @object, validFrom, validTo, transactionTime, graph);

            // 4. Check if checkpoint needed
            CheckpointIfNeeded();

            // 5. Check disk space AFTER write (current op succeeds, next may be refused)
            CheckDiskSpaceAfterWrite();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Add a current fact (valid from now onwards) with WAL durability.
    /// Thread-safe: acquires write lock.
    /// </summary>
    public void AddCurrent(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        ReadOnlySpan<char> graph = default)
    {
        Add(subject, predicate, @object, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, graph);
    }

    /// <summary>
    /// Soft-delete a temporal triple from all indexes with WAL durability.
    /// Thread-safe: acquires write lock.
    /// Returns true if the quad was found and deleted, false otherwise.
    /// </summary>
    public bool Delete(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        ReadOnlySpan<char> graph = default)
    {
        ThrowIfDisposed();
        ThrowIfDiskSpaceLow();
        _lock.EnterWriteLock();
        try
        {
            // 1. Look up atoms (don't intern - if they don't exist, quad doesn't exist)
            var graphId = graph.IsEmpty ? 0 : _atoms.GetAtomId(graph);
            var subjectId = _atoms.GetAtomId(subject);
            var predicateId = _atoms.GetAtomId(predicate);
            var objectId = _atoms.GetAtomId(@object);

            if (subjectId == 0 || predicateId == 0 || objectId == 0)
                return false;
            if (!graph.IsEmpty && graphId == 0)
                return false;

            var transactionTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 2. Write to WAL (fsync ensures durability)
            var record = LogRecord.CreateDelete(subjectId, predicateId, objectId, validFrom, validTo, graphId,
                transactionTimeTicks: transactionTime);
            _wal.Append(record);

            // 3. Apply to indexes
            var deleted = ApplyDeleteToIndexes(subject, predicate, @object, validFrom, validTo, transactionTime, graph);

            // 4. Check if checkpoint needed
            CheckpointIfNeeded();

            // 5. Check disk space AFTER write (current op succeeds, next may be refused)
            CheckDiskSpaceAfterWrite();

            return deleted;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Soft-delete a current fact with WAL durability.
    /// Thread-safe: acquires write lock.
    /// Returns true if the quad was found and deleted, false otherwise.
    /// </summary>
    public bool DeleteCurrent(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        ReadOnlySpan<char> graph = default)
    {
        // Use a wide time range to match any current fact
        return Delete(subject, predicate, @object, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, graph);
    }

    #region Batch API

    /// <summary>
    /// Begin a batch write transaction for high-throughput bulk loading.
    /// Acquires exclusive write lock until CommitBatch() or RollbackBatch() is called.
    ///
    /// Usage:
    ///   store.BeginBatch();
    ///   try {
    ///       foreach (var triple in triples)
    ///           store.AddBatched(...);
    ///       store.CommitBatch();
    ///   } catch {
    ///       store.RollbackBatch();
    ///       throw;
    ///   }
    /// </summary>
    public void BeginBatch()
    {
        ThrowIfDisposed();
        ThrowIfDiskSpaceLow();
        _lock.EnterWriteLock();
        try
        {
            if (_activeBatchTxId >= 0)
                throw new InvalidOperationException("A batch is already active.");

            _activeBatchTxId = _wal.BeginBatch();
            _batchBuffer ??= new();
            _batchBuffer.Clear();
        }
        catch
        {
            _lock.ExitWriteLock();
            throw;
        }
        // Lock held until CommitBatch/RollbackBatch
    }

    /// <summary>
    /// Add a temporal quad to the current batch (no fsync until CommitBatch).
    /// Must be called between BeginBatch() and CommitBatch()/RollbackBatch().
    /// </summary>
    public void AddBatched(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        ReadOnlySpan<char> graph = default)
    {
        if (_activeBatchTxId < 0)
            throw new InvalidOperationException("No active batch. Call BeginBatch() first.");

        // 1. Intern atoms (AtomStore is append-only — orphans from rollback are harmless)
        var graphId = graph.IsEmpty ? 0 : _atoms.Intern(graph);
        var subjectId = _atoms.Intern(subject);
        var predicateId = _atoms.Intern(predicate);
        var objectId = _atoms.Intern(@object);

        var transactionTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 2. Write to WAL without fsync
        var record = LogRecord.CreateAdd(subjectId, predicateId, objectId, validFrom, validTo, graphId,
            transactionTimeTicks: transactionTime);
        _wal.AppendBatch(record, _activeBatchTxId);

        // 3. Buffer for deferred materialization at CommitBatch (indexes untouched until commit)
        var graphStr = graph.IsEmpty ? string.Empty : _atoms.GetAtomString(graphId);
        _batchBuffer!.Add((record, graphStr, _atoms.GetAtomString(subjectId),
            _atoms.GetAtomString(predicateId), _atoms.GetAtomString(objectId)));
    }

    /// <summary>
    /// Add a current fact to the batch (valid from now onwards).
    /// </summary>
    public void AddCurrentBatched(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        ReadOnlySpan<char> graph = default)
    {
        AddBatched(subject, predicate, @object, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, graph);
    }

    /// <summary>
    /// Soft-delete a temporal quad in the current batch (no fsync until CommitBatch).
    /// Must be called between BeginBatch() and CommitBatch()/RollbackBatch().
    /// Returns true if the atoms exist and the delete was buffered, false if atoms are unknown.
    /// With deferred materialization, the actual index mutation is applied at CommitBatch time.
    /// </summary>
    public bool DeleteBatched(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        ReadOnlySpan<char> graph = default)
    {
        if (_activeBatchTxId < 0)
            throw new InvalidOperationException("No active batch. Call BeginBatch() first.");

        // 1. Look up atoms (don't intern for deletes)
        var graphId = graph.IsEmpty ? 0 : _atoms.GetAtomId(graph);
        var subjectId = _atoms.GetAtomId(subject);
        var predicateId = _atoms.GetAtomId(predicate);
        var objectId = _atoms.GetAtomId(@object);

        if (subjectId == 0 || predicateId == 0 || objectId == 0)
            return false;
        if (!graph.IsEmpty && graphId == 0)
            return false;

        var transactionTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 2. Write to WAL without fsync
        var record = LogRecord.CreateDelete(subjectId, predicateId, objectId, validFrom, validTo, graphId,
            transactionTimeTicks: transactionTime);
        _wal.AppendBatch(record, _activeBatchTxId);

        // 3. Buffer for deferred materialization at CommitBatch (indexes untouched until commit)
        var graphStr = graph.IsEmpty ? string.Empty : _atoms.GetAtomString(graphId);
        _batchBuffer!.Add((record, graphStr, _atoms.GetAtomString(subjectId),
            _atoms.GetAtomString(predicateId), _atoms.GetAtomString(objectId)));
        return true;
    }

    /// <summary>
    /// Soft-delete a current fact in the batch.
    /// Returns true if the atoms exist and the delete was buffered, false if atoms are unknown.
    /// </summary>
    public bool DeleteCurrentBatched(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        ReadOnlySpan<char> graph = default)
    {
        return DeleteBatched(subject, predicate, @object, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, graph);
    }

    /// <summary>
    /// Commit the current batch transaction with a single fsync.
    /// Releases the write lock acquired by BeginBatch().
    /// </summary>
    public void CommitBatch()
    {
        try
        {
            if (_activeBatchTxId < 0)
                throw new InvalidOperationException("No active batch.");

            // 1. Write CommitTx marker and fsync — batch is now durable
            _wal.CommitBatch(_activeBatchTxId);
            _activeBatchTxId = -1;

            // 2. Materialize buffered records into indexes
            if (_batchBuffer != null)
            {
                foreach (var (record, graph, subject, predicate, obj) in _batchBuffer)
                {
                    var validFrom = new DateTimeOffset(record.ValidFromTicks, TimeSpan.Zero);
                    var validTo = new DateTimeOffset(record.ValidToTicks, TimeSpan.Zero);

                    if (record.Operation == LogOperation.Add)
                        ApplyToIndexes(subject, predicate, obj, validFrom, validTo, record.TransactionTimeTicks, graph);
                    else if (record.Operation == LogOperation.Delete)
                        ApplyDeleteToIndexes(subject, predicate, obj, validFrom, validTo, record.TransactionTimeTicks, graph);
                }
                _batchBuffer.Clear();
            }

            CheckpointIfNeeded();

            // Check disk space AFTER commit (batch succeeds, next operation may be refused)
            CheckDiskSpaceAfterWrite();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Rollback the current batch (releases lock without committing).
    /// Indexes are untouched — deferred materialization means no mutations to undo.
    /// WAL records have BeginTx but no CommitTx, so recovery will discard them.
    /// </summary>
    public void RollbackBatch()
    {
        _batchBuffer?.Clear();
        _activeBatchTxId = -1;
        _lock.ExitWriteLock();
    }

    /// <summary>
    /// Returns true if a batch is currently active.
    /// </summary>
    public bool IsBatchActive => _activeBatchTxId >= 0;

    #endregion

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(QuadStore));
    }

    /// <summary>
    /// Apply a quad to all indexes (internal, no WAL).
    /// Each index receives its dimensions in the order that matches its sort:
    /// GSPO: S→Primary, P→Secondary, O→Tertiary
    /// GPOS: P→Primary, O→Secondary, S→Tertiary
    /// GOSP: O→Primary, S→Secondary, P→Tertiary
    /// TGSP: S→Primary, P→Secondary, O→Tertiary (same mapping as GSPO, different sort order)
    /// </summary>
    private void ApplyToIndexes(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        long transactionTime = 0,
        ReadOnlySpan<char> graph = default)
    {
        _gspoIndex.AddHistorical(subject, predicate, @object, validFrom, validTo, transactionTime, graph);
        _gposIndex.AddHistorical(predicate, @object, subject, validFrom, validTo, transactionTime, graph);
        _gospIndex.AddHistorical(@object, subject, predicate, validFrom, validTo, transactionTime, graph);
        _tgspIndex.AddHistorical(subject, predicate, @object, validFrom, validTo, transactionTime, graph);

        // Index object for full-text search if it's a literal (starts with ")
        if (!@object.IsEmpty && @object[0] == '"')
        {
            var objectId = _atoms.GetAtomId(@object);
            if (objectId > 0)
            {
                var utf8Span = _atoms.GetAtomSpan(objectId);
                _trigramIndex.IndexAtom(objectId, utf8Span);
            }
        }
    }

    /// <summary>
    /// Apply a delete to all indexes (internal, no WAL).
    /// Returns true if deleted from at least one index.
    /// </summary>
    private bool ApplyDeleteToIndexes(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        long transactionTime = 0,
        ReadOnlySpan<char> graph = default)
    {
        // Delete from all 4 indexes - use the appropriate dimension order for each
        var d1 = _gspoIndex.DeleteHistorical(subject, predicate, @object, validFrom, validTo, transactionTime, graph);
        var d2 = _gposIndex.DeleteHistorical(predicate, @object, subject, validFrom, validTo, transactionTime, graph);
        var d3 = _gospIndex.DeleteHistorical(@object, subject, predicate, validFrom, validTo, transactionTime, graph);
        var d4 = _tgspIndex.DeleteHistorical(subject, predicate, @object, validFrom, validTo, transactionTime, graph);

        return d1 || d2 || d3 || d4;
    }

    /// <summary>
    /// Recover uncommitted transactions from WAL after crash.
    /// </summary>
    private void Recover()
    {
        _logger.Debug("Starting WAL recovery".AsSpan());
        var enumerator = _wal.GetUncommittedRecords();
        var recoveredCount = 0;

        try
        {
            while (enumerator.MoveNext())
            {
                var record = enumerator.Current;

                if (record.Operation == LogOperation.Add)
                {
                    // Get atom strings from IDs
                    var graph = record.GraphId == 0 ? string.Empty : _atoms.GetAtomString(record.GraphId);
                    var subject = _atoms.GetAtomString(record.SubjectId);
                    var predicate = _atoms.GetAtomString(record.PredicateId);
                    var @object = _atoms.GetAtomString(record.ObjectId);

                    var validFrom = new DateTimeOffset(record.ValidFromTicks, TimeSpan.Zero);
                    var validTo = new DateTimeOffset(record.ValidToTicks, TimeSpan.Zero);

                    // Apply to indexes with original transaction time from WAL record
                    ApplyToIndexes(subject, predicate, @object, validFrom, validTo, record.TransactionTimeTicks, graph);
                    recoveredCount++;
                }
                else if (record.Operation == LogOperation.Delete)
                {
                    // Get atom strings from IDs
                    var graph = record.GraphId == 0 ? string.Empty : _atoms.GetAtomString(record.GraphId);
                    var subject = _atoms.GetAtomString(record.SubjectId);
                    var predicate = _atoms.GetAtomString(record.PredicateId);
                    var @object = _atoms.GetAtomString(record.ObjectId);

                    var validFrom = new DateTimeOffset(record.ValidFromTicks, TimeSpan.Zero);
                    var validTo = new DateTimeOffset(record.ValidToTicks, TimeSpan.Zero);

                    // Apply delete to indexes with original transaction time from WAL record
                    ApplyDeleteToIndexes(subject, predicate, @object, validFrom, validTo, record.TransactionTimeTicks, graph);
                    recoveredCount++;
                }
            }
        }
        finally
        {
            enumerator.Dispose(); // Return pooled buffer
        }

        if (recoveredCount > 0)
        {
            _logger.Info("Recovered {0} records from WAL".AsSpan(), recoveredCount);
            // Checkpoint after recovery to avoid re-replaying
            // Note: No lock needed here - called from constructor before any concurrent access
            CheckpointInternal();
        }
        else
        {
            _logger.Debug("No records to recover".AsSpan());
        }
    }

    /// <summary>
    /// Force a checkpoint: flush indexes and truncate WAL.
    /// Thread-safe: acquires write lock.
    /// </summary>
    public void Checkpoint()
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try
        {
            CheckpointInternal();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Internal checkpoint without locking (called from within write lock).
    /// </summary>
    private void CheckpointInternal()
    {
        _logger.Debug("Starting checkpoint".AsSpan());

        // Flush all indexes (memory-mapped files auto-flush, but we can force it)
        // In a more complete implementation, we'd flush the mmap views here

        // Collect predicate statistics for query optimization
        CollectPredicateStatistics();

        // Flush trigram index if enabled
        _trigramIndex.Flush();

        // Write checkpoint marker to WAL
        _wal.Checkpoint();

        _logger.Debug("Checkpoint complete".AsSpan());
    }

    /// <summary>
    /// Check if checkpoint is needed and perform it.
    /// Must be called within write lock.
    /// </summary>
    private void CheckpointIfNeeded()
    {
        if (_wal.ShouldCheckpoint())
        {
            CheckpointInternal();
        }
    }

    /// <summary>
    /// Collect per-predicate statistics by scanning the GPOS index.
    /// Called during checkpoint when write lock is held.
    /// Uses GPOS index ordering (predicate-first) for efficient collection.
    /// </summary>
    private void CollectPredicateStatistics()
    {
        var stats = new System.Collections.Generic.Dictionary<long, (long count, System.Collections.Generic.HashSet<long> subjects, System.Collections.Generic.HashSet<long> objects)>();
        long totalTriples = 0;

        // Scan GPOS index using QueryAsOf with empty bounds
        // GPOS ordering: Predicate-Object-Subject, so grouped by predicate
        var enumerator = _gposIndex.QueryAsOf(
            ReadOnlySpan<char>.Empty,  // All predicates
            ReadOnlySpan<char>.Empty,  // All objects (Secondary in GPOS)
            ReadOnlySpan<char>.Empty,  // All subjects (Tertiary in GPOS)
            DateTimeOffset.UtcNow);

        while (enumerator.MoveNext())
        {
            var quad = enumerator.Current;

            // In GPOS index: Primary=predicate, Secondary=object, Tertiary=subject
            var predicateAtom = quad.Primary;
            var objectAtom = quad.Secondary;
            var subjectAtom = quad.Tertiary;

            if (!stats.TryGetValue(predicateAtom, out var entry))
            {
                entry = (0, new System.Collections.Generic.HashSet<long>(), new System.Collections.Generic.HashSet<long>());
            }

            entry.subjects.Add(subjectAtom);
            entry.objects.Add(objectAtom);
            stats[predicateAtom] = (entry.count + 1, entry.subjects, entry.objects);
            totalTriples++;
        }

        // Convert to immutable PredicateStats
        var txId = _wal.CurrentTxId;
        var predicateStats = new System.Collections.Generic.Dictionary<long, PredicateStats>();
        foreach (var (predicateId, (count, subjects, objects)) in stats)
        {
            predicateStats[predicateId] = new PredicateStats(
                predicateId, count, subjects.Count, objects.Count, txId);
        }

        _statistics.Update(predicateStats, totalTriples, txId);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.Debug($"Collected statistics for {predicateStats.Count} predicates, {totalTriples} triples".AsSpan());
        }
    }

    /// <summary>
    /// Query with optimal index selection.
    ///
    /// Note: For thread-safety with concurrent writes, either:
    /// 1. Use AcquireReadLock/ReleaseReadLock around the query and enumeration, or
    /// 2. Ensure no writes occur during enumeration
    /// </summary>
    public TemporalResultEnumerator Query(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        TemporalQueryType queryType,
        DateTimeOffset? asOfTime = null,
        DateTimeOffset? rangeStart = null,
        DateTimeOffset? rangeEnd = null,
        ReadOnlySpan<char> graph = default)
    {
        // Select optimal index
        var (selectedIndex, indexType) = SelectOptimalIndex(subject, predicate, @object, queryType);

        // Map RDF terms to index dimensions:
        // GSPO: S→Primary, P→Secondary, O→Tertiary
        // GPOS: P→Primary, O→Secondary, S→Tertiary
        // GOSP: O→Primary, S→Secondary, P→Tertiary
        // TGSP: S→Primary, P→Secondary, O→Tertiary
        ReadOnlySpan<char> arg1, arg2, arg3;
        switch (indexType)
        {
            case TemporalIndexType.GPOS:
                arg1 = predicate;
                arg2 = @object;
                arg3 = subject;
                break;
            case TemporalIndexType.GOSP:
                arg1 = @object;
                arg2 = subject;
                arg3 = predicate;
                break;
            default: // GSPO, TGSP
                arg1 = subject;
                arg2 = predicate;
                arg3 = @object;
                break;
        }

        var enumerator = queryType switch
        {
            TemporalQueryType.AsOf =>
                selectedIndex.QueryAsOf(arg1, arg2, arg3,
                    asOfTime ?? DateTimeOffset.UtcNow, graph),

            TemporalQueryType.Range =>
                selectedIndex.QueryRange(arg1, arg2, arg3,
                    rangeStart ?? DateTimeOffset.MinValue,
                    rangeEnd ?? DateTimeOffset.MaxValue, graph),

            _ => selectedIndex.QueryHistory(arg1, arg2, arg3, graph)
        };

        return new TemporalResultEnumerator(enumerator, indexType, _atoms);
    }

    /// <summary>
    /// Query current state (as of now).
    /// Empty graph means default graph.
    /// </summary>
    public TemporalResultEnumerator QueryCurrent(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        ReadOnlySpan<char> graph = default)
    {
        return Query(subject, predicate, @object, TemporalQueryType.AsOf, graph: graph);
    }

    /// <summary>
    /// Query as of a specific point in time (time-travel query).
    /// Empty graph means default graph.
    /// </summary>
    public TemporalResultEnumerator QueryAsOf(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        DateTimeOffset asOfTime,
        ReadOnlySpan<char> graph = default)
    {
        return Query(subject, predicate, @object, TemporalQueryType.AsOf, asOfTime: asOfTime, graph: graph);
    }

    /// <summary>
    /// Query all versions (evolution over time).
    /// Empty graph means default graph.
    /// </summary>
    public TemporalResultEnumerator QueryEvolution(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        ReadOnlySpan<char> graph = default)
    {
        return Query(subject, predicate, @object, TemporalQueryType.AllTime, graph: graph);
    }

    /// <summary>
    /// Time-travel query: What was true at specific time?
    /// Empty graph means default graph.
    /// </summary>
    public TemporalResultEnumerator TimeTravelTo(
        DateTimeOffset targetTime,
        ReadOnlySpan<char> subject = default,
        ReadOnlySpan<char> predicate = default,
        ReadOnlySpan<char> @object = default,
        ReadOnlySpan<char> graph = default)
    {
        return Query(subject, predicate, @object, TemporalQueryType.AsOf, asOfTime: targetTime, graph: graph);
    }

    /// <summary>
    /// Temporal range query: What changed during period?
    /// Empty graph means default graph.
    /// </summary>
    public TemporalResultEnumerator QueryChanges(
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        ReadOnlySpan<char> subject = default,
        ReadOnlySpan<char> predicate = default,
        ReadOnlySpan<char> @object = default,
        ReadOnlySpan<char> graph = default)
    {
        return Query(
            subject, predicate, @object,
            TemporalQueryType.Range,
            rangeStart: periodStart,
            rangeEnd: periodEnd,
            graph: graph);
    }

    /// <summary>
    /// Get all named graph IRIs in the store.
    /// Returns graph IRIs as strings. Default graph (empty) is not included.
    /// Caller must hold read lock.
    /// </summary>
    public NamedGraphEnumerator GetNamedGraphs()
    {
        return new NamedGraphEnumerator(_gspoIndex, _atoms);
    }

    private (QuadIndex Index, TemporalIndexType Type) SelectOptimalIndex(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        TemporalQueryType queryType)
    {
        var subjectBound = !subject.IsEmpty && subject[0] != '?';
        var predicateBound = !predicate.IsEmpty && predicate[0] != '?';
        var objectBound = !@object.IsEmpty && @object[0] != '?';

        // For time-range queries, prefer TGSP index
        if (queryType == TemporalQueryType.Range)
        {
            return (_tgspIndex, TemporalIndexType.TGSP);
        }

        // Otherwise select based on bound variables
        if (subjectBound)
        {
            return (_gspoIndex, TemporalIndexType.GSPO);
        }
        else if (predicateBound)
        {
            return (_gposIndex, TemporalIndexType.GPOS);
        }
        else if (objectBound)
        {
            return (_gospIndex, TemporalIndexType.GOSP);
        }
        else
        {
            return (_gspoIndex, TemporalIndexType.GSPO);
        }
    }

    /// <summary>
    /// Acquire read lock for thread-safe enumeration.
    /// Must be paired with ReleaseReadLock after enumeration completes.
    /// </summary>
    public void AcquireReadLock()
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
    }

    /// <summary>
    /// Release read lock after enumeration.
    /// </summary>
    public void ReleaseReadLock()
    {
        _lock.ExitReadLock();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Checkpoint before closing to minimize recovery on next open
        // Note: Skip locking here - we're disposing, no concurrent access expected
        CheckpointInternal();

        _wal?.Dispose();
        _gspoIndex?.Dispose();
        _gposIndex?.Dispose();
        _gospIndex?.Dispose();
        _tgspIndex?.Dispose();
        _trigramIndex.Dispose();
        _atoms?.Dispose();
        _lock?.Dispose();
    }

    /// <summary>
    /// Resets the store to empty state. All data is discarded.
    /// Files are preserved (memory mappings stay valid) - only logical content is reset.
    /// Thread-safe: acquires write lock.
    /// </summary>
    /// <remarks>
    /// This operation is designed for store pooling scenarios where recreating
    /// files is more expensive than clearing in-place. The store can be reused
    /// immediately after Clear() returns.
    /// </remarks>
    public void Clear()
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try
        {
            _logger.Info("Clearing store".AsSpan());

            // Clear WAL first (durability layer)
            _wal.Clear();

            // Clear all indexes
            _gspoIndex.Clear();
            _gposIndex.Clear();
            _gospIndex.Clear();
            _tgspIndex.Clear();

            // Clear atom store
            _atoms.Clear();

            // Clear trigram index
            _trigramIndex.Clear();

            // Clear cached statistics
            _statistics.Clear();

            // Reset disk space flag (fresh start)
            _diskSpaceLow = false;

            _logger.Info("Store cleared".AsSpan());
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Get storage statistics
    /// </summary>
    public (long QuadCount, long AtomCount, long TotalBytes) GetStatistics()
    {
        var quadCount = _gspoIndex.QuadCount;
        var (atomCount, atomBytes, _) = _atoms.GetStatistics();
        var walBytes = _wal.LogSize;
        return (quadCount, atomCount, atomBytes + walBytes);
    }

    /// <summary>
    /// Get WAL statistics for monitoring
    /// </summary>
    public (long CurrentTxId, long LastCheckpointTxId, long LogSize) GetWalStatistics()
    {
        return (_wal.CurrentTxId, _wal.LastCheckpointTxId, _wal.LogSize);
    }
}

/// <summary>
/// Enumerator that remaps results from different temporal indexes.
/// Uses pooled buffers to avoid allocations when decoding atom strings.
///
/// IMPORTANT: The spans returned by Current are only valid until the next
/// MoveNext() call. Do not store references to Subject/Predicate/Object
/// across iterations.
///
/// Call Dispose() when done to return the pooled buffer.
/// Changed from ref struct to struct to enable pooled array storage (ADR-011).
/// </summary>
public struct TemporalResultEnumerator
{
    private QuadIndex.TemporalQuadEnumerator _baseEnumerator;
    private readonly TemporalIndexType _indexType;
    private readonly AtomStore _atoms;

    // Pooled buffer for zero-allocation string decoding
    private char[]? _buffer;
    private int _bufferOffset;
    private const int InitialBufferSize = 4096; // 4KB - typical for 3 URIs

    internal TemporalResultEnumerator(
        QuadIndex.TemporalQuadEnumerator baseEnumerator,
        TemporalIndexType indexType,
        AtomStore atoms)
    {
        _baseEnumerator = baseEnumerator;
        _indexType = indexType;
        _atoms = atoms;
        _buffer = null;
        _bufferOffset = 0;
    }

    public bool MoveNext()
    {
        // Reset buffer offset for new result - reuse same buffer
        _bufferOffset = 0;
        return _baseEnumerator.MoveNext();
    }

    public ResolvedTemporalQuad Current
    {
        get
        {
            var quad = _baseEnumerator.Current;

            // Remap generic dimensions back to RDF terms based on index type
            long s, p, o;

            switch (_indexType)
            {
                case TemporalIndexType.GSPO:
                    s = quad.Primary;    // GSPO: Primary=subject
                    p = quad.Secondary;  // GSPO: Secondary=predicate
                    o = quad.Tertiary;   // GSPO: Tertiary=object
                    break;

                case TemporalIndexType.GPOS:
                    p = quad.Primary;    // GPOS: Primary=predicate
                    o = quad.Secondary;  // GPOS: Secondary=object
                    s = quad.Tertiary;   // GPOS: Tertiary=subject
                    break;

                case TemporalIndexType.GOSP:
                    o = quad.Primary;    // GOSP: Primary=object
                    s = quad.Secondary;  // GOSP: Secondary=subject
                    p = quad.Tertiary;   // GOSP: Tertiary=predicate
                    break;

                case TemporalIndexType.TGSP:
                    s = quad.Primary;    // TGSP: Primary=subject
                    p = quad.Secondary;  // TGSP: Secondary=predicate
                    o = quad.Tertiary;   // TGSP: Tertiary=object
                    break;

                default:
                    s = p = o = 0;
                    break;
            }

            // Clamp milliseconds to valid DateTimeOffset range
            const long MaxValidMs = 253402300799999L; // Dec 31, 9999

            return new ResolvedTemporalQuad(
                quad.Graph == 0 ? ReadOnlySpan<char>.Empty : DecodeAtomToBuffer(quad.Graph),
                DecodeAtomToBuffer(s),
                DecodeAtomToBuffer(p),
                DecodeAtomToBuffer(o),
                DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(quad.ValidFrom, MaxValidMs)),
                DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(quad.ValidTo, MaxValidMs)),
                DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(quad.TransactionTime, MaxValidMs)),
                quad.IsDeleted
            );
        }
    }

    /// <summary>
    /// Decode a UTF-8 atom into the pooled buffer and return a span to it.
    /// </summary>
    private ReadOnlySpan<char> DecodeAtomToBuffer(long atomId)
    {
        var utf8Span = _atoms.GetAtomSpan(atomId);
        if (utf8Span.IsEmpty)
            return ReadOnlySpan<char>.Empty;

        // Ensure buffer is allocated
        _buffer ??= PooledBufferManager.Shared.Rent<char>(InitialBufferSize).Array!;

        // Calculate char count needed
        int charCount = Encoding.UTF8.GetCharCount(utf8Span);

        // Grow buffer if needed
        if (_bufferOffset + charCount > _buffer.Length)
        {
            var newSize = Math.Max(_buffer.Length * 2, _bufferOffset + charCount + 256);
            var newBuffer = PooledBufferManager.Shared.Rent<char>(newSize).Array!;
            _buffer.AsSpan(0, _bufferOffset).CopyTo(newBuffer);
            PooledBufferManager.Shared.Return(_buffer);
            _buffer = newBuffer;
        }

        // Decode UTF-8 to UTF-16 in the buffer
        var destination = _buffer.AsSpan(_bufferOffset, charCount);
        Encoding.UTF8.GetChars(utf8Span, destination);

        _bufferOffset += charCount;
        return destination;
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

    public TemporalResultEnumerator GetEnumerator() => this;
}

internal enum TemporalIndexType
{
    GSPO, // Graph-Subject-Predicate-Object (Primary=S, Secondary=P, Tertiary=O)
    GPOS, // Graph-Predicate-Object-Subject (Primary=P, Secondary=O, Tertiary=S)
    GOSP, // Graph-Object-Subject-Predicate (Primary=O, Secondary=S, Tertiary=P)
    TGSP  // Time-Graph-Subject-Predicate-Object (Primary=S, Secondary=P, Tertiary=O)
}

/// <summary>
/// Resolved temporal quad with time dimensions
/// </summary>
public readonly ref struct ResolvedTemporalQuad
{
    public readonly ReadOnlySpan<char> Graph;
    public readonly ReadOnlySpan<char> Subject;
    public readonly ReadOnlySpan<char> Predicate;
    public readonly ReadOnlySpan<char> Object;
    public readonly DateTimeOffset ValidFrom;
    public readonly DateTimeOffset ValidTo;
    public readonly DateTimeOffset TransactionTime;
    public readonly bool IsDeleted;

    public ResolvedTemporalQuad(
        ReadOnlySpan<char> graph,
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        DateTimeOffset transactionTime,
        bool isDeleted = false)
    {
        Graph = graph;
        Subject = subject;
        Predicate = predicate;
        Object = @object;
        ValidFrom = validFrom;
        ValidTo = validTo;
        TransactionTime = transactionTime;
        IsDeleted = isDeleted;
    }
}

/// <summary>
/// Enumerator for distinct named graph IRIs.
/// Scans the index and returns each unique graph IRI once.
/// </summary>
public ref struct NamedGraphEnumerator
{
    private QuadIndex.TemporalQuadEnumerator _enumerator;
    private readonly AtomStore _atoms;
    private long _lastGraphAtom;
    private string? _current;

    internal NamedGraphEnumerator(QuadIndex index, AtomStore atoms)
    {
        // Query all current quads across ALL graphs (default + named)
        _enumerator = index.QueryCurrentAllGraphs();
        _atoms = atoms;
        _lastGraphAtom = -1; // -1 means "no graph seen yet"
        _current = null;
    }

    /// <summary>
    /// Current graph IRI.
    /// </summary>
    public readonly ReadOnlySpan<char> Current => _current.AsSpan();

    /// <summary>
    /// Move to the next distinct named graph.
    /// </summary>
    public bool MoveNext()
    {
        while (_enumerator.MoveNext())
        {
            var graphAtom = _enumerator.Current.Graph;

            // Skip default graph (atom 0)
            if (graphAtom == 0)
                continue;

            // Skip if same as last graph (takes advantage of GSPO ordering)
            if (graphAtom == _lastGraphAtom)
                continue;

            _lastGraphAtom = graphAtom;

            // Resolve atom to string
            _current = _atoms.GetAtomString(graphAtom);
            return true;
        }

        return false;
    }

    public NamedGraphEnumerator GetEnumerator() => this;
}
