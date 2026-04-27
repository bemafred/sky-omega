using System;
using SkyOmega.Mercury.Compression;
using Xunit;

namespace SkyOmega.Mercury.Tests.Compression;

/// <summary>
/// ADR-036 Decision 2: MTF inverse correctness. The inverse mirrors a standard
/// move-to-front transform; sequences validated against hand-computed expected output.
/// </summary>
public class MoveToFrontInverseTests
{
    [Fact]
    public void Reset_ProducesIdentityList()
    {
        var mtf = new MoveToFrontInverse();
        mtf.Reset();
        Span<byte> view = stackalloc byte[256]; mtf.CopyListTo(view);
        for (int i = 0; i < 256; i++)
            Assert.Equal((byte)i, view[i]);
    }

    [Fact]
    public void Decode_IndexZero_OutputsHeadAndPreservesList()
    {
        var mtf = new MoveToFrontInverse();
        mtf.Reset();
        Assert.Equal(0, mtf.Decode(0));
        // Decoding index 0 on identity list outputs 0 and the list stays identity.
        Span<byte> view = stackalloc byte[256]; mtf.CopyListTo(view);
        for (int i = 0; i < 256; i++)
            Assert.Equal((byte)i, view[i]);
    }

    [Fact]
    public void Decode_NonzeroIndex_MovesToFront()
    {
        // Identity: [0, 1, 2, 3, 4, ...].
        // Decode(3) → output 3, list becomes [3, 0, 1, 2, 4, ...].
        var mtf = new MoveToFrontInverse();
        mtf.Reset();
        Assert.Equal(3, mtf.Decode(3));
        Span<byte> view = stackalloc byte[256]; mtf.CopyListTo(view);
        Assert.Equal(3, view[0]);
        Assert.Equal(0, view[1]);
        Assert.Equal(1, view[2]);
        Assert.Equal(2, view[3]);
        Assert.Equal(4, view[4]);
    }

    [Fact]
    public void Decode_Sequence_HandComputed()
    {
        // Identity start: [0, 1, 2, 3, ...].
        // Sequence of indices: 1, 0, 2, 1, 3
        // Step 1: Decode(1) → output 1, list = [1, 0, 2, 3, 4, ...]
        // Step 2: Decode(0) → output 1 (at pos 0), list unchanged = [1, 0, 2, 3, ...]
        // Step 3: Decode(2) → output 2, list = [2, 1, 0, 3, 4, ...]
        // Step 4: Decode(1) → output 1, list = [1, 2, 0, 3, 4, ...]
        // Step 5: Decode(3) → output 3, list = [3, 1, 2, 0, 4, ...]
        var mtf = new MoveToFrontInverse();
        mtf.Reset();
        Assert.Equal(1, mtf.Decode(1));
        Assert.Equal(1, mtf.Decode(0));
        Assert.Equal(2, mtf.Decode(2));
        Assert.Equal(1, mtf.Decode(1));
        Assert.Equal(3, mtf.Decode(3));

        Span<byte> view = stackalloc byte[256]; mtf.CopyListTo(view);
        Assert.Equal(3, view[0]);
        Assert.Equal(1, view[1]);
        Assert.Equal(2, view[2]);
        Assert.Equal(0, view[3]);
        Assert.Equal(4, view[4]);
    }

    [Fact]
    public void Initialize_FromSymbolMap_ListMatchesAlphabet()
    {
        // bzip2 initializes the MTF from the block's symbol map (the bytes that appear
        // in the block, in ascending byte order). Initialize with [0x41, 0x42, 0x5A].
        var mtf = new MoveToFrontInverse();
        mtf.Initialize(new byte[] { 0x41, 0x42, 0x5A }, 3);
        Span<byte> view1 = stackalloc byte[256]; mtf.CopyListTo(view1);
        Assert.Equal(0x41, view1[0]);
        Assert.Equal(0x42, view1[1]);
        Assert.Equal(0x5A, view1[2]);

        Assert.Equal(0x42, mtf.Decode(1));
        Span<byte> view2 = stackalloc byte[256]; mtf.CopyListTo(view2);
        Assert.Equal(0x42, view2[0]);
        Assert.Equal(0x41, view2[1]);
        Assert.Equal(0x5A, view2[2]);
    }

    [Fact]
    public void Decode_AllPositions_RoundTripDoesNotCorruptList()
    {
        // Apply MTF to a synthetic sequence and verify the list remains a permutation
        // of [0, 255] throughout — no entry is lost or duplicated.
        var mtf = new MoveToFrontInverse();
        mtf.Reset();
        var rng = new Random(7);
        for (int step = 0; step < 1000; step++)
        {
            int idx = rng.Next(0, 256);
            mtf.Decode(idx);
        }

        Span<byte> view = stackalloc byte[256];
        mtf.CopyListTo(view);
        var seen = new bool[256];
        for (int i = 0; i < 256; i++)
        {
            Assert.False(seen[view[i]], $"duplicate at position {i}");
            seen[view[i]] = true;
        }
    }
}
