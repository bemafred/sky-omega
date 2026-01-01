using System;
using System.Collections.Generic;
using System.Threading;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// Cached execution plan data.
/// </summary>
/// <remarks>
/// <para><strong>INTERNAL USE ONLY:</strong> This class is internal because it is an
/// implementation detail of query optimization. Query planning is transparent to users.</para>
/// </remarks>
internal sealed class CachedPlan
{
    /// <summary>Pattern execution order.</summary>
    public required int[] PatternOrder { get; init; }

    /// <summary>Statistics transaction ID when plan was created.</summary>
    public required long StatsTxId { get; init; }

    /// <summary>When this plan was last accessed.</summary>
    public DateTime LastAccessed { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// LRU cache for query execution plans.
/// Thread-safe with lock-free reads via copy-on-write.
/// </summary>
/// <remarks>
/// <para><strong>INTERNAL USE ONLY:</strong> This class is internal because it is an
/// implementation detail of query optimization. Query planning is transparent to users.</para>
/// </remarks>
internal sealed class QueryPlanCache
{
    private readonly int _capacity;
    private volatile Dictionary<long, CachedPlan> _cache = new();
    private readonly object _updateLock = new();
    private long _statsTxId;  // Track statistics version for invalidation

    /// <summary>
    /// Create a new plan cache.
    /// </summary>
    /// <param name="capacity">Maximum number of plans to cache (default: 1000).</param>
    public QueryPlanCache(int capacity = 1000)
    {
        _capacity = capacity;
    }

    /// <summary>
    /// Get cached plan if it exists and is still valid.
    /// Returns null if not found or if statistics have changed.
    /// Thread-safe, lock-free read.
    /// </summary>
    public CachedPlan? Get(long queryHash, long currentStatsTxId)
    {
        var snapshot = _cache;
        if (snapshot.TryGetValue(queryHash, out var plan))
        {
            // Check if plan was created with current statistics
            if (plan.StatsTxId == currentStatsTxId)
            {
                plan.LastAccessed = DateTime.UtcNow;
                return plan;
            }
            // Stats have changed - plan is stale
            return null;
        }
        return null;
    }

    /// <summary>
    /// Store a plan in the cache.
    /// Thread-safe via copy-on-write.
    /// </summary>
    public void Put(long queryHash, CachedPlan plan)
    {
        lock (_updateLock)
        {
            var newCache = new Dictionary<long, CachedPlan>(_cache);

            // Evict oldest if at capacity
            while (newCache.Count >= _capacity)
            {
                EvictOldest(newCache);
            }

            newCache[queryHash] = plan;
            _cache = newCache;
            _statsTxId = plan.StatsTxId;
        }
    }

    /// <summary>
    /// Invalidate all cached plans.
    /// Called when statistics change significantly.
    /// </summary>
    public void Invalidate()
    {
        lock (_updateLock)
        {
            _cache = new Dictionary<long, CachedPlan>();
        }
    }

    /// <summary>
    /// Number of plans currently cached.
    /// </summary>
    public int Count => _cache.Count;

    /// <summary>
    /// Compute FNV-1a 64-bit hash for a query string.
    /// </summary>
    public static long ComputeQueryHash(ReadOnlySpan<char> query)
    {
        unchecked
        {
            long hash = unchecked((long)14695981039346656037UL);
            foreach (var c in query)
            {
                hash ^= c;
                hash *= 1099511628211L;
            }
            return hash;
        }
    }

    /// <summary>
    /// Evict the least recently used plan from the cache.
    /// </summary>
    private static void EvictOldest(Dictionary<long, CachedPlan> cache)
    {
        long oldestKey = 0;
        DateTime oldestTime = DateTime.MaxValue;

        foreach (var (key, plan) in cache)
        {
            if (plan.LastAccessed < oldestTime)
            {
                oldestTime = plan.LastAccessed;
                oldestKey = key;
            }
        }

        if (oldestKey != 0)
        {
            cache.Remove(oldestKey);
        }
        else if (cache.Count > 0)
        {
            // Fallback: remove first item
            using var enumerator = cache.GetEnumerator();
            if (enumerator.MoveNext())
            {
                cache.Remove(enumerator.Current.Key);
            }
        }
    }
}
