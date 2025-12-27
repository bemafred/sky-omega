using System;
using System.Runtime.CompilerServices;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// LRU page cache for B+Tree pages.
/// Uses hash table for O(1) lookup and clock algorithm for eviction.
/// Zero-allocation design using fixed-size arrays.
/// </summary>
public sealed unsafe class PageCache : IDisposable
{
    private readonly int _capacity;
    private readonly CacheEntry[] _entries;
    private readonly long[] _pageIds;
    private int _count;
    private int _clock; // For clock algorithm (approximation of LRU)

    // Hash table for O(1) lookup: maps pageId -> slot index
    // Size is 2x capacity to reduce collisions
    private readonly int _hashTableSize;
    private readonly HashEntry[] _hashTable;

    private const int EmptySlot = -1;
    private const long EmptyPageId = long.MinValue;

    public PageCache(int capacity)
    {
        _capacity = capacity;
        _entries = new CacheEntry[capacity];
        _pageIds = new long[capacity];
        _count = 0;
        _clock = 0;

        // Hash table sized at 2x capacity for good load factor
        _hashTableSize = Math.Max(capacity * 2, 16);
        _hashTable = new HashEntry[_hashTableSize];

        // Initialize hash table with empty markers
        for (int i = 0; i < _hashTableSize; i++)
        {
            _hashTable[i] = new HashEntry { PageId = EmptyPageId, SlotIndex = EmptySlot };
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(long pageId, out void* pagePtr)
    {
        var slot = HashLookup(pageId);
        if (slot >= 0)
        {
            ref var entry = ref _entries[slot];
            entry.Referenced = true;
            entry.AccessCount++;
            pagePtr = entry.PagePtr;
            return true;
        }

        pagePtr = null;
        return false;
    }

    public void Add(long pageId, void* pagePtr)
    {
        // Check if pageId already exists (update case)
        var existingSlot = HashLookup(pageId);
        if (existingSlot >= 0)
        {
            _entries[existingSlot] = new CacheEntry
            {
                PagePtr = pagePtr,
                Referenced = true,
                AccessCount = _entries[existingSlot].AccessCount + 1
            };
            return;
        }

        int targetSlot;
        if (_count < _capacity)
        {
            // Simple add to next available slot
            targetSlot = _count;
            _count++;
        }
        else
        {
            // Evict using clock algorithm
            targetSlot = FindVictim();

            // Remove evicted page from hash table
            var evictedPageId = _pageIds[targetSlot];
            HashRemove(evictedPageId);
        }

        // Store the new entry
        _pageIds[targetSlot] = pageId;
        _entries[targetSlot] = new CacheEntry
        {
            PagePtr = pagePtr,
            Referenced = true,
            AccessCount = 1
        };

        // Add to hash table
        HashInsert(pageId, targetSlot);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int HashLookup(long pageId)
    {
        var hash = ComputeHash(pageId);
        var index = (int)((ulong)hash % (ulong)_hashTableSize);

        // Linear probing
        for (int probe = 0; probe < _hashTableSize; probe++)
        {
            var i = (index + probe) % _hashTableSize;
            ref var entry = ref _hashTable[i];

            if (entry.SlotIndex == EmptySlot)
                return -1; // Not found, hit empty slot

            if (entry.PageId == pageId)
                return entry.SlotIndex;
        }

        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void HashInsert(long pageId, int slotIndex)
    {
        var hash = ComputeHash(pageId);
        var index = (int)((ulong)hash % (ulong)_hashTableSize);

        // Linear probing to find empty slot
        for (int probe = 0; probe < _hashTableSize; probe++)
        {
            var i = (index + probe) % _hashTableSize;
            ref var entry = ref _hashTable[i];

            if (entry.SlotIndex == EmptySlot || entry.PageId == pageId)
            {
                entry.PageId = pageId;
                entry.SlotIndex = slotIndex;
                return;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void HashRemove(long pageId)
    {
        var hash = ComputeHash(pageId);
        var index = (int)((ulong)hash % (ulong)_hashTableSize);

        // Find the entry
        for (int probe = 0; probe < _hashTableSize; probe++)
        {
            var i = (index + probe) % _hashTableSize;
            ref var entry = ref _hashTable[i];

            if (entry.SlotIndex == EmptySlot)
                return; // Not found

            if (entry.PageId == pageId)
            {
                // Mark as deleted but don't break probe chain
                // Use tombstone: keep SlotIndex as EmptySlot but mark differently
                entry.PageId = EmptyPageId;
                entry.SlotIndex = EmptySlot;

                // Rehash subsequent entries to maintain probe chain
                RehashFrom((i + 1) % _hashTableSize);
                return;
            }
        }
    }

    private void RehashFrom(int startIndex)
    {
        // Rehash entries that might have been displaced
        for (int probe = 0; probe < _hashTableSize; probe++)
        {
            var i = (startIndex + probe) % _hashTableSize;
            ref var entry = ref _hashTable[i];

            if (entry.SlotIndex == EmptySlot)
                return; // End of chain

            // Remove and reinsert to find optimal position
            var pageId = entry.PageId;
            var slotIndex = entry.SlotIndex;
            entry.PageId = EmptyPageId;
            entry.SlotIndex = EmptySlot;

            HashInsert(pageId, slotIndex);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ComputeHash(long pageId)
    {
        // Simple but effective hash mixing
        unchecked
        {
            var hash = (ulong)pageId;
            hash ^= hash >> 33;
            hash *= 0xff51afd7ed558ccdUL;
            hash ^= hash >> 33;
            hash *= 0xc4ceb9fe1a85ec53UL;
            hash ^= hash >> 33;
            return (long)hash;
        }
    }

    public void Clear()
    {
        Array.Clear(_entries, 0, _entries.Length);
        Array.Clear(_pageIds, 0, _pageIds.Length);
        _count = 0;
        _clock = 0;

        // Clear hash table
        for (int i = 0; i < _hashTableSize; i++)
        {
            _hashTable[i] = new HashEntry { PageId = EmptyPageId, SlotIndex = EmptySlot };
        }
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

    private struct HashEntry
    {
        public long PageId;
        public int SlotIndex;
    }
}
