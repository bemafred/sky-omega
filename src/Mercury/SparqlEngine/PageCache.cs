using System;
using System.Runtime.CompilerServices;

namespace SkyOmega.Mercury.SparqlEngine.Storage;

/// <summary>
/// LRU page cache for B+Tree pages
/// Zero-allocation design using fixed-size cache
/// </summary>
public sealed unsafe class PageCache : IDisposable
{
    private readonly int _capacity;
    private readonly CacheEntry[] _entries;
    private readonly long[] _pageIds;
    private int _count;
    private int _clock; // For clock algorithm (approximation of LRU)

    public PageCache(int capacity)
    {
        _capacity = capacity;
        _entries = new CacheEntry[capacity];
        _pageIds = new long[capacity];
        _count = 0;
        _clock = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(long pageId, out void* pagePtr)
    {
        // Linear probe (could use hash table for larger caches)
        for (int i = 0; i < _count; i++)
        {
            if (_pageIds[i] == pageId)
            {
                ref var entry = ref _entries[i];
                entry.Referenced = true;
                entry.AccessCount++;
                pagePtr = entry.PagePtr;
                return true;
            }
        }

        pagePtr = null;
        return false;
    }

    public void Add(long pageId, void* pagePtr)
    {
        if (_count < _capacity)
        {
            // Simple add
            _pageIds[_count] = pageId;
            _entries[_count] = new CacheEntry
            {
                PagePtr = pagePtr,
                Referenced = true,
                AccessCount = 1
            };
            _count++;
        }
        else
        {
            // Evict using clock algorithm
            var victim = FindVictim();
            _pageIds[victim] = pageId;
            _entries[victim] = new CacheEntry
            {
                PagePtr = pagePtr,
                Referenced = true,
                AccessCount = 1
            };
        }
    }

    private int FindVictim()
    {
        // Clock algorithm (second-chance)
        while (true)
        {
            ref var entry = ref _entries[_clock];

            if (!entry.Referenced)
            {
                var victim = _clock;
                _clock = (_clock + 1) % _capacity;
                return victim;
            }

            entry.Referenced = false;
            _clock = (_clock + 1) % _capacity;
        }
    }

    public void Clear()
    {
        Array.Clear(_entries, 0, _entries.Length);
        Array.Clear(_pageIds, 0, _pageIds.Length);
        _count = 0;
        _clock = 0;
    }

    public (int Count, int Capacity, long TotalAccesses) GetStatistics()
    {
        long totalAccesses = 0;
        for (int i = 0; i < _count; i++)
        {
            totalAccesses += _entries[i].AccessCount;
        }

        return (_count, _capacity, totalAccesses);
    }

    public void Dispose()
    {
        Clear();
    }

    private struct CacheEntry
    {
        public void* PagePtr;
        public bool Referenced;
        public int AccessCount;
    }
}
