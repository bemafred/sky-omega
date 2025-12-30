using System;
using System.Collections.Generic;
using System.Threading;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// Per-predicate cardinality statistics for query optimization.
/// Immutable struct for thread-safe access.
/// </summary>
public readonly struct PredicateStats
{
    /// <summary>Atom ID of the predicate.</summary>
    public readonly long PredicateAtomId;

    /// <summary>Total number of triples with this predicate.</summary>
    public readonly long TripleCount;

    /// <summary>Number of distinct subjects for this predicate.</summary>
    public readonly long DistinctSubjects;

    /// <summary>Number of distinct objects for this predicate.</summary>
    public readonly long DistinctObjects;

    /// <summary>Transaction ID when statistics were last computed.</summary>
    public readonly long LastUpdatedTxId;

    public PredicateStats(long predicateAtomId, long tripleCount,
        long distinctSubjects, long distinctObjects, long lastUpdatedTxId)
    {
        PredicateAtomId = predicateAtomId;
        TripleCount = tripleCount;
        DistinctSubjects = distinctSubjects;
        DistinctObjects = distinctObjects;
        LastUpdatedTxId = lastUpdatedTxId;
    }

    /// <summary>
    /// Estimate cardinality when subject is bound.
    /// Returns average objects per subject.
    /// </summary>
    public double EstimateWithBoundSubject()
        => DistinctSubjects > 0 ? (double)TripleCount / DistinctSubjects : TripleCount;

    /// <summary>
    /// Estimate cardinality when object is bound.
    /// Returns average subjects per object.
    /// </summary>
    public double EstimateWithBoundObject()
        => DistinctObjects > 0 ? (double)TripleCount / DistinctObjects : TripleCount;
}

/// <summary>
/// Thread-safe statistics store using copy-on-write pattern.
/// Provides lock-free reads for query optimization.
/// </summary>
public sealed class StatisticsStore
{
    private volatile Dictionary<long, PredicateStats> _stats = new();
    private readonly object _updateLock = new();
    private long _totalTripleCount;
    private long _lastUpdateTxId;

    /// <summary>
    /// Get statistics for a predicate by its atom ID.
    /// Returns null if no statistics are available.
    /// Thread-safe, lock-free read.
    /// </summary>
    public PredicateStats? GetStats(long predicateAtomId)
    {
        var snapshot = _stats;
        return snapshot.TryGetValue(predicateAtomId, out var stats) ? stats : null;
    }

    /// <summary>
    /// Total number of triples in the store when statistics were last computed.
    /// </summary>
    public long TotalTripleCount => Volatile.Read(ref _totalTripleCount);

    /// <summary>
    /// Transaction ID when statistics were last updated.
    /// </summary>
    public long LastUpdateTxId => Volatile.Read(ref _lastUpdateTxId);

    /// <summary>
    /// Whether any statistics have been collected.
    /// </summary>
    public bool HasStats => _stats.Count > 0;

    /// <summary>
    /// Number of distinct predicates with statistics.
    /// </summary>
    public int PredicateCount => _stats.Count;

    /// <summary>
    /// Update statistics with new values.
    /// Thread-safe via copy-on-write.
    /// </summary>
    public void Update(IReadOnlyDictionary<long, PredicateStats> newStats, long totalTriples, long txId)
    {
        lock (_updateLock)
        {
            // Copy-on-write: create new dictionary
            _stats = new Dictionary<long, PredicateStats>(newStats);
            Volatile.Write(ref _totalTripleCount, totalTriples);
            Volatile.Write(ref _lastUpdateTxId, txId);
        }
    }

    /// <summary>
    /// Clear all statistics.
    /// </summary>
    public void Clear()
    {
        lock (_updateLock)
        {
            _stats = new Dictionary<long, PredicateStats>();
            Volatile.Write(ref _totalTripleCount, 0);
            Volatile.Write(ref _lastUpdateTxId, 0);
        }
    }
}
