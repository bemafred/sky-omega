using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
    // Temporal-profile indexes (Cognitive, Graph). Null for Reference/Minimal.
    private readonly TemporalQuadIndex? _gspoIndex; // Primary index: S→Primary, P→Secondary, O→Tertiary
    private readonly TemporalQuadIndex? _gposIndex; // Predicate-first: P→Primary, O→Secondary, S→Tertiary
    private readonly TemporalQuadIndex? _gospIndex; // Object-first: O→Primary, S→Secondary, P→Tertiary
    private readonly TemporalQuadIndex? _tgspIndex; // Time-first: S→Primary, P→Secondary, O→Tertiary
    // Reference-profile indexes. Null for Cognitive/Graph.
    private readonly ReferenceQuadIndex? _gspoReference;
    private readonly ReferenceQuadIndex? _gposReference;
    private readonly TrigramIndex _trigramIndex;

    private readonly AtomStore _atoms;
    // WAL is only constructed for profiles that provide versioning (Cognitive, Graph).
    // Reference has no WAL by design — bulk-load is the only write path, and its
    // durability is provided by FlushToDisk at load completion (ADR-029 / ADR-026).
    private readonly WriteAheadLog? _wal;
    private readonly string _baseDirectory;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly ILogger _logger;
    private readonly IBufferManager _bufferManager;
    private readonly long _minimumFreeDiskSpace;
    private readonly StatisticsStore _statistics = new();
    private long _activeBatchTxId = -1;
    // Reference profile has no WAL and therefore no batch transaction id. A simple
    // in-bulk flag lets AddCurrentBatched / CommitBatch enforce the "must be inside
    // BeginBatch" contract without fabricating a tx id that has no durable meaning.
    private bool _referenceBulkActive;
    // Batched records hold atom IDs only. Resolving IDs back to strings just so
    // ApplyToIndexes could re-intern them was a 1% hot path in bulk-load profiles,
    // plus one string allocation per atom per triple. The IDs are already in the
    // WAL record — we keep the record and let ApplyToIndexesById use it directly.
    private List<LogRecord>? _batchBuffer;
    // Transaction time captured at BeginBatch; shared by every record in the batch.
    // Bitemporally this is correct ("all triples in the batch were recorded at the
    // same moment"), and it removes one UtcNow call per triple from AddBatched.
    private long _batchTransactionTimeTicks;
    private DateTimeOffset _batchCurrentFrom;
    private bool _disposed;
    private bool _diskSpaceLow; // Set when disk space drops below threshold
    private bool _bulkLoadMode; // When true: skip fsync on CommitBatch, skip secondary indexes in ApplyToIndexes
    private StoreIndexState _indexState;
    private readonly StoreSchema _schema;

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

        // ADR-029: resolve store schema. Persisted schema wins over caller intent;
        // legacy stores without a schema file are assumed Cognitive and backfilled.
        var persistedSchema = StoreSchema.ReadFrom(baseDirectory);
        if (persistedSchema is not null)
        {
            _schema = persistedSchema;
        }
        else if (File.Exists(gspoPath))
        {
            _schema = StoreSchema.ForProfile(StoreProfile.Cognitive);
            _schema.WriteTo(baseDirectory);
        }
        else
        {
            _schema = StoreSchema.ForProfile(options.Profile);
            _schema.WriteTo(baseDirectory);
        }

        // Create shared atom store for all indexes.
        // ForceAtomHashCapacity suppresses the bulk-mode floor for ADR-028 validation.
        var effectiveBulkMode = options.BulkMode && !options.ForceAtomHashCapacity;
        _atoms = new AtomStore(atomPath, _bufferManager, options.MaxAtomSize,
            options.AtomDataInitialSizeBytes, options.AtomOffsetInitialCapacity,
            options.AtomHashTableInitialCapacity, effectiveBulkMode);

        _bulkLoadMode = options.BulkMode;

        // Create trigram index for full-text search (both profile families index it).
        var trigramPath = Path.Combine(baseDirectory, "trigram");
        _trigramIndex = new TrigramIndex(trigramPath, _bufferManager);

        // ADR-029 Phase 2d: dispatch index-family and WAL creation on the store's
        // schema profile. Cognitive/Graph produce four TemporalQuadIndex instances
        // and a WAL; Reference produces two ReferenceQuadIndex instances and no WAL
        // (bulk-load is the only write path per Decision 7, no per-session durability).
        switch (_schema.Profile)
        {
            case StoreProfile.Cognitive:
            case StoreProfile.Graph:
                _wal = new WriteAheadLog(walPath, WriteAheadLog.DefaultCheckpointSizeThreshold,
                    WriteAheadLog.DefaultCheckpointTimeSeconds, _bufferManager, options.BulkMode);
                _gspoIndex = new TemporalQuadIndex(gspoPath, _atoms, options.IndexInitialSizeBytes, TemporalQuadIndex.KeySortOrder.EntityFirst, options.BulkMode);
                _gposIndex = new TemporalQuadIndex(gposPath, _atoms, options.IndexInitialSizeBytes, TemporalQuadIndex.KeySortOrder.EntityFirst, options.BulkMode);
                _gospIndex = new TemporalQuadIndex(gospPath, _atoms, options.IndexInitialSizeBytes, TemporalQuadIndex.KeySortOrder.EntityFirst, options.BulkMode);
                _tgspIndex = new TemporalQuadIndex(tgspPath, _atoms, options.IndexInitialSizeBytes, TemporalQuadIndex.KeySortOrder.TimeFirst, options.BulkMode);
                break;

            case StoreProfile.Reference:
                // No WAL: Reference bulk-load durability = one FlushToDisk at completion.
                _gspoReference = new ReferenceQuadIndex(gspoPath, _atoms, options.IndexInitialSizeBytes, options.BulkMode);
                _gposReference = new ReferenceQuadIndex(gposPath, _atoms, options.IndexInitialSizeBytes, options.BulkMode);
                break;

            case StoreProfile.Minimal:
                throw new System.NotSupportedException(
                    "Minimal profile is defined in ADR-029 but not yet dispatched by QuadStore. " +
                    "Use Cognitive or Reference until Minimal-profile work begins.");

            default:
                throw new System.NotSupportedException(
                    $"Unknown StoreProfile {_schema.Profile} — this build does not know how to dispatch index construction for it.");
        }

        // Read index construction state.
        _indexState = StoreStateFile.Read(baseDirectory);
        // Cognitive/Graph bulk-load populates only GSPO; the rest is built by
        // RebuildSecondaryIndexes. Reference has no separate rebuild phase — both
        // of its indexes are written inline during bulk-load — so the store stays
        // in Ready state throughout.
        if (_bulkLoadMode && _indexState == StoreIndexState.Ready && _schema.HasTemporal)
        {
            _indexState = StoreIndexState.PrimaryOnly;
            StoreStateFile.Write(baseDirectory, _indexState);
        }

        _logger.Info("Opening store at {0}".AsSpan(), baseDirectory);

        // Recover any uncommitted transactions — WAL only exists for versioned profiles.
        if (_wal is not null)
            Recover();
    }

    /// <summary>
    /// Durable schema for this store. Resolved at open time from <c>store-schema.json</c>
    /// (or backfilled as Cognitive for legacy stores). Immutable for the life of the store —
    /// changing profiles requires a reload from source (ADR-029).
    /// </summary>
    public StoreSchema Schema => _schema;

    /// <summary>
    /// Throws <see cref="ProfileCapabilityException"/> when the store's profile does not
    /// permit session-API writes. Reference (and future Minimal) are read-only at the
    /// session API per ADR-029 Decision 7 — bulk-load is the only write path.
    /// After this call returns, <see cref="_gspoIndex"/>/<see cref="_gposIndex"/>/
    /// <see cref="_gospIndex"/>/<see cref="_tgspIndex"/>/<see cref="_wal"/> are non-null.
    /// </summary>
    [MemberNotNull(nameof(_gspoIndex), nameof(_gposIndex), nameof(_gospIndex), nameof(_tgspIndex), nameof(_wal))]
    private void RequireWriteCapableProfile(string operation)
    {
        if (_schema.HasVersioning && _gspoIndex is not null && _gposIndex is not null
            && _gospIndex is not null && _tgspIndex is not null && _wal is not null)
            return;
        throw new ProfileCapabilityException(
            $"Operation '{operation}' requires a store profile that supports session-API writes. " +
            $"This store's profile is {_schema.Profile}, which per ADR-029 Decision 7 is session-API immutable " +
            "(bulk-load is the only write path). Reload from source to change profiles.");
    }

    /// <summary>
    /// Throws <see cref="ProfileCapabilityException"/> when the store's profile has no
    /// temporal dimension. Per ADR-029 Decision 4, temporal queries against non-temporal
    /// profiles fail at the API boundary — never silently degraded. After this call
    /// returns, the four temporal-index fields are non-null.
    /// </summary>
    [MemberNotNull(nameof(_gspoIndex), nameof(_gposIndex), nameof(_gospIndex), nameof(_tgspIndex))]
    private void RequireTemporalProfile(string operation)
    {
        if (_schema.HasTemporal && _gspoIndex is not null && _gposIndex is not null
            && _gospIndex is not null && _tgspIndex is not null)
            return;
        throw new ProfileCapabilityException(
            $"Operation '{operation}' requires temporal semantics (profile must declare HasTemporal). " +
            $"This store's profile is {_schema.Profile}, which has no temporal dimension. " +
            "Use a Cognitive-profile store for temporal queries or reload from source.");
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
        RequireWriteCapableProfile(nameof(Add));
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

            // 3. Apply to indexes using pre-resolved IDs (no re-intern)
            ApplyToIndexesById(record);

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
        RequireWriteCapableProfile(nameof(Delete));
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

            // 3. Apply to indexes using pre-resolved IDs (no re-lookup)
            var deleted = ApplyDeleteToIndexesById(record);

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
            if (_activeBatchTxId >= 0 || _referenceBulkActive)
                throw new InvalidOperationException("A batch is already active.");

            // ADR-029 Decision 7: Reference allows bulk-load as a programmatic interface
            // but has no WAL. Hold the writer lock and set a flag; all writes go straight
            // into the Reference indexes and become durable on CommitBatch's Flush.
            if (_schema.Profile == StoreProfile.Reference)
            {
                _referenceBulkActive = true;
                return;
            }

            // Cognitive / Graph: WAL-backed batch with transactional semantics.
            if (_wal is null)
                throw new ProfileCapabilityException(
                    $"BeginBatch for profile {_schema.Profile} is not supported — no WAL available and no bulk dispatch defined.");

            _activeBatchTxId = _wal.BeginBatch();
            _batchBuffer ??= new();
            _batchBuffer.Clear();
            _batchCurrentFrom = DateTimeOffset.UtcNow;
            _batchTransactionTimeTicks = _batchCurrentFrom.ToUnixTimeMilliseconds();
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
        // Reference-profile bulk: write directly to the two indexes, no WAL buffering.
        // Valid-time bounds are discarded — Reference has no temporal dimension.
        if (_schema.Profile == StoreProfile.Reference)
        {
            AddReferenceBulkTriple(subject, predicate, @object, graph);
            return;
        }

        RequireWriteCapableProfile(nameof(AddBatched));
        if (_activeBatchTxId < 0)
            throw new InvalidOperationException("No active batch. Call BeginBatch() first.");

        // 1. Intern atoms (AtomStore is append-only — orphans from rollback are harmless)
        var graphId = graph.IsEmpty ? 0 : _atoms.Intern(graph);
        var subjectId = _atoms.Intern(subject);
        var predicateId = _atoms.Intern(predicate);
        var objectId = _atoms.Intern(@object);

        // 2. Write to WAL without fsync — transaction time captured at BeginBatch
        var record = LogRecord.CreateAdd(subjectId, predicateId, objectId, validFrom, validTo, graphId,
            transactionTimeTicks: _batchTransactionTimeTicks);
        _wal.AppendBatch(record, _activeBatchTxId);

        // 3. Buffer the record itself; ApplyToIndexesById uses IDs from it at commit.
        _batchBuffer!.Add(record);
    }

    /// <summary>
    /// Materialize a single Reference-profile triple inside an active bulk load. Shared
    /// by AddBatched and AddCurrentBatched because the temporal dimensions they differ on
    /// are meaningless for Reference — both reduce to "insert (g,s,p,o)."
    /// </summary>
    private void AddReferenceBulkTriple(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        ReadOnlySpan<char> graph)
    {
        if (!_referenceBulkActive)
            throw new InvalidOperationException("No active batch. Call BeginBatch() first.");

        var graphId = graph.IsEmpty ? 0 : _atoms.Intern(graph);
        var subjectId = _atoms.Intern(subject);
        var predicateId = _atoms.Intern(predicate);
        var objectId = _atoms.Intern(@object);

        // Both indexes are populated inline — Reference has no separate rebuild phase.
        _gspoReference!.AddRaw(graphId, subjectId, predicateId, objectId);
        _gposReference!.AddRaw(graphId, predicateId, objectId, subjectId);

        // Trigram the literal object so full-text search works over Reference stores too.
        var utf8Span = _atoms.GetAtomSpan(objectId);
        if (utf8Span.Length > 0 && utf8Span[0] == (byte)'"')
            _trigramIndex.IndexAtom(objectId, utf8Span);
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
        // Use the cached batch timestamp instead of calling UtcNow per triple — save
        // ~1.4% of bulk-load time. Bitemporally equivalent: all triples in one batch
        // share the same "valid from" moment.
        AddBatched(subject, predicate, @object, _batchCurrentFrom, DateTimeOffset.MaxValue, graph);
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
        RequireWriteCapableProfile(nameof(DeleteBatched));
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

        // 2. Write to WAL without fsync — transaction time captured at BeginBatch
        var record = LogRecord.CreateDelete(subjectId, predicateId, objectId, validFrom, validTo, graphId,
            transactionTimeTicks: _batchTransactionTimeTicks);
        _wal.AppendBatch(record, _activeBatchTxId);

        // 3. Buffer the record itself; ApplyDeleteToIndexesById uses IDs at commit.
        _batchBuffer!.Add(record);
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
        // validFrom=MinValue selects every historical version; cached batch stamp
        // only affects the transactionTime column recorded into the WAL record.
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
            // Reference-profile commit: flush the two indexes and the trigram index,
            // leave atom-store durability to its own Flush/mmap semantics, release lock.
            // No WAL → no tx-id machinery → no buffered replay.
            if (_referenceBulkActive)
            {
                _gspoReference!.Flush();
                _gposReference!.Flush();
                _trigramIndex.Flush();
                _referenceBulkActive = false;
                CheckDiskSpaceAfterWrite();
                return;
            }

            RequireWriteCapableProfile(nameof(CommitBatch));
            if (_activeBatchTxId < 0)
                throw new InvalidOperationException("No active batch.");

            // 1. Write CommitTx marker — fsync in cognitive mode, deferred in bulk mode
            if (_bulkLoadMode)
                _wal.CommitBatchNoSync(_activeBatchTxId);
            else
                _wal.CommitBatch(_activeBatchTxId);
            _activeBatchTxId = -1;

            // 2. Materialize buffered records into indexes. IDs are already in the
            // record — use the by-ID path so TemporalQuadIndex doesn't re-intern strings.
            if (_batchBuffer != null)
            {
                foreach (var record in _batchBuffer)
                {
                    if (record.Operation == LogOperation.Add)
                        ApplyToIndexesById(record);
                    else if (record.Operation == LogOperation.Delete)
                        ApplyDeleteToIndexesById(record);
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
        // Reference profile has no WAL — once AddCurrentBatched writes a triple, it is
        // already in the indexes. Per ADR-026, a mid-bulk failure means "delete the store
        // and retry." RollbackBatch here just releases the lock so the caller can do that.
        if (_referenceBulkActive)
        {
            _referenceBulkActive = false;
            _lock.ExitWriteLock();
            return;
        }

        _batchBuffer?.Clear();
        _activeBatchTxId = -1;
        _lock.ExitWriteLock();
    }

    /// <summary>
    /// Returns true if a batch is currently active.
    /// </summary>
    public bool IsBatchActive => _activeBatchTxId >= 0;

    /// <summary>
    /// Returns true if the store was opened in bulk load mode.
    /// </summary>
    public bool IsBulkLoadMode => _bulkLoadMode;

    /// <summary>
    /// Flush all WAL writes and B+Tree index pages to durable storage. Call once
    /// at bulk load completion to make the entire load durable. No-op in cognitive
    /// mode for the WAL (each CommitBatch fsyncs); the index Flush() calls are
    /// the only durability guarantee for B+Tree pages written in bulk mode, where
    /// per-page msync is deferred to avoid O(N×region) full-region flushes.
    /// </summary>
    public void FlushToDisk()
    {
        _wal?.FlushToDisk();
        _gspoIndex?.Flush();
        _gposIndex?.Flush();
        _gospIndex?.Flush();
        _tgspIndex?.Flush();
        _gspoReference?.Flush();
        _gposReference?.Flush();
    }

    /// <summary>
    /// Returns the current index construction state.
    /// </summary>
    public StoreIndexState IndexState => _indexState;

    /// <summary>
    /// Rebuild all secondary indexes (GPOS, GOSP, TGSP) and trigram index
    /// by scanning the primary GSPO index. Call after a bulk load to make
    /// all query patterns available.
    /// </summary>
    /// <param name="onProgress">Optional progress callback with (indexName, entriesProcessed).</param>
    public void RebuildSecondaryIndexes(Action<string, long>? onProgress = null)
    {
        ThrowIfDisposed();

        // Reference builds all of its indexes inline during bulk-load — there is nothing
        // separately to rebuild. Log and return so `mercury --bulk-load --rebuild-indexes`
        // pipelines work uniformly across profiles without the caller having to branch.
        if (_schema.Profile == StoreProfile.Reference)
        {
            _logger.Info("Reference profile: indexes are built inline during bulk-load — rebuild is a no-op.".AsSpan());
            return;
        }

        RequireTemporalProfile(nameof(RebuildSecondaryIndexes));
        _lock.EnterWriteLock();
        try
        {
            RebuildIndex(_gposIndex, "GPOS", StoreIndexState.BuildingGPOS,
                (q) => (q.Secondary, q.Tertiary, q.Primary), onProgress);

            RebuildIndex(_gospIndex, "GOSP", StoreIndexState.BuildingGOSP,
                (q) => (q.Tertiary, q.Primary, q.Secondary), onProgress);

            RebuildIndex(_tgspIndex, "TGSP", StoreIndexState.BuildingTGSP,
                (q) => (q.Primary, q.Secondary, q.Tertiary), onProgress);

            // Trigram rebuild
            _indexState = StoreIndexState.BuildingTrigram;
            StoreStateFile.Write(_baseDirectory, _indexState);
            long trigramCount = 0;
            var gspoEnum = _gspoIndex.QueryHistory(
                ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
            while (gspoEnum.MoveNext())
            {
                var quad = gspoEnum.Current;
                // In GSPO: Tertiary = object. Check if it's a literal (atom starts with ")
                var objectId = quad.Tertiary;
                if (objectId > 0)
                {
                    var utf8Span = _atoms.GetAtomSpan(objectId);
                    if (utf8Span.Length > 0 && utf8Span[0] == (byte)'"')
                    {
                        _trigramIndex.IndexAtom(objectId, utf8Span);
                        trigramCount++;
                    }
                }
            }
            onProgress?.Invoke("Trigram", trigramCount);

            // All done
            _indexState = StoreIndexState.Ready;
            _bulkLoadMode = false;
            StoreStateFile.Write(_baseDirectory, _indexState);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Rebuild a single secondary index from the primary GSPO index.
    /// </summary>
    private void RebuildIndex(TemporalQuadIndex target, string name, StoreIndexState duringState,
        Func<TemporalQuad, (long Primary, long Secondary, long Tertiary)> remapDimensions,
        Action<string, long>? onProgress)
    {
        _indexState = duringState;
        StoreStateFile.Write(_baseDirectory, _indexState);

        // Secondary-index construction is a bulk-shape operation: each AllocatePage
        // would otherwise trigger a full-region msync via SaveMetadata (the same
        // 1.7.15 bug, hidden behind a different code path). Borrow the bulk-mode
        // msync-deferral semantics for the duration of the rebuild, then Flush once.
        target.SetDeferMsync(true);
        long count = 0;
        try
        {
            var gspoEnum = _gspoIndex!.QueryHistory(
                ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);

            while (gspoEnum.MoveNext())
            {
                var quad = gspoEnum.Current;
                var (primary, secondary, tertiary) = remapDimensions(quad);

                target.AddRaw(quad.Graph, primary, secondary, tertiary,
                    quad.ValidFrom, quad.ValidTo, quad.TransactionTime);
                count++;
            }
        }
        finally
        {
            // One msync covers every deferred metadata write and page flush
            // performed during the rebuild. Must run before SetDeferMsync(false)
            // so subsequent cognitive-mode writes see a consistent durable state.
            target.Flush();
            target.SetDeferMsync(false);
        }

        onProgress?.Invoke(name, count);
    }

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
    // DateTime(1970,1,1).Ticks — Unix epoch expressed in .NET UtcTicks (100-ns since year 1).
    private const long UnixEpochUtcTicks = 621_355_968_000_000_000L;
    private const long TicksPerMillisecond = 10_000L;

    /// <summary>
    /// Convert DateTimeOffset.UtcTicks to Unix milliseconds. LogRecord stores valid-time
    /// as UtcTicks (via DateTimeOffset.UtcTicks in CreateAdd/CreateDelete); TemporalQuadIndex
    /// keys use Unix ms. Cheaper than round-tripping through DateTimeOffset.
    /// </summary>
    private static long UtcTicksToUnixMs(long utcTicks) =>
        (utcTicks - UnixEpochUtcTicks) / TicksPerMillisecond;

    /// <summary>
    /// Apply an add to all indexes using pre-resolved atom IDs (no string round-trip,
    /// no re-intern). Reads validFrom/validTo/transactionTime and all four atom IDs
    /// directly from the WAL record. Preconditions: caller has gone through
    /// <see cref="RequireWriteCapableProfile"/> (via Add/Delete/CommitBatch/Recover),
    /// so all four temporal indexes are non-null.
    /// </summary>
    private void ApplyToIndexesById(in LogRecord record)
    {
        // LogRecord stores valid-time as .UtcTicks (100-ns since year 1); the B+Tree
        // keys are Unix milliseconds. Convert once per record instead of per index.
        var validFromMs = UtcTicksToUnixMs(record.ValidFromTicks);
        var validToMs = UtcTicksToUnixMs(record.ValidToTicks);

        _gspoIndex!.AddRaw(record.GraphId, record.SubjectId, record.PredicateId, record.ObjectId,
            validFromMs, validToMs, record.TransactionTimeTicks);

        if (!_bulkLoadMode)
        {
            _gposIndex!.AddRaw(record.GraphId, record.PredicateId, record.ObjectId, record.SubjectId,
                validFromMs, validToMs, record.TransactionTimeTicks);
            _gospIndex!.AddRaw(record.GraphId, record.ObjectId, record.SubjectId, record.PredicateId,
                validFromMs, validToMs, record.TransactionTimeTicks);
            _tgspIndex!.AddRaw(record.GraphId, record.SubjectId, record.PredicateId, record.ObjectId,
                validFromMs, validToMs, record.TransactionTimeTicks);

            // Full-text trigram indexing for literal objects. Peek the first UTF-8
            // byte directly from the atom store; avoids the GetAtomSpan → re-check
            // pattern used when we only had the string.
            var utf8Span = _atoms.GetAtomSpan(record.ObjectId);
            if (utf8Span.Length > 0 && utf8Span[0] == (byte)'"')
            {
                _trigramIndex.IndexAtom(record.ObjectId, utf8Span);
            }
        }
    }

    /// <summary>
    /// Apply a delete to all indexes (internal, no WAL).
    /// Returns true if deleted from at least one index.
    /// </summary>
    /// <summary>
    /// Apply a delete to all indexes using pre-resolved atom IDs. Mirror of
    /// ApplyToIndexesById for the delete path; reads IDs directly from the record.
    /// </summary>
    private bool ApplyDeleteToIndexesById(in LogRecord record)
    {
        // Precondition mirrors ApplyToIndexesById: temporal indexes non-null on entry.
        var validFromMs = UtcTicksToUnixMs(record.ValidFromTicks);
        var validToMs = UtcTicksToUnixMs(record.ValidToTicks);

        var d1 = _gspoIndex!.DeleteRaw(record.GraphId, record.SubjectId, record.PredicateId, record.ObjectId,
            validFromMs, validToMs, record.TransactionTimeTicks);
        var d2 = _gposIndex!.DeleteRaw(record.GraphId, record.PredicateId, record.ObjectId, record.SubjectId,
            validFromMs, validToMs, record.TransactionTimeTicks);
        var d3 = _gospIndex!.DeleteRaw(record.GraphId, record.ObjectId, record.SubjectId, record.PredicateId,
            validFromMs, validToMs, record.TransactionTimeTicks);
        var d4 = _tgspIndex!.DeleteRaw(record.GraphId, record.SubjectId, record.PredicateId, record.ObjectId,
            validFromMs, validToMs, record.TransactionTimeTicks);

        return d1 || d2 || d3 || d4;
    }

    /// <summary>
    /// Recover uncommitted transactions from WAL after crash.
    /// Precondition: <see cref="_wal"/> is non-null (constructor only calls this for
    /// profiles that instantiate a WAL — Cognitive and Graph).
    /// </summary>
    private void Recover()
    {
        _logger.Debug("Starting WAL recovery".AsSpan());
        var enumerator = _wal!.GetUncommittedRecords();
        var recoveredCount = 0;

        try
        {
            while (enumerator.MoveNext())
            {
                var record = enumerator.Current;

                if (record.Operation == LogOperation.Add)
                {
                    ApplyToIndexesById(record);
                    recoveredCount++;
                }
                else if (record.Operation == LogOperation.Delete)
                {
                    ApplyDeleteToIndexesById(record);
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
        // Non-versioned profiles (Reference/Minimal) have no WAL and no statistics —
        // checkpoint is a no-op. Flush-to-disk durability is handled by FlushToDisk.
        if (_wal is null) return;

        _logger.Debug("Starting checkpoint".AsSpan());

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
    /// <remarks>
    /// In bulk-load mode, checkpoints are skipped entirely. The bulk-load
    /// contract defers all durability to a single FlushToDisk() at load
    /// completion, and the secondary indexes (which CollectPredicateStatistics
    /// scans during checkpoint) receive no writes during bulk load — scanning
    /// an uninitialized secondary index hits AccessViolationException.
    /// </remarks>
    private void CheckpointIfNeeded()
    {
        if (_bulkLoadMode) return;
        if (_wal is null) return;
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
        // GPOS ordering: Predicate-Object-Subject, so grouped by predicate.
        // CollectPredicateStatistics is only called from CheckpointInternal, which
        // short-circuits on non-versioned profiles — _gposIndex is non-null here.
        var enumerator = _gposIndex!.QueryAsOf(
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
        var txId = _wal!.CurrentTxId;
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
        RequireTemporalProfile(nameof(Query));
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
    /// Query the trigram index for candidate atom IDs matching a text search.
    /// Caller must hold read lock.
    /// </summary>
    internal List<long> QueryTrigramCandidates(ReadOnlySpan<char> searchQuery)
    {
        return _trigramIndex.QueryCandidates(searchQuery);
    }

    /// <summary>
    /// Query current state with candidate object atom ID filtering.
    /// The enumerator skips quads whose object atom ID is not in the candidate set,
    /// avoiding string decode for non-candidates.
    /// Caller must hold read lock.
    /// </summary>
    internal TemporalResultEnumerator QueryCurrentWithCandidates(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        HashSet<long> candidateObjectAtomIds,
        ReadOnlySpan<char> graph = default)
    {
        RequireTemporalProfile(nameof(QueryCurrentWithCandidates));
        var (selectedIndex, indexType) = SelectOptimalIndex(subject, predicate, @object, TemporalQueryType.AsOf);

        ReadOnlySpan<char> arg1, arg2, arg3;
        switch (indexType)
        {
            case TemporalIndexType.GPOS:
                arg1 = predicate; arg2 = @object; arg3 = subject;
                break;
            case TemporalIndexType.GOSP:
                arg1 = @object; arg2 = subject; arg3 = predicate;
                break;
            default:
                arg1 = subject; arg2 = predicate; arg3 = @object;
                break;
        }

        var enumerator = selectedIndex.QueryAsOf(arg1, arg2, arg3, DateTimeOffset.UtcNow, graph);
        return new TemporalResultEnumerator(enumerator, indexType, _atoms, candidateObjectAtomIds);
    }

    /// <summary>
    /// Get all named graph IRIs in the store.
    /// Returns graph IRIs as strings. Default graph (empty) is not included.
    /// Caller must hold read lock.
    /// </summary>
    public NamedGraphEnumerator GetNamedGraphs()
    {
        RequireTemporalProfile(nameof(GetNamedGraphs));
        return new NamedGraphEnumerator(_gspoIndex, _atoms);
    }

    private (TemporalQuadIndex Index, TemporalIndexType Type) SelectOptimalIndex(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        TemporalQueryType queryType)
    {
        var subjectBound = !subject.IsEmpty && subject[0] != '?';
        var predicateBound = !predicate.IsEmpty && predicate[0] != '?';
        var objectBound = !@object.IsEmpty && @object[0] != '?';

        // When secondary indexes are not available, fall back to GSPO for everything.
        // Queries still work — just slower (full GSPO scan instead of targeted index).
        if (_indexState != StoreIndexState.Ready)
        {
            // TGSP is only available after full rebuild
            // GPOS is available after BuildingGPOS completes
            // GOSP is available after BuildingGOSP completes
            var gposAvailable = _indexState != StoreIndexState.PrimaryOnly
                && _indexState != StoreIndexState.BuildingGPOS;
            var gospAvailable = gposAvailable
                && _indexState != StoreIndexState.BuildingGOSP;
            var tgspAvailable = gospAvailable
                && _indexState != StoreIndexState.BuildingTGSP
                && _indexState != StoreIndexState.BuildingTrigram;

            if (queryType == TemporalQueryType.Range && tgspAvailable)
                return (_tgspIndex!, TemporalIndexType.TGSP);
            if (subjectBound)
                return (_gspoIndex!, TemporalIndexType.GSPO);
            if (predicateBound && gposAvailable)
                return (_gposIndex!, TemporalIndexType.GPOS);
            if (objectBound && gospAvailable)
                return (_gospIndex!, TemporalIndexType.GOSP);

            return (_gspoIndex!, TemporalIndexType.GSPO);
        }

        // For time-range queries, prefer TGSP index
        if (queryType == TemporalQueryType.Range)
        {
            return (_tgspIndex!, TemporalIndexType.TGSP);
        }

        // Otherwise select based on bound variables
        if (subjectBound)
        {
            return (_gspoIndex!, TemporalIndexType.GSPO);
        }
        else if (predicateBound)
        {
            return (_gposIndex!, TemporalIndexType.GPOS);
        }
        else if (objectBound)
        {
            return (_gospIndex!, TemporalIndexType.GOSP);
        }
        else
        {
            return (_gspoIndex!, TemporalIndexType.GSPO);
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
        _gspoReference?.Dispose();
        _gposReference?.Dispose();
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

            // WAL and indexes: clear only what the profile actually instantiated.
            _wal?.Clear();
            _gspoIndex?.Clear();
            _gposIndex?.Clear();
            _gospIndex?.Clear();
            _tgspIndex?.Clear();
            _gspoReference?.Clear();
            _gposReference?.Clear();

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
        // Whichever index family this store has, GSPO is always the primary. Sum its
        // count; for WAL bytes, non-versioned profiles have no WAL so report 0.
        var quadCount = (_gspoIndex?.QuadCount) ?? (_gspoReference?.QuadCount) ?? 0;
        var (atomCount, atomBytes, _) = _atoms.GetStatistics();
        var walBytes = _wal?.LogSize ?? 0;
        return (quadCount, atomCount, atomBytes + walBytes);
    }

    /// <summary>
    /// Get WAL statistics for monitoring. Returns zeros for non-versioned profiles
    /// (Reference/Minimal) which have no WAL by design.
    /// </summary>
    public (long CurrentTxId, long LastCheckpointTxId, long LogSize) GetWalStatistics()
    {
        if (_wal is null) return (0, 0, 0);
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
    private TemporalQuadIndex.TemporalQuadEnumerator _baseEnumerator;
    private readonly TemporalIndexType _indexType;
    private readonly AtomStore _atoms;
    private readonly HashSet<long>? _candidateObjectAtomIds;

    // Pooled buffer for zero-allocation string decoding
    private char[]? _buffer;
    private int _bufferOffset;
    private const int InitialBufferSize = 4096; // 4KB - typical for 3 URIs

    internal TemporalResultEnumerator(
        TemporalQuadIndex.TemporalQuadEnumerator baseEnumerator,
        TemporalIndexType indexType,
        AtomStore atoms)
    {
        _baseEnumerator = baseEnumerator;
        _indexType = indexType;
        _atoms = atoms;
        _candidateObjectAtomIds = null;
        _buffer = null;
        _bufferOffset = 0;
    }

    internal TemporalResultEnumerator(
        TemporalQuadIndex.TemporalQuadEnumerator baseEnumerator,
        TemporalIndexType indexType,
        AtomStore atoms,
        HashSet<long>? candidateObjectAtomIds)
    {
        _baseEnumerator = baseEnumerator;
        _indexType = indexType;
        _atoms = atoms;
        _candidateObjectAtomIds = candidateObjectAtomIds;
        _buffer = null;
        _bufferOffset = 0;
    }

    public bool MoveNext()
    {
        // Reset buffer offset for new result - reuse same buffer
        _bufferOffset = 0;

        if (_candidateObjectAtomIds == null)
            return _baseEnumerator.MoveNext();

        // Candidate-filtered iteration: skip non-candidate objects at atom-ID level
        // (before expensive UTF-8 → UTF-16 string decode)
        while (_baseEnumerator.MoveNext())
        {
            var quad = _baseEnumerator.Current;
            long objectAtomId = _indexType switch
            {
                TemporalIndexType.GPOS => quad.Secondary,
                TemporalIndexType.GOSP => quad.Primary,
                _ => quad.Tertiary // GSPO, TGSP
            };

            if (_candidateObjectAtomIds.Contains(objectAtomId))
                return true;
        }
        return false;
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
    private TemporalQuadIndex.TemporalQuadEnumerator _enumerator;
    private readonly AtomStore _atoms;
    private long _lastGraphAtom;
    private string? _current;

    internal NamedGraphEnumerator(TemporalQuadIndex index, AtomStore atoms)
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
