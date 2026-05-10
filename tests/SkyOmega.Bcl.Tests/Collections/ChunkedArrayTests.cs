using System;
using SkyOmega.Bcl.Collections;
using Xunit;

namespace SkyOmega.Bcl.Tests.Collections;

public class ChunkedArrayTests
{
    [Fact]
    public void FixedSize_Access()
    {
        var arr = new ChunkedArray<long>(1000, chunkShift: 8);
        Assert.Equal(1000, arr.Length);
        for (int i = 0; i < 1000; i++) arr[i] = i * 7L;
        for (int i = 0; i < 1000; i++) Assert.Equal(i * 7L, arr[i]);
    }

    [Fact]
    public void OutOfRange_Throws()
    {
        var arr = new ChunkedArray<int>(100);
        Assert.Throws<ArgumentOutOfRangeException>(() => arr[100]);
        Assert.Throws<ArgumentOutOfRangeException>(() => arr[-1]);
    }

    [Fact]
    public void TailChunk_PartialSize_Works()
    {
        // 1000 elements at chunkShift=8 (256 per chunk) → 4 chunks: 256+256+256+232.
        var arr = new ChunkedArray<int>(1000, chunkShift: 8);
        Assert.Equal(4, arr.ChunkCount);
        arr[999] = 12345;
        Assert.Equal(12345, arr[999]);
    }

    [Fact]
    public void Empty_LengthIsZero()
    {
        var arr = new ChunkedArray<long>(0);
        Assert.Equal(0, arr.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => arr[0]);
    }
}
