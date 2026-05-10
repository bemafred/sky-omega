using System;
using SkyOmega.Bcl.Collections;
using Xunit;

namespace SkyOmega.Bcl.Tests.Collections;

public class ChunkedListTests
{
    [Fact]
    public void Empty_CountIsZero()
    {
        var list = new ChunkedList<long>();
        Assert.Equal(0, list.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => list[0]);
    }

    [Fact]
    public void Add_BasicSequence_RoundTrips()
    {
        var list = new ChunkedList<long>(chunkShift: 8);  // tiny chunk = 256 elements
        for (int i = 0; i < 1000; i++) list.Add(i * 3L);
        Assert.Equal(1000, list.Count);
        for (int i = 0; i < 1000; i++)
            Assert.Equal(i * 3L, list[i]);
    }

    [Fact]
    public void Add_CrossesChunkBoundaries_NoLossOfData()
    {
        // Force multiple chunks at chunkShift=8 (256 per chunk) with 4097 elements
        // — covers fresh-chunk allocation, top-level array growth.
        var list = new ChunkedList<long>(chunkShift: 8);
        for (int i = 0; i < 4097; i++) list.Add(i + 1);
        Assert.Equal(4097, list.Count);
        for (int i = 0; i < 4097; i++)
            Assert.Equal(i + 1, list[i]);
        // Spot-check chunk allocation: 4097 / 256 = 17 chunks (16 full + 1 partial)
        Assert.Equal(17, list.AllocatedChunkCount);
    }

    [Fact]
    public void Indexer_OutOfRange_Throws()
    {
        var list = new ChunkedList<int>(chunkShift: 8);
        list.Add(42);
        Assert.Throws<ArgumentOutOfRangeException>(() => list[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => list[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => list[1] = 99);
    }

    [Fact]
    public void Indexer_Setter_OverwritesExisting()
    {
        var list = new ChunkedList<long>(chunkShift: 8);
        for (int i = 0; i < 500; i++) list.Add(i);
        list[100] = 9999;
        list[499] = -1;
        Assert.Equal(9999, list[100]);
        Assert.Equal(-1, list[499]);
        Assert.Equal(99, list[99]);  // untouched
    }

    [Fact]
    public void Clear_ResetsToEmpty()
    {
        var list = new ChunkedList<long>(chunkShift: 8);
        for (int i = 0; i < 500; i++) list.Add(i);
        list.Clear();
        Assert.Equal(0, list.Count);
        Assert.Equal(0, list.AllocatedChunkCount);
    }

    [Fact]
    public void Add_PastInt32Limit_StillIndexable()
    {
        // Exercise long indexing with a stride pattern that reaches past int.MaxValue
        // virtually. Allocate ~3 M actual entries; index using long math but stay
        // within RAM.
        var list = new ChunkedList<int>(chunkShift: 16);  // 64 K per chunk
        const int N = 3_000_000;
        for (int i = 0; i < N; i++) list.Add(i ^ 0x55AA);
        Assert.Equal(N, list.Count);
        // Spot-check index values that would overflow int if naively cast
        long idx1 = 2_500_000L;
        long idx2 = 2_999_999L;
        Assert.Equal((int)(idx1 ^ 0x55AA), list[idx1]);
        Assert.Equal((int)(idx2 ^ 0x55AA), list[idx2]);
    }

    [Fact]
    public void DefaultChunkShift_IsReasonable()
    {
        var list = new ChunkedList<int>();
        Assert.Equal(20, list.ChunkShift);
        Assert.Equal(1L << 20, list.ChunkSize);
    }
}
