using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using SkyOmega.Mercury.Diagnostics;

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
    private readonly QuadIndex _gspoIndex; // Primary index (was SPOT)
    private readonly QuadIndex _gposIndex; // Predicate-first (was POST)
    private readonly QuadIndex _gospIndex; // Object-first (was OSPT)
    private readonly QuadIndex _tgspIndex; // Time-first (was TSPO)

    private readonly AtomStore _atoms;
    private readonly WriteAheadLog _wal;
    private readonly string _baseDirectory;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly ILogger _logger;
    private readonly StatisticsStore _statistics = new();
    private long _activeBatchTxId = -1;
    private bool _disposed;

    /// <summary>
    /// Creates a new QuadStore at the specified directory.
    /// </summary>
    /// <param name="baseDirectory">Directory for store files.</param>
    public QuadStore(string baseDirectory) : this(baseDirectory, null) { }

    /// <summary>
    /// Creates a new QuadStore at the specified directory with logging.
    /// </summary>
    /// <param name="baseDirectory">Directory for store files.</param>
    /// <param name="logger">Logger for diagnostics (null for no logging).</param>
    public QuadStore(string baseDirectory, ILogger? logger)
    {
        _baseDirectory = baseDirectory;
        _logger = logger ?? NullLogger.Instance;

        if (!Directory.Exists(baseDirectory))
            Directory.CreateDirectory(baseDirectory);

        var gspoPath = Path.Combine(baseDirectory, "gspo.tdb");
        var gposPath = Path.Combine(baseDirectory, "gpos.tdb");
        var gospPath = Path.Combine(baseDirectory, "gosp.tdb");
        var tgspPath = Path.Combine(baseDirectory, "tgsp.tdb");
        var atomPath = Path.Combine(baseDirectory, "atoms");
        var walPath = Path.Combine(baseDirectory, "wal.log");

        // Create shared atom store for all indexes
        _atoms = new AtomStore(atomPath);

        // Create WAL for durability
        _wal = new WriteAheadLog(walPath);

        // Create indexes with shared atom store
        _gspoIndex = new QuadIndex(gspoPath, _atoms);
        _gposIndex = new QuadIndex(gposPath, _atoms);
        _gospIndex = new QuadIndex(gospPath, _atoms);
        _tgspIndex = new QuadIndex(tgspPath, _atoms);

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
    public AtomStore Atoms => _atoms;

    /// <summary>
    /// Add a temporal quad to all indexes with WAL durability.
    /// Thread-safe: acquires write lock.
    /// </summary>
    public void Add(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        ReadOnlySpan<char> graph = default)
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try
        {
            // 1. Intern atoms first (AtomStore is append-only, naturally durable)
            var graphId = graph.IsEmpty ? 0 : _atoms.Intern(graph);
            var subjectId = _atoms.Intern(subject);
            var predicateId = _atoms.Intern(predicate);
            var objectId = _atoms.Intern(obj);

            // 2. Write to WAL (fsync ensures durability)
            var record = LogRecord.CreateAdd(subjectId, predicateId, objectId, validFrom, validTo, graphId);
            _wal.Append(record);

            // 3. Apply to indexes
            ApplyToIndexes(subject, predicate, obj, validFrom, validTo, graph);

            // 4. Check if checkpoint needed
            CheckpointIfNeeded();
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
        ReadOnlySpan<char> obj,
        ReadOnlySpan<char> graph = default)
    {
        Add(subject, predicate, obj, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, graph);
    }

    /// <summary>
    /// Soft-delete a temporal triple from all indexes with WAL durability.
    /// Thread-safe: acquires write lock.
    /// Returns true if the quad was found and deleted, false otherwise.
    /// </summary>
    public bool Delete(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        ReadOnlySpan<char> graph = default)
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try
        {
            // 1. Look up atoms (don't intern - if they don't exist, quad doesn't exist)
            var graphId = graph.IsEmpty ? 0 : _atoms.GetAtomId(graph);
            var subjectId = _atoms.GetAtomId(subject);
            var predicateId = _atoms.GetAtomId(predicate);
            var objectId = _atoms.GetAtomId(obj);

            if (subjectId == 0 || predicateId == 0 || objectId == 0)
                return false;
            if (!graph.IsEmpty && graphId == 0)
                return false;

            // 2. Write to WAL (fsync ensures durability)
            var record = LogRecord.CreateDelete(subjectId, predicateId, objectId, validFrom, validTo, graphId);
            _wal.Append(record);

            // 3. Apply to indexes
            var deleted = ApplyDeleteToIndexes(subject, predicate, obj, validFrom, validTo, graph);

            // 4. Check if checkpoint needed
            CheckpointIfNeeded();

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
        ReadOnlySpan<char> obj,
        ReadOnlySpan<char> graph = default)
    {
        // Use a wide time range to match any current fact
        return Delete(subject, predicate, obj, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, graph);
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
        _lock.EnterWriteLock();
        try
        {
            if (_activeBatchTxId >= 0)
                throw new InvalidOperationException("A batch is already active.");

            _activeBatchTxId = _wal.BeginBatch();
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
        ReadOnlySpan<char> obj,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        ReadOnlySpan<char> graph = default)
    {
        if (_activeBatchTxId < 0)
            throw new InvalidOperationException("No active batch. Call BeginBatch() first.");

        // 1. Intern atoms (AtomStore is append-only)
        var graphId = graph.IsEmpty ? 0 : _atoms.Intern(graph);
        var subjectId = _atoms.Intern(subject);
        var predicateId = _atoms.Intern(predicate);
        var objectId = _atoms.Intern(obj);

        // 2. Write to WAL without fsync
        var record = LogRecord.CreateAdd(subjectId, predicateId, objectId, validFrom, validTo, graphId);
        _wal.AppendBatch(record, _activeBatchTxId);

        // 3. Apply to indexes immediately (in-memory, fast)
        ApplyToIndexes(subject, predicate, obj, validFrom, validTo, graph);
    }

    /// <summary>
    /// Add a current fact to the batch (valid from now onwards).
    /// </summary>
    public void AddCurrentBatched(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj,
        ReadOnlySpan<char> graph = default)
    {
        AddBatched(subject, predicate, obj, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, graph);
    }

    /// <summary>
    /// Soft-delete a temporal quad in the current batch (no fsync until CommitBatch).
    /// Must be called between BeginBatch() and CommitBatch()/RollbackBatch().
    /// Returns true if the quad was found and deleted, false otherwise.
    /// </summary>
    public bool DeleteBatched(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        ReadOnlySpan<char> graph = default)
    {
        if (_activeBatchTxId < 0)
            throw new InvalidOperationException("No active batch. Call BeginBatch() first.");

        // 1. Look up atoms (don't intern)
        var graphId = graph.IsEmpty ? 0 : _atoms.GetAtomId(graph);
        var subjectId = _atoms.GetAtomId(subject);
        var predicateId = _atoms.GetAtomId(predicate);
        var objectId = _atoms.GetAtomId(obj);

        if (subjectId == 0 || predicateId == 0 || objectId == 0)
            return false;
        if (!graph.IsEmpty && graphId == 0)
            return false;

        // 2. Write to WAL without fsync
        var record = LogRecord.CreateDelete(subjectId, predicateId, objectId, validFrom, validTo, graphId);
        _wal.AppendBatch(record, _activeBatchTxId);

        // 3. Apply to indexes immediately (in-memory, fast)
        return ApplyDeleteToIndexes(subject, predicate, obj, validFrom, validTo, graph);
    }

    /// <summary>
    /// Soft-delete a current fact in the batch.
    /// Returns true if the quad was found and deleted, false otherwise.
    /// </summary>
    public bool DeleteCurrentBatched(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj,
        ReadOnlySpan<char> graph = default)
    {
        return DeleteBatched(subject, predicate, obj, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, graph);
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

            _wal.CommitBatch(_activeBatchTxId);
            _activeBatchTxId = -1;

            CheckpointIfNeeded();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Rollback the current batch (releases lock without committing).
    /// In-memory index changes remain but WAL records are uncommitted.
    /// Recovery will not replay these records.
    /// </summary>
    public void RollbackBatch()
    {
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
    /// </summary>
    private void ApplyToIndexes(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        ReadOnlySpan<char> graph = default)
    {
        _gspoIndex.AddHistorical(subject, predicate, obj, validFrom, validTo, graph);
        _gposIndex.AddHistorical(predicate, obj, subject, validFrom, validTo, graph);
        _gospIndex.AddHistorical(obj, subject, predicate, validFrom, validTo, graph);
        _tgspIndex.AddHistorical(subject, predicate, obj, validFrom, validTo, graph);
    }

    /// <summary>
    /// Apply a delete to all indexes (internal, no WAL).
    /// Returns true if deleted from at least one index.
    /// </summary>
    private bool ApplyDeleteToIndexes(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        ReadOnlySpan<char> graph = default)
    {
        // Delete from all 4 indexes - use the appropriate argument order for each
        var d1 = _gspoIndex.DeleteHistorical(subject, predicate, obj, validFrom, validTo, graph);
        var d2 = _gposIndex.DeleteHistorical(predicate, obj, subject, validFrom, validTo, graph);
        var d3 = _gospIndex.DeleteHistorical(obj, subject, predicate, validFrom, validTo, graph);
        var d4 = _tgspIndex.DeleteHistorical(subject, predicate, obj, validFrom, validTo, graph);

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
                    var obj = _atoms.GetAtomString(record.ObjectId);

                    var validFrom = new DateTimeOffset(record.ValidFromTicks, TimeSpan.Zero);
                    var validTo = new DateTimeOffset(record.ValidToTicks, TimeSpan.Zero);

                    // Apply to indexes (no WAL write - already in log)
                    ApplyToIndexes(subject, predicate, obj, validFrom, validTo, graph);
                    recoveredCount++;
                }
                else if (record.Operation == LogOperation.Delete)
                {
                    // Get atom strings from IDs
                    var graph = record.GraphId == 0 ? string.Empty : _atoms.GetAtomString(record.GraphId);
                    var subject = _atoms.GetAtomString(record.SubjectId);
                    var predicate = _atoms.GetAtomString(record.PredicateId);
                    var obj = _atoms.GetAtomString(record.ObjectId);

                    var validFrom = new DateTimeOffset(record.ValidFromTicks, TimeSpan.Zero);
                    var validTo = new DateTimeOffset(record.ValidToTicks, TimeSpan.Zero);

                    // Apply delete to indexes (no WAL write - already in log)
                    ApplyDeleteToIndexes(subject, predicate, obj, validFrom, validTo, graph);
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
            ReadOnlySpan<char>.Empty,  // All objects (arg2 in POST ordering)
            ReadOnlySpan<char>.Empty,  // All subjects (arg3 in POST ordering)
            DateTimeOffset.UtcNow);

        while (enumerator.MoveNext())
        {
            var quad = enumerator.Current;

            // In GPOS index, Subject position is predicate, Predicate position is object, Object position is subject
            // (based on TemporalIndexType.POST remapping)
            var predicateAtom = quad.SubjectAtom;  // POST: predicate stored in Subject position
            var objectAtom = quad.PredicateAtom;    // POST: object stored in Predicate position
            var subjectAtom = quad.ObjectAtom;      // POST: subject stored in Object position

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
        ReadOnlySpan<char> obj,
        TemporalQueryType queryType,
        DateTimeOffset? asOfTime = null,
        DateTimeOffset? rangeStart = null,
        DateTimeOffset? rangeEnd = null,
        ReadOnlySpan<char> graph = default)
    {
        // Select optimal index
        var (selectedIndex, indexType) = SelectOptimalIndex(subject, predicate, obj, queryType);

        // Reorder parameters based on index type (indexes store data in different orders)
        // SPOT: (subject, predicate, object) - no change
        // POST: (predicate, object, subject)
        // OSPT: (object, subject, predicate)
        // TSPO: (subject, predicate, object) - no change
        ReadOnlySpan<char> arg1, arg2, arg3;
        switch (indexType)
        {
            case TemporalIndexType.POST:
                arg1 = predicate;
                arg2 = obj;
                arg3 = subject;
                break;
            case TemporalIndexType.OSPT:
                arg1 = obj;
                arg2 = subject;
                arg3 = predicate;
                break;
            default: // SPOT, TSPO
                arg1 = subject;
                arg2 = predicate;
                arg3 = obj;
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
        ReadOnlySpan<char> obj,
        ReadOnlySpan<char> graph = default)
    {
        return Query(subject, predicate, obj, TemporalQueryType.AsOf, graph: graph);
    }

    /// <summary>
    /// Query as of a specific point in time (time-travel query).
    /// Empty graph means default graph.
    /// </summary>
    public TemporalResultEnumerator QueryAsOf(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj,
        DateTimeOffset asOfTime,
        ReadOnlySpan<char> graph = default)
    {
        return Query(subject, predicate, obj, TemporalQueryType.AsOf, asOfTime: asOfTime, graph: graph);
    }

    /// <summary>
    /// Query all versions (evolution over time).
    /// Empty graph means default graph.
    /// </summary>
    public TemporalResultEnumerator QueryEvolution(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj,
        ReadOnlySpan<char> graph = default)
    {
        return Query(subject, predicate, obj, TemporalQueryType.AllTime, graph: graph);
    }

    /// <summary>
    /// Time-travel query: What was true at specific time?
    /// Empty graph means default graph.
    /// </summary>
    public TemporalResultEnumerator TimeTravelTo(
        DateTimeOffset targetTime,
        ReadOnlySpan<char> subject = default,
        ReadOnlySpan<char> predicate = default,
        ReadOnlySpan<char> obj = default,
        ReadOnlySpan<char> graph = default)
    {
        return Query(subject, predicate, obj, TemporalQueryType.AsOf, asOfTime: targetTime, graph: graph);
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
        ReadOnlySpan<char> obj = default,
        ReadOnlySpan<char> graph = default)
    {
        return Query(
            subject, predicate, obj,
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
        ReadOnlySpan<char> obj,
        TemporalQueryType queryType)
    {
        var subjectBound = !subject.IsEmpty && subject[0] != '?';
        var predicateBound = !predicate.IsEmpty && predicate[0] != '?';
        var objectBound = !obj.IsEmpty && obj[0] != '?';

        // For time-range queries, prefer TSPO index
        if (queryType == TemporalQueryType.Range)
        {
            return (_tgspIndex, TemporalIndexType.TSPO);
        }

        // Otherwise select based on bound variables
        if (subjectBound)
        {
            return (_gspoIndex, TemporalIndexType.SPOT);
        }
        else if (predicateBound)
        {
            return (_gposIndex, TemporalIndexType.POST);
        }
        else if (objectBound)
        {
            return (_gospIndex, TemporalIndexType.OSPT);
        }
        else
        {
            return (_gspoIndex, TemporalIndexType.SPOT);
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
        _atoms?.Dispose();
        _lock?.Dispose();
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
/// </summary>
public ref struct TemporalResultEnumerator
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
            var triple = _baseEnumerator.Current;

            // Remap based on index type
            long s, p, o;

            switch (_indexType)
            {
                case TemporalIndexType.SPOT:
                    s = triple.SubjectAtom;
                    p = triple.PredicateAtom;
                    o = triple.ObjectAtom;
                    break;

                case TemporalIndexType.POST:
                    p = triple.SubjectAtom;
                    o = triple.PredicateAtom;
                    s = triple.ObjectAtom;
                    break;

                case TemporalIndexType.OSPT:
                    o = triple.SubjectAtom;
                    s = triple.PredicateAtom;
                    p = triple.ObjectAtom;
                    break;

                case TemporalIndexType.TSPO:
                    s = triple.SubjectAtom;
                    p = triple.PredicateAtom;
                    o = triple.ObjectAtom;
                    break;

                default:
                    s = p = o = 0;
                    break;
            }

            // Clamp milliseconds to valid DateTimeOffset range
            const long MaxValidMs = 253402300799999L; // Dec 31, 9999

            return new ResolvedTemporalQuad(
                triple.GraphAtom == 0 ? ReadOnlySpan<char>.Empty : DecodeAtomToBuffer(triple.GraphAtom),
                DecodeAtomToBuffer(s),
                DecodeAtomToBuffer(p),
                DecodeAtomToBuffer(o),
                DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(triple.ValidFrom, MaxValidMs)),
                DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(triple.ValidTo, MaxValidMs)),
                DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(triple.TransactionTime, MaxValidMs)),
                triple.IsDeleted
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
        _buffer ??= ArrayPool<char>.Shared.Rent(InitialBufferSize);

        // Calculate char count needed
        int charCount = Encoding.UTF8.GetCharCount(utf8Span);

        // Grow buffer if needed
        if (_bufferOffset + charCount > _buffer.Length)
        {
            var newSize = Math.Max(_buffer.Length * 2, _bufferOffset + charCount + 256);
            var newBuffer = ArrayPool<char>.Shared.Rent(newSize);
            _buffer.AsSpan(0, _bufferOffset).CopyTo(newBuffer);
            ArrayPool<char>.Shared.Return(_buffer);
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
            ArrayPool<char>.Shared.Return(_buffer);
            _buffer = null;
        }
    }

    public TemporalResultEnumerator GetEnumerator() => this;
}

public enum TemporalIndexType
{
    SPOT, // Subject-Predicate-Object-Time
    POST, // Predicate-Object-Subject-Time
    OSPT, // Object-Subject-Predicate-Time
    TSPO  // Time-Subject-Predicate-Object
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
        ReadOnlySpan<char> obj,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        DateTimeOffset transactionTime,
        bool isDeleted = false)
    {
        Graph = graph;
        Subject = subject;
        Predicate = predicate;
        Object = obj;
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
            var graphAtom = _enumerator.Current.GraphAtom;

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
