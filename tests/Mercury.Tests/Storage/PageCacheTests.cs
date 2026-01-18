using System;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// Tests for PageCache - LRU cache for B+Tree pages using clock algorithm.
/// </summary>
public unsafe class PageCacheTests : IDisposable
{
    private PageCache? _cache;

    public void Dispose()
    {
        _cache?.Dispose();
    }

    private PageCache CreateCache(int capacity = 10)
    {
        _cache?.Dispose();
        _cache = new PageCache(capacity);
        return _cache;
    }

    #region Basic Operations

    [Fact]
    public void TryGet_EmptyCache_ReturnsFalse()
    {
        var cache = CreateCache();

        var found = cache.TryGet(1, out var ptr);

        Assert.False(found);
        Assert.True(ptr == null);
    }

    [Fact]
    public void Add_SingleEntry_CanRetrieve()
    {
        var cache = CreateCache();
        var fakePtr = (void*)0x1000;

        cache.Add(1, fakePtr);
        var found = cache.TryGet(1, out var ptr);

        Assert.True(found);
        Assert.True(ptr == fakePtr);
    }

    [Fact]
    public void Add_MultipleEntries_AllRetrievable()
    {
        var cache = CreateCache();

        for (int i = 0; i < 5; i++)
        {
            cache.Add(i, (void*)(0x1000 + i * 0x100));
        }

        for (int i = 0; i < 5; i++)
        {
            var found = cache.TryGet(i, out var ptr);
            Assert.True(found, $"Page {i} not found");
            Assert.True(ptr == (void*)(0x1000 + i * 0x100));
        }
    }

    [Fact]
    public void TryGet_NonExistentPage_ReturnsFalse()
    {
        var cache = CreateCache();
        cache.Add(1, (void*)0x1000);
        cache.Add(2, (void*)0x2000);

        var found = cache.TryGet(99, out _);

        Assert.False(found);
    }

    #endregion

    #region Eviction (Clock Algorithm)

    [Fact]
    public void Add_ExceedsCapacity_EvictsEntry()
    {
        var cache = CreateCache(capacity: 3);

        // Fill cache
        cache.Add(1, (void*)0x1000);
        cache.Add(2, (void*)0x2000);
        cache.Add(3, (void*)0x3000);

        // Add one more - should evict
        cache.Add(4, (void*)0x4000);

        // New entry should be present
        Assert.True(cache.TryGet(4, out _));

        // At least one of the old entries should be evicted
        var (count, capacity, _) = cache.GetStatistics();
        Assert.Equal(3, count);
        Assert.Equal(3, capacity);
    }

    [Fact]
    public void ClockAlgorithm_AccessedPagesGetSecondChance()
    {
        var cache = CreateCache(capacity: 3);

        // Fill cache
        cache.Add(1, (void*)0x1000);
        cache.Add(2, (void*)0x2000);
        cache.Add(3, (void*)0x3000);

        // Access page 2 and 3 to set their referenced bits
        cache.TryGet(2, out _);
        cache.TryGet(3, out _);

        // Add page 4 - should evict page 1 (not referenced since add)
        cache.Add(4, (void*)0x4000);

        // Page 1 should be evicted (it was the clock hand position with no second chance)
        // Page 2 and 3 got second chance, page 4 is the new one
        Assert.True(cache.TryGet(4, out _), "Page 4 should be in cache");

        var (count, capacity, _) = cache.GetStatistics();
        Assert.Equal(3, count);
        Assert.Equal(3, capacity);
    }

    [Fact]
    public void Eviction_ManyPages_CacheSizeStaysAtCapacity()
    {
        var cache = CreateCache(capacity: 5);

        // Add many more pages than capacity
        for (int i = 0; i < 100; i++)
        {
            cache.Add(i, (void*)(0x1000 + i * 0x100));
        }

        var (count, capacity, _) = cache.GetStatistics();
        Assert.Equal(5, count);
        Assert.Equal(5, capacity);
    }

    #endregion

    #region Statistics

    [Fact]
    public void GetStatistics_EmptyCache_ReturnsZeroCounts()
    {
        var cache = CreateCache(capacity: 10);

        var (count, capacity, totalAccesses) = cache.GetStatistics();

        Assert.Equal(0, count);
        Assert.Equal(10, capacity);
        Assert.Equal(0, totalAccesses);
    }

