using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Sparql;
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
    // ADR-066 D1: a write waits for the lock outside it, on this monitor, woken by every release of the lock. The count
    // of the writes waiting is kept and read under the monitor; nothing decides by it.
    private readonly object _writeWait = new();
    private int _writesWaiting;
    private readonly ILogger _logger;
    private readonly IBufferManager _bufferManager;
    private readonly long _minimumFreeDiskSpace;
    private readonly long _maxResultRows;
    private readonly StatisticsStore _statistics = new();
    private long _activeBatchTxId = -1;

    // ADR-058: set while Recover replays the WAL. Entries a crashed process inserted after an
    // index last committed its count replay as no-ops; these sets count each of them once.
    private bool _recovering;
    private HashSet<(TemporalQuadIndex, TemporalKeyIds)>? _countedDuringRecovery;
    // Reference profile has no WAL and therefore no batch transaction id. A simple
    // in-bulk flag lets AddCurrentBatched / CommitBatch enforce the "must be inside
    // BeginBatch" contract without fabricating a tx id that has no durable meaning.
    private bool _referenceBulkActive;

    // ADR-064 D10: a Reference store takes one bulk load, whatever its atom store (ADR-007, ADR-034 D7). A store that
    // holds atoms when it opens holds its load: it refuses bulk mode at open and a batch while it is open.
    private readonly bool _referenceHoldsItsLoad;
    // ADR-029 Minimal profile: same shape as Reference's bulk-active flag — no WAL,
    // writes go straight into the single GSPO index. CommitBatch is a no-op (no WAL
    // marker); durability via FlushToDisk at the end of the bulk-load session.
    // ADR-031 Piece 2: tracks whether anything has actually been written since the
    // last checkpoint. Set by every mutation entry point; reset by CheckpointInternal.
    // Dispose gates the 14-min CollectPredicateStatistics + WAL-checkpoint-marker path
    // on this flag — read-only sessions see Dispose drop from minutes to milliseconds.
    // Volatile because SessionMutated reads it without the writer lock under which
    // mutations set it.
    private volatile bool _sessionMutated;
    // ADR-061 M7: the exception that applying a record met after the log had committed it, or null. Once set, the
    // indexes may lack a write the log holds, and may disagree with each other. The store then answers no read, takes
    // no write and makes no checkpoint, and it closes without one: the log is the only complete copy, and opening the
    // store again applies it. Volatile because RebuildSecondaryIndexes reads it before it takes the lock.
    private volatile Exception? _unappliedWriteCause;
    // Batched records hold atom IDs only. Resolving IDs back to strings just so
    // ApplyToIndexes could re-intern them was a 1% hot path in bulk-load profiles,
    // plus one string allocation per atom per triple. The IDs are already in the
    // WAL record — we keep the record and let ApplyToIndexesById use it directly.
    private List<LogRecord>? _batchBuffer;
    // The batch's one clock reading, taken at BeginBatch under the write lock (ADR-061 I12): every operation of the
    // batch has it.
    private long _batchTransactionTimeTicks;
    // The store's one clock (ADR-061 D4). Every stamp is a reading of it taken under the write lock (I12).
    private readonly TimeProvider _clock;

    // ADR-061 M5, I9: a batch has one clock reading, so only the last operation on a quad in the batch counts, as
    // if it were the only one. The batch keeps, per quad it touches, what the store held for the quad before the
    // batch and the last operation asked. CommitBatch decides one change per quad from those two and logs it.
    private Dictionary<QuadIds, int>? _batchQuadPositions;
    private List<BatchQuad>? _batchQuads;

    // ADR-064 M2: on a Graph store every operation of a batch that changes a fact is a state of its own. The batch
    // keeps, per fact it changed, the version the change took it to; the next operation on the fact starts there.
    private Dictionary<QuadIds, int>? _batchVersions;

    /// <summary>
    /// What a write asks of a quad: <c>INSERT</c> memorizes it on a Cognitive store (ADR-061 D2) and asserts it on a
    /// Graph store (ADR-064 M4); <c>DELETE</c> forgets it, or retracts it.
    /// </summary>
    private enum Asked : byte { Insert, Delete }

    /// <summary>
    /// A Graph or Minimal store numbers each fact's states and keeps no time (ADR-064 M2): its writes are decided from
    /// the version the store holds, and its log holds <see cref="LogOperation.NextVersion"/> records.
    /// </summary>
    private bool NumbersFactStates => _schema.Profile is StoreProfile.Graph or StoreProfile.Minimal;

    /// <summary>A quad by its four atoms: its identity until ADR-053 makes one term one atom (ADR-061 I1).</summary>
    private readonly record struct QuadIds(long Graph, long Subject, long Predicate, long Object);

    /// <summary>One quad a batch has touched: what held before the batch, and the last operation asked.</summary>
    private struct BatchQuad
    {
        public QuadIds Quad;
        public TemporalQuadIndex.QuadAtReading Before;
        public Asked Last;
    }

    /// <summary>
    /// The store's clock (ADR-061 D4, <see cref="StorageOptions.Clock"/>). A query's <c>NOW()</c> is a reading of it,
    /// so every write the query did not see is stamped at or after that reading (ADR-061 M9).
    /// </summary>
    internal TimeProvider Clock => _clock;
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
        _clock = (storageOptions ?? StorageOptions.Default).Clock;
        _maxResultRows = (storageOptions ?? StorageOptions.Default).MaxResultRows;

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

        // ADR-058 D7: the atom store checks its header against the data at open.
        if (_atoms is HashAtomStore hashAtoms && hashAtoms.AtomsRecoveredAtOpen > 0)
            _logger.Warning("Atom header was behind the data; took {0} atoms written after it".AsSpan(), hashAtoms.AtomsRecoveredAtOpen);
        if (_atoms.WriteRefusal is { } refusal)
            _logger.Error(refusal.AsSpan());

        // ADR-064 D10: a Reference store takes one bulk load, whatever its atom store; it is read-only by design
        // (ADR-007). ADR-034 D7 made this so for a sorted atom store. A second bulk load against a store that holds its
        // load is refused — recreate the store to replace its contents.
        _referenceHoldsItsLoad = _schema.Profile == StoreProfile.Reference && _atoms.AtomCount > 0;
        if (_referenceHoldsItsLoad && _bulkLoadMode)
        {
            var atomCount = _atoms.AtomCount;
            _atoms.Dispose();
            throw new ProfileCapabilityException(
                "ADR-064 D10: a Reference store takes one bulk load, and this one holds " + atomCount + " atoms. " +
                "Recreate the store to replace its contents, or open it without --bulk-load to query it.");
        }

        // Create trigram index for full-text search (both profile families index it).
        var trigramPath = Path.Combine(baseDirectory, "trigram");
        _trigramIndex = new TrigramIndex(trigramPath, _bufferManager);

        // ADR-029 Phase 2d: dispatch index-family and WAL creation on the store's
        // schema profile. Cognitive produces four TemporalQuadIndex instances (88 B
        // entries, memory time, ADR-061); Graph produces three VersionedQuadIndex instances
        // (versioned + soft-delete, no time, ADR-064); both carry a WAL.
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
                // ADR-029 Graph profile, with ADR-064's model: VersionedQuadIndex × 3. Graph keeps
                // no time, so it has no TGSP (ADR-064 D3): single G→P→S→T comparison. The WAL
                // is Cognitive's; a Graph record is NextVersion, whose version takes the bytes
                // of MemorizedAtTicks and whose other time fields are 0 (ADR-064 D5).
                _wal = new WriteAheadLog(walPath, WriteAheadLog.DefaultCheckpointSizeThreshold,
                    WriteAheadLog.DefaultCheckpointTimeSeconds, _bufferManager, options.BulkMode);
                _gspoGraph = new VersionedQuadIndex(gspoPath, _atoms, options.IndexInitialSizeBytes, options.BulkMode);
                _gposGraph = new VersionedQuadIndex(gposPath, _atoms, options.IndexInitialSizeBytes, options.BulkMode);
                _gospGraph = new VersionedQuadIndex(gospPath, _atoms, options.IndexInitialSizeBytes, options.BulkMode);
                break;

            case StoreProfile.Reference:
                // No WAL: Reference bulk-load durability = one FlushToDisk at completion.
                _gspoReference = new ReferenceQuadIndex(gspoPath, _atoms, options.IndexInitialSizeBytes, options.BulkMode);
                _gposReference = new ReferenceQuadIndex(gposPath, _atoms, options.IndexInitialSizeBytes, options.BulkMode);
                break;

            case StoreProfile.Minimal:
                // ADR-029 Minimal profile, with ADR-064 D2: Graph without secondary indexes, named
                // graphs or trigram index. A single GSPO index, no graph dimension, no time; session
                // writes with a log, soft deletes and each fact's version, as Graph. Distinct
                // concrete class (MinimalQuadIndex, 28 B leaf entries) per the no-behavior-flags rule.
                _wal = new WriteAheadLog(walPath, WriteAheadLog.DefaultCheckpointSizeThreshold,
                    WriteAheadLog.DefaultCheckpointTimeSeconds, _bufferManager, options.BulkMode);
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
        {
            try
            {
                Recover();
            }
            catch (CommittedWriteNotAppliedException)
            {
                // The store does not open (ADR-061 M7). Its files are released, with no checkpoint, so the same
                // process can open it again once the cause is gone.
                Dispose();
                throw;
            }
        }
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
        // ADR-058 D7: a store whose atom IDs were reused stops taking writes.
        if (_atoms.WriteRefusal is { } refusal)
            throw new InvalidOperationException($"Operation '{operation}' refused. {refusal}");
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
    /// returns, the four temporal-index fields are non-null. The SPARQL engine calls it before it reads anything
    /// for a query that uses the words of Cognitive SPARQL (ADR-061 D9).
    /// </summary>
    [MemberNotNull(nameof(_gspoIndex), nameof(_gposIndex), nameof(_gospIndex), nameof(_tgspIndex))]
    internal void RequireTemporalProfile(string operation)
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
    /// The configured result-row cap for queries against this store (<c>StorageOptions.MaxResultRows</c>; 0 =
    /// unbounded). The query path checks this at every row-accumulation point and fails fast with
    /// <see cref="Abstractions.ResultLimitExceededException"/> rather than OOMing — the <c>unbounded-result-
    /// materialization</c> guard.
    /// </summary>
    internal long MaxResultRows => _maxResultRows;

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
            var available = DiskSpaceGuard.GetAvailableSpace(_baseDirectory);
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

        var available = DiskSpaceGuard.GetAvailableSpace(_baseDirectory);
        if (available >= 0 && available < _minimumFreeDiskSpace)
        {
            _diskSpaceLow = true;
            _logger.Warning($"Disk space low: {available} bytes available, minimum is {_minimumFreeDiskSpace} bytes. Further writes will be refused.".AsSpan());
        }
    }

    /// <summary>
    /// Memorize a fact — a SPARQL insert of one quad, with WAL durability. On a Cognitive store the write rule
    /// decides what changes (ADR-061 M5): a fact that is remembered stays as it is, a fact forgotten in this clock
    /// reading continues its period, and any other fact begins a period at this reading.
    /// Thread-safe: acquires write lock.
    /// Returns true if the fact was not remembered before and is now (ADR-061 D2), or on a Graph store, was not
    /// asserted before and is now (ADR-064 M4).
    /// </summary>
    public bool AddCurrent(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        ReadOnlySpan<char> graph = default)
    {
        RequireWriteCapableProfile(nameof(AddCurrent));
        ThrowIfDiskSpaceLow();
        AcquireWriteLock();
        try
        {
            ThrowIfStoppedOverUnappliedWrite();
            var quad = new QuadIds(graph.IsEmpty ? 0 : _atoms.Intern(graph),
                _atoms.Intern(subject), _atoms.Intern(predicate), _atoms.Intern(@object));

            // A Graph or Minimal store keeps no time (ADR-064 D1, D2): it reads no clock.
            if (NumbersFactStates)
                return ChangeStateHoldingWriteLock(quad, Asked.Insert);

            // One reading of the clock, taken under the lock, is the stamp (ADR-061 I12). Reading it before the
            // lock let a write carry a stamp earlier than the NOW() of a query that did not see it.
            return ChangeHoldingWriteLock(quad, Asked.Insert, _clock.GetUtcNow().ToUnixTimeMilliseconds());
        }
        finally
        {
            ReleaseWriteLock();
        }
    }

    /// <summary>
    /// One single write on a Cognitive store, with the write lock held: decide the change (ADR-061 M5), and when
    /// there is one, log it, apply it and save the counts. Returns whether the quad's being remembered changed.
    /// </summary>
    private bool ChangeHoldingWriteLock(QuadIds quad, Asked asked, long clockReadingMs)
    {
        var before = _gspoIndex!.FindQuadAtReading(quad.Graph, quad.Subject, quad.Predicate, quad.Object, clockReadingMs);
        if (DecideChange(asked, before, quad, clockReadingMs) is not { } record)
            return false;

        // The WAL record holds only atom IDs, so the atoms must be durable first (ADR-058 D6)
        MakeAtomsDurable();
        _wal!.Append(record);

        // The log has committed the record: a failure from here on stops the store (ADR-061 M7).
        try
        {
            ApplyRecordToIndexesById(record);
            CommitIndexCounts(_wal.CurrentTxId, durable: false);
        }
        catch (Exception cause)
        {
            throw StopOverUnappliedWrite(cause);
        }
        CheckpointIfNeeded();
        CheckDiskSpaceAfterWrite();
        return true;
    }

    /// <summary>
    /// The write rule (ADR-061 M5): the change one operation makes to one quad at one clock reading, or null when it
    /// changes nothing. It looks at what the store holds for the quad at the reading and at nothing else: no
    /// operation identity, no sequence number, no second timestamp.
    /// </summary>
    private static LogRecord? DecideChange(Asked asked, TemporalQuadIndex.QuadAtReading before, QuadIds quad, long clockReadingMs)
    {
        if (asked == Asked.Insert)
        {
            // Remembered: nothing (I3).
            if (before.HoldingMemorizedAt is not null)
                return null;

            // Forgotten in this clock reading: the period continues, its forgotten time is removed (M2).
            if (before.ForgottenAtReadingMemorizedAt is { } continued)
                return LogRecord.CreatePeriodChange(LogOperation.ContinuePeriod, quad.Graph, quad.Subject, quad.Predicate,
                    quad.Object, continued, clockReadingMs, clockReadingMs);

            // Never remembered, or forgotten before this reading: a period begins.
            return LogRecord.CreatePeriodChange(LogOperation.BeginPeriod, quad.Graph, quad.Subject, quad.Predicate,
                quad.Object, clockReadingMs, TemporalQuadIndex.OpenForgottenAt, clockReadingMs);
        }

        // Not remembered: nothing (I4).
        if (before.HoldingMemorizedAt is not { } held)
            return null;

        // An entry that holds and has a stated end was written by Add with a stated time, before ADR-061 Step 6
        // removed it. Such an entry is ended as it was then: the End record shortens it.
        if (!before.HoldingIsOpen)
            return LogRecord.CreateEnd(quad.Subject, quad.Predicate, quad.Object,
                DateTimeOffset.FromUnixTimeMilliseconds(held), DateTimeOffset.FromUnixTimeMilliseconds(clockReadingMs),
                quad.Graph, transactionTimeTicks: clockReadingMs);

        // Memorized in this clock reading: the quad was remembered at no representable time, so no period is left (M2).
        if (held == clockReadingMs)
            return LogRecord.CreatePeriodChange(LogOperation.RemovePeriod, quad.Graph, quad.Subject, quad.Predicate,
                quad.Object, held, clockReadingMs, clockReadingMs);

        // Remembered since an earlier reading: the period ends here.
        return LogRecord.CreatePeriodChange(LogOperation.EndPeriod, quad.Graph, quad.Subject, quad.Predicate,
            quad.Object, held, clockReadingMs, clockReadingMs);
    }

    /// <summary>
    /// One single write on a Graph or Minimal store, with the write lock held: decide the change from the version GSPO
    /// holds (ADR-064 M4), and when there is one, log it, apply it and save the counts. Returns whether the fact's state
    /// changed.
    /// </summary>
    private bool ChangeStateHoldingWriteLock(QuadIds quad, Asked asked)
    {
        RefuseNamedGraphOnMinimal(quad);
        if (DecideNextVersion(asked, HeldVersion(quad), quad) is not { } record)
            return false;

        // The WAL record holds only atom IDs, so the atoms must be durable first (ADR-058 D6)
        MakeAtomsDurable();
        _wal!.Append(record);

        // The log has committed the record: a failure from here on stops the store (ADR-061 M7).
        try
        {
            ApplyRecordToIndexesById(record);
            CommitIndexCounts(_wal.CurrentTxId, durable: false);
        }
        catch (Exception cause)
        {
            throw StopOverUnappliedWrite(cause);
        }
        CheckpointIfNeeded();
        CheckDiskSpaceAfterWrite();
        return true;
    }

    /// <summary>
    /// The version GSPO holds a fact at on a Graph or Minimal store, or 0 when it holds none. The store's own read of
    /// the version, through the index's cursor (ADR-064 D4).
    /// </summary>
    private int HeldVersion(QuadIds quad)
    {
        var held = _schema.Profile == StoreProfile.Minimal
            ? _gspoMinimal!.QueryHeld(quad.Subject, quad.Predicate, quad.Object)
            : _gspoGraph!.QueryHeld(quad.Graph, quad.Subject, quad.Predicate, quad.Object);
        return held.MoveNext() ? held.Version : 0;
    }

    /// <summary>
    /// A Minimal store has no named graphs (ADR-064 D2): a write that names one is refused, before anything is logged.
    /// </summary>
    private void RefuseNamedGraphOnMinimal(QuadIds quad)
    {
        if (quad.Graph != 0 && _schema.Profile == StoreProfile.Minimal)
            throw new ProfileCapabilityException(
                "Minimal profile does not support named graphs (hasGraph=false per ADR-029). " +
                "Use Cognitive / Graph / Reference if your workload requires multi-graph semantics.");
    }

    /// <summary>
    /// The change an assert or a retract makes to a fact held at <paramref name="held"/> (ADR-064 M4), or null when it
    /// changes nothing: an assert of a fact that is asserted, or a retract of one that is not. An odd version is
    /// asserted; 0 is a fact the store does not hold.
    /// </summary>
    private static LogRecord? DecideNextVersion(Asked asked, int held, QuadIds quad)
    {
        var asserted = held % 2 == 1;
        if (asked == Asked.Insert ? asserted : !asserted)
            return null;
        return LogRecord.CreateNextVersion(quad.Graph, quad.Subject, quad.Predicate, quad.Object, held);
    }

    /// <summary>
    /// Forget a fact — a SPARQL delete of one quad. On a Cognitive store the write rule decides what changes
    /// (ADR-061 M5): the period that holds ends at this clock reading and stays in the store with both times; a fact
    /// memorized in this same reading leaves no period. On a Graph store (no times) the fact is retracted: its version
    /// goes to the next, even one, and the store keeps it (ADR-064 M4).
    /// Thread-safe: acquires write lock.
    /// Returns true if the fact was remembered, or asserted, and is not any more (ADR-061 D2), false if there was
    /// nothing to forget.
    /// </summary>
    public bool DeleteCurrent(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        ReadOnlySpan<char> graph = default)
    {
        RequireWriteCapableProfile(nameof(DeleteCurrent));
        ThrowIfDiskSpaceLow();
        AcquireWriteLock();
        try
        {
            ThrowIfStoppedOverUnappliedWrite();
            if (!TryResolveQuadIds(subject, predicate, @object, graph, out var graphId, out var subjectId, out var predicateId, out var objectId))
                return false;

            var quad = new QuadIds(graphId, subjectId, predicateId, objectId);
            if (NumbersFactStates)
                return ChangeStateHoldingWriteLock(quad, Asked.Delete);
            return ChangeHoldingWriteLock(quad, Asked.Delete, _clock.GetUtcNow().ToUnixTimeMilliseconds());
        }
        finally
        {
            ReleaseWriteLock();
        }
    }

    /// <summary>
    /// Look up the atom IDs of a quad without interning: a quad whose atoms are unknown does not exist.
    /// </summary>
    private bool TryResolveQuadIds(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> @object,
        ReadOnlySpan<char> graph, out long graphId, out long subjectId, out long predicateId, out long objectId)
    {
        graphId = graph.IsEmpty ? 0 : _atoms.GetAtomId(graph);
        subjectId = _atoms.GetAtomId(subject);
        predicateId = _atoms.GetAtomId(predicate);
        objectId = _atoms.GetAtomId(@object);
        if (subjectId == 0 || predicateId == 0 || objectId == 0)
            return false;
        return graph.IsEmpty || graphId != 0;
    }

    /// <summary>
    /// Repair the valid-time history a pre-ADR-054 SPARQL delete erased (ADR-054 D7). Before ADR-054 every delete
    /// flagged the entry LegacyIsDeleted and left ForgottenAt open, so the fact left every view of the past. Each flagged entry becomes
    /// what an End would have written (ForgottenAt = the deletion's transaction time, LegacyIsDeleted = false), except entries of a
    /// deletion event named in <paramref name="restore"/> (graph IRI, or "" for the default graph, and the deletion time
    /// as the report prints it, in milliseconds). Those are restored: the delete was the mistake and the facts never
    /// stopped holding. An entry deleted before its MemorizedAt is left and listed.
    /// Writes the report (every deletion event with its entries and action) to <paramref name="report"/>; with
    /// <paramref name="dryRun"/> it changes nothing. Run on a store no other process has open. No WAL record is
    /// written: each index is repaired on its own entries, so an interrupted run can be repeated.
    /// </summary>
    public (int Ended, int Restored, int Left) RepairDeletes(
        IReadOnlyCollection<(string Graph, DateTimeOffset DeletedAt)> restore, bool dryRun, TextWriter report)
    {
        RequireTemporalProfile(nameof(RepairDeletes));
        AcquireWriteLock();
        try
        {
            ThrowIfStoppedOverUnappliedWrite();
            if (_activeBatchTxId >= 0)
                throw new InvalidOperationException("RepairDeletes cannot run inside a batch.");

            var restoreEvents = new HashSet<(long GraphId, long DeletedAtMs)>();
            foreach (var (graphIri, deletedAt) in restore)
            {
                long graphId = graphIri.Length == 0 ? 0 : _atoms.GetAtomId(graphIri);
                if (graphIri.Length > 0 && graphId == 0)
                    throw new ArgumentException($"--restore names graph {graphIri}, which is not in this store.", nameof(restore));
                restoreEvents.Add((graphId, deletedAt.ToUnixTimeMilliseconds()));
            }

            TemporalQuadIndex.DeletedEntryRepair Decide(long graph, long memorizedAt, long deletedAt) =>
                deletedAt < memorizedAt ? TemporalQuadIndex.DeletedEntryRepair.Leave
                : restoreEvents.Contains((graph, deletedAt)) ? TemporalQuadIndex.DeletedEntryRepair.Restore
                : TemporalQuadIndex.DeletedEntryRepair.End;

            // Report from GSPO (subject, predicate, object order), grouped into deletion events: one graph, one time.
            var entries = new List<(long G, long S, long P, long O, long From, long To, long DeletedAt)>();
            _gspoIndex!.RepairDeleted((g, s, p, o, from, to, deletedAt) =>
            {
                entries.Add((g, s, p, o, from, to, deletedAt));
                return TemporalQuadIndex.DeletedEntryRepair.Leave;
            });

            int ended = 0, restored = 0, left = 0;
            var matchedRestore = new HashSet<(long, long)>();
            foreach (var deletionEvent in entries.GroupBy(e => (e.G, e.DeletedAt)).OrderBy(e => e.Key.DeletedAt))
            {
                var graphName = deletionEvent.Key.G == 0 ? "(default graph)" : _atoms.GetAtomString(deletionEvent.Key.G);
                var action = restoreEvents.Contains(deletionEvent.Key) ? "restore" : "end";
                if (action == "restore") matchedRestore.Add(deletionEvent.Key);
                report.WriteLine($"== {FormatRepairTime(deletionEvent.Key.DeletedAt)}  {graphName}  {deletionEvent.Count()} entries  -> {action}");
                foreach (var e in deletionEvent)
                {
                    var decision = Decide(e.G, e.From, e.DeletedAt);
                    switch (decision)
                    {
                        case TemporalQuadIndex.DeletedEntryRepair.End: ended++; break;
                        case TemporalQuadIndex.DeletedEntryRepair.Restore: restored++; break;
                        default: left++; break;
                    }
                    var after = decision switch
                    {
                        TemporalQuadIndex.DeletedEntryRepair.End => $"[{FormatRepairTime(e.From)}, {FormatRepairTime(e.DeletedAt)})",
                        TemporalQuadIndex.DeletedEntryRepair.Restore => $"[{FormatRepairTime(e.From)}, {FormatRepairTime(e.To)}) current",
                        _ => "left: deleted before its MemorizedAt",
                    };
                    report.WriteLine($"   {_atoms.GetAtomString(e.S)} {_atoms.GetAtomString(e.P)} {_atoms.GetAtomString(e.O)}");
                    report.WriteLine($"      valid [{FormatRepairTime(e.From)}, {FormatRepairTime(e.To)}) deleted -> {after}");
                }
            }
            foreach (var missing in restoreEvents.Where(r => !matchedRestore.Contains(r)))
                report.WriteLine($"!! --restore matched no deletion event: graph id {missing.GraphId} at {FormatRepairTime(missing.DeletedAtMs)}");
            report.WriteLine($"{entries.Count} deleted entries in {entries.Select(e => (e.G, e.DeletedAt)).Distinct().Count()} deletion events: " +
                $"{ended} end, {restored} restore, {left} left{(dryRun ? " (dry run, nothing changed)" : "")}");

            if (!dryRun && entries.Count > 0)
            {
                // Each index is repaired on its own entries. The decision depends only on graph, MemorizedAt and the
                // deletion time, which every index holds in the same fields, so all four reach the same result.
                _gspoIndex.RepairDeleted((g, _, _, _, from, _, deletedAt) => Decide(g, from, deletedAt));
                _gposIndex!.RepairDeleted((g, _, _, _, from, _, deletedAt) => Decide(g, from, deletedAt));
                _gospIndex!.RepairDeleted((g, _, _, _, from, _, deletedAt) => Decide(g, from, deletedAt));
                _tgspIndex!.RepairDeleted((g, _, _, _, from, _, deletedAt) => Decide(g, from, deletedAt));
                _sessionMutated = true;
            }

            return (ended, restored, left);
        }
        finally
        {
            ReleaseWriteLock();
        }
    }

    private static string FormatRepairTime(long unixMs)
    {
        const long MaxValidMs = 253402300799999L;
        if (unixMs >= MaxValidMs) return "∞";
        if (unixMs <= -62135596800000L) return "-∞";
        return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
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
    ///           store.AddCurrentBatched(...);
    ///       store.CommitBatch();
    ///   } catch {
    ///       store.RollbackBatch();
    ///       throw;
    ///   }
    /// </summary>
    public void BeginBatch()
    {
        ThrowIfDiskSpaceLow();
        AcquireWriteLock();
        try
        {
            ThrowIfStoppedOverUnappliedWrite();
            if (_activeBatchTxId >= 0 || _referenceBulkActive)
                throw new InvalidOperationException("A batch is already active.");

            // ADR-058 D7: a damaged store refuses the batch here, before the WAL begin record is
            // written, so a refused write leaves the store's files as they were.
            if (_atoms.WriteRefusal is { } refusal)
                throw new InvalidOperationException($"Operation '{nameof(BeginBatch)}' refused. {refusal}");

            // ADR-029 Decision 7: Reference allows bulk-load as a programmatic interface
            // but has no WAL. Hold the writer lock and set a flag; all writes go straight
            // into the Reference indexes and become durable on CommitBatch's Flush.
            if (_schema.Profile == StoreProfile.Reference)
            {
                // ADR-064 D10: a batch is Reference's one write path, its bulk-load entry. A store that held its load
                // when it opened takes no second one this way either.
                if (_referenceHoldsItsLoad)
                    throw new ProfileCapabilityException(
                        "ADR-064 D10: a Reference store takes one bulk load, and this one holds it: it takes no batch. " +
                        "Recreate the store to replace its contents.");
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
            _batchQuadPositions?.Clear();
            _batchQuads?.Clear();
            _batchVersions?.Clear();
            _batchTransactionTimeTicks = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        }
        catch
        {
            ReleaseWriteLock();
            throw;
        }
        // Lock held until CommitBatch/RollbackBatch
    }

    /// <summary>
    /// Materialize a single Reference-profile triple inside an active bulk load: Reference keeps no
    /// times, so a write is "insert (g,s,p,o)."
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
    /// Memorize a fact in the current batch — a SPARQL insert of one quad (see <see cref="AddCurrent"/>). Every
    /// operation of a batch has the batch's one clock reading, so on a Cognitive store only the last operation on a
    /// quad in the batch counts (ADR-061 M2); the change is decided and logged at CommitBatch.
    /// Returns true if the fact was not remembered when this was called and is now (ADR-061 D2), or on a Graph or
    /// Minimal store, was not asserted and is now (ADR-064 M4); on Reference, always true.
    /// </summary>
    public bool AddCurrentBatched(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        ReadOnlySpan<char> graph = default)
    {
        if (_schema.Profile == StoreProfile.Reference)
        {
            // Reference keeps no times: the batch's reading is not stored.
            AddReferenceBulkTriple(subject, predicate, @object, graph);
            return true;
        }

        if (_activeBatchTxId < 0)
            throw new InvalidOperationException("No active batch. Call BeginBatch() first.");

        // Intern atoms (IAtomStore is append-only — orphans from rollback are harmless)
        var quad = new QuadIds(graph.IsEmpty ? 0 : _atoms.Intern(graph),
            _atoms.Intern(subject), _atoms.Intern(predicate), _atoms.Intern(@object));
        if (NumbersFactStates)
            return AskStateInBatch(quad, Asked.Insert);
        return AskInBatch(quad, Asked.Insert);
    }

    /// <summary>
    /// One assert or retract of the current batch on a Graph or Minimal store (ADR-064 M2, M4). Each one that changes the fact is
    /// a state of its own: it is decided from the version the batch's earlier operations took the fact to, or else
    /// from the version GSPO holds, and logged now. Returns whether the fact's state changed.
    /// </summary>
    private bool AskStateInBatch(QuadIds quad, Asked asked)
    {
        RefuseNamedGraphOnMinimal(quad);
        _batchVersions ??= new();
        if (!_batchVersions.TryGetValue(quad, out var held))
            held = HeldVersion(quad);

        if (DecideNextVersion(asked, held, quad) is not { } record)
            return false;

        _wal!.AppendBatch(record, _activeBatchTxId);
        _batchBuffer!.Add(record);
        _batchVersions[quad] = held + 1;
        return true;
    }

    /// <summary>
    /// Note one operation of the current batch on one quad, and say whether it changes if the quad is remembered.
    /// The first operation on a quad looks the quad up; a later one replaces the one before it (ADR-061 I9).
    /// </summary>
    private bool AskInBatch(QuadIds quad, Asked asked)
    {
        _batchQuadPositions ??= new();
        _batchQuads ??= new();

        bool remembered;
        if (_batchQuadPositions.TryGetValue(quad, out var position))
        {
            var touched = _batchQuads[position];
            remembered = touched.Last == Asked.Insert;
            touched.Last = asked;
            _batchQuads[position] = touched;
        }
        else
        {
            var before = _gspoIndex!.FindQuadAtReading(quad.Graph, quad.Subject, quad.Predicate, quad.Object, _batchTransactionTimeTicks);
            remembered = before.HoldingMemorizedAt is not null;
            _batchQuadPositions[quad] = _batchQuads.Count;
            _batchQuads.Add(new BatchQuad { Quad = quad, Before = before, Last = asked });
        }

        return asked == Asked.Insert ? !remembered : remembered;
    }

    /// <summary>
    /// The copy that carries a time (ADR-061 D12, I11, I14), for a prune: memorize a fact in the current batch at the
    /// time its present period was memorized in the store it is copied from. No writer states a time; a copy between
    /// stores carries the source's, and only into a store that holds nothing of the fact: a prune clears its target
    /// first. <paramref name="memorizedAt"/>, in milliseconds of the source's clock, is null when the source keeps no
    /// times: the fact is then memorized as <see cref="AddCurrentBatched"/> memorizes it. A store that keeps no times
    /// takes the fact as <see cref="AddCurrentBatched"/> does.
    /// </summary>
    internal void CopyRememberedBatched(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        ReadOnlySpan<char> graph,
        long? memorizedAt)
    {
        if (memorizedAt is not { } copied || _gspoIndex is null)
        {
            AddCurrentBatched(subject, predicate, @object, graph);
            return;
        }
        if (_activeBatchTxId < 0)
            throw new InvalidOperationException("No active batch. Call BeginBatch() first.");

        var quad = new QuadIds(graph.IsEmpty ? 0 : _atoms.Intern(graph),
            _atoms.Intern(subject), _atoms.Intern(predicate), _atoms.Intern(@object));
        var record = LogRecord.CreatePeriodChange(LogOperation.BeginPeriod, quad.Graph, quad.Subject, quad.Predicate,
            quad.Object, copied, TemporalQuadIndex.OpenForgottenAt, _batchTransactionTimeTicks);
        _wal!.AppendBatch(record, _activeBatchTxId);
        _batchBuffer!.Add(record);
    }

    /// <summary>
    /// Forget a fact in the current batch — a SPARQL delete of one quad (see <see cref="DeleteCurrent"/>). Every
    /// operation of a batch has the batch's one clock reading, so on a Cognitive store only the last operation on a
    /// quad in the batch counts (ADR-061 M2); the change is decided and logged at CommitBatch. On a Graph store every
    /// retract that changes the fact is a state of its own (ADR-064 M2).
    /// Returns true if the fact was remembered, or asserted, when this was called and is not any more (ADR-061 D2).
    /// </summary>
    public bool DeleteCurrentBatched(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        ReadOnlySpan<char> graph = default)
    {
        RequireWriteCapableProfile(nameof(DeleteCurrentBatched));
        if (_activeBatchTxId < 0)
            throw new InvalidOperationException("No active batch. Call BeginBatch() first.");

        if (!TryResolveQuadIds(subject, predicate, @object, graph, out var graphId, out var subjectId, out var predicateId, out var objectId))
            return false;

        var quad = new QuadIds(graphId, subjectId, predicateId, objectId);
        if (NumbersFactStates)
            return AskStateInBatch(quad, Asked.Delete);
        return AskInBatch(quad, Asked.Delete);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSameQuad(in LogRecord record, long graphId, long subjectId, long predicateId, long objectId) =>
        record.GraphId == graphId && record.SubjectId == subjectId &&
        record.PredicateId == predicateId && record.ObjectId == objectId;

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

            RequireWriteCapableProfile(nameof(CommitBatch));
            if (_activeBatchTxId < 0)
                throw new InvalidOperationException("No active batch.");

            // 0. Decide one change per quad the batch touched (ADR-061 M5, I9) and log it. The batch has one clock
            // reading, so the last operation asked of a quad decides, against what the store held before the batch.
            if (_batchQuads is { Count: > 0 })
            {
                foreach (var touched in _batchQuads)
                {
                    if (DecideChange(touched.Last, touched.Before, touched.Quad, _batchTransactionTimeTicks) is not { } change)
                        continue;
                    _wal.AppendBatch(change, _activeBatchTxId);
                    _batchBuffer!.Add(change);
                }
                _batchQuads.Clear();
                _batchQuadPositions!.Clear();
            }

            // 1. Write CommitTx marker — fsync in cognitive mode, deferred in bulk mode.
            // The WAL records hold only atom IDs, so the atoms they reference are made
            // durable before the commit that makes the records count (ADR-058 D6).
            var committedTxId = _activeBatchTxId;
            if (_bulkLoadMode)
            {
                _wal.CommitBatchNoSync(committedTxId);
            }
            else
            {
                MakeAtomsDurable();
                _wal.CommitBatch(committedTxId);
            }
            _activeBatchTxId = -1;

            // 2. Materialize buffered records into indexes. IDs are already in the
            // record — use the by-ID path so TemporalQuadIndex doesn't re-intern strings.
            // The log has committed the batch: a failure from here on stops the store (ADR-061 M7). The records
            // after the one that failed have reached no index, and the log holds them all.
            try
            {
                if (_batchBuffer != null)
                {
                    foreach (var record in _batchBuffer)
                        ApplyRecordToIndexesById(record);
                }

                // 3. The indexes now hold this transaction: save their counts as of it (ADR-058).
                if (!_bulkLoadMode)
                    CommitIndexCounts(committedTxId, durable: false);
            }
            catch (Exception cause)
            {
                throw StopOverUnappliedWrite(cause);
            }
            finally
            {
                _batchBuffer?.Clear();
                _batchVersions?.Clear();
            }

            CheckpointIfNeeded();

            // Check disk space AFTER commit (batch succeeds, next operation may be refused)
            CheckDiskSpaceAfterWrite();
        }
        finally
        {
            ReleaseWriteLock();
        }
    }

    /// <summary>
    /// Rollback the current batch (releases lock without committing).
    /// Indexes are untouched — deferred materialization means no mutations to undo.
    /// WAL records have BeginTx but no CommitTx, so recovery will discard them.
    /// </summary>
    public void RollbackBatch()
    {
        // A CommitBatch that threw has already ended the batch and released the lock. The callers roll back in the
        // catch block that also catches a failed commit: there is nothing to roll back then, and releasing a lock
        // that is not held would throw over the commit's own exception.
        if (!_lock.IsWriteLockHeld)
            return;

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
            ReleaseWriteLock();
            return;
        }

        _batchBuffer?.Clear();
        _batchQuadPositions?.Clear();
        _batchQuads?.Clear();
        _batchVersions?.Clear();
        _activeBatchTxId = -1;
        ReleaseWriteLock();
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

        // ADR-058 D6: the atoms the WAL references are durable before the WAL is, and the
        // index headers carry counts as of the last transaction.
        _atoms.Flush();
        if (_wal is not null)
            CommitIndexCounts(_wal.CurrentTxId, durable: true);
        _wal?.FlushToDisk();
        _gspoIndex?.Flush();
        _gposIndex?.Flush();
        _gospIndex?.Flush();
        _tgspIndex?.Flush();
        _gspoGraph?.Flush();
        _gposGraph?.Flush();
        _gospGraph?.Flush();
        _gspoReference?.Flush();
        _gposReference?.Flush();
        _gspoMinimal?.Flush();
    }

    private void FinalizeSortedAtomBulkIfPresent()
    {
        if (_sortedAtomBulkBuilder is null) return;

        AcquireWriteLock();
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
            ReleaseWriteLock();
        }
    }

    private void DrainBulkSorter()
    {
        if (_bulkSorter is null) return;

        AcquireWriteLock();
        try
        {
            DrainBulkSorterHoldingWriteLock();
        }
        finally
        {
            ReleaseWriteLock();
        }
    }

    private void DrainBulkSorterHoldingWriteLock()
    {
        if (_bulkSorter is null) return;

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
        ThrowIfStoppedOverUnappliedWrite();

        // ADR-030 Decision 5: Reference's bulk-load writes only _gspoReference. The
        // rebuild phase populates _gposReference and the trigram index from a single
        // GSPO scan — structurally identical to Cognitive's rebuild, just two targets
        // instead of four.
        if (_schema.Profile == StoreProfile.Reference)
        {
            RebuildReferenceSecondaryIndexes(onProgress);
            return;
        }

        // ADR-029 Graph profile, with ADR-064 D6: parallel to Cognitive's rebuild. It compares GPOS and GOSP with
        // GSPO first, then carries every fact GSPO holds into them at its version, and rebuilds the trigram posting
        // list.
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
            AcquireWriteLock();
            try
            {
                _indexState = StoreIndexState.Ready;
                _bulkLoadMode = false;
                StoreStateFile.Write(_baseDirectory, _indexState);
            }
            finally
            {
                ReleaseWriteLock();
            }
            return;
        }

        RequireTemporalProfile(nameof(RebuildSecondaryIndexes));
        AcquireWriteLock();
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var phases = HasRebuildListener ? new List<RebuildPhaseMetrics>(4) : null;
        var started = false;
        try
        {
            // A rebuild replaces GPOS, GOSP and TGSP with GSPO's entries (ADR-061 I14). First it looks for what that
            // would discard: an entry of one of them that GSPO does not hold as it is. Resolving such a difference
            // needs a rule (ADR-061 LQ5), and the rebuild has none, so it then changes nothing and says how many it
            // found (ADR-061 I19). An emptied or partly built index holds nothing GSPO lacks.
            var gposOnly = CountEntriesGspoDoesNotHold(_gposIndex!, static (p, o, s) => (s, p, o));
            var gospOnly = CountEntriesGspoDoesNotHold(_gospIndex!, static (o, s, p) => (s, p, o));
            var tgspOnly = CountEntriesGspoDoesNotHold(_tgspIndex!, static (s, p, o) => (s, p, o));
            if (gposOnly + gospOnly + tgspOnly > 0)
            {
                var refusal = $"The rebuild changed nothing: GPOS holds {gposOnly:N0}, GOSP {gospOnly:N0} and TGSP " +
                    $"{tgspOnly:N0} entries that GSPO does not hold as they are. A rebuild derives these indexes from " +
                    "GSPO and would discard them; a difference between the indexes needs a rule of its own (ADR-061 I19, LQ5).";
                _logger.Warning(refusal.AsSpan());
                throw new InvalidOperationException(refusal);
            }

            // ADR-031 Piece 2: rebuilding secondary indexes touches every B+Tree. Even
            // though the data came from the primary, the secondaries are now different.
            // Mark mutated so Dispose runs CheckpointInternal for the new statistics.
            _sessionMutated = true;
            started = true;

            RebuildIndex(_gposIndex!, "GPOS", StoreIndexState.BuildingGPOS,
                static (s, p, o) => (p, o, s), onProgress, phases);

            RebuildIndex(_gospIndex!, "GOSP", StoreIndexState.BuildingGOSP,
                static (s, p, o) => (o, s, p), onProgress, phases);

            RebuildIndex(_tgspIndex!, "TGSP", StoreIndexState.BuildingTGSP,
                static (s, p, o) => (s, p, o), onProgress, phases);

            // Trigram rebuild, from every graph's entries. The trigram index ignores an atom it already holds.
            _indexState = StoreIndexState.BuildingTrigram;
            StoreStateFile.Write(_baseDirectory, _indexState);
            var trigramStopwatch = System.Diagnostics.Stopwatch.StartNew();
            long trigramCount = 0;
            var gspo = _gspoIndex!.QueryEveryEntryAllGraphs();
            while (gspo.MoveNext())
            {
                // In GSPO: Tertiary = object. Check if it's a literal (atom starts with ")
                var objectId = gspo.Tertiary;
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
            ReleaseWriteLock();

            if (HasRebuildListener && started)
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
    /// The number of entries of a Cognitive secondary index that GSPO does not hold as they are (ADR-061 I19).
    /// <paramref name="toGspo"/> takes an entry's three dimensions in the index's order and gives subject, predicate
    /// and object. Every entry is read, retracted ones too, in every graph.
    /// </summary>
    private long CountEntriesGspoDoesNotHold(TemporalQuadIndex target,
        Func<long, long, long, (long Subject, long Predicate, long Object)> toGspo)
    {
        long count = 0;
        var entry = target.QueryEveryEntryAllGraphs();
        while (entry.MoveNext())
        {
            var (s, p, o) = toGspo(entry.Primary, entry.Secondary, entry.Tertiary);
            if (!_gspoIndex!.HoldsEntry(entry.Graph, s, p, o, entry.MemorizedAt, entry.ForgottenAt,
                    entry.LegacyTransactionTime, entry.LegacyModifiedAt, entry.LegacyIsDeleted))
                count++;
        }
        return count;
    }

    /// <summary>
    /// Rebuild a single secondary index from the primary GSPO index: empty it, then carry every GSPO entry of every
    /// graph into it as it is, with its times and its retraction mark (ADR-061 I14).
    /// <paramref name="fromGspo"/> takes subject, predicate and object and gives the index's three dimensions.
    /// </summary>
    private void RebuildIndex(TemporalQuadIndex target, string name, StoreIndexState duringState,
        Func<long, long, long, (long Primary, long Secondary, long Tertiary)> fromGspo,
        Action<string, long>? onProgress,
        List<RebuildPhaseMetrics>? phases)
    {
        _indexState = duringState;
        StoreStateFile.Write(_baseDirectory, _indexState);

        var heldBefore = target.QuadCount;
        target.Clear();

        // Secondary-index construction is a bulk-shape operation: each AllocatePage
        // would otherwise trigger a full-region msync via SaveMetadata (the same
        // 1.7.15 bug, hidden behind a different code path). Borrow the bulk-mode
        // msync-deferral semantics for the duration of the rebuild, then Flush once.
        target.SetDeferMsync(true);
        long count = 0;
        var phaseStopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var gspo = _gspoIndex!.QueryEveryEntryAllGraphs();
            while (gspo.MoveNext())
            {
                var (primary, secondary, tertiary) = fromGspo(gspo.Primary, gspo.Secondary, gspo.Tertiary);
                target.CarryEntry(gspo.Graph, primary, secondary, tertiary, gspo.MemorizedAt, gspo.ForgottenAt,
                    gspo.LegacyTransactionTime, gspo.LegacyModifiedAt, gspo.LegacyIsDeleted);
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

        // Clear started the index's count over at transaction 0, as if the log were emptied with it, and the log is
        // not. The index now holds what GSPO holds, so its count is exact up to the transaction GSPO's is: without
        // this, recovery after a crash would count again what the log holds since the last checkpoint (ADR-058).
        target.CommitEntryCount(_gspoIndex!.CountTxId, durable: true);

        _logger.Info($"Rebuilt {name} from GSPO: it held {heldBefore:N0} entries, each of them in GSPO, and holds {count:N0}.".AsSpan());
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
    /// Rebuild the secondary indexes of a Graph store, GPOS and GOSP, from GSPO (ADR-064 D6), then the trigram posting
    /// list from GSPO's asserted literal objects. State transitions PrimaryOnly → BuildingGPOS → BuildingGOSP →
    /// BuildingTrigram → Ready.
    /// </summary>
    /// <remarks>
    /// Inserts one entry at a time rather than appending a sorted run. AppendSorted would require a sorted scan via an
    /// external sorter (the Reference profile's ADR-032 pattern), and Graph workloads aren't aimed at Wikidata-scale
    /// where the radix-external-sort win becomes load-bearing. If a Graph workload surfaces scale past what memory
    /// holds, follow the ADR-032 pattern as a sibling round.
    /// </remarks>
    private void RebuildGraphSecondaryIndexes(Action<string, long>? onProgress)
    {
        AcquireWriteLock();
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var phases = HasRebuildListener ? new List<RebuildPhaseMetrics>(3) : null;
        var started = false;
        try
        {
            // A rebuild replaces GPOS and GOSP with what GSPO holds (ADR-064 G6). First it looks for what that would
            // discard: an entry of either that GSPO does not hold as it is, at its version. Resolving such a
            // difference needs a rule, and the rebuild has none, so it then changes nothing and says how many it
            // found (G8). An emptied or partly built index holds nothing GSPO lacks.
            var gposOnly = CountGraphEntriesGspoDoesNotHold(_gposGraph!, static (p, o, s) => (s, p, o));
            var gospOnly = CountGraphEntriesGspoDoesNotHold(_gospGraph!, static (o, s, p) => (s, p, o));
            if (gposOnly + gospOnly > 0)
            {
                var refusal = $"The rebuild changed nothing: GPOS holds {gposOnly:N0} and GOSP {gospOnly:N0} entries " +
                    "that GSPO does not hold as they are. A rebuild derives these indexes from GSPO and would discard " +
                    "them; a difference between the indexes needs a rule of its own (ADR-064 G8).";
                _logger.Warning(refusal.AsSpan());
                throw new InvalidOperationException(refusal);
            }

            // ADR-031 Piece 2: the secondaries are now different — Dispose runs CheckpointInternal for the statistics.
            _sessionMutated = true;
            started = true;

            RebuildGraphIndex(_gposGraph!, "GPOS", StoreIndexState.BuildingGPOS,
                static (s, p, o) => (p, o, s), onProgress, phases);

            RebuildGraphIndex(_gospGraph!, "GOSP", StoreIndexState.BuildingGOSP,
                static (s, p, o) => (o, s, p), onProgress, phases);

            // Trigram rebuild from the primary GSPO scan: the literal objects of the facts that are asserted.
            _indexState = StoreIndexState.BuildingTrigram;
            StoreStateFile.Write(_baseDirectory, _indexState);
            _trigramIndex.Clear();
            var trigramStopwatch = System.Diagnostics.Stopwatch.StartNew();
            long trigramCount = 0;
            var gspo = _gspoGraph!.Query(-1, -1, -1, -1);
            while (gspo.MoveNext())
            {
                var objectId = gspo.Tertiary; // GSPO: Tertiary = object
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
            ReleaseWriteLock();

            if (HasRebuildListener && started)
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
    /// The number of entries of a Graph secondary index that GSPO does not hold as they are, at their version
    /// (ADR-064 G8). <paramref name="toGspo"/> takes an entry's three dimensions in the index's order and gives
    /// subject, predicate and object. Every fact held is read, retracted ones too, in every graph.
    /// </summary>
    private long CountGraphEntriesGspoDoesNotHold(VersionedQuadIndex target,
        Func<long, long, long, (long Subject, long Predicate, long Object)> toGspo)
    {
        long count = 0;
        var entry = target.QueryHeld(-1, -1, -1, -1);
        while (entry.MoveNext())
        {
            var (s, p, o) = toGspo(entry.Primary, entry.Secondary, entry.Tertiary);
            var inGspo = _gspoGraph!.QueryHeld(entry.Graph, s, p, o);
            if (!inGspo.MoveNext() || inGspo.Version != entry.Version)
                count++;
        }
        return count;
    }

    /// <summary>
    /// Rebuild one Graph secondary index from GSPO: empty it, then carry every fact GSPO holds, of every graph and
    /// retracted ones too, at its version (ADR-064 D6, G6). <paramref name="fromGspo"/> takes subject, predicate and
    /// object and gives the index's three dimensions. Borrows the deferred-msync pattern: a rebuild is a bulk-shape
    /// operation, and a per-page msync would dominate wall-clock for the same reason it does in Cognitive.
    /// </summary>
    private void RebuildGraphIndex(VersionedQuadIndex target, string name, StoreIndexState duringState,
        Func<long, long, long, (long Primary, long Secondary, long Tertiary)> fromGspo,
        Action<string, long>? onProgress,
        List<RebuildPhaseMetrics>? phases)
    {
        _indexState = duringState;
        StoreStateFile.Write(_baseDirectory, _indexState);

        var heldBefore = target.QuadCount;
        target.Clear();
        target.SetDeferMsync(true);
        long count = 0;
        var phaseStopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var gspo = _gspoGraph!.QueryHeld(-1, -1, -1, -1);
            while (gspo.MoveNext())
            {
                var (primary, secondary, tertiary) = fromGspo(gspo.Primary, gspo.Secondary, gspo.Tertiary);
                target.CarryEntry(gspo.Graph, primary, secondary, tertiary, gspo.Version);
                count++;
            }
        }
        finally
        {
            target.Flush();
            target.SetDeferMsync(false);
            phaseStopwatch.Stop();
        }

        // Clear started the index's count over at transaction 0, as if the log were emptied with it, and the log is
        // not. The index now holds what GSPO holds, so its count is exact up to the transaction GSPO's is: without
        // this, recovery after a crash would count again what the log holds since the last checkpoint (ADR-058).
        target.CommitEntryCount(_gspoGraph!.CountTxId, durable: true);

        _logger.Info($"Rebuilt {name} from GSPO: it held {heldBefore:N0} entries, each of them in GSPO, and holds {count:N0}.".AsSpan());
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
        AcquireWriteLock();
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
                var gspo = _gspoReference!.Query(-1, -1, -1, -1);
                while (gspo.MoveNext())
                {
                    // GSPO layout: Primary=Subject, Secondary=Predicate, Tertiary=Object.
                    // GPOS layout: Primary=Predicate, Secondary=Object, Tertiary=Subject.
                    var gposKey = new ReferenceQuadIndex.ReferenceKey
                    {
                        Graph = gspo.Graph,
                        Primary = gspo.Secondary,
                        Secondary = gspo.Tertiary,
                        Tertiary = gspo.Primary,
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
                var gspo = _gspoReference.Query(-1, -1, -1, -1);
                while (gspo.MoveNext())
                {
                    var objectId = gspo.Tertiary;
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
            ReleaseWriteLock();

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
            throw new ObjectDisposedException(null, "The store is closed.");
    }

    /// <summary>
    /// ADR-061 M7: a store that could not apply a write its log has committed stops. Reads and writes call this with
    /// the lock held, so a caller that waited for the lock while the write failed is refused too.
    /// </summary>
    private void ThrowIfStoppedOverUnappliedWrite()
    {
        if (_unappliedWriteCause is { } cause)
            throw new CommittedWriteNotAppliedException(_baseDirectory, cause);
    }

    /// <summary>
    /// ADR-061 M7: applying a record threw after the log had committed it. Stops the store and returns the exception
    /// for the caller to throw. No kind of cause is told from another, and nothing is tried again: what would fail
    /// again fails when the store is opened, with the log kept.
    /// </summary>
    private CommittedWriteNotAppliedException StopOverUnappliedWrite(Exception cause)
    {
        _unappliedWriteCause = cause;
        var stopped = new CommittedWriteNotAppliedException(_baseDirectory, cause);
        _logger.Critical($"{stopped.Message}{Environment.NewLine}{cause}".AsSpan());
        return stopped;
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
    /// Convert DateTimeOffset.UtcTicks to Unix milliseconds. LogRecord stores the memory times
    /// as UtcTicks; TemporalQuadIndex
    /// keys use Unix ms. Cheaper than round-tripping through DateTimeOffset.
    /// </summary>
    private static long UtcTicksToUnixMs(long utcTicks) =>
        (utcTicks - UnixEpochUtcTicks) / TicksPerMillisecond;

    /// <summary>
    /// Apply an add to all indexes using pre-resolved atom IDs (no string round-trip,
    /// no re-intern). Reads memorizedAt/forgottenAt/transactionTime and all four atom IDs
    /// directly from the WAL record. Preconditions: caller has gone through
    /// <see cref="RequireWriteCapableProfile"/> (via Add/Delete/CommitBatch/Recover),
    /// so all four temporal indexes are non-null.
    /// </summary>
    private void ApplyToIndexesById(in LogRecord record)
    {
        // ADR-031 Piece 2: any mutation that reaches the indexes bumps the session-
        // mutated flag so Dispose knows to run CheckpointInternal.
        _sessionMutated = true;

        // A Graph or Minimal store's log holds the decided change, never the operation (ADR-064 D5).
        if (NumbersFactStates)
            throw new InvalidDataException(
                $"WAL record (TxId {record.TxId}) is an Add, which a {_schema.Profile} store does not write: its log holds NextVersion records (ADR-064 D5).");

        // Cognitive profile path.
        // LogRecord stores the memory times as .UtcTicks (100-ns since year 1); the B+Tree
        // keys are Unix milliseconds. Convert once per record instead of per index.
        var memorizedAtMs = UtcTicksToUnixMs(record.MemorizedAtTicks);
        var forgottenAtMs = UtcTicksToUnixMs(record.ForgottenAtTicks);

        AddToTemporalIndex(_gspoIndex!, record, record.SubjectId, record.PredicateId, record.ObjectId, memorizedAtMs, forgottenAtMs);

        if (!_bulkLoadMode)
        {
            AddToTemporalIndex(_gposIndex!, record, record.PredicateId, record.ObjectId, record.SubjectId, memorizedAtMs, forgottenAtMs);
            AddToTemporalIndex(_gospIndex!, record, record.ObjectId, record.SubjectId, record.PredicateId, memorizedAtMs, forgottenAtMs);
            AddToTemporalIndex(_tgspIndex!, record, record.SubjectId, record.PredicateId, record.ObjectId, memorizedAtMs, forgottenAtMs);

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
    /// Add one WAL record to one Cognitive index. During recovery, an entry the index already
    /// holds from a transaction after its saved count (<see cref="TemporalQuadIndex.CountTxId"/>)
    /// was inserted by the crashed process but never counted: count it, once (ADR-058).
    /// </summary>
    private void AddToTemporalIndex(TemporalQuadIndex index, in LogRecord record,
        long primary, long secondary, long tertiary, long memorizedAtMs, long forgottenAtMs)
    {
        var outcome = index.AddRaw(record.GraphId, primary, secondary, tertiary,
            memorizedAtMs, forgottenAtMs, record.TransactionTimeTicks);

        if (_recovering && outcome == TemporalQuadIndex.InsertOutcome.AlreadyPresent && record.TxId > index.CountTxId)
        {
            _countedDuringRecovery ??= new HashSet<(TemporalQuadIndex, TemporalKeyIds)>();
            var key = new TemporalKeyIds(record.GraphId, primary, secondary, tertiary, memorizedAtMs, record.TransactionTimeTicks);
            if (_countedDuringRecovery.Add((index, key)))
                index.CountRecoveredEntry();
        }
    }

    /// <summary>The identity of a Cognitive index entry for <see cref="AddToTemporalIndex"/>.</summary>
    private readonly record struct TemporalKeyIds(long Graph, long Primary, long Secondary, long Tertiary, long MemorizedAt, long LegacyTransactionTime);

    /// <summary>
    /// Make the atoms interned so far durable before a WAL commit that references them
    /// (ADR-058 D6). A WAL record carries atom IDs, not strings. Bulk mode defers this to
    /// <see cref="FlushToDisk"/>.
    /// </summary>
    private void MakeAtomsDurable()
    {
        if (!_bulkLoadMode)
            _atoms.Flush();
    }

    /// <summary>
    /// Save every WAL-profile index's entry count as exact up to <paramref name="txId"/> (ADR-058).
    /// After each transaction the save is not msynced (<paramref name="durable"/> false): a
    /// killed process keeps it, and after a power loss recovery recounts from the older header
    /// it finds. The checkpoint and the end of bulk load save durably, before the log they rely
    /// on can be truncated.
    /// </summary>
    private void CommitIndexCounts(long txId, bool durable)
    {
        _gspoIndex?.CommitEntryCount(txId, durable);
        _gposIndex?.CommitEntryCount(txId, durable);
        _gospIndex?.CommitEntryCount(txId, durable);
        _tgspIndex?.CommitEntryCount(txId, durable);
        _gspoGraph?.CommitEntryCount(txId, durable);
        _gposGraph?.CommitEntryCount(txId, durable);
        _gospGraph?.CommitEntryCount(txId, durable);
        _gspoMinimal?.CommitEntryCount(txId, durable);
    }

    /// <summary>
    /// Apply one WAL record to the indexes (batch commit and recovery share this). Returns whether anything changed.
    /// A record with an operation this Mercury doesn't know throws instead of being skipped, so a log written by a
    /// newer Mercury fails loudly here rather than losing its writes (ADR-054 D8).
    /// </summary>
    private bool ApplyRecordToIndexesById(in LogRecord record)
    {
        switch (record.Operation)
        {
            case LogOperation.Add:
                ApplyToIndexesById(record);
                return true;
            case LogOperation.Retract:
                return ApplyRetractToIndexesById(record);
            case LogOperation.End:
                return ApplyEndToIndexesById(record);
            case LogOperation.BeginPeriod:
            case LogOperation.EndPeriod:
            case LogOperation.RemovePeriod:
            case LogOperation.ContinuePeriod:
                return ApplyPeriodChangeToIndexesById(record);
            case LogOperation.NextVersion:
                return ApplyNextVersionToIndexesById(record);
            default:
                throw new InvalidDataException(
                    $"WAL record (TxId {record.TxId}) has operation {(byte)record.Operation}, which this Mercury does not know. " +
                    "It was written by a newer Mercury; open the store with that version.");
        }
    }

    /// <summary>
    /// Apply one of the four decided changes to a period (ADR-061 M7), in every index. Each index finds the period by
    /// the quad and its memorized time, which both sort orders can do, and applies the change only in the state the
    /// change names. So an index that already holds it, or that holds a later state, is left as it is.
    /// </summary>
    private bool ApplyPeriodChangeToIndexesById(in LogRecord record)
    {
        // ADR-031 Piece 2: a change to a period is a mutation — track it.
        _sessionMutated = true;

        var memorizedAtMs = UtcTicksToUnixMs(record.MemorizedAtTicks);
        var forgottenAtMs = UtcTicksToUnixMs(record.ForgottenAtTicks);

        var changed = ApplyPeriodChange(_gspoIndex!, record, record.SubjectId, record.PredicateId, record.ObjectId, memorizedAtMs, forgottenAtMs);

        if (!_bulkLoadMode)
        {
            changed |= ApplyPeriodChange(_gposIndex!, record, record.PredicateId, record.ObjectId, record.SubjectId, memorizedAtMs, forgottenAtMs);
            changed |= ApplyPeriodChange(_gospIndex!, record, record.ObjectId, record.SubjectId, record.PredicateId, memorizedAtMs, forgottenAtMs);
            changed |= ApplyPeriodChange(_tgspIndex!, record, record.SubjectId, record.PredicateId, record.ObjectId, memorizedAtMs, forgottenAtMs);

            // A period that begins makes its literal object searchable, as an Add does.
            if (record.Operation == LogOperation.BeginPeriod)
            {
                var utf8Span = _atoms.GetAtomSpan(record.ObjectId);
                if (utf8Span.Length > 0 && utf8Span[0] == (byte)'"')
                    _trigramIndex.IndexAtom(record.ObjectId, utf8Span);
            }
        }

        return changed;
    }

    /// <summary>
    /// Apply one decided change to one Cognitive index, and keep the index's entry count right during recovery.
    /// A decided change always took effect when it was first applied. So in recovery a change of a transaction
    /// after the index's saved count (<see cref="TemporalQuadIndex.CountTxId"/>) counts by what it means, one more
    /// entry for a period that begins and one fewer for a period that is removed, whatever the page already held;
    /// a change at or before the saved count is in that count already, whatever it does to the page now (ADR-058).
    /// </summary>
    private bool ApplyPeriodChange(TemporalQuadIndex index, in LogRecord record,
        long primary, long secondary, long tertiary, long memorizedAtMs, long forgottenAtMs)
    {
        bool changed;
        int entriesAdded;
        switch (record.Operation)
        {
            case LogOperation.BeginPeriod:
                changed = index.BeginPeriod(record.GraphId, primary, secondary, tertiary, memorizedAtMs);
                entriesAdded = 1;
                break;
            case LogOperation.RemovePeriod:
                changed = index.RemovePeriod(record.GraphId, primary, secondary, tertiary, memorizedAtMs);
                entriesAdded = -1;
                break;
            case LogOperation.EndPeriod:
                changed = index.EndPeriod(record.GraphId, primary, secondary, tertiary, memorizedAtMs, forgottenAtMs);
                entriesAdded = 0;
                break;
            default:
                changed = index.ContinuePeriod(record.GraphId, primary, secondary, tertiary, memorizedAtMs, forgottenAtMs);
                entriesAdded = 0;
                break;
        }

        if (_recovering && entriesAdded != 0)
        {
            var counted = changed ? entriesAdded : 0;
            var owed = record.TxId > index.CountTxId ? entriesAdded : 0;
            index.AdjustRecoveredEntryCount(owed - counted);
        }

        return changed;
    }

    /// <summary>
    /// Apply a NextVersion record to every index of a Graph or Minimal store (ADR-064 M5). Each index changes only when
    /// it holds the fact at the record's version, so an index that already holds the change, or a later one, is left
    /// as it is. On a Graph store, a fact that becomes asserted makes its literal object searchable, as an Add did;
    /// Minimal has no trigram index (D2).
    /// </summary>
    private bool ApplyNextVersionToIndexesById(in LogRecord record)
    {
        if (!NumbersFactStates)
            throw new InvalidDataException($"WAL record (TxId {record.TxId}) is a NextVersion, which only a Graph or Minimal store writes (ADR-064 D5).");

        // ADR-031 Piece 2: a change of a fact's state is a mutation — track it.
        _sessionMutated = true;

        var fromVersion = checked((int)record.FromVersion);
        if (_schema.Profile == StoreProfile.Minimal)
        {
            var changedInGspo = _gspoMinimal!.NextVersion(record.SubjectId, record.PredicateId, record.ObjectId, fromVersion);
            if (_recovering && fromVersion == 0)
                _gspoMinimal.AdjustRecoveredEntryCount(FirstAssertCountOwed(record, _gspoMinimal.CountTxId, changedInGspo));
            return changedInGspo;
        }

        var changed = ApplyNextVersion(_gspoGraph!, record, record.SubjectId, record.PredicateId, record.ObjectId, fromVersion);

        if (!_bulkLoadMode)
        {
            changed |= ApplyNextVersion(_gposGraph!, record, record.PredicateId, record.ObjectId, record.SubjectId, fromVersion);
            changed |= ApplyNextVersion(_gospGraph!, record, record.ObjectId, record.SubjectId, record.PredicateId, fromVersion);

            // The next version is odd: the fact is asserted.
            if (fromVersion % 2 == 0)
            {
                var utf8Span = _atoms.GetAtomSpan(record.ObjectId);
                if (utf8Span.Length > 0 && utf8Span[0] == (byte)'"')
                    _trigramIndex.IndexAtom(record.ObjectId, utf8Span);
            }
        }

        return changed;
    }

    /// <summary>
    /// Apply one NextVersion record to one Graph index, and keep the index's entry count right during recovery
    /// (<see cref="FirstAssertCountOwed"/>).
    /// </summary>
    private bool ApplyNextVersion(VersionedQuadIndex index, in LogRecord record,
        long primary, long secondary, long tertiary, int fromVersion)
    {
        var changed = index.NextVersion(record.GraphId, primary, secondary, tertiary, fromVersion);
        if (_recovering && fromVersion == 0)
            index.AdjustRecoveredEntryCount(FirstAssertCountOwed(record, index.CountTxId, changed));
        return changed;
    }

    /// <summary>
    /// What a fact's first assert, the one change that adds an entry, still owes an index's entry count when recovery
    /// replays it (ADR-058). A decided change always took effect when it was first applied, so it counts by the
    /// transaction it belongs to, whatever the page already held: one entry after the index's saved count
    /// (<paramref name="countTxId"/>), none at or before it, less the one the replay itself added.
    /// </summary>
    private static int FirstAssertCountOwed(in LogRecord record, long countTxId, bool addedByTheReplay) =>
        (record.TxId > countTxId ? 1 : 0) - (addedByTheReplay ? 1 : 0);

    /// <summary>
    /// Apply a Retract record (Cognitive): the version with exactly the record's interval is marked LegacyIsDeleted
    /// (ADR-054 D2); a record written before ADR-054 by DeleteCurrent carries [MinValue, MaxValue] and is replayed with
    /// the meaning it had then (mark the first overlapping version). Nothing writes a Retract record since ADR-061
    /// Step 6; a log written before it is replayed as it was written.
    /// </summary>
    private bool ApplyRetractToIndexesById(in LogRecord record)
    {
        // A Graph or Minimal store's log holds the decided change, never the operation (ADR-064 D5).
        if (NumbersFactStates)
            throw new InvalidDataException(
                $"WAL record (TxId {record.TxId}) is a Retract, which a {_schema.Profile} store does not write: its log holds NextVersion records (ADR-064 D5).");

        // ADR-031 Piece 2: a retract is a mutation — track it.
        _sessionMutated = true;

        var memorizedAtMs = UtcTicksToUnixMs(record.MemorizedAtTicks);
        var forgottenAtMs = UtcTicksToUnixMs(record.ForgottenAtTicks);
        var tt = record.TransactionTimeTicks;

        if (record.MemorizedAtTicks == DateTimeOffset.MinValue.UtcTicks && record.ForgottenAtTicks == DateTimeOffset.MaxValue.UtcTicks)
        {
            var o1 = _gspoIndex!.ReplayPreAdr054Delete(record.GraphId, record.SubjectId, record.PredicateId, record.ObjectId, memorizedAtMs, forgottenAtMs, tt);
            var o2 = _gposIndex!.ReplayPreAdr054Delete(record.GraphId, record.PredicateId, record.ObjectId, record.SubjectId, memorizedAtMs, forgottenAtMs, tt);
            var o3 = _gospIndex!.ReplayPreAdr054Delete(record.GraphId, record.ObjectId, record.SubjectId, record.PredicateId, memorizedAtMs, forgottenAtMs, tt);
            var o4 = _tgspIndex!.ReplayPreAdr054Delete(record.GraphId, record.SubjectId, record.PredicateId, record.ObjectId, memorizedAtMs, forgottenAtMs, tt);
            return o1 || o2 || o3 || o4;
        }

        var r1 = _gspoIndex!.RetractRaw(record.GraphId, record.SubjectId, record.PredicateId, record.ObjectId, memorizedAtMs, forgottenAtMs, tt);
        var r2 = _gposIndex!.RetractRaw(record.GraphId, record.PredicateId, record.ObjectId, record.SubjectId, memorizedAtMs, forgottenAtMs, tt);
        var r3 = _gospIndex!.RetractRaw(record.GraphId, record.ObjectId, record.SubjectId, record.PredicateId, memorizedAtMs, forgottenAtMs, tt);
        var r4 = _tgspIndex!.RetractRaw(record.GraphId, record.SubjectId, record.PredicateId, record.ObjectId, memorizedAtMs, forgottenAtMs, tt);
        return r1 || r2 || r3 || r4;
    }

    /// <summary>
    /// Apply an End record (Cognitive only): the version whose MemorizedAt is the record's MemorizedAt stops holding at the
    /// record's ForgottenAt, in all four indexes (ADR-054 D1). Applying it again is a no-op.
    /// </summary>
    private bool ApplyEndToIndexesById(in LogRecord record)
    {
        if (NumbersFactStates)
            throw new InvalidDataException($"WAL record (TxId {record.TxId}) is an End, which a {_schema.Profile} store (no times) never writes.");

        _sessionMutated = true;
        var versionFromMs = UtcTicksToUnixMs(record.MemorizedAtTicks);
        var endMs = UtcTicksToUnixMs(record.ForgottenAtTicks);
        var tt = record.TransactionTimeTicks;

        var e1 = _gspoIndex!.EndRaw(record.GraphId, record.SubjectId, record.PredicateId, record.ObjectId, versionFromMs, endMs, tt);
        var e2 = _gposIndex!.EndRaw(record.GraphId, record.PredicateId, record.ObjectId, record.SubjectId, versionFromMs, endMs, tt);
        var e3 = _gospIndex!.EndRaw(record.GraphId, record.ObjectId, record.SubjectId, record.PredicateId, versionFromMs, endMs, tt);
        var e4 = _tgspIndex!.EndRaw(record.GraphId, record.SubjectId, record.PredicateId, record.ObjectId, versionFromMs, endMs, tt);
        return e1 || e2 || e3 || e4;
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

        _recovering = true;
        try
        {
            while (enumerator.MoveNext())
            {
                var record = enumerator.Current;
                ApplyRecordToIndexesById(record);
                recoveredCount++;
            }
        }
        catch (Exception cause)
        {
            // ADR-061 M7: if recovery fails, the store does not open, and the log is kept as it is.
            throw StopOverUnappliedWrite(cause);
        }
        finally
        {
            _recovering = false;
            _countedDuringRecovery = null;
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
        AcquireWriteLock();
        try
        {
            ThrowIfStoppedOverUnappliedWrite();
            CheckpointInternal();
        }
        finally
        {
            ReleaseWriteLock();
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

        // ADR-058 D1/D2: the store must be whole without the log before the log is truncated.
        // Atoms and index counts are already saved at each commit; saving them here again
        // covers recovery's replay and any path that did not. An exception here leaves the
        // WAL as it is, and the next open replays it.
        _atoms.Flush();
        CommitIndexCounts(_wal.CurrentTxId, durable: true);

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

        // A Minimal store has no GPOS to scan, and collected no statistics before it had a log (ADR-064 D2).
        if (_schema.Profile == StoreProfile.Minimal)
            return;

        // ADR-029 Graph profile: scan via the VersionedQuadIndex GPOS instead of
        // TemporalQuadIndex. Same Predicate→Object→Subject mapping; no temporal
        // bounds because Graph has no time dimension.
        if (_schema.Profile == StoreProfile.Graph)
        {
            var gpos = _gposGraph!.Query(-1, -1, -1, -1);
            while (gpos.MoveNext())
            {
                var predicateAtom = gpos.Primary;
                var objectAtom = gpos.Secondary;
                var subjectAtom = gpos.Tertiary;

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
            // Cognitive profile: scan TemporalQuadIndex GPOS for what is remembered. It reads no clock (ADR-061 D7).
            var gpos = _gposIndex!.QueryCurrent(
                ReadOnlySpan<char>.Empty,  // All predicates
                ReadOnlySpan<char>.Empty,  // All objects (Secondary in GPOS)
                ReadOnlySpan<char>.Empty); // All subjects (Tertiary in GPOS)

            while (gpos.MoveNext())
            {
                // In GPOS index: Primary=predicate, Secondary=object, Tertiary=subject
                var predicateAtom = gpos.Primary;
                var objectAtom = gpos.Secondary;
                var subjectAtom = gpos.Tertiary;

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
    /// What is remembered (ADR-061 D7), on every profile: each matching quad and nothing else (ADR-062 D1). On a
    /// Cognitive store, every period that has no forgotten time; it reads no clock. On a Graph or Minimal store, what is
    /// asserted; on a Reference store, what it holds. Empty graph means default graph.
    ///
    /// Note: For thread-safety with concurrent writes, either:
    /// 1. Use AcquireReadLock/ReleaseReadLock around the query and enumeration, or
    /// 2. Ensure no writes occur during enumeration
    /// </summary>
    public ResultEnumerator QueryCurrent(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        ReadOnlySpan<char> graph = default)
    {
        if (_schema.Profile == StoreProfile.Reference)
            return QueryReferenceCurrent(subject, predicate, @object, graph);
        if (_schema.Profile == StoreProfile.Graph)
            return QueryGraphCurrent(subject, predicate, @object, graph);
        if (_schema.Profile == StoreProfile.Minimal)
            return QueryMinimalCurrent(subject, predicate, @object, graph);
        return QueryCognitive(subject, predicate, @object, graph, TemporalQuery.Remembered);
    }

    /// <summary>
    /// The memories of the matching quads, each with the two times of its period (ADR-062 D2): the store read of the
    /// words of Cognitive SPARQL (ADR-061 D6, D10), with the bounds a pattern's words give.
    /// <list type="bullet">
    /// <item>The periods memorized in <c>[memorizedSince, memorizedBefore)</c>.</item>
    /// <item>With a bound on the forgotten time, the periods forgotten in <c>[forgottenSince, forgottenBefore)</c>, so
    /// none that is remembered.</item>
    /// <item>Without one, the periods that are remembered, and with <paramref name="includingForgotten"/> the forgotten
    /// ones too: <c>FORGOTTEN</c> with no bound, or <c>INCLUDING FORGOTTEN</c>.</item>
    /// </list>
    /// An absent bound is null. A bound between two milliseconds is taken at the next one, as the words take it: every
    /// stored time is a whole millisecond. Only a Cognitive store keeps times (ADR-061 D9): on any other this throws
    /// <see cref="ProfileCapabilityException"/>. Empty graph means default graph.
    /// </summary>
    public TemporalResultEnumerator QueryMemories(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        ReadOnlySpan<char> graph = default,
        DateTimeOffset? memorizedSince = null,
        DateTimeOffset? memorizedBefore = null,
        DateTimeOffset? forgottenSince = null,
        DateTimeOffset? forgottenBefore = null,
        bool includingForgotten = false)
    {
        var times = TemporalQuery.Words(
            memorizedSince is { } a ? TemporalQuery.Bound(a.UtcTicks) : long.MinValue,
            memorizedBefore is { } b ? TemporalQuery.Bound(b.UtcTicks) : long.MaxValue,
            forgottenSince is { } c ? TemporalQuery.Bound(c.UtcTicks) : null,
            forgottenBefore is { } d ? TemporalQuery.Bound(d.UtcTicks) : null,
            forgottenToo: includingForgotten);
        return new TemporalResultEnumerator(QueryByMemoryTimes(subject, predicate, @object, graph, times));
    }

    /// <summary>
    /// The quads whose periods <paramref name="times"/> selects: the read under the words of Cognitive SPARQL. The two
    /// times of each period are read from the enumerator's position (ADR-062 D2). Refused on a store that keeps no
    /// times (ADR-061 D9). Empty graph means default graph. Caller must hold read lock.
    /// </summary>
    internal ResultEnumerator QueryByMemoryTimes(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        ReadOnlySpan<char> graph,
        TemporalQuery times)
    {
        RequireTemporalProfile(nameof(QueryMemories));
        return QueryCognitive(subject, predicate, @object, graph, times);
    }

    /// <summary>
    /// Every period the TGSP index holds in <paramref name="graph"/>, remembered or forgotten, with its two times. No
    /// read of the store chooses TGSP: its key leads with the time, and on the live store it differs from GSPO
    /// (ADR-061 LQ5). The store still writes and rebuilds it, and this read is how the store's tests check that it
    /// holds what the other three hold (ADR-061 I13). Empty graph means default graph. Caller must hold read lock.
    /// </summary>
    internal TemporalResultEnumerator QueryTimeFirstIndex(ReadOnlySpan<char> graph = default)
    {
        RequireTemporalProfile(nameof(QueryTimeFirstIndex));
        var times = TemporalQuery.Words(long.MinValue, long.MaxValue, null, null, forgottenToo: true);
        return new TemporalResultEnumerator(new ResultEnumerator(
            _tgspIndex!.Query(ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty, times, graph),
            TermOrder.SPO, _atoms));
    }

    /// <summary>
    /// A Cognitive read: the index chosen by the bound terms, read with the time selection <paramref name="times"/>.
    /// </summary>
    private ResultEnumerator QueryCognitive(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        ReadOnlySpan<char> graph,
        TemporalQuery times)
    {
        var (selectedIndex, order) = SelectOptimalIndex(subject, predicate, @object);

        // Map RDF terms to index dimensions:
        // SPO (GSPO): S→Primary, P→Secondary, O→Tertiary
        // POS (GPOS): P→Primary, O→Secondary, S→Tertiary
        // OSP (GOSP): O→Primary, S→Secondary, P→Tertiary
        ReadOnlySpan<char> arg1, arg2, arg3;
        switch (order)
        {
            case TermOrder.POS:
                arg1 = predicate;
                arg2 = @object;
                arg3 = subject;
                break;
            case TermOrder.OSP:
                arg1 = @object;
                arg2 = subject;
                arg3 = predicate;
                break;
            default:
                arg1 = subject;
                arg2 = predicate;
                arg3 = @object;
                break;
        }

        return new ResultEnumerator(selectedIndex.Query(arg1, arg2, arg3, times, graph), order, _atoms);
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
    internal ResultEnumerator QueryCurrentWithCandidates(
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
        var (selectedIndex, order) = SelectOptimalIndex(subject, predicate, @object);

        ReadOnlySpan<char> arg1, arg2, arg3;
        switch (order)
        {
            case TermOrder.POS:
                arg1 = predicate; arg2 = @object; arg3 = subject;
                break;
            case TermOrder.OSP:
                arg1 = @object; arg2 = subject; arg3 = predicate;
                break;
            default:
                arg1 = subject; arg2 = predicate; arg3 = @object;
                break;
        }

        var enumerator = selectedIndex.QueryCurrent(arg1, arg2, arg3, graph);
        return new ResultEnumerator(enumerator, order, _atoms, candidateObjectAtomIds);
    }

    /// <summary>
    /// The named graphs that hold a period, remembered or forgotten (ADR-061 D10, Q15): what a graph variable ranges
    /// over when a query asks for forgotten memories too. A graph in which everything is forgotten is in this
    /// listing and not in <see cref="GetNamedGraphs"/>. A store without times has no forgotten memories, so there the
    /// two are the same. Default graph (empty) is not included. Caller must hold read lock.
    /// </summary>
    internal NamedGraphEnumerator GetNamedGraphsHoldingAPeriod()
    {
        if (_gspoIndex is not null)
            return new NamedGraphEnumerator(_gspoIndex.QueryEveryEntryAllGraphs(), _atoms);
        return GetNamedGraphs();
    }

    /// <summary>
    /// Get all named graph IRIs in the store.
    /// Returns graph IRIs as strings. Default graph (empty) is not included.
    /// Caller must hold read lock.
    /// </summary>
    public NamedGraphEnumerator GetNamedGraphs()
    {
        // Named-graph enumeration needs a graph-aware GSPO index, NOT temporal semantics — the enumerator
        // walks GSPO graph-first and dedups by the leading graph atom; the temporal dimension is irrelevant.
        // Dispatch to whichever B+Tree GSPO the profile built. (Dogfood fix, 2026-06-28: a Graph-profile store
        // could not enumerate its own named graphs because the path was only ever wired for the temporal
        // index — surfaced by DrHook.Capture.)
        if (_gspoIndex is not null)                                                         // Cognitive (temporal)
            return new NamedGraphEnumerator(_gspoIndex.QueryRememberedAllGraphs(), _atoms);
        if (_gspoGraph is not null) return new NamedGraphEnumerator(_gspoGraph.Query(-1, -1, -1, -1), _atoms);   // Graph: what is asserted now
        if (_gspoMinimal is not null) return new NamedGraphEnumerator(_atoms);             // Minimal: single-graph → no named graphs
        throw new ProfileCapabilityException(
            $"GetNamedGraphs is not yet implemented for the {_schema.Profile} profile. The enumerator-union " +
            "covers Cognitive (temporal) and Graph (versioned); a Reference branch is planned.");
    }

    /// <summary>
    /// Resolve a SPARQL term (IRI, literal, or variable) to an atom ID for Reference-profile
    /// queries. Empty spans and variables become -1 (wildcard); IRIs/literals are
    /// looked up in the IAtomStore (returning 0 if not present, which matches nothing).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ResolveQueryTerm(ReadOnlySpan<char> term)
    {
        if (term.IsEmpty || VariableName.IsSigil(term[0]))
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
        if (graph.IsEmpty || VariableName.IsSigil(graph[0]))
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
    private (ReferenceQuadIndex Index, TermOrder Order) SelectOptimalReferenceIndex(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object)
    {
        _ = @object; // reserved for future cost-based selection
        var subjectBound = !subject.IsEmpty && !VariableName.IsSigil(subject[0]);
        var predicateBound = !predicate.IsEmpty && !VariableName.IsSigil(predicate[0]);

        if (predicateBound && !subjectBound)
            return (_gposReference!, TermOrder.POS);
        return (_gspoReference!, TermOrder.SPO);
    }

    /// <summary>
    /// Query entry for Minimal profile. Resolves SPARQL-shaped spans to atom IDs,
    /// rejects any non-empty graph constraint, queries the single GSPO index, and
    /// returns a <see cref="ResultEnumerator"/> over that index's
    /// <see cref="QuadIndexCursor"/> (ADR-062). ADR-029 Minimal profile.
    /// </summary>
    private ResultEnumerator QueryMinimalCurrent(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        ReadOnlySpan<char> graph,
        HashSet<long>? candidateObjectAtomIds = null)
    {
        if (!graph.IsEmpty && !VariableName.IsSigil(graph[0]))
            throw new ProfileCapabilityException(
                "Minimal profile does not support named-graph queries (hasGraph=false per ADR-029). " +
                "Drop the graph parameter or use Cognitive / Graph / Reference for multi-graph workloads.");

        var primary = ResolveQueryTerm(subject);
        var secondary = ResolveQueryTerm(predicate);
        var tertiary = ResolveQueryTerm(@object);

        var enumerator = _gspoMinimal!.Query(primary, secondary, tertiary);
        return new ResultEnumerator(enumerator, TermOrder.SPO, _atoms, candidateObjectAtomIds);
    }

    /// <summary>
    /// Pick GSPO / GPOS / GOSP for a Graph-profile query: the same selection as Cognitive's.
    /// ADR-029 Graph profile; Graph has no TGSP (ADR-064).
    /// </summary>
    private (VersionedQuadIndex Index, TermOrder Order) SelectOptimalGraphIndex(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object)
    {
        var subjectBound = !subject.IsEmpty && !VariableName.IsSigil(subject[0]);
        var predicateBound = !predicate.IsEmpty && !VariableName.IsSigil(predicate[0]);
        var objectBound = !@object.IsEmpty && !VariableName.IsSigil(@object[0]);

        // When secondary indexes aren't built yet, fall back to GSPO scan.
        if (_indexState != StoreIndexState.Ready)
        {
            var gposAvailable = _indexState != StoreIndexState.PrimaryOnly
                && _indexState != StoreIndexState.BuildingGPOS;
            var gospAvailable = gposAvailable
                && _indexState != StoreIndexState.BuildingGOSP;

            if (subjectBound)
                return (_gspoGraph!, TermOrder.SPO);
            if (predicateBound && gposAvailable)
                return (_gposGraph!, TermOrder.POS);
            if (objectBound && gospAvailable)
                return (_gospGraph!, TermOrder.OSP);
            return (_gspoGraph!, TermOrder.SPO);
        }

        if (subjectBound)
            return (_gspoGraph!, TermOrder.SPO);
        if (predicateBound)
            return (_gposGraph!, TermOrder.POS);
        if (objectBound)
            return (_gospGraph!, TermOrder.OSP);
        return (_gspoGraph!, TermOrder.SPO);
    }

    /// <summary>
    /// Query entry for Graph profile. Mirrors <see cref="QueryReferenceCurrent"/>'s
    /// shape but routes through <see cref="VersionedQuadIndex"/>. Live-only by default
    /// (soft-deleted entries are filtered out by VersionedQuadIndex.Query).
    /// </summary>
    private ResultEnumerator QueryGraphCurrent(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        ReadOnlySpan<char> graph,
        HashSet<long>? candidateObjectAtomIds = null)
    {
        var (gIndex, order) = SelectOptimalGraphIndex(subject, predicate, @object);

        var g = ResolveQueryGraph(graph);
        long primary, secondary, tertiary;
        switch (order)
        {
            case TermOrder.POS:
                primary = ResolveQueryTerm(predicate);
                secondary = ResolveQueryTerm(@object);
                tertiary = ResolveQueryTerm(subject);
                break;
            case TermOrder.OSP:
                primary = ResolveQueryTerm(@object);
                secondary = ResolveQueryTerm(subject);
                tertiary = ResolveQueryTerm(predicate);
                break;
            default: // GSPO
                primary = ResolveQueryTerm(subject);
                secondary = ResolveQueryTerm(predicate);
                tertiary = ResolveQueryTerm(@object);
                break;
        }

        var enumerator = gIndex.Query(g, primary, secondary, tertiary);
        return new ResultEnumerator(enumerator, order, _atoms, candidateObjectAtomIds);
    }

    /// <summary>
    /// Query entry for Reference profile. Resolves SPARQL-shaped spans to atom IDs,
    /// chooses the right index (GSPO or GPOS), and returns a
    /// <see cref="ResultEnumerator"/> over that index's <see cref="QuadIndexCursor"/>
    /// (ADR-062), which remaps the dimensions for uniform consumption by the SPARQL
    /// executor.
    /// </summary>
    private ResultEnumerator QueryReferenceCurrent(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object,
        ReadOnlySpan<char> graph,
        HashSet<long>? candidateObjectAtomIds = null)
    {
        var (refIndex, order) = SelectOptimalReferenceIndex(subject, predicate, @object);

        var g = ResolveQueryGraph(graph);
        long primary, secondary, tertiary;
        switch (order)
        {
            case TermOrder.POS:
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
        return new ResultEnumerator(refEnum, order, _atoms, candidateObjectAtomIds);
    }

    /// <summary>
    /// Pick GSPO, GPOS or GOSP for a Cognitive read, by the bound terms. TGSP is not read: its key leads with the time,
    /// and on the live store it differs from GSPO (ADR-061 LQ5). No read chooses it since the time-range read went with
    /// ADR-061 Step 6.
    /// </summary>
    private (TemporalQuadIndex Index, TermOrder Order) SelectOptimalIndex(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object)
    {
        var subjectBound = !subject.IsEmpty && !VariableName.IsSigil(subject[0]);
        var predicateBound = !predicate.IsEmpty && !VariableName.IsSigil(predicate[0]);
        var objectBound = !@object.IsEmpty && !VariableName.IsSigil(@object[0]);

        // When secondary indexes are not available, fall back to GSPO for everything.
        // Queries still work — just slower (full GSPO scan instead of targeted index).
        if (_indexState != StoreIndexState.Ready)
        {
            // GPOS is available after BuildingGPOS completes
            // GOSP is available after BuildingGOSP completes
            var gposAvailable = _indexState != StoreIndexState.PrimaryOnly
                && _indexState != StoreIndexState.BuildingGPOS;
            var gospAvailable = gposAvailable
                && _indexState != StoreIndexState.BuildingGOSP;

            if (subjectBound)
                return (_gspoIndex!, TermOrder.SPO);
            if (predicateBound && gposAvailable)
                return (_gposIndex!, TermOrder.POS);
            if (objectBound && gospAvailable)
                return (_gospIndex!, TermOrder.OSP);

            return (_gspoIndex!, TermOrder.SPO);
        }

        if (subjectBound)
            return (_gspoIndex!, TermOrder.SPO);
        if (predicateBound)
            return (_gposIndex!, TermOrder.POS);
        if (objectBound)
            return (_gospIndex!, TermOrder.OSP);
        return (_gspoIndex!, TermOrder.SPO);
    }

    /// <summary>
    /// Acquire read lock for thread-safe enumeration.
    /// Must be paired with ReleaseReadLock after enumeration completes.
    /// </summary>
    /// <remarks>
    /// A read waits only for a write that holds the lock, never for one that waits (ADR-066 D1). It finds the store
    /// closed, or stopped over a committed write not applied, after it has taken the lock, and then releases it having
    /// read nothing (D2).
    /// </remarks>
    public void AcquireReadLock()
    {
        _lock.EnterReadLock();
        if (_disposed || _unappliedWriteCause is not null)
        {
            ReleaseReadLock();
            ThrowIfDisposed();
            ThrowIfStoppedOverUnappliedWrite();
        }
    }

    /// <summary>
    /// Release read lock after enumeration.
    /// </summary>
    /// <remarks>
    /// One of the two places the lock is left, with <see cref="ReleaseWriteLock"/>: it leaves the lock, then wakes every
    /// write waiting for it (ADR-066 D1).
    /// </remarks>
    public void ReleaseReadLock()
    {
        _lock.ExitReadLock();
        lock (_writeWait)
            Monitor.PulseAll(_writeWait);
    }

    /// <summary>
    /// The one place the write lock is entered (ADR-066 D1). The write enters when no read and no write holds the lock,
    /// and does not wait inside it: until it enters, it waits on <see cref="_writeWait"/>, woken by every release. The
    /// try and the wait are made under the monitor, so a release between them is not lost: it wakes no one until it
    /// holds the monitor, which the write gives up only by waiting.
    /// </summary>
    private void EnterWriteLock()
    {
        lock (_writeWait)
        {
            while (!_lock.TryEnterWriteLock(0))
            {
                _writesWaiting++;
                try
                {
                    Monitor.Wait(_writeWait);
                }
                finally
                {
                    _writesWaiting--;
                }
            }
        }
    }

    /// <summary>
    /// The write lock for a write: entered when no read is in progress (ADR-066 D1), and the store found closed after
    /// it is entered, in which case it is released and the write fails having changed nothing (D2).
    /// </summary>
    private void AcquireWriteLock()
    {
        EnterWriteLock();
        if (_disposed)
        {
            ReleaseWriteLock();
            ThrowIfDisposed();
        }
    }

    /// <summary>
    /// The other place the lock is left, with <see cref="ReleaseReadLock"/>: it leaves the write lock, then wakes every
    /// write waiting for it (ADR-066 D1).
    /// </summary>
    private void ReleaseWriteLock()
    {
        _lock.ExitWriteLock();
        lock (_writeWait)
            Monitor.PulseAll(_writeWait);
    }

    /// <summary>The writes waiting for the lock, outside it (ADR-066 D1), so that a test waits for a state, not a
    /// time.</summary>
    internal int WritesWaiting
    {
        get
        {
            lock (_writeWait)
                return _writesWaiting;
        }
    }

    /// <summary>The monitor the writes wait on (ADR-066 D1), for a test that holds it to stop a write before its
    /// try.</summary>
    internal object WriteWait => _writeWait;

    /// <remarks>
    /// A close is a write (ADR-066 D2): it waits for the reads in progress, outside the lock, and closes the store under
    /// the write lock, so a read or a write that takes the lock after it finds the store closed. A close that finds the
    /// store already closed returns. The lock itself is left to the collector: a <c>ReaderWriterLockSlim</c> refuses to
    /// be disposed while a thread waits on it.
    /// </remarks>
    public void Dispose()
    {
        EnterWriteLock();
        try
        {
            if (_disposed) return;
            _disposed = true;

            // ADR-033: defensive drain — if a bulk-load session is still holding the sorter
            // (caller forgot FlushToDisk) the buffered tuples must reach GSPO before the
            // index files are closed. The drain is idempotent if FlushToDisk was already
            // called (sorter is null).
            DrainBulkSorterHoldingWriteLock();

            // ADR-031 Piece 2: Dispose's CheckpointInternal is the 14-minute-at-1B dominant
            // cost (CollectPredicateStatistics scans the entire GPOS index). A session that
            // never mutated anything has no new statistics to collect and no WAL checkpoint
            // marker to write — skip unconditionally. For Reference/Minimal profiles the
            // inner `_wal is null` short-circuit makes this redundant but cheap.
            // ADR-061 M7: a store that could not apply a committed write closes without a checkpoint. A checkpoint would
            // truncate the log, which is the only complete copy of that write.
            // The indexes save their entry counts when they close. The counts in memory include what was applied of the
            // write that failed, and no index has saved a count for that transaction, so each count goes back to the one
            // saved with its transaction: recovery then counts the write once (ADR-058).
            if (_unappliedWriteCause is not null)
            {
                _gspoIndex?.ReturnToSavedEntryCount();
                _gposIndex?.ReturnToSavedEntryCount();
                _gospIndex?.ReturnToSavedEntryCount();
                _tgspIndex?.ReturnToSavedEntryCount();
                _gspoGraph?.ReturnToSavedEntryCount();
                _gposGraph?.ReturnToSavedEntryCount();
                _gospGraph?.ReturnToSavedEntryCount();
                _gspoMinimal?.ReturnToSavedEntryCount();
            }
            else if (_sessionMutated)
            {
                CheckpointInternal();
            }

            _wal?.Dispose();
            _gspoIndex?.Dispose();
            _gposIndex?.Dispose();
            _gospIndex?.Dispose();
            _tgspIndex?.Dispose();
            _gspoGraph?.Dispose();
            _gposGraph?.Dispose();
            _gospGraph?.Dispose();
            _gspoReference?.Dispose();
            _gposReference?.Dispose();
            _gspoMinimal?.Dispose();
            _trigramIndex.Dispose();
            _atoms?.Dispose();
        }
        finally
        {
            ReleaseWriteLock();
        }
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
        AcquireWriteLock();
        try
        {
            ThrowIfStoppedOverUnappliedWrite();
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
            ReleaseWriteLock();
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
/// The result of a read on any profile (ADR-062 D1, D3): one <see cref="QuadIndexCursor"/>, moved by the index the
/// read uses, and the remapping of its row to subject, predicate and object. It yields the quad and nothing else.
/// Uses pooled buffers to avoid allocations when decoding atom strings.
///
/// IMPORTANT: The spans returned by Current are only valid until the next
/// MoveNext() call. Do not store references to Subject/Predicate/Object
/// across iterations.
///
/// Call Dispose() when done to return the pooled buffer.
/// Changed from ref struct to struct to enable pooled array storage (ADR-011).
/// </summary>
public struct ResultEnumerator
{
    // One cursor, moved by the index of the store's profile (ADR-062 D3). What follows is the same for every
    // profile and is written once: the candidate filter, the mapping from the index's order to subject, predicate
    // and object, and the decoding of atoms into the buffer.
    private QuadIndexCursor _cursor;

    private readonly TermOrder _order;
    private readonly IAtomStore _atoms;
    private readonly HashSet<long>? _candidateObjectAtomIds;

    // Pooled buffer for zero-allocation string decoding
    private char[]? _buffer;
    private int _bufferOffset;
    private const int InitialBufferSize = 4096; // 4KB - typical for 3 URIs

    /// <param name="cursor">A cursor of the index the read uses, at its start.</param>
    /// <param name="order">Where subject, predicate and object are in a row of that index.</param>
    /// <param name="atoms">The store's atoms.</param>
    /// <param name="candidateObjectAtomIds">When given, only rows whose object is one of these atoms are returned.</param>
    internal ResultEnumerator(
        QuadIndexCursor cursor,
        TermOrder order,
        IAtomStore atoms,
        HashSet<long>? candidateObjectAtomIds = null)
    {
        _cursor = cursor;
        _order = order;
        _atoms = atoms;
        _candidateObjectAtomIds = candidateObjectAtomIds;
        _buffer = null;
        _bufferOffset = 0;
    }

    public bool MoveNext()
    {
        // Reset buffer offset for new result - reuse same buffer
        _bufferOffset = 0;

        if (_candidateObjectAtomIds is null)
            return _cursor.MoveNext();

        // Candidate-filtered iteration: skip non-candidate objects at atom-ID level
        // (before expensive UTF-8 → UTF-16 string decode)
        while (_cursor.MoveNext())
        {
            long objectAtomId = _order switch
            {
                TermOrder.POS => _cursor.Secondary,
                TermOrder.OSP => _cursor.Primary,
                _ => _cursor.Tertiary
            };

            if (_candidateObjectAtomIds.Contains(objectAtomId))
                return true;
        }
        return false;
    }

    public ResolvedQuad Current
    {
        get
        {
            long graph = _cursor.Graph;
            long s, p, o;

            // Remap generic dimensions back to RDF terms by the index's term order
            switch (_order)
            {
                case TermOrder.POS:
                    p = _cursor.Primary;
                    o = _cursor.Secondary;
                    s = _cursor.Tertiary;
                    break;

                case TermOrder.OSP:
                    o = _cursor.Primary;
                    s = _cursor.Secondary;
                    p = _cursor.Tertiary;
                    break;

                default:
                    s = _cursor.Primary;
                    p = _cursor.Secondary;
                    o = _cursor.Tertiary;
                    break;
            }

            var graphTerm = graph == 0 ? ReadOnlySpan<char>.Empty : DecodeAtomToBuffer(graph);
            var subjectTerm = DecodeAtomToBuffer(s);
            var predicateTerm = DecodeAtomToBuffer(p);
            var objectTerm = DecodeAtomToBuffer(o);
            return new ResolvedQuad(graphTerm, subjectTerm, predicateTerm, objectTerm);
        }
    }

    /// <summary>
    /// When the period at this position was memorized, in milliseconds of the store's clock; null when the store keeps
    /// no times (ADR-062 D2). The engine binds <c>MEMORIZED</c> from it, a prune carries it (ADR-061 D12), and
    /// <see cref="TemporalResultEnumerator"/> gives it.
    /// </summary>
    internal readonly long? MemorizedAt => _cursor.HasMemoryTimes ? _cursor.MemorizedAt : null;

    /// <summary>
    /// When the period at this position was forgotten, in milliseconds of the store's clock; null while it is
    /// remembered (ADR-061 I8), and when the store keeps no times. The stored open value never leaves the store.
    /// </summary>
    internal readonly long? ForgottenAt =>
        _cursor.HasMemoryTimes && !TemporalQuadIndex.IsRemembered(_cursor.ForgottenAt) ? _cursor.ForgottenAt : null;

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

    public ResultEnumerator GetEnumerator() => this;
}

/// <summary>
/// The result of a Cognitive read (ADR-062 D2, D6): a <see cref="ResultEnumerator"/>, with the two times of each
/// period. It has no loop of its own: it moves the enumerator it holds, and adds the times from its position. Only the
/// reads of a store that keeps times return it, so whoever holds one knows the times are kept.
///
/// IMPORTANT: The spans returned by Current are only valid until the next MoveNext() call.
/// Call Dispose() when done to return the pooled buffer.
/// </summary>
public struct TemporalResultEnumerator
{
    private ResultEnumerator _quads;

    internal TemporalResultEnumerator(ResultEnumerator quads) => _quads = quads;

    public bool MoveNext() => _quads.MoveNext();

    public ResolvedTemporalQuad Current
    {
        get
        {
            var quad = _quads.Current;
            var memorizedAt = DateTimeOffset.FromUnixTimeMilliseconds(_quads.MemorizedAt!.Value);
            DateTimeOffset? forgottenAt = _quads.ForgottenAt is { } forgotten ? DateTimeOffset.FromUnixTimeMilliseconds(forgotten) : null;
            return new ResolvedTemporalQuad(quad, memorizedAt, forgottenAt);
        }
    }

    /// <summary>Return the pooled buffer. Call this when done iterating.</summary>
    public void Dispose() => _quads.Dispose();

    public TemporalResultEnumerator GetEnumerator() => this;
}

/// <summary>
/// Where the subject, the predicate and the object are in a row of an index (ADR-062 D6): primary, secondary and
/// tertiary. GSPO and the Cognitive TGSP have the order <see cref="SPO"/>, GPOS <see cref="POS"/>, GOSP
/// <see cref="OSP"/>. A row's term order has no time in it: the time is in the TGSP index's key, which the index builds
/// itself.
/// </summary>
internal enum TermOrder
{
    SPO, // Primary=S, Secondary=P, Tertiary=O
    POS, // Primary=P, Secondary=O, Tertiary=S
    OSP  // Primary=O, Secondary=S, Tertiary=P
}

/// <summary>
/// A quad as a read on any profile yields it (ADR-062 D1): graph, subject, predicate and object, and nothing else. On
/// a Minimal store the graph is empty: the profile has no graphs.
/// </summary>
public readonly ref struct ResolvedQuad
{
    public readonly ReadOnlySpan<char> Graph;
    public readonly ReadOnlySpan<char> Subject;
    public readonly ReadOnlySpan<char> Predicate;
    public readonly ReadOnlySpan<char> Object;

    internal ResolvedQuad(
        ReadOnlySpan<char> graph,
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object)
    {
        Graph = graph;
        Subject = subject;
        Predicate = predicate;
        Object = @object;
    }
}

/// <summary>
/// A memory as a Cognitive read yields it (ADR-062 D2): the quad and the two times of its period (ADR-061 D1).
/// </summary>
public readonly ref struct ResolvedTemporalQuad
{
    public readonly ReadOnlySpan<char> Graph;
    public readonly ReadOnlySpan<char> Subject;
    public readonly ReadOnlySpan<char> Predicate;
    public readonly ReadOnlySpan<char> Object;

    /// <summary>When the period was memorized: the store's clock reading at the write (ADR-061 D1).</summary>
    public readonly DateTimeOffset MemorizedAt;

    /// <summary>When the period was forgotten; absent while the memory is remembered (ADR-061 I8).</summary>
    public readonly DateTimeOffset? ForgottenAt;

    internal ResolvedTemporalQuad(ResolvedQuad quad, DateTimeOffset memorizedAt, DateTimeOffset? forgottenAt)
    {
        Graph = quad.Graph;
        Subject = quad.Subject;
        Predicate = quad.Predicate;
        Object = quad.Object;
        MemorizedAt = memorizedAt;
        ForgottenAt = forgottenAt;
    }
}

/// <summary>
/// Enumerator for distinct named graph IRIs.
/// Scans the index and returns each unique graph IRI once.
/// </summary>
public ref struct NamedGraphEnumerator
{
    // One cursor over the store's GSPO index (ADR-062 Q5): it walks graph-first, so the entries of one graph are
    // contiguous and the distinct-graph dedup below is the same on every profile. A store without named graphs (the
    // Minimal profile) has no cursor and lists nothing: it has none to list, and nothing is scanned.
    private QuadIndexCursor _entries;
    private readonly bool _storeHasGraphs;
    private readonly IAtomStore _atoms;
    private long _lastGraphAtom;
    private string? _current;

    /// <summary>The named graphs of the entries a GSPO cursor yields.</summary>
    /// <param name="entries">A cursor over GSPO, in its order: on a Cognitive store what is remembered now, or every
    /// entry (<see cref="QuadStore.GetNamedGraphsHoldingAPeriod"/>); on a Graph store what is asserted now.</param>
    internal NamedGraphEnumerator(QuadIndexCursor entries, IAtomStore atoms)
    {
        _entries = entries;
        _storeHasGraphs = true;
        _atoms = atoms;
        _lastGraphAtom = -1; // -1 means "no graph seen yet"
        _current = null;
    }

    /// <summary>A store without named graphs (the Minimal profile): yields nothing.</summary>
    internal NamedGraphEnumerator(IAtomStore atoms)
    {
        _entries = default;
        _storeHasGraphs = false;
        _atoms = atoms;
        _lastGraphAtom = -1;
        _current = null;
    }

    /// <summary>Current graph IRI.</summary>
    public readonly ReadOnlySpan<char> Current => _current.AsSpan();

    /// <summary>Move to the next distinct named graph (the default graph, atom 0, is skipped).</summary>
    public bool MoveNext()
    {
        if (!_storeHasGraphs)
            return false;
        while (_entries.MoveNext())
            if (Accept(_entries.Graph)) return true;
        return false;
    }

    // Accept the next DISTINCT named graph: skip the default graph (atom 0) and runs of the same graph
    // (GSPO is graph-first, so identical graphs are contiguous). Resolves the atom to its IRI on accept.
    private bool Accept(long graphAtom)
    {
        if (graphAtom == 0 || graphAtom == _lastGraphAtom) return false;
        _lastGraphAtom = graphAtom;
        _current = _atoms.GetAtomString(graphAtom);
        return true;
    }

    public NamedGraphEnumerator GetEnumerator() => this;
}
