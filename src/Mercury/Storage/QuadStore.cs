using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
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
    // Cognitive-profile indexes. Null for Graph/Reference/Minimal.
    private readonly TemporalQuadIndex? _gspoIndex; // Primary index: S→Primary, P→Secondary, O→Tertiary
    private readonly TemporalQuadIndex? _gposIndex; // Predicate-first: P→Primary, O→Secondary, S→Tertiary
    private readonly TemporalQuadIndex? _gospIndex; // Object-first: O→Primary, S→Secondary, P→Tertiary
    private readonly TemporalQuadIndex? _tgspIndex; // Time-first: S→Primary, P→Secondary, O→Tertiary
    // Graph-profile indexes (ADR-029 Graph: versioned, soft-delete, no temporal).
    // Null for Cognitive/Reference/Minimal.
    private readonly VersionedQuadIndex? _gspoGraph;
    private readonly VersionedQuadIndex? _gposGraph;
    private readonly VersionedQuadIndex? _gospGraph;
    private readonly VersionedQuadIndex? _tgspGraph;
    // Reference-profile indexes. Null for Cognitive/Graph/Minimal.
    private readonly ReferenceQuadIndex? _gspoReference;
    private readonly ReferenceQuadIndex? _gposReference;
    // Minimal-profile index — single GSPO with no graph dimension (ADR-029).
    // Null for Cognitive/Graph/Reference.
    private readonly MinimalQuadIndex? _gspoMinimal;
    private readonly TrigramIndex _trigramIndex;

    // Non-readonly: ADR-034 Phase 1B-5b allows replacement after a SortedAtomStore-backed
    // bulk-load completes (placeholder empty -> real vocab). All other writes are pinned
    // to construction-time state.
    private IAtomStore _atoms;
    private SortedAtomBulkBuilder? _sortedAtomBulkBuilder;
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
    // ADR-029 Minimal profile: same shape as Reference's bulk-active flag — no WAL,
    // writes go straight into the single GSPO index. CommitBatch is a no-op (no WAL
    // marker); durability via FlushToDisk at the end of the bulk-load session.
    private bool _minimalBulkActive;
    // ADR-031 Piece 2: tracks whether anything has actually been written since the
    // last checkpoint. Set by every mutation entry point; reset by CheckpointInternal.
    // Dispose gates the 14-min CollectPredicateStatistics + WAL-checkpoint-marker path
    // on this flag — read-only sessions see Dispose drop from minutes to milliseconds.
    // Volatile because Dispose reads it without re-acquiring the writer lock under
    // which mutations set it.
    private volatile bool _sessionMutated;
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
    // ADR-033: Reference bulk-load buffers (G,S,P,O) tuples through an external sorter
    // so the GSPO B+Tree append is sequential instead of random-write-amplified.
    //
    // Lifecycle: allocated lazily on the first AddReferenceBulkTriple call when
    // _bulkLoadMode is set; persists across many BeginBatch/CommitBatch cycles within
    // the same bulk session (RdfEngine.LoadStreamingAsync flushes every 100K triples,
    // so a 1B-triple load makes ~10K BeginBatch/CommitBatch round-trips). Drained on
    // FlushToDisk (the explicit "bulk-load complete" signal) via AppendSorted, then
    // disposed (which removes the temp dir).
    //
    // Per-batch alloc/drain would trigger a 1 GB LOH allocation per cycle plus break
    // AppendSorted's "non-decreasing keys" contract from batch 2 onward — both
    // observed as 25× slowdowns in the v1 implementation.
    private ExternalSorter<ReferenceQuadIndex.ReferenceKey, ReferenceKeyChunkSorter>? _bulkSorter;
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

        // Create shared atom store for all indexes. ADR-034: dispatch on schema.AtomStore.
        // ForceAtomHashCapacity suppresses the bulk-mode floor for ADR-028 validation.
        var effectiveBulkMode = options.BulkMode && !options.ForceAtomHashCapacity;
        _atoms = _schema.AtomStore switch
        {
            AtomStoreImplementation.Sorted => OpenSortedAtomStoreOrPlaceholder(atomPath),
            AtomStoreImplementation.Hash => new HashAtomStore(atomPath, _bufferManager, options.MaxAtomSize,
                options.AtomDataInitialSizeBytes, options.AtomOffsetInitialCapacity,
                options.AtomHashTableInitialCapacity, effectiveBulkMode),
            _ => throw new InvalidOperationException(
                $"Unknown AtomStoreImplementation {_schema.AtomStore} — this build cannot construct an atom store for it.")
        };

        _bulkLoadMode = options.BulkMode;

        // ADR-034 Decision 7: SortedAtomStore-backed stores accept exactly ONE bulk-load
        // (the build that populates the vocab files). A second bulk-load against an
        // already-populated Sorted store violates the single-bulk-load contract —
        // recreate the store to replace contents.
        if (_atoms is SortedAtomStore sas && _bulkLoadMode && sas.AtomCount > 0)
        {
            _atoms.Dispose();
            throw new ProfileCapabilityException(
                "ADR-034 Decision 7: SortedAtomStore-backed Reference stores are single-bulk-load. " +
                "This store already has " + sas.AtomCount + " atoms; recreate the store to replace contents, " +
                "or open without --bulk-load for query.");
        }

        // Create trigram index for full-text search (both profile families index it).
        var trigramPath = Path.Combine(baseDirectory, "trigram");
        _trigramIndex = new TrigramIndex(trigramPath, _bufferManager);

        // ADR-029 Phase 2d: dispatch index-family and WAL creation on the store's
        // schema profile. Cognitive produces four TemporalQuadIndex instances (88 B
        // entries, bitemporal); Graph produces four VersionedQuadIndex instances
        // (64 B entries, versioned + soft-delete, no temporal); both carry a WAL.
        // Reference produces two ReferenceQuadIndex instances (32 B entries) with
        // no WAL — bulk-load is the only write path per Decision 7. Each profile
        // gets its own concrete index implementation per the no-behavior-flags rule
        // (`feedback_no_behavior_flags.md`, 2026-05-16).
        switch (_schema.Profile)
        {
            case StoreProfile.Cognitive:
                _wal = new WriteAheadLog(walPath, WriteAheadLog.DefaultCheckpointSizeThreshold,
                    WriteAheadLog.DefaultCheckpointTimeSeconds, _bufferManager, options.BulkMode);
                _gspoIndex = new TemporalQuadIndex(gspoPath, _atoms, options.IndexInitialSizeBytes, TemporalQuadIndex.KeySortOrder.EntityFirst, options.BulkMode);
                _gposIndex = new TemporalQuadIndex(gposPath, _atoms, options.IndexInitialSizeBytes, TemporalQuadIndex.KeySortOrder.EntityFirst, options.BulkMode);
                _gospIndex = new TemporalQuadIndex(gospPath, _atoms, options.IndexInitialSizeBytes, TemporalQuadIndex.KeySortOrder.EntityFirst, options.BulkMode);
                _tgspIndex = new TemporalQuadIndex(tgspPath, _atoms, options.IndexInitialSizeBytes, TemporalQuadIndex.KeySortOrder.TimeFirst, options.BulkMode);
                break;

            case StoreProfile.Graph:
                // ADR-029 Graph profile: VersionedQuadIndex × 4. No temporal sort order
                // variants — single G→P→S→T comparison. WAL is identical to Cognitive's
                // (LogRecord shape is shared; temporal fields are zeroed/sentinel for
                // Graph records, costing 24 unused bytes per WAL record but avoiding
                // a parallel WAL serialization surface).
                _wal = new WriteAheadLog(walPath, WriteAheadLog.DefaultCheckpointSizeThreshold,
                    WriteAheadLog.DefaultCheckpointTimeSeconds, _bufferManager, options.BulkMode);
                _gspoGraph = new VersionedQuadIndex(gspoPath, _atoms, options.IndexInitialSizeBytes, options.BulkMode);
                _gposGraph = new VersionedQuadIndex(gposPath, _atoms, options.IndexInitialSizeBytes, options.BulkMode);
                _gospGraph = new VersionedQuadIndex(gospPath, _atoms, options.IndexInitialSizeBytes, options.BulkMode);
                _tgspGraph = new VersionedQuadIndex(tgspPath, _atoms, options.IndexInitialSizeBytes, options.BulkMode);
                break;

            case StoreProfile.Reference:
                // No WAL: Reference bulk-load durability = one FlushToDisk at completion.
                _gspoReference = new ReferenceQuadIndex(gspoPath, _atoms, options.IndexInitialSizeBytes, options.BulkMode);
                _gposReference = new ReferenceQuadIndex(gposPath, _atoms, options.IndexInitialSizeBytes, options.BulkMode);
                break;

            case StoreProfile.Minimal:
                // ADR-029 Minimal profile: single GSPO index, no graph dimension, no
                // versioning, no temporal, no WAL. Bulk-load is the session-API entry
                // (same shape as Reference) — Decision 7 stance applies. Re-add is a
                // no-op enforced at the B+Tree level. Distinct concrete class
                // (MinimalQuadIndex, 24 B leaf entries) per the no-behavior-flags rule.
                _gspoMinimal = new MinimalQuadIndex(gspoPath, _atoms, options.IndexInitialSizeBytes, options.BulkMode);
                break;

            default:
                throw new System.NotSupportedException(
                    $"Unknown StoreProfile {_schema.Profile} — this build does not know how to dispatch index construction for it.");
        }

        // Read index construction state.
        _indexState = StoreStateFile.Read(baseDirectory);
        // Bulk-load populates only the primary GSPO index per ADR-030 Decision 5;
        // secondary indexes (GPOS / GOSP / TGSP / Trigram depending on the profile)
        // are populated by RebuildSecondaryIndexes. Any profile with more than one
        // index enters PrimaryOnly during bulk load. Minimal (GSPO-only) stays Ready
        // because it has no secondaries to rebuild.
        if (_bulkLoadMode && _indexState == StoreIndexState.Ready && _schema.Indexes.Count > 1)
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
    /// Optional listener that receives <see cref="QueryMetrics"/> on every SPARQL query
    /// execution. Default null: zero overhead on the query hot path — no struct is
    /// allocated unless a listener is attached. ADR-030 Phase 1.
    /// </summary>
    public IQueryMetricsListener? QueryMetricsListener { get; set; }

    /// <summary>
    /// Optional listener that receives <see cref="RebuildPhaseMetrics"/> per secondary
    /// index and a <see cref="RebuildMetrics"/> summary per rebuild invocation. Default
    /// null: zero overhead. ADR-030 Phase 1.
    /// </summary>
    public IRebuildMetricsListener? RebuildMetricsListener { get; set; }

    /// <summary>
    /// Optional umbrella observer (ADR-035 Decision 1). Receives every event type Mercury
    /// emits — query, rebuild, load progress, GC/RSS state, atom-store events, scope
    /// correlation. Default null: zero overhead. Fan-out is reference-equality-checked
    /// against the legacy listeners to avoid double emission when the same instance is
    /// registered through multiple slots. The setter propagates to <see cref="IAtomStore"/>
    /// so atom-side discrete events (rehash, file growth) flow through the same observer
    /// and per-Intern probe-distance recording activates.
    /// </summary>
    public IObservabilityListener? ObservabilityListener
    {
        get => _observabilityListener;
        set
        {
            _observabilityListener = value;
            _atoms.ObservabilityListener = value;
        }
    }
    private IObservabilityListener? _observabilityListener;

    /// <summary>
    /// Register the three Category B atom-store state producers (intern rate, load factor,
    /// probe-distance percentiles) on the given listener's periodic timer. ADR-035 Phase 7a.3.
    /// Caller must also set <see cref="ObservabilityListener"/> for discrete events
    /// (rehash, file growth) to flow.
    /// </summary>
    public void RegisterAtomStateProducers(Diagnostics.JsonlMetricsListener listener)
    {
        if (listener is null) throw new ArgumentNullException(nameof(listener));
        // Phase 7a.3 producers are HashAtomStore-specific (probe distance, bucket count).
        // SortedAtomStore (ADR-034) will register its own producers via a different path.
        if (_atoms is HashAtomStore hashStore)
            AtomStoreProducers.RegisterAll(listener, hashStore);
    }

    /// <summary>True iff at least one rebuild observer is registered (legacy or umbrella).</summary>
    internal bool HasRebuildListener => RebuildMetricsListener is not null || ObservabilityListener is not null;

    /// <summary>Fan out a query metrics event; reference-equality avoids double emission.</summary>
    internal void EmitQueryMetrics(in QueryMetrics metrics)
    {
        QueryMetricsListener?.OnQueryMetrics(in metrics);
        var obs = ObservabilityListener;
        if (obs is not null && !ReferenceEquals(obs, QueryMetricsListener))
            obs.OnQueryMetrics(in metrics);
    }

    /// <summary>Fan out a rebuild-phase event.</summary>
    internal void EmitRebuildPhase(in RebuildPhaseMetrics phase)
    {
        RebuildMetricsListener?.OnRebuildPhase(in phase);
        var obs = ObservabilityListener;
        if (obs is not null && !ReferenceEquals(obs, RebuildMetricsListener))
            obs.OnRebuildPhase(in phase);
    }

    /// <summary>Fan out a rebuild-complete event.</summary>
    internal void EmitRebuildComplete(RebuildMetrics summary)
    {
        RebuildMetricsListener?.OnRebuildComplete(summary);
        var obs = ObservabilityListener;
        if (obs is not null && !ReferenceEquals(obs, RebuildMetricsListener))
            obs.OnRebuildComplete(summary);
    }

    /// <summary>
    /// Fan out an in-progress rebuild record. Only ObservabilityListener carries this event;
    /// the legacy IRebuildMetricsListener has no equivalent (it fires once per phase end).
    /// </summary>
    internal void EmitRebuildProgress(in RebuildProgressMetrics progress)
        => ObservabilityListener?.OnRebuildProgress(in progress);

    /// <summary>
    /// Fan out a drain-phase progress record. Closes the silent GSPO drain gap surfaced
    /// by cycle 9. Only ObservabilityListener carries this event.
    /// </summary>
    internal void EmitDrainProgress(in Abstractions.DrainProgressEvent progress)
        => ObservabilityListener?.OnDrainProgress(in progress);

    /// <summary>True iff the umbrella observer is registered (rebuild progress only fans there).</summary>
    internal bool HasRebuildProgressListener => ObservabilityListener is not null;

    /// <summary>
    /// Entries between rebuild-progress emission ticks. Default 1M matches the LoadProgress
    /// chunk-flush cadence; tests lower it to validate emission with smaller datasets.
    /// </summary>
    internal long RebuildProgressTickThreshold { get; set; } = 1_000_000;

    /// <summary>
    /// Minimum time gap between progress-event emissions (rebuild + drain). Caps the
    /// emission rate regardless of record-throughput — addresses the cycle 9
    /// backpressure-on-shared-disk pattern where per-records emission queued events
    /// faster than the JSONL writer could flush against a disk saturated by the
    /// workload's mmap I/O. Default 30 s matches <c>--metrics-state-interval</c>.
    /// <para>
    /// Composed with record-based threshold via AND: emission requires both N records
    /// since last emit AND T seconds since last emit. At low rates the record threshold
    /// dominates (cadence is records-based); at high rates the time threshold dominates
    /// (cadence is rate-capped). Tests use <c>TimeSpan.Zero</c> to bypass time gating
    /// and rely on records threshold alone.
    /// </para>
    /// </summary>
    // ADR-043 Part 2: lowered from 30s → 5s. The 30s default predated ADR-043 and
    // was set before cycle 9's 2-hour staleness symptom surfaced the live-observability
    // ceiling. 5s matches the merge-side throttle default and bounds live JSONL
    // tail-to-real-time staleness at the metric-emission layer.
    internal TimeSpan ProgressEmissionMinInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Drain-phase emission uses time-based cadence only (no record threshold).</summary>
    internal TimeSpan DrainProgressEmissionInterval => ProgressEmissionMinInterval;

    /// <summary>
    /// Internal accessor for the ADR-031 Piece 2 session-mutation flag. Tests assert
    /// that every public mutation flips it and every pure-query path leaves it false.
    /// </summary>
    internal bool SessionMutated => _sessionMutated;

    /// <summary>
    /// Throws <see cref="ProfileCapabilityException"/> when the store's profile does not
    /// permit session-API writes. Reference (and future Minimal) are read-only at the
    /// session API per ADR-029 Decision 7 — bulk-load is the only write path.
    /// After this call returns, <see cref="_wal"/> is non-null (both Cognitive and Graph
    /// profiles carry a WAL); the index fields are profile-discriminated and the
    /// downstream Apply* methods branch on <see cref="_schema"/>.Profile.
    /// </summary>
    [MemberNotNull(nameof(_wal))]
    private void RequireWriteCapableProfile(string operation)
    {
        if (_schema.HasVersioning && _wal is not null)
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
    /// Internal: IAtomStore requires external synchronization via this QuadStore's
    /// read/write locks. Direct access is only safe within Mercury internals.
    /// </remarks>
    internal IAtomStore Atoms => _atoms;

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
            // 1. Intern atoms first (IAtomStore is append-only, naturally durable)
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
            if (_activeBatchTxId >= 0 || _referenceBulkActive || _minimalBulkActive)
                throw new InvalidOperationException("A batch is already active.");

            // ADR-029 Minimal profile: same shape as Reference but with a single GSPO
            // index and no graph dimension. No WAL, no sorter (Minimal is mid-scale,
            // not Wikidata-scale), no SortedAtomBulkBuilder. Writes go straight into
            // _gspoMinimal via AddRaw on each AddCurrentBatched call; durability comes
            // from FlushToDisk at the session's end.
            if (_schema.Profile == StoreProfile.Minimal)
            {
                _minimalBulkActive = true;
                return;
            }

            // ADR-029 Decision 7: Reference allows bulk-load as a programmatic interface
            // but has no WAL. Hold the writer lock and set a flag; all writes go straight
            // into the Reference indexes and become durable on CommitBatch's Flush.
            if (_schema.Profile == StoreProfile.Reference)
            {
                _referenceBulkActive = true;
                // ADR-033: sorter is allocated lazily on first AddReferenceBulkTriple
                // and persists across many BeginBatch/CommitBatch cycles. See _bulkSorter
                // declaration for lifecycle.
                //
                // ADR-034 Phase 1B-5b: when the schema is Sorted-backed, allocate the
                // SortedAtomBulkBuilder here. AddReferenceBulkTriple buffers atom strings
                // into it; CommitBatch finalizes the vocabulary and re-opens the store's
                // _atoms over the freshly-written files.
                //
                // ADR-034 Phase 1B-5d: use disk-backed AssignedIds resolution. The
                // alternative (in-memory long[] of every input occurrence) costs 32 GB
                // at 1 B triples and 681 GB at 21.3 B Wikidata — incompatible with a
                // single-host bulk load. The disk-backed path streams resolution records
                // through ExternalSorter<ResolveRecord> with a 256 MB bounded chunk buffer
                // independent of input scale. At small scales (<100 M) the disk overhead
                // is ~10-50 ms — negligible against the 1 B+ requirement.
                if (_schema.AtomStore == AtomStoreImplementation.Sorted && _bulkLoadMode
                    && _sortedAtomBulkBuilder is null)
                {
                    // Session-scoped: allocated on the FIRST BeginBatch and persists across
                    // every subsequent BeginBatch/CommitBatch cycle within the bulk-load
                    // session. Finalized in FlushToDisk via FinalizeSortedAtomBulkIfPresent.
                    // Re-creating per-batch would overwrite atoms.atoms at every chunk-flush
                    // and lose every chunk's vocabulary except the last.
                    var sortedTempDir = Path.Combine(_baseDirectory, "bulk-tmp", "sorted-vocab");
                    Directory.CreateDirectory(sortedTempDir);
                    _sortedAtomBulkBuilder = new SortedAtomBulkBuilder(
                        Path.Combine(_baseDirectory, "atoms"),
                        sortedTempDir,
                        useDiskBackedAssigned: true,
                        listener: ObservabilityListener);

                    // Emit run-configuration event at the start of the bulk-load. The
                    // earliest deterministic point with full configuration visible — catches
                    // dispatch bugs (Hash-vs-Sorted, chunk-size in effect, pool cap) in the
                    // second the run begins, not at the end. ADR-035 / cognitive-orchestrator
                    // observability discipline.
                    ObservabilityListener?.OnRunConfiguration(new Abstractions.RunConfigurationEvent(
                        Timestamp: DateTimeOffset.UtcNow,
                        Profile: _schema.Profile.ToString(),
                        AtomStoreImplementation: _schema.AtomStore.ToString(),
                        ChunkBufferBytes: SortedAtomBulkBuilder.DefaultChunkBufferBytes,
                        ResolveSorterChunkSize: SortedAtomStoreExternalBuilder.DefaultResolveSorterChunkSize,
                        DiskBackedAssignedIds: true,
                        MergeFileStreamPoolHardCap: SortedAtomStoreExternalBuilder.MergeFileStreamPoolHardCap,
                        MergeFileStreamBufferSize: SortedAtomStoreExternalBuilder.MergeFileStreamBufferSize,
                        UserPoolSizeOverride: long.TryParse(
                            Environment.GetEnvironmentVariable("MERCURY_MERGE_POOL_SIZE"), out var ovr) ? ovr : null,
                        StorePath: _baseDirectory,
                        SourceFilePath: null,
                        Limit: null));
                }
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
        // Minimal-profile bulk: single GSPO index, no graph. Non-empty graph rejected
        // at the API boundary — Minimal's schema declares hasGraph=false (ADR-029).
        if (_schema.Profile == StoreProfile.Minimal)
        {
            AddMinimalBulkTriple(subject, predicate, @object, graph);
            return;
        }

        RequireWriteCapableProfile(nameof(AddBatched));
        if (_activeBatchTxId < 0)
            throw new InvalidOperationException("No active batch. Call BeginBatch() first.");

        // 1. Intern atoms (IAtomStore is append-only — orphans from rollback are harmless)
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
    /// <remarks>
    /// ADR-030 Decision 5: writes only to the primary GSPO index inline. GPOS and the
    /// trigram posting list are populated by <see cref="RebuildSecondaryIndexes"/> from
    /// a single sorted GSPO scan — mirrors Cognitive's bulk/rebuild split. Writing to
    /// multiple B+Trees in different sort orders from the same loop thrashes the page
    /// cache as soon as the combined working set exceeds RAM; 2026-04-20's gradient
    /// measured the cost (210K → 31K triples/sec at 100M). Keep inline writes to one
    /// index, defer the secondaries.
    /// </remarks>
    private void AddReferenceBulkTriple(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        ReadOnlySpan<char> graph)
    {
        if (!_referenceBulkActive)
            throw new InvalidOperationException("No active batch. Call BeginBatch() first.");

        // ADR-031 Piece 2: Reference bulk mutates the primary index directly, no WAL —
        // still a mutation for flag purposes. Dispose short-circuits anyway because
        // Reference has no WAL and CheckpointInternal returns early, but keeping the
        // flag consistent makes the invariant "pure-query leaves flag false" hold
        // for every profile.
        _sessionMutated = true;

        // ADR-034 Phase 1B-5b: SortedAtomStore-backed bulk path. SortedAtomStore can't
        // synthesize atom IDs synchronously (the dense-sorted assignment is unknowable
        // until the full vocabulary is sorted), so we buffer the four UTF-8 byte spans
        // into the SortedAtomBulkBuilder. CommitBatch drains the buffer through the
        // external merge sort, writes the vocab files, and replays the buffered triples
        // into the GSPO sorter with their newly-resolved atom IDs.
        if (_sortedAtomBulkBuilder is not null)
        {
            _sortedAtomBulkBuilder.AddTriple(graph, subject, predicate, @object);
            return;
        }

        var graphId = graph.IsEmpty ? 0 : _atoms.Intern(graph);
        var subjectId = _atoms.Intern(subject);
        var predicateId = _atoms.Intern(predicate);
        var objectId = _atoms.Intern(@object);

        if (_bulkLoadMode)
        {
            // ADR-033 fast path: buffer through the external sorter so the eventual
            // GSPO append is sequential. Sorter is allocated lazily here on first call
            // and persists across BeginBatch/CommitBatch cycles for the entire bulk
            // session — drained at FlushToDisk. GSPO key layout: (Graph, Subject,
            // Predicate, Object) maps to (Graph, Primary, Secondary, Tertiary).
            if (_bulkSorter is null)
            {
                var bulkTempDir = Path.Combine(_baseDirectory, "bulk-tmp");
                _bulkSorter = new ExternalSorter<ReferenceQuadIndex.ReferenceKey, ReferenceKeyChunkSorter>(
                    tempDir: bulkTempDir,
                    chunkSize: 16_000_000); // 512 MB scratch on the LOH for the bulk session
            }
            var key = new ReferenceQuadIndex.ReferenceKey
            {
                Graph = graphId,
                Primary = subjectId,
                Secondary = predicateId,
                Tertiary = objectId,
            };
            _bulkSorter.Add(in key);
        }
        else
        {
            _gspoReference!.AddRaw(graphId, subjectId, predicateId, objectId);
        }
    }

    /// <summary>
    /// Materialize a single Minimal-profile triple inside an active bulk-load batch.
    /// ADR-029 Minimal: no graph dimension, no WAL, single GSPO index. Non-empty
    /// graph spans are rejected at the API boundary — Minimal's schema declares
    /// hasGraph=false; queries against a Minimal store with a graph constraint
    /// also fail at plan time.
    /// </summary>
    private void AddMinimalBulkTriple(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        ReadOnlySpan<char> graph)
    {
        if (!_minimalBulkActive)
            throw new InvalidOperationException("No active batch. Call BeginBatch() first.");
        if (!graph.IsEmpty)
            throw new ProfileCapabilityException(
                "Minimal profile does not support named graphs (hasGraph=false per ADR-029). " +
                "Use Cognitive / Graph / Reference if your workload requires multi-graph semantics.");

        // Mutation tracking is irrelevant for Minimal (no WAL, no CheckpointInternal),
        // but flip the flag so the invariant "pure-query leaves flag false" holds uniformly.
        _sessionMutated = true;

        var subjectId = _atoms.Intern(subject);
        var predicateId = _atoms.Intern(predicate);
        var objectId = _atoms.Intern(@object);

        _gspoMinimal!.AddRaw(subjectId, predicateId, objectId);
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
                // ADR-034 Phase 1B-5b: SortedAtomStore-backed bulk-load is a SESSION-scoped
                // operation. The SortedAtomBulkBuilder accumulates atom occurrences across
                // every BeginBatch/CommitBatch cycle within a single bulk-load session and
                // is finalized exactly once at FlushToDisk. Per-batch finalize would
                // overwrite the atoms files at every chunk-flush — losing all but the last
                // chunk's vocabulary. The Hash-backed Reference path doesn't have this
                // problem because Intern() is incremental.
                //
                // Therefore: CommitBatch on a Sorted-backed Reference bulk leaves the
                // bulk-builder alone. The work that used to happen here — Finalize, atoms
                // re-open, EnumerateResolved replay into _bulkSorter — moves into
                // FlushToDisk's DrainBulkSorter path (FinalizeSortedAtomBulk).

                // ADR-033: when _bulkSorter is active the GSPO writes are deferred to
                // FlushToDisk's drain. Skipping the per-CommitBatch index Flush() avoids
                // ~10K msync-on-256GB-sparse-mmap calls over a 1B-triple bulk session
                // (the parser flushes every 100K triples). The session-end FlushToDisk
                // is the single durability boundary in that mode.
                if (_bulkSorter is null && _sortedAtomBulkBuilder is null)
                {
                    _gspoReference!.Flush();
                    _gposReference!.Flush();
                    _trigramIndex.Flush();
                }
                _referenceBulkActive = false;
                CheckDiskSpaceAfterWrite();
                return;
            }

            // Minimal-profile commit: flush the single GSPO index, release lock. No WAL,
            // no sorter, no trigram (Minimal doesn't index literals — the single-index
            // simplicity is the point).
            if (_minimalBulkActive)
            {
                _gspoMinimal!.Flush();
                _minimalBulkActive = false;
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
        // Minimal profile rollback: no WAL, no sorter — once AddCurrentBatched writes
        // a triple, it's already in _gspoMinimal. Per ADR-026's pattern, a mid-bulk
        // failure means "delete the store and retry"; RollbackBatch just releases the lock.
        if (_minimalBulkActive)
        {
            _minimalBulkActive = false;
            _lock.ExitWriteLock();
            return;
        }

        // Reference profile has no WAL — once AddCurrentBatched writes a triple, it is
        // already in the indexes. Per ADR-026, a mid-bulk failure means "delete the store
        // and retry." RollbackBatch here just releases the lock so the caller can do that.
        if (_referenceBulkActive)
        {
            // ADR-033: dispose the sorter (removes {storeRoot}/bulk-tmp). The contract
            // for Reference rollback per ADR-026 is "delete the store and retry"; the
            // sorter cleanup ensures the temp dir does not leak between retry attempts.
            if (_bulkSorter is not null)
            {
                _bulkSorter.Dispose();
                _bulkSorter = null;
            }
            // ADR-034 Phase 1B-5b: dispose any in-flight Sorted bulk builder. The
            // {storeRoot}/bulk-tmp/sorted-vocab/ chunk files are released via the
            // builder's Dispose; the placeholder atom files stay in place since they
            // were never overwritten (Finalize wasn't called).
            if (_sortedAtomBulkBuilder is not null)
            {
                _sortedAtomBulkBuilder.Dispose();
                _sortedAtomBulkBuilder = null;
            }
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
        // ADR-034 Phase 1B-5b session boundary: if a SortedAtomBulkBuilder accumulated
        // across the bulk-load session, finalize its vocabulary, replay buffered triples
        // into the GSPO sorter, and dispose. Per-CommitBatch finalize would have
        // overwritten the atoms files chunk-by-chunk — see CommitBatch comment.
        FinalizeSortedAtomBulkIfPresent();

        // ADR-033: drain the bulk-load external sorter (if any) before flushing GSPO.
        // The sorter has been accumulating across BeginBatch/CommitBatch cycles since
        // the first AddReferenceBulkTriple call; this is the explicit "bulk-load
        // complete" boundary where we stream the sorted (G,S,P,O) tuples into the
        // GSPO B+Tree as a single sequential AppendSorted run.
        DrainBulkSorter();
        _wal?.FlushToDisk();
        _gspoIndex?.Flush();
        _gposIndex?.Flush();
        _gospIndex?.Flush();
        _tgspIndex?.Flush();
        _gspoGraph?.Flush();
        _gposGraph?.Flush();
        _gospGraph?.Flush();
        _tgspGraph?.Flush();
        _gspoReference?.Flush();
        _gposReference?.Flush();
        _gspoMinimal?.Flush();
    }

    private void FinalizeSortedAtomBulkIfPresent()
    {
        if (_sortedAtomBulkBuilder is null) return;

        _lock.EnterWriteLock();
        try
        {
            if (_sortedAtomBulkBuilder is null) return; // re-check under lock

            var bulkBuilder = _sortedAtomBulkBuilder;
            _sortedAtomBulkBuilder = null;
            var atomPath = Path.Combine(_baseDirectory, "atoms");

            // ADR-041: bulkBuilder.Dispose() must run on every exit path so its tempDir
            // (bulk-tmp/sorted-vocab/* including the resolveSorter chunks at
            // bulk-tmp/sorted-vocab/assigned-ids-resolver/) is reclaimed even when
            // bulkBuilder.Finalize() throws — the cycle-10-r3 incident pattern
            // (BBHash OverflowException 2026-05-10, MPHF non-convergence 2026-05-11)
            // left ~1.2 TB orphaned because Dispose lived on the success path.
            try
            {
                // Release the placeholder mmap before the builder rewrites the files.
                _atoms.Dispose();

                bulkBuilder.Finalize();

                // Reopen over the fresh vocab files so GetAtomSpan works for the rest
                // of the session.
                _atoms = new SortedAtomStore(atomPath);

                // Replay the resolved triples into the GSPO sorter (same path the
                // Hash-backed Reference bulk uses; see _bulkSorter declaration).
                //
                // ADR-034 Phase 1B-5d: scope the GSPO sorter's tempDir to a dedicated
                // subdirectory ("bulk-tmp/gspo"). The previous shape ("bulk-tmp")
                // collided with the SortedAtomBulkBuilder's resolver chunks at
                // "bulk-tmp/sorted-vocab/assigned-ids-resolver/", because
                // ExternalSorter's constructor wipes its tempDir recursively — which
                // would delete the resolver's chunks before EnumerateResolved drains
                // them just below.
                if (_bulkSorter is null)
                {
                    var bulkTempDir = Path.Combine(_baseDirectory, "bulk-tmp", "gspo");
                    _bulkSorter = new ExternalSorter<ReferenceQuadIndex.ReferenceKey, ReferenceKeyChunkSorter>(
                        tempDir: bulkTempDir,
                        chunkSize: 16_000_000);
                }
                // ADR-A1 (cycle 10 Phase 1): emit drain progress on a time-based cadence.
                // Closes the silent-phase gap surfaced by cycle 9 (drain ran ~1 h 40 m
                // with no progress emission). Time-based interval (default 30 s) caps
                // emission rate regardless of throughput — addresses the
                // backpressure-on-shared-disk pattern (cycle 9 trigram drain showed
                // record-based emission queueing 2 h behind real time when the workload
                // saturates the disk).
                var replayStopwatch = System.Diagnostics.Stopwatch.StartNew();
                var lastReplayEmit = TimeSpan.Zero;
                long replayEntries = 0;
                long replayLastEmittedAt = 0;
                long? replayEstimatedTotal = bulkBuilder.TripleCount > 0 ? bulkBuilder.TripleCount : null;
                foreach (var (g, s, p, o) in bulkBuilder.EnumerateResolved())
                {
                    var key = new ReferenceQuadIndex.ReferenceKey
                    {
                        Graph = g,
                        Primary = s,
                        Secondary = p,
                        Tertiary = o,
                    };
                    _bulkSorter.Add(in key);
                    replayEntries++;

                    if (ObservabilityListener is not null &&
                        replayStopwatch.Elapsed - lastReplayEmit >= DrainProgressEmissionInterval)
                    {
                        var elapsed = replayStopwatch.Elapsed;
                        var dt = (elapsed - lastReplayEmit).TotalSeconds;
                        var rate = dt > 0 ? (replayEntries - replayLastEmittedAt) / dt : 0;
                        EmitDrainProgress(new Abstractions.DrainProgressEvent(
                            Timestamp: DateTimeOffset.UtcNow,
                            PhaseName: "GSPO",
                            SubPhase: "ReplayResolved",
                            EntriesProcessed: replayEntries,
                            EstimatedTotal: replayEstimatedTotal,
                            RatePerSecond: rate,
                            GcHeapBytes: GC.GetTotalMemory(forceFullCollection: false),
                            WorkingSetBytes: Environment.WorkingSet,
                            Elapsed: elapsed));
                        lastReplayEmit = elapsed;
                        replayLastEmittedAt = replayEntries;
                    }
                }
            }
            finally
            {
                bulkBuilder.Dispose();
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private void DrainBulkSorter()
    {
        if (_bulkSorter is null) return;

        _lock.EnterWriteLock();
        try
        {
            if (_bulkSorter is null) return; // re-check under lock

            _bulkSorter.Complete();
            _gspoReference!.SetDeferMsync(true);
            try
            {
                _gspoReference!.BeginAppendSorted();
                // ADR-A1: emit drain progress on time-based cadence (matches replay loop).
                var appendStopwatch = System.Diagnostics.Stopwatch.StartNew();
                var lastAppendEmit = TimeSpan.Zero;
                long appendEntries = 0;
                long appendLastEmittedAt = 0;
                while (_bulkSorter.TryDrainNext(out var key))
                {
                    _gspoReference.AppendSorted(key);
                    appendEntries++;

                    if (ObservabilityListener is not null &&
                        appendStopwatch.Elapsed - lastAppendEmit >= DrainProgressEmissionInterval)
                    {
                        var elapsed = appendStopwatch.Elapsed;
                        var dt = (elapsed - lastAppendEmit).TotalSeconds;
                        var rate = dt > 0 ? (appendEntries - appendLastEmittedAt) / dt : 0;
                        EmitDrainProgress(new Abstractions.DrainProgressEvent(
                            Timestamp: DateTimeOffset.UtcNow,
                            PhaseName: "GSPO",
                            SubPhase: "AppendSorted",
                            EntriesProcessed: appendEntries,
                            EstimatedTotal: null, // append doesn't know total in advance
                            RatePerSecond: rate,
                            GcHeapBytes: GC.GetTotalMemory(forceFullCollection: false),
                            WorkingSetBytes: Environment.WorkingSet,
                            Elapsed: elapsed));
                        lastAppendEmit = elapsed;
                        appendLastEmittedAt = appendEntries;
                    }
                }
                _gspoReference.EndAppendSorted();
            }
            finally
            {
                _gspoReference.SetDeferMsync(false);
                _bulkSorter.Dispose(); // removes {storeRoot}/bulk-tmp
                _bulkSorter = null;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Returns the current index construction state.
    /// </summary>
    public StoreIndexState IndexState => _indexState;

    /// <summary>
    /// Rebuild the MPHF + translation-table files (<c>atoms.mphf</c>, <c>atoms.idx</c>) against
    /// the already-sealed <see cref="SortedAtomStore"/>. Recovery path for Finalize-time
    /// MPHF construction failures — preserves the parser+merge work, only re-runs MPHF
    /// construction. Requires a SortedAtomStore-backed store (Reference profile); throws
    /// for Hash-backed stores (Cognitive profile) which don't use MPHF.
    /// </summary>
    public void RebuildMphf(Abstractions.IObservabilityListener? listener = null)
    {
        ThrowIfDisposed();
        if (_atoms is not SortedAtomStore)
        {
            throw new InvalidOperationException(
                "RebuildMphf requires a SortedAtomStore-backed store (Reference profile). " +
                $"This store uses {_atoms.GetType().Name}.");
        }
        var baseFilePath = Path.Combine(_baseDirectory, "atoms");
        // Fall back to the instance-level listener when no explicit one is supplied.
        // Matches the pattern at SortedAtomBulkBuilder construction — the CLI sets
        // ObservabilityListener once at startup; methods should honor it without
        // forcing every caller to pass the listener explicitly. The 2026-05-11 1.7.56
        // MPHF instrumentation crash + recovery was caused by --rebuild-mphf silently
        // passing null because Program.cs forgot the listener argument.
        SortedAtomStoreExternalBuilder.RebuildMphfFromSealedStore(
            baseFilePath, listener ?? _observabilityListener);
    }

    /// <summary>
    /// Rebuild the profile-appropriate secondary indexes by scanning the primary GSPO
    /// index. Call after a bulk load to make all query patterns available. The exact
    /// set rebuilt depends on profile (per ADR-030 Decision 5):
    /// <list type="bullet">
    ///   <item><b>Reference:</b> GPOS + trigram (two targets) — dispatched to
    ///   <see cref="RebuildReferenceSecondaryIndexes"/> via the radix external sort path
    ///   (ADR-032).</item>
    ///   <item><b>Cognitive / temporal:</b> GPOS + GOSP + TGSP + trigram (four targets) —
    ///   the in-line branch below.</item>
    /// </list>
    /// </summary>
    /// <param name="onProgress">Optional progress callback with (indexName, entriesProcessed).</param>
    public void RebuildSecondaryIndexes(Action<string, long>? onProgress = null)
    {
        ThrowIfDisposed();

        // ADR-030 Decision 5: Reference's bulk-load writes only _gspoReference. The
        // rebuild phase populates _gposReference and the trigram index from a single
        // GSPO scan — structurally identical to Cognitive's rebuild, just two targets
        // instead of four.
        if (_schema.Profile == StoreProfile.Reference)
        {
            RebuildReferenceSecondaryIndexes(onProgress);
            return;
        }

        // ADR-029 Graph profile (Commit 3): structurally parallel to Cognitive's
        // rebuild but uses VersionedQuadIndex.AddRaw (no temporal args). Scans
        // _gspoGraph as primary; populates _gposGraph / _gospGraph / _tgspGraph
        // and the trigram posting list. Uses the random-insert AddRaw path rather
        // than AppendSorted — Graph isn't aiming for Wikidata-scale; the
        // radix-external-sort optimization is a follow-up if a Graph workload
        // surfaces past-RAM-working-set behavior.
        if (_schema.Profile == StoreProfile.Graph)
        {
            RebuildGraphSecondaryIndexes(onProgress);
            return;
        }

        // ADR-029 Minimal profile: single GSPO index, no secondaries to rebuild.
        // Just transition the state to Ready so subsequent queries can route through
        // the optimal path. Stays under the writer lock for the brief state flip;
        // mutation tracking is a no-op since Minimal has no checkpoint discipline.
        if (_schema.Profile == StoreProfile.Minimal)
        {
            _lock.EnterWriteLock();
            try
            {
                _indexState = StoreIndexState.Ready;
                _bulkLoadMode = false;
                StoreStateFile.Write(_baseDirectory, _indexState);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
            return;
        }

        RequireTemporalProfile(nameof(RebuildSecondaryIndexes));
        _lock.EnterWriteLock();
        // ADR-031 Piece 2: rebuilding secondary indexes touches every B+Tree. Even
        // though the data came from the primary, the secondaries are now different.
        // Mark mutated so Dispose runs CheckpointInternal for the new statistics.
        _sessionMutated = true;
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var phases = HasRebuildListener ? new List<RebuildPhaseMetrics>(4) : null;
        try
        {
            RebuildIndex(_gposIndex, "GPOS", StoreIndexState.BuildingGPOS,
                (q) => (q.Secondary, q.Tertiary, q.Primary), onProgress, phases);

            RebuildIndex(_gospIndex, "GOSP", StoreIndexState.BuildingGOSP,
                (q) => (q.Tertiary, q.Primary, q.Secondary), onProgress, phases);

            RebuildIndex(_tgspIndex, "TGSP", StoreIndexState.BuildingTGSP,
                (q) => (q.Primary, q.Secondary, q.Tertiary), onProgress, phases);

            // Trigram rebuild
            _indexState = StoreIndexState.BuildingTrigram;
            StoreStateFile.Write(_baseDirectory, _indexState);
            var trigramStopwatch = System.Diagnostics.Stopwatch.StartNew();
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
            trigramStopwatch.Stop();
            onProgress?.Invoke("Trigram", trigramCount);

            if (HasRebuildListener)
            {
                var trigramPhase = new RebuildPhaseMetrics(
                    Timestamp: DateTimeOffset.UtcNow,
                    IndexName: "Trigram",
                    EntriesProcessed: trigramCount,
                    Elapsed: trigramStopwatch.Elapsed);
                phases!.Add(trigramPhase);
                EmitRebuildPhase(in trigramPhase);
            }

            // All done
            _indexState = StoreIndexState.Ready;
            _bulkLoadMode = false;
            StoreStateFile.Write(_baseDirectory, _indexState);
        }
        finally
        {
            totalStopwatch.Stop();
            _lock.ExitWriteLock();

            if (HasRebuildListener)
            {
                EmitRebuildComplete(new RebuildMetrics(
                    Timestamp: DateTimeOffset.UtcNow,
                    Profile: _schema.Profile,
                    TotalElapsed: totalStopwatch.Elapsed,
                    Phases: phases ?? (IReadOnlyList<RebuildPhaseMetrics>)System.Array.Empty<RebuildPhaseMetrics>(),
                    WasNoOp: false));
            }
        }
    }

    /// <summary>
    /// Rebuild a single secondary index from the primary GSPO index.
    /// </summary>
    private void RebuildIndex(TemporalQuadIndex target, string name, StoreIndexState duringState,
        Func<TemporalQuad, (long Primary, long Secondary, long Tertiary)> remapDimensions,
        Action<string, long>? onProgress,
        List<RebuildPhaseMetrics>? phases)
    {
        _indexState = duringState;
        StoreStateFile.Write(_baseDirectory, _indexState);

        // Secondary-index construction is a bulk-shape operation: each AllocatePage
        // would otherwise trigger a full-region msync via SaveMetadata (the same
        // 1.7.15 bug, hidden behind a different code path). Borrow the bulk-mode
        // msync-deferral semantics for the duration of the rebuild, then Flush once.
        target.SetDeferMsync(true);
        long count = 0;
        var phaseStopwatch = System.Diagnostics.Stopwatch.StartNew();
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
            phaseStopwatch.Stop();
        }

        onProgress?.Invoke(name, count);

        if (phases is not null)
        {
            var phase = new RebuildPhaseMetrics(
                Timestamp: DateTimeOffset.UtcNow,
                IndexName: name,
                EntriesProcessed: count,
                Elapsed: phaseStopwatch.Elapsed);
            phases.Add(phase);
            EmitRebuildPhase(in phase);
        }
    }

    /// <summary>
    /// Rebuild secondary indexes for a Graph-profile store — ADR-029 Commit 3.
    /// Scans the primary <see cref="_gspoGraph"/> once per target, remapping
    /// (Graph, Subject, Predicate, Object) → (Graph, Primary, Secondary, Tertiary)
    /// to populate GPOS / GOSP / TGSP; then scans a final time for the trigram
    /// posting list (literal objects only). State transitions PrimaryOnly →
    /// BuildingGPOS → BuildingGOSP → BuildingTGSP → BuildingTrigram → Ready.
    /// </summary>
    /// <remarks>
    /// Uses random-insert <see cref="VersionedQuadIndex.AddRaw"/> rather than
    /// AppendSorted. AppendSorted would require a sorted scan via an external sorter
    /// (the Reference profile's ADR-032 pattern) — VersionedQuadIndex doesn't expose
    /// AppendSorted today, and Graph workloads aren't aimed at Wikidata-scale where
    /// the radix-external-sort win becomes load-bearing. If a Graph workload surfaces
    /// scale past in-memory page cache, follow the ADR-032 pattern as a sibling round.
    /// </remarks>
    private void RebuildGraphSecondaryIndexes(Action<string, long>? onProgress)
    {
        _lock.EnterWriteLock();
        _sessionMutated = true;
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var phases = HasRebuildListener ? new List<RebuildPhaseMetrics>(4) : null;
        try
        {
            // Source index is GSPO: VersionedQuad.{Primary, Secondary, Tertiary} = (s, p, o).
            // The remap lambda projects logical RDF roles (g, s, p, o) onto each target's
            // (Graph, Primary, Secondary, Tertiary).
            RebuildGraphIndex(_gposGraph!, "GPOS", StoreIndexState.BuildingGPOS,
                (g, s, p, o) => (g, p, o, s), // GPOS: Primary=P, Secondary=O, Tertiary=S
                onProgress, phases);

            RebuildGraphIndex(_gospGraph!, "GOSP", StoreIndexState.BuildingGOSP,
                (g, s, p, o) => (g, o, s, p), // GOSP: Primary=O, Secondary=S, Tertiary=P
                onProgress, phases);

            RebuildGraphIndex(_tgspGraph!, "TGSP", StoreIndexState.BuildingTGSP,
                (g, s, p, o) => (g, s, p, o), // TGSP: Primary=S, Secondary=P, Tertiary=O (same as GSPO; no time-leading variant for Graph)
                onProgress, phases);

            // Trigram rebuild from the primary GSPO scan.
            _indexState = StoreIndexState.BuildingTrigram;
            StoreStateFile.Write(_baseDirectory, _indexState);
            _trigramIndex.Clear();
            var trigramStopwatch = System.Diagnostics.Stopwatch.StartNew();
            long trigramCount = 0;
            var gspoEnum = _gspoGraph!.Query(-1, -1, -1, -1);
            while (gspoEnum.MoveNext())
            {
                var quad = gspoEnum.Current;
                var objectId = quad.Tertiary; // GSPO: Tertiary = object
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
            trigramStopwatch.Stop();
            onProgress?.Invoke("Trigram", trigramCount);

            if (HasRebuildListener)
            {
                var trigramPhase = new RebuildPhaseMetrics(
                    Timestamp: DateTimeOffset.UtcNow,
                    IndexName: "Trigram",
                    EntriesProcessed: trigramCount,
                    Elapsed: trigramStopwatch.Elapsed);
                phases!.Add(trigramPhase);
                EmitRebuildPhase(in trigramPhase);
            }

            _indexState = StoreIndexState.Ready;
            _bulkLoadMode = false;
            StoreStateFile.Write(_baseDirectory, _indexState);
        }
        finally
        {
            totalStopwatch.Stop();
            _lock.ExitWriteLock();

            if (HasRebuildListener)
            {
                EmitRebuildComplete(new RebuildMetrics(
                    Timestamp: DateTimeOffset.UtcNow,
                    Profile: _schema.Profile,
                    TotalElapsed: totalStopwatch.Elapsed,
                    Phases: phases ?? (IReadOnlyList<RebuildPhaseMetrics>)System.Array.Empty<RebuildPhaseMetrics>(),
                    WasNoOp: false));
            }
        }
    }

    /// <summary>
    /// Per-target scan-and-fill for a Graph-profile secondary index. Mirrors
    /// <see cref="RebuildIndex"/> (the Cognitive path) but consumes <see cref="VersionedQuad"/>
    /// and dispatches to <see cref="VersionedQuadIndex.AddRaw"/>. Borrows the
    /// deferred-msync pattern: rebuild is a bulk-shape operation, per-page msync
    /// would dominate wall-clock for the same reason it does in Cognitive.
    /// </summary>
    private void RebuildGraphIndex(VersionedQuadIndex target, string name, StoreIndexState duringState,
        Func<long, long, long, long, (long Graph, long Primary, long Secondary, long Tertiary)> remap,
        Action<string, long>? onProgress,
        List<RebuildPhaseMetrics>? phases)
    {
        _indexState = duringState;
        StoreStateFile.Write(_baseDirectory, _indexState);
        target.Clear();
        target.SetDeferMsync(true);
        long count = 0;
        var phaseStopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var gspoEnum = _gspoGraph!.Query(-1, -1, -1, -1);
            while (gspoEnum.MoveNext())
            {
                var quad = gspoEnum.Current;
                // Source is GSPO: quad.Primary=Subject, quad.Secondary=Predicate, quad.Tertiary=Object.
                // The remap projects logical (g, s, p, o) onto the target's (Graph, Primary, Secondary, Tertiary).
                var (tg, tp, ts, tt) = remap(quad.Graph, quad.Primary, quad.Secondary, quad.Tertiary);
                target.AddRaw(tg, tp, ts, tt);
                count++;
            }
        }
        finally
        {
            target.Flush();
            target.SetDeferMsync(false);
            phaseStopwatch.Stop();
        }

        onProgress?.Invoke(name, count);

        if (phases is not null)
        {
            var phase = new RebuildPhaseMetrics(
                Timestamp: DateTimeOffset.UtcNow,
                IndexName: name,
                EntriesProcessed: count,
                Elapsed: phaseStopwatch.Elapsed);
            phases.Add(phase);
            EmitRebuildPhase(in phase);
        }
    }

    /// <summary>
    /// Rebuild secondary indexes for a Reference-profile store — ADR-030 Decision 5
    /// follow-through. Scans the primary GSPO index once to populate GPOS via key
    /// remap, then scans again to index literal objects in the trigram posting list.
    /// State transitions PrimaryOnly → BuildingGPOS → BuildingTrigram → Ready.
    /// </summary>
    private void RebuildReferenceSecondaryIndexes(Action<string, long>? onProgress)
    {
        _lock.EnterWriteLock();
        _sessionMutated = true;
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var phases = HasRebuildListener ? new List<RebuildPhaseMetrics>(2) : null;
        try
        {
            // GPOS rebuild via ADR-032 radix external sort: scan _gspoReference, remap
            // Graph/Subject/Predicate/Object → Graph/Predicate/Object/Subject, buffer
            // into ExternalSorter chunks (radix-sorted in memory, spilled to temp files
            // when the in-memory buffer fills), k-way-merge the chunks, AppendSorted
            // each entry to _gposReference. Sequential append touches each B+Tree leaf
            // once instead of N times, eliminating the random-insert write amplification
            // measured at ~3× useful I/O in Phase 5.2 (docs/validations/adr-030-phase52-
            // trace-2026-04-21.md).
            //
            // Idempotence requirement: AppendSorted demands an empty target (its
            // contract is "non-decreasing keys" — appending to a populated tree would
            // violate that on the first call against the new range). Clear() first.
            _indexState = StoreIndexState.BuildingGPOS;
            StoreStateFile.Write(_baseDirectory, _indexState);
            _gposReference!.Clear();

            _gposReference!.SetDeferMsync(true);
            long gposCount = 0;
            var gposStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var gposTempDir = Path.Combine(_baseDirectory, "rebuild-tmp", "gpos");
            // ADR-035 Phase 7a.1: per-1M progress emission within each sub-phase. Sub-phase
            // identification (emission vs drain) lets operators correlate JSONL records
            // with disk-activity patterns. Ticking-rate calculation mirrors LoadProgress's
            // sliding-window discipline: rate is over the most recent interval, not lifetime.
            long progressTickThreshold = RebuildProgressTickThreshold;
            TimeSpan progressMinInterval = ProgressEmissionMinInterval;
            bool emitProgress = HasRebuildProgressListener;
            long gposEstimatedTotal = _gspoReference!.QuadCount;
            try
            {
                using var sorter = new ExternalSorter<ReferenceQuadIndex.ReferenceKey, ReferenceKeyChunkSorter>(
                    tempDir: gposTempDir,
                    chunkSize: 16_000_000); // 512 MB scratch; 7 chunks at 100M scale

                long gposScanned = 0;
                long lastTickEntries = 0;
                TimeSpan lastTickElapsed = TimeSpan.Zero;
                var gspoEnum = _gspoReference!.Query(-1, -1, -1, -1);
                while (gspoEnum.MoveNext())
                {
                    var key = gspoEnum.Current;
                    // GSPO layout: Primary=Subject, Secondary=Predicate, Tertiary=Object.
                    // GPOS layout: Primary=Predicate, Secondary=Object, Tertiary=Subject.
                    var gposKey = new ReferenceQuadIndex.ReferenceKey
                    {
                        Graph = key.Graph,
                        Primary = key.Secondary,
                        Secondary = key.Tertiary,
                        Tertiary = key.Primary,
                    };
                    sorter.Add(in gposKey);
                    gposScanned++;

                    if (emitProgress &&
                        gposScanned - lastTickEntries >= progressTickThreshold &&
                        gposStopwatch.Elapsed - lastTickElapsed >= progressMinInterval)
                    {
                        var elapsed = gposStopwatch.Elapsed;
                        var dt = (elapsed - lastTickElapsed).TotalSeconds;
                        var rate = dt > 0 ? (gposScanned - lastTickEntries) / dt : 0;
                        EmitRebuildProgress(new RebuildProgressMetrics(
                            Timestamp: DateTimeOffset.UtcNow,
                            PhaseName: "GPOS",
                            SubPhase: "emission",
                            EntriesProcessed: gposScanned,
                            EstimatedTotal: gposEstimatedTotal,
                            RatePerSecond: rate,
                            GcHeapBytes: GC.GetTotalMemory(forceFullCollection: false),
                            WorkingSetBytes: Environment.WorkingSet,
                            Elapsed: elapsed));
                        lastTickEntries = gposScanned;
                        lastTickElapsed = elapsed;
                    }
                }
                sorter.Complete();

                // Reset tick state for the drain sub-phase. Drain total = entries that
                // entered the sorter during emission; the elapsed clock continues from
                // the current point so rate calculations stay continuous.
                lastTickEntries = 0;
                lastTickElapsed = gposStopwatch.Elapsed;
                long gposDrainTotal = gposScanned;

                _gposReference.BeginAppendSorted();
                while (sorter.TryDrainNext(out var sortedKey))
                {
                    _gposReference.AppendSorted(sortedKey);
                    gposCount++;

                    if (emitProgress &&
                        gposCount - lastTickEntries >= progressTickThreshold &&
                        gposStopwatch.Elapsed - lastTickElapsed >= progressMinInterval)
                    {
                        var elapsed = gposStopwatch.Elapsed;
                        var dt = (elapsed - lastTickElapsed).TotalSeconds;
                        var rate = dt > 0 ? (gposCount - lastTickEntries) / dt : 0;
                        EmitRebuildProgress(new RebuildProgressMetrics(
                            Timestamp: DateTimeOffset.UtcNow,
                            PhaseName: "GPOS",
                            SubPhase: "drain",
                            EntriesProcessed: gposCount,
                            EstimatedTotal: gposDrainTotal,
                            RatePerSecond: rate,
                            GcHeapBytes: GC.GetTotalMemory(forceFullCollection: false),
                            WorkingSetBytes: Environment.WorkingSet,
                            Elapsed: elapsed));
                        lastTickEntries = gposCount;
                        lastTickElapsed = elapsed;
                    }
                }
                _gposReference.EndAppendSorted();
            }
            finally
            {
                _gposReference.Flush();
                _gposReference.SetDeferMsync(false);
                gposStopwatch.Stop();
            }

            onProgress?.Invoke("GPOS", gposCount);
            if (phases is not null)
            {
                var gposPhase = new RebuildPhaseMetrics(
                    Timestamp: DateTimeOffset.UtcNow,
                    IndexName: "GPOS",
                    EntriesProcessed: gposCount,
                    Elapsed: gposStopwatch.Elapsed);
                phases.Add(gposPhase);
                EmitRebuildPhase(in gposPhase);
            }

            // Trigram rebuild: a second GSPO scan indexing literal objects only.
            // Clear first so a re-run of rebuild against a Ready store does not
            // double-add atoms to existing posting lists (the IndexAtom path is
            // incremental, not idempotent).
            _indexState = StoreIndexState.BuildingTrigram;
            StoreStateFile.Write(_baseDirectory, _indexState);
            _trigramIndex.Clear();

            // Trigram rebuild via ADR-032 Phase 4 radix external sort: extract
            // (trigram, atomId) pairs from each literal, sort by trigram so all atoms
            // for one trigram arrive contiguously, batch-append each trigram's atoms
            // in one allocation. Each posting list is touched once instead of N times,
            // eliminating the random-write amplification on hash-bucket pages.
            long trigramCount = 0;          // atoms with at least one trigram
            var trigramStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var trigramTempDir = Path.Combine(_baseDirectory, "rebuild-tmp", "trigram");
            long trigramScanTotal = _gspoReference.QuadCount;
            using (var sorter = new ExternalSorter<TrigramEntry, TrigramEntryChunkSorter>(
                tempDir: trigramTempDir,
                chunkSize: 16_000_000)) // 192 MB scratch per chunk for the 12-byte entries
            {
                long quadsScanned = 0;
                long lastTickEntries = 0;
                TimeSpan lastTickElapsed = TimeSpan.Zero;
                var trigramEnum = _gspoReference.Query(-1, -1, -1, -1);
                while (trigramEnum.MoveNext())
                {
                    var objectId = trigramEnum.Current.Tertiary;
                    if (objectId > 0)
                    {
                        var utf8Span = _atoms.GetAtomSpan(objectId);
                        if (utf8Span.Length > 0 && utf8Span[0] == (byte)'"')
                        {
                            _trigramIndex.EmitTrigramsToSorter(objectId, utf8Span, sorter);
                            trigramCount++;
                        }
                    }
                    quadsScanned++;

                    if (emitProgress &&
                        quadsScanned - lastTickEntries >= progressTickThreshold &&
                        trigramStopwatch.Elapsed - lastTickElapsed >= progressMinInterval)
                    {
                        var elapsed = trigramStopwatch.Elapsed;
                        var dt = (elapsed - lastTickElapsed).TotalSeconds;
                        var rate = dt > 0 ? (quadsScanned - lastTickEntries) / dt : 0;
                        EmitRebuildProgress(new RebuildProgressMetrics(
                            Timestamp: DateTimeOffset.UtcNow,
                            PhaseName: "Trigram",
                            SubPhase: "emission",
                            EntriesProcessed: quadsScanned,
                            EstimatedTotal: trigramScanTotal,
                            RatePerSecond: rate,
                            GcHeapBytes: GC.GetTotalMemory(forceFullCollection: false),
                            WorkingSetBytes: Environment.WorkingSet,
                            Elapsed: elapsed));
                        lastTickEntries = quadsScanned;
                        lastTickElapsed = elapsed;
                    }
                }
                sorter.Complete();

                // Drain sorted (trigram, atomId) pairs and batch-append per trigram group.
                // Adjacent dedup handles both within-atom duplicates (same trigram in one
                // literal) and cross-quad duplicates (same atom as object of multiple quads).
                lastTickEntries = 0;
                lastTickElapsed = trigramStopwatch.Elapsed;
                long drainEntries = 0;
                var atomBuffer = new List<long>(64);
                uint currentTrigram = 0;
                bool hasCurrent = false;
                while (sorter.TryDrainNext(out var entry))
                {
                    if (!hasCurrent || entry.Hash != currentTrigram)
                    {
                        if (hasCurrent)
                        {
                            _trigramIndex.AppendBatch(currentTrigram, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(atomBuffer));
                            atomBuffer.Clear();
                        }
                        currentTrigram = entry.Hash;
                        hasCurrent = true;
                        atomBuffer.Add(entry.AtomId);
                    }
                    else if (atomBuffer[^1] != entry.AtomId)
                    {
                        atomBuffer.Add(entry.AtomId);
                    }
                    drainEntries++;

                    if (emitProgress &&
                        drainEntries - lastTickEntries >= progressTickThreshold &&
                        trigramStopwatch.Elapsed - lastTickElapsed >= progressMinInterval)
                    {
                        var elapsed = trigramStopwatch.Elapsed;
                        var dt = (elapsed - lastTickElapsed).TotalSeconds;
                        var rate = dt > 0 ? (drainEntries - lastTickEntries) / dt : 0;
                        // Drain total is unknown precisely (depends on trigrams-per-literal);
                        // pass 0 as estimated total to signal "indeterminate" — the rate and
                        // entries_processed are still load-bearing for operator visibility.
                        EmitRebuildProgress(new RebuildProgressMetrics(
                            Timestamp: DateTimeOffset.UtcNow,
                            PhaseName: "Trigram",
                            SubPhase: "drain",
                            EntriesProcessed: drainEntries,
                            EstimatedTotal: 0,
                            RatePerSecond: rate,
                            GcHeapBytes: GC.GetTotalMemory(forceFullCollection: false),
                            WorkingSetBytes: Environment.WorkingSet,
                            Elapsed: elapsed));
                        lastTickEntries = drainEntries;
                        lastTickElapsed = elapsed;
                    }
                }
                if (hasCurrent)
                {
                    _trigramIndex.AppendBatch(currentTrigram, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(atomBuffer));
                }
            }
            _trigramIndex.SetIndexedAtomCount(trigramCount);
            _trigramIndex.Flush();
            trigramStopwatch.Stop();

            onProgress?.Invoke("Trigram", trigramCount);
            if (phases is not null)
            {
                var trigramPhase = new RebuildPhaseMetrics(
                    Timestamp: DateTimeOffset.UtcNow,
                    IndexName: "Trigram",
                    EntriesProcessed: trigramCount,
                    Elapsed: trigramStopwatch.Elapsed);
                phases.Add(trigramPhase);
                EmitRebuildPhase(in trigramPhase);
            }

            _indexState = StoreIndexState.Ready;
            _bulkLoadMode = false;
            StoreStateFile.Write(_baseDirectory, _indexState);
        }
        finally
        {
            totalStopwatch.Stop();
            _lock.ExitWriteLock();

            if (HasRebuildListener)
            {
                EmitRebuildComplete(new RebuildMetrics(
                    Timestamp: DateTimeOffset.UtcNow,
                    Profile: _schema.Profile,
                    TotalElapsed: totalStopwatch.Elapsed,
                    Phases: phases ?? (IReadOnlyList<RebuildPhaseMetrics>)System.Array.Empty<RebuildPhaseMetrics>(),
                    WasNoOp: false));
            }
        }
    }

    #endregion

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(QuadStore));
    }

    /// <summary>
    /// ADR-034 Phase 1B-5b: open <see cref="SortedAtomStore"/> for the given atom path,
    /// or create empty placeholder files if this is a fresh store. The placeholder
    /// (<c>{base}.atoms</c> at 0 bytes, <c>{base}.offsets</c> with a single int64 sentinel
    /// of 0) lets <see cref="SortedAtomStore"/> open with <c>AtomCount = 0</c> until the
    /// first bulk-load populates real vocab.
    /// </summary>
    private static SortedAtomStore OpenSortedAtomStoreOrPlaceholder(string atomPath)
    {
        var dataPath = atomPath + ".atoms";
        var offsetsPath = atomPath + ".offsets";
        if (!File.Exists(dataPath) || !File.Exists(offsetsPath))
        {
            File.WriteAllBytes(dataPath, Array.Empty<byte>());
            File.WriteAllBytes(offsetsPath, new byte[8]);  // single int64 sentinel = 0
        }
        return new SortedAtomStore(atomPath);
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
        // ADR-031 Piece 2: any mutation that reaches the indexes bumps the session-
        // mutated flag so Dispose knows to run CheckpointInternal.
        _sessionMutated = true;

        // ADR-029 Graph profile: dispatch by profile. VersionedQuadIndex has no
        // temporal fields, so the WAL's ValidFrom/ValidTo/TransactionTime are
        // ignored on the Graph path. CreatedAt/ModifiedAt/Version are recorded
        // inside VersionedQuadIndex from `DateTimeOffset.UtcNow` at insert time.
        if (_schema.Profile == StoreProfile.Graph)
        {
            _gspoGraph!.AddRaw(record.GraphId, record.SubjectId, record.PredicateId, record.ObjectId);

            if (!_bulkLoadMode)
            {
                _gposGraph!.AddRaw(record.GraphId, record.PredicateId, record.ObjectId, record.SubjectId);
                _gospGraph!.AddRaw(record.GraphId, record.ObjectId, record.SubjectId, record.PredicateId);
                _tgspGraph!.AddRaw(record.GraphId, record.SubjectId, record.PredicateId, record.ObjectId);

                var utf8Span = _atoms.GetAtomSpan(record.ObjectId);
                if (utf8Span.Length > 0 && utf8Span[0] == (byte)'"')
                {
                    _trigramIndex.IndexAtom(record.ObjectId, utf8Span);
                }
            }
            return;
        }

        // Cognitive profile path.
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
        // ADR-031 Piece 2: delete is a mutation — track it.
        _sessionMutated = true;

        // ADR-029 Graph profile: dispatch by profile. Graph delete is soft (IsDeleted
        // flag + version bump) per VersionedQuadIndex semantics. Returns true if at
        // least one index found the entry live.
        if (_schema.Profile == StoreProfile.Graph)
        {
            var g1 = _gspoGraph!.DeleteRaw(record.GraphId, record.SubjectId, record.PredicateId, record.ObjectId);
            var g2 = _gposGraph!.DeleteRaw(record.GraphId, record.PredicateId, record.ObjectId, record.SubjectId);
            var g3 = _gospGraph!.DeleteRaw(record.GraphId, record.ObjectId, record.SubjectId, record.PredicateId);
            var g4 = _tgspGraph!.DeleteRaw(record.GraphId, record.SubjectId, record.PredicateId, record.ObjectId);
            return g1 || g2 || g3 || g4;
        }

        // Cognitive profile path. Precondition: temporal indexes non-null on entry.
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

        // Collect predicate statistics for query optimization. This is the ~14-min
        // dominant cost at 1 B cognitive scale (per 2026-04-20 Dispose profile);
        // gated out of read-only Dispose via _sessionMutated (ADR-031 Piece 2).
        CollectPredicateStatistics();

        // Flush trigram index if enabled
        _trigramIndex.Flush();

        // Write checkpoint marker to WAL
        _wal.Checkpoint();

        // ADR-031 Piece 2: state is now durable on disk — future Dispose without
        // additional mutations can skip CheckpointInternal. Reset the flag last so
        // a crash mid-checkpoint leaves it true and the next Open's Recover +
        // auto-checkpoint cleans up.
        _sessionMutated = false;

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

        // ADR-029 Graph profile: scan via the VersionedQuadIndex GPOS instead of
        // TemporalQuadIndex. Same Predicate→Object→Subject mapping; no temporal
        // bounds because Graph has no time dimension.
        if (_schema.Profile == StoreProfile.Graph)
        {
            var gEnum = _gposGraph!.Query(-1, -1, -1, -1);
            while (gEnum.MoveNext())
            {
                var gquad = gEnum.Current;
                var predicateAtom = gquad.Primary;
                var objectAtom = gquad.Secondary;
                var subjectAtom = gquad.Tertiary;

                if (!stats.TryGetValue(predicateAtom, out var entry))
                {
                    entry = (0, new System.Collections.Generic.HashSet<long>(), new System.Collections.Generic.HashSet<long>());
                }
                entry.subjects.Add(subjectAtom);
                entry.objects.Add(objectAtom);
                stats[predicateAtom] = (entry.count + 1, entry.subjects, entry.objects);
                totalTriples++;
            }
        }
        else
        {
            // Cognitive profile: scan TemporalQuadIndex GPOS via AsOf-now.
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
        // ADR-029 Phase 2d query dispatch. A Reference or Graph store has no time
        // dimension, so only "current state" queries make sense. Range, AllTime, and
        // asOf-at-a-specific-past-time are all temporal queries and fail loudly at the
        // API boundary (Decision 4). QueryCurrent's pass-through lands here with
        // queryType=AsOf and asOfTime=null, which is exactly the supported shape.
        if (_schema.Profile == StoreProfile.Reference)
        {
            if (queryType != TemporalQueryType.AsOf)
                throw new ProfileCapabilityException(
                    $"Query type {queryType} requires temporal semantics. This store's profile is Reference, " +
                    "which has no temporal dimension — only current-state (AsOf, no explicit time) queries are supported.");
            return QueryReferenceCurrent(subject, predicate, @object, graph);
        }
        if (_schema.Profile == StoreProfile.Graph)
        {
            if (queryType != TemporalQueryType.AsOf)
                throw new ProfileCapabilityException(
                    $"Query type {queryType} requires temporal semantics. This store's profile is Graph, " +
                    "which has no temporal dimension (versioning is mutation-audit only, not bitemporal). " +
                    "Only current-state (AsOf, no explicit time) queries are supported.");
            if (asOfTime is not null)
                throw new ProfileCapabilityException(
                    "Explicit AsOf time-travel requires temporal semantics. This store's profile is Graph, " +
                    "which has no temporal dimension. Use QueryCurrent (no explicit time) instead.");
            return QueryGraphCurrent(subject, predicate, @object, graph);
        }
        if (_schema.Profile == StoreProfile.Minimal)
        {
            if (queryType != TemporalQueryType.AsOf)
                throw new ProfileCapabilityException(
                    $"Query type {queryType} requires temporal semantics. This store's profile is Minimal, " +
                    "which has no temporal dimension. Only current-state (AsOf, no explicit time) queries are supported.");
            if (asOfTime is not null)
                throw new ProfileCapabilityException(
                    "Explicit AsOf time-travel requires temporal semantics. This store's profile is Minimal, " +
                    "which has no temporal dimension. Use QueryCurrent (no explicit time) instead.");
            return QueryMinimalCurrent(subject, predicate, @object, graph);
        }

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
        // Explicit time-travel — Reference can't satisfy this even with synthesized fields,
        // so reject here rather than silently ignoring the time in Query's dispatch.
        RequireTemporalProfile(nameof(QueryAsOf));
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
        RequireTemporalProfile(nameof(QueryEvolution));
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
        RequireTemporalProfile(nameof(TimeTravelTo));
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
        RequireTemporalProfile(nameof(QueryChanges));
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
        // Reference profile also indexes literal objects in the trigram index during
        // bulk-load, so candidate-filtered iteration works there too.
        if (_schema.Profile == StoreProfile.Reference)
            return QueryReferenceCurrent(subject, predicate, @object, graph, candidateObjectAtomIds);
        if (_schema.Profile == StoreProfile.Graph)
            return QueryGraphCurrent(subject, predicate, @object, graph, candidateObjectAtomIds);
        if (_schema.Profile == StoreProfile.Minimal)
            return QueryMinimalCurrent(subject, predicate, @object, graph, candidateObjectAtomIds);

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

    /// <summary>
    /// Resolve a SPARQL term (IRI, literal, or variable) to an atom ID for Reference-profile
    /// queries. Empty spans and ?-prefixed variables become -1 (wildcard); IRIs/literals are
    /// looked up in the IAtomStore (returning 0 if not present, which matches nothing).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ResolveQueryTerm(ReadOnlySpan<char> term)
    {
        if (term.IsEmpty || term[0] == '?')
            return -1;
        return _atoms.GetAtomId(term);
    }

    /// <summary>
    /// Resolve a graph IRI for query. Empty = default graph (0); not-found = -2 sentinel so
    /// the scan matches nothing; otherwise the atom ID.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ResolveQueryGraph(ReadOnlySpan<char> graph)
    {
        if (graph.IsEmpty || graph[0] == '?')
            return graph.IsEmpty ? 0 : -1;
        var id = _atoms.GetAtomId(graph);
        return id == 0 ? -2 : id;
    }

    /// <summary>
    /// Pick GSPO or GPOS for a Reference-profile query. Predicate-bound-without-subject
    /// picks GPOS (predicate-first layout); everything else falls back to GSPO. Reference
    /// has no GOSP or TGSP, so object-only queries still go through GSPO — correct but a
    /// full scan bound only by graph. Cost-based selection is a later ADR.
    /// </summary>
    private (ReferenceQuadIndex Index, TemporalIndexType Type) SelectOptimalReferenceIndex(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object)
    {
        _ = @object; // reserved for future cost-based selection
        var subjectBound = !subject.IsEmpty && subject[0] != '?';
        var predicateBound = !predicate.IsEmpty && predicate[0] != '?';

        if (predicateBound && !subjectBound)
            return (_gposReference!, TemporalIndexType.GPOS);
        return (_gspoReference!, TemporalIndexType.GSPO);
    }

    /// <summary>
    /// Query entry for Minimal profile. Resolves SPARQL-shaped spans to atom IDs,
    /// rejects any non-empty graph constraint, queries the single GSPO index, and
    /// wraps the resulting <see cref="MinimalQuadIndex.MinimalQuadEnumerator"/> in
    /// a <see cref="TemporalResultEnumerator"/>. ADR-029 Minimal profile.
    /// </summary>
    private TemporalResultEnumerator QueryMinimalCurrent(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        ReadOnlySpan<char> graph,
        HashSet<long>? candidateObjectAtomIds = null)
    {
        if (!graph.IsEmpty && graph[0] != '?')
            throw new ProfileCapabilityException(
                "Minimal profile does not support named-graph queries (hasGraph=false per ADR-029). " +
                "Drop the graph parameter or use Cognitive / Graph / Reference for multi-graph workloads.");

        var primary = ResolveQueryTerm(subject);
        var secondary = ResolveQueryTerm(predicate);
        var tertiary = ResolveQueryTerm(@object);

        var enumerator = _gspoMinimal!.Query(primary, secondary, tertiary);
        return new TemporalResultEnumerator(enumerator, _atoms, candidateObjectAtomIds);
    }

    /// <summary>
    /// Pick GSPO / GPOS / GOSP / TGSP for a Graph-profile query. Graph has all four
    /// indexes available like Cognitive, so the selection logic is the same shape
    /// minus the temporal-range branch. ADR-029 Graph profile.
    /// </summary>
    private (VersionedQuadIndex Index, TemporalIndexType Type) SelectOptimalGraphIndex(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object)
    {
        var subjectBound = !subject.IsEmpty && subject[0] != '?';
        var predicateBound = !predicate.IsEmpty && predicate[0] != '?';
        var objectBound = !@object.IsEmpty && @object[0] != '?';

        // When secondary indexes aren't built yet, fall back to GSPO scan.
        if (_indexState != StoreIndexState.Ready)
        {
            var gposAvailable = _indexState != StoreIndexState.PrimaryOnly
                && _indexState != StoreIndexState.BuildingGPOS;
            var gospAvailable = gposAvailable
                && _indexState != StoreIndexState.BuildingGOSP;

            if (subjectBound)
                return (_gspoGraph!, TemporalIndexType.GSPO);
            if (predicateBound && gposAvailable)
                return (_gposGraph!, TemporalIndexType.GPOS);
            if (objectBound && gospAvailable)
                return (_gospGraph!, TemporalIndexType.GOSP);
            return (_gspoGraph!, TemporalIndexType.GSPO);
        }

        if (subjectBound)
            return (_gspoGraph!, TemporalIndexType.GSPO);
        if (predicateBound)
            return (_gposGraph!, TemporalIndexType.GPOS);
        if (objectBound)
            return (_gospGraph!, TemporalIndexType.GOSP);
        return (_gspoGraph!, TemporalIndexType.GSPO);
    }

    /// <summary>
    /// Query entry for Graph profile. Mirrors <see cref="QueryReferenceCurrent"/>'s
    /// shape but routes through <see cref="VersionedQuadIndex"/>. Live-only by default
    /// (soft-deleted entries are filtered out by VersionedQuadIndex.Query).
    /// </summary>
    private TemporalResultEnumerator QueryGraphCurrent(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        ReadOnlySpan<char> graph,
        HashSet<long>? candidateObjectAtomIds = null)
    {
        var (gIndex, indexType) = SelectOptimalGraphIndex(subject, predicate, @object);

        var g = ResolveQueryGraph(graph);
        long primary, secondary, tertiary;
        switch (indexType)
        {
            case TemporalIndexType.GPOS:
                primary = ResolveQueryTerm(predicate);
                secondary = ResolveQueryTerm(@object);
                tertiary = ResolveQueryTerm(subject);
                break;
            case TemporalIndexType.GOSP:
                primary = ResolveQueryTerm(@object);
                secondary = ResolveQueryTerm(subject);
                tertiary = ResolveQueryTerm(predicate);
                break;
            default: // GSPO (TGSP for Graph is also entity-first; query the same as GSPO)
                primary = ResolveQueryTerm(subject);
                secondary = ResolveQueryTerm(predicate);
                tertiary = ResolveQueryTerm(@object);
                break;
        }

        var enumerator = gIndex.Query(g, primary, secondary, tertiary);
        return new TemporalResultEnumerator(enumerator, indexType, _atoms, candidateObjectAtomIds);
    }

    /// <summary>
    /// Query entry for Reference profile. Resolves SPARQL-shaped spans to atom IDs,
    /// chooses the right index (GSPO or GPOS), remaps dimensions, and wraps the
    /// resulting <see cref="ReferenceQuadIndex.ReferenceQuadEnumerator"/> in a
    /// <see cref="TemporalResultEnumerator"/> for uniform consumption by the SPARQL
    /// executor.
    /// </summary>
    private TemporalResultEnumerator QueryReferenceCurrent(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        ReadOnlySpan<char> graph,
        HashSet<long>? candidateObjectAtomIds = null)
    {
        var (refIndex, indexType) = SelectOptimalReferenceIndex(subject, predicate, @object);

        var g = ResolveQueryGraph(graph);
        long primary, secondary, tertiary;
        switch (indexType)
        {
            case TemporalIndexType.GPOS:
                primary = ResolveQueryTerm(predicate);
                secondary = ResolveQueryTerm(@object);
                tertiary = ResolveQueryTerm(subject);
                break;
            default: // GSPO
                primary = ResolveQueryTerm(subject);
                secondary = ResolveQueryTerm(predicate);
                tertiary = ResolveQueryTerm(@object);
                break;
        }

        var refEnum = refIndex.Query(g, primary, secondary, tertiary);
        return new TemporalResultEnumerator(refEnum, indexType, _atoms, candidateObjectAtomIds);
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

        // ADR-033: defensive drain — if a bulk-load session is still holding the sorter
        // (caller forgot FlushToDisk) the buffered tuples must reach GSPO before the
        // index files are closed. The drain is idempotent if FlushToDisk was already
        // called (sorter is null).
        DrainBulkSorter();

        // ADR-031 Piece 2: Dispose's CheckpointInternal is the 14-minute-at-1B dominant
        // cost (CollectPredicateStatistics scans the entire GPOS index). A session that
        // never mutated anything has no new statistics to collect and no WAL checkpoint
        // marker to write — skip unconditionally. For Reference/Minimal profiles the
        // inner `_wal is null` short-circuit makes this redundant but cheap.
        // Locking note: skip — we're disposing, no concurrent access expected.
        if (_sessionMutated)
            CheckpointInternal();

        _wal?.Dispose();
        _gspoIndex?.Dispose();
        _gposIndex?.Dispose();
        _gospIndex?.Dispose();
        _tgspIndex?.Dispose();
        _gspoGraph?.Dispose();
        _gposGraph?.Dispose();
        _gospGraph?.Dispose();
        _tgspGraph?.Dispose();
        _gspoReference?.Dispose();
        _gposReference?.Dispose();
        _gspoMinimal?.Dispose();
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
            // ADR-031 Piece 2: Clear is an all-encompassing mutation.
            _sessionMutated = true;

            _logger.Info("Clearing store".AsSpan());

            // WAL and indexes: clear only what the profile actually instantiated.
            _wal?.Clear();
            _gspoIndex?.Clear();
            _gposIndex?.Clear();
            _gospIndex?.Clear();
            _tgspIndex?.Clear();
            _gspoGraph?.Clear();
            _gposGraph?.Clear();
            _gospGraph?.Clear();
            _tgspGraph?.Clear();
            _gspoReference?.Clear();
            _gposReference?.Clear();
            _gspoMinimal?.Clear();

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
        var quadCount = (_gspoIndex?.QuadCount) ?? (_gspoGraph?.QuadCount) ?? (_gspoReference?.QuadCount) ?? (_gspoMinimal?.QuadCount) ?? 0;
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
    // Exactly one of these backing enumerators is active, chosen at construction time
    // based on the profile of the store that produced the result. ADR-029 Phase 2d.
    private TemporalQuadIndex.TemporalQuadEnumerator _baseEnumerator;
    private ReferenceQuadIndex.ReferenceQuadEnumerator _referenceEnumerator;
    private VersionedQuadIndex.VersionedQuadEnumerator _graphEnumerator;
    private MinimalQuadIndex.MinimalQuadEnumerator _minimalEnumerator;
    private readonly bool _isReference;
    private readonly bool _isGraph;
    private readonly bool _isMinimal;

    private readonly TemporalIndexType _indexType;
    private readonly IAtomStore _atoms;
    private readonly HashSet<long>? _candidateObjectAtomIds;

    // Pooled buffer for zero-allocation string decoding
    private char[]? _buffer;
    private int _bufferOffset;
    private const int InitialBufferSize = 4096; // 4KB - typical for 3 URIs

    internal TemporalResultEnumerator(
        TemporalQuadIndex.TemporalQuadEnumerator baseEnumerator,
        TemporalIndexType indexType,
        IAtomStore atoms)
    {
        _baseEnumerator = baseEnumerator;
        _referenceEnumerator = default;
        _graphEnumerator = default;
        _minimalEnumerator = default;
        _isReference = false;
        _isGraph = false;
        _isMinimal = false;
        _indexType = indexType;
        _atoms = atoms;
        _candidateObjectAtomIds = null;
        _buffer = null;
        _bufferOffset = 0;
    }

    internal TemporalResultEnumerator(
        TemporalQuadIndex.TemporalQuadEnumerator baseEnumerator,
        TemporalIndexType indexType,
        IAtomStore atoms,
        HashSet<long>? candidateObjectAtomIds)
    {
        _baseEnumerator = baseEnumerator;
        _referenceEnumerator = default;
        _graphEnumerator = default;
        _minimalEnumerator = default;
        _isReference = false;
        _isGraph = false;
        _isMinimal = false;
        _indexType = indexType;
        _atoms = atoms;
        _candidateObjectAtomIds = candidateObjectAtomIds;
        _buffer = null;
        _bufferOffset = 0;
    }

    /// <summary>
    /// Wrap a Reference-profile enumeration. Temporal fields in the projected
    /// <see cref="ResolvedTemporalQuad"/> are synthesized (ValidFrom=MinValue,
    /// ValidTo=MaxValue, TransactionTime=MinValue, IsDeleted=false) because a Reference
    /// store has no temporal dimension — every triple is current by construction.
    /// </summary>
    internal TemporalResultEnumerator(
        ReferenceQuadIndex.ReferenceQuadEnumerator referenceEnumerator,
        TemporalIndexType indexType,
        IAtomStore atoms,
        HashSet<long>? candidateObjectAtomIds = null)
    {
        _baseEnumerator = default;
        _referenceEnumerator = referenceEnumerator;
        _graphEnumerator = default;
        _minimalEnumerator = default;
        _isReference = true;
        _isGraph = false;
        _isMinimal = false;
        _indexType = indexType;
        _atoms = atoms;
        _candidateObjectAtomIds = candidateObjectAtomIds;
        _buffer = null;
        _bufferOffset = 0;
    }

    /// <summary>
    /// Wrap a Graph-profile enumeration (ADR-029 Graph). Temporal fields are
    /// synthesized identically to the Reference path (no time dimension); the
    /// VersionedQuad's <c>IsDeleted</c> flag flows through into the projected
    /// <see cref="ResolvedTemporalQuad"/>. Soft-deleted entries are filtered out
    /// by <see cref="VersionedQuadIndex.Query"/> before they reach this enumerator,
    /// so the IsDeleted flag here is always false for live queries.
    /// </summary>
    internal TemporalResultEnumerator(
        VersionedQuadIndex.VersionedQuadEnumerator graphEnumerator,
        TemporalIndexType indexType,
        IAtomStore atoms,
        HashSet<long>? candidateObjectAtomIds = null)
    {
        _baseEnumerator = default;
        _referenceEnumerator = default;
        _graphEnumerator = graphEnumerator;
        _minimalEnumerator = default;
        _isReference = false;
        _isGraph = true;
        _isMinimal = false;
        _indexType = indexType;
        _atoms = atoms;
        _candidateObjectAtomIds = candidateObjectAtomIds;
        _buffer = null;
        _bufferOffset = 0;
    }

    /// <summary>
    /// Wrap a Minimal-profile enumeration (ADR-029 Minimal). Graph dimension is
    /// projected as the default graph (empty span); temporal fields are synthesized
    /// identically to Reference / Graph (no time dimension). Minimal has only a
    /// single GSPO index, so <see cref="_indexType"/> is always
    /// <see cref="TemporalIndexType.GSPO"/>.
    /// </summary>
    internal TemporalResultEnumerator(
        MinimalQuadIndex.MinimalQuadEnumerator minimalEnumerator,
        IAtomStore atoms,
        HashSet<long>? candidateObjectAtomIds = null)
    {
        _baseEnumerator = default;
        _referenceEnumerator = default;
        _graphEnumerator = default;
        _minimalEnumerator = minimalEnumerator;
        _isReference = false;
        _isGraph = false;
        _isMinimal = true;
        _indexType = TemporalIndexType.GSPO;
        _atoms = atoms;
        _candidateObjectAtomIds = candidateObjectAtomIds;
        _buffer = null;
        _bufferOffset = 0;
    }

    public bool MoveNext()
    {
        // Reset buffer offset for new result - reuse same buffer
        _bufferOffset = 0;

        if (_isReference)
        {
            if (_candidateObjectAtomIds is null)
                return _referenceEnumerator.MoveNext();

            while (_referenceEnumerator.MoveNext())
            {
                var key = _referenceEnumerator.Current;
                long objectAtomId = _indexType switch
                {
                    TemporalIndexType.GPOS => key.Secondary,
                    _ => key.Tertiary // GSPO (Reference has no GOSP/TGSP)
                };
                if (_candidateObjectAtomIds.Contains(objectAtomId))
                    return true;
            }
            return false;
        }

        if (_isGraph)
        {
            if (_candidateObjectAtomIds is null)
                return _graphEnumerator.MoveNext();

            while (_graphEnumerator.MoveNext())
            {
                var gquad = _graphEnumerator.Current;
                long objectAtomId = _indexType switch
                {
                    TemporalIndexType.GPOS => gquad.Secondary,
                    TemporalIndexType.GOSP => gquad.Primary,
                    _ => gquad.Tertiary // GSPO, TGSP
                };
                if (_candidateObjectAtomIds.Contains(objectAtomId))
                    return true;
            }
            return false;
        }

        if (_isMinimal)
        {
            if (_candidateObjectAtomIds is null)
                return _minimalEnumerator.MoveNext();

            while (_minimalEnumerator.MoveNext())
            {
                // Minimal has only GSPO; Tertiary = object.
                if (_candidateObjectAtomIds.Contains(_minimalEnumerator.Current.Tertiary))
                    return true;
            }
            return false;
        }

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
            long graph, s, p, o;

            if (_isReference)
            {
                var key = _referenceEnumerator.Current;
                graph = key.Graph;
                // Reference supports only GSPO and GPOS.
                switch (_indexType)
                {
                    case TemporalIndexType.GSPO:
                        s = key.Primary; p = key.Secondary; o = key.Tertiary;
                        break;
                    case TemporalIndexType.GPOS:
                        p = key.Primary; o = key.Secondary; s = key.Tertiary;
                        break;
                    default:
                        s = p = o = 0;
                        break;
                }

                return new ResolvedTemporalQuad(
                    graph == 0 ? ReadOnlySpan<char>.Empty : DecodeAtomToBuffer(graph),
                    DecodeAtomToBuffer(s),
                    DecodeAtomToBuffer(p),
                    DecodeAtomToBuffer(o),
                    // Synthesized temporal fields: Reference has no time dimension.
                    DateTimeOffset.MinValue,
                    DateTimeOffset.MaxValue,
                    DateTimeOffset.MinValue,
                    false);
            }

            if (_isMinimal)
            {
                // Minimal has only GSPO with no graph dimension. Project graph as
                // default (empty span); temporal fields are synthesized like Reference.
                var mkey = _minimalEnumerator.Current;
                return new ResolvedTemporalQuad(
                    ReadOnlySpan<char>.Empty,
                    DecodeAtomToBuffer(mkey.Primary),
                    DecodeAtomToBuffer(mkey.Secondary),
                    DecodeAtomToBuffer(mkey.Tertiary),
                    DateTimeOffset.MinValue,
                    DateTimeOffset.MaxValue,
                    DateTimeOffset.MinValue,
                    false);
            }

            if (_isGraph)
            {
                var gquad = _graphEnumerator.Current;
                graph = gquad.Graph;
                // Graph supports GSPO/GPOS/GOSP/TGSP.
                switch (_indexType)
                {
                    case TemporalIndexType.GSPO:
                    case TemporalIndexType.TGSP:
                        s = gquad.Primary; p = gquad.Secondary; o = gquad.Tertiary;
                        break;
                    case TemporalIndexType.GPOS:
                        p = gquad.Primary; o = gquad.Secondary; s = gquad.Tertiary;
                        break;
                    case TemporalIndexType.GOSP:
                        o = gquad.Primary; s = gquad.Secondary; p = gquad.Tertiary;
                        break;
                    default:
                        s = p = o = 0;
                        break;
                }

                // Graph has versioning (CreatedAt/ModifiedAt/Version/IsDeleted) but no
                // bitemporal time dimension. Project ValidFrom/ValidTo/TransactionTime
                // as synthesized "always current" values (same shape as Reference); the
                // IsDeleted flag flows through from the entry — though under live queries
                // (the default) only live entries reach here, so it's always false.
                return new ResolvedTemporalQuad(
                    graph == 0 ? ReadOnlySpan<char>.Empty : DecodeAtomToBuffer(graph),
                    DecodeAtomToBuffer(s),
                    DecodeAtomToBuffer(p),
                    DecodeAtomToBuffer(o),
                    DateTimeOffset.MinValue,
                    DateTimeOffset.MaxValue,
                    DateTimeOffset.MinValue,
                    gquad.IsDeleted);
            }

            var quad = _baseEnumerator.Current;
            graph = quad.Graph;

            // Remap generic dimensions back to RDF terms based on index type
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
                graph == 0 ? ReadOnlySpan<char>.Empty : DecodeAtomToBuffer(graph),
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
    private readonly IAtomStore _atoms;
    private long _lastGraphAtom;
    private string? _current;

    internal NamedGraphEnumerator(TemporalQuadIndex index, IAtomStore atoms)
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