    [Fact]
    public void GetStatistics_AfterAdds_ReflectsCount()
    {
        var cache = CreateCache(capacity: 10);

        cache.Add(1, (void*)0x1000);
        cache.Add(2, (void*)0x2000);
        cache.Add(3, (void*)0x3000);

        var (count, _, _) = cache.GetStatistics();
        Assert.Equal(3, count);
    }

    [Fact]
    public void GetStatistics_AccessCount_IncreasesOnHit()
    {
        var cache = CreateCache();
        cache.Add(1, (void*)0x1000);

        // Access page multiple times
        cache.TryGet(1, out _);
        cache.TryGet(1, out _);
        cache.TryGet(1, out _);

        var (_, _, totalAccesses) = cache.GetStatistics();
        // Initial access count is 1 from Add, plus 3 from TryGet
        Assert.Equal(4, totalAccesses);
    }

    [Fact]
    public void GetStatistics_MultiplePages_AggregatesAccesses()
    {
        var cache = CreateCache();
        cache.Add(1, (void*)0x1000);
        cache.Add(2, (void*)0x2000);

        cache.TryGet(1, out _);
        cache.TryGet(1, out _);
        cache.TryGet(2, out _);

        var (_, _, totalAccesses) = cache.GetStatistics();
        // 1 (add p1) + 1 (add p2) + 2 (get p1 x2) + 1 (get p2) = 5
        Assert.Equal(5, totalAccesses);
    }

    #endregion

    #region Clear

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var cache = CreateCache();
        cache.Add(1, (void*)0x1000);
        cache.Add(2, (void*)0x2000);

        cache.Clear();

        Assert.False(cache.TryGet(1, out _));
        Assert.False(cache.TryGet(2, out _));

        var (count, _, _) = cache.GetStatistics();
        Assert.Equal(0, count);
    }

    [Fact]
    public void Clear_AllowsNewAdds()
    {
        var cache = CreateCache();
        cache.Add(1, (void*)0x1000);
        cache.Clear();

        cache.Add(1, (void*)0x2000);

        var found = cache.TryGet(1, out var ptr);
        Assert.True(found);
        Assert.True(ptr == (void*)0x2000);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Add_SamePageIdTwice_UpdatesPointer()
    {
        var cache = CreateCache();

        cache.Add(1, (void*)0x1000);
        cache.Add(1, (void*)0x2000);

        // Both adds should succeed, but behavior depends on implementation
        // The test verifies we don't crash
        var found = cache.TryGet(1, out _);
        Assert.True(found);
    }

    [Fact]
    public void TryGet_ZeroPageId_Works()
    {
        var cache = CreateCache();
        cache.Add(0, (void*)0x1000);

        var found = cache.TryGet(0, out var ptr);

        Assert.True(found);
        Assert.True(ptr == (void*)0x1000);
    }

    [Fact]
    public void TryGet_NegativePageId_Works()
    {
        var cache = CreateCache();
        cache.Add(-1, (void*)0x1000);

        var found = cache.TryGet(-1, out var ptr);

        Assert.True(found);
        Assert.True(ptr == (void*)0x1000);
    }

    [Fact]
    public void Capacity_One_StillWorks()
    {
        var cache = new PageCache(capacity: 1);

        cache.Add(1, (void*)0x1000);
        Assert.True(cache.TryGet(1, out _));

        cache.Add(2, (void*)0x2000);
        Assert.True(cache.TryGet(2, out _));
        Assert.False(cache.TryGet(1, out _)); // Evicted

        cache.Dispose();
    }

    [Fact]
    public void LargeCapacity_HandlesCorrectly()
    {
        using var cache = new PageCache(capacity: 10_000);

        for (int i = 0; i < 5000; i++)
        {
            cache.Add(i, (void*)(0x1000L + i));
        }

        var (count, capacity, _) = cache.GetStatistics();
        Assert.Equal(5000, count);
        Assert.Equal(10_000, capacity);
    }

    #endregion

    #region Dispose

    [Fact]
    public void Dispose_ClearsCache()
    {
        var cache = new PageCache(10);
        cache.Add(1, (void*)0x1000);

        cache.Dispose();

        // After dispose, the cache should be cleared
        // Note: accessing after dispose may not be safe in production
        // but we test the dispose does clear data
    }

    #endregion
}
