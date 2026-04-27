using System;
using SkyOmega.Mercury.Compression;
using Xunit;

namespace SkyOmega.Mercury.Tests.Compression;

/// <summary>
/// ADR-036 Decision 2: RLE1 inverse correctness. Encoded streams are constructed
/// by hand using bzip2's RLE1 specification (4-copy preamble + count byte) and the
/// inverse must reproduce the original byte sequence exactly.
/// </summary>
public class Rle1InverseTests
{
    [Fact]
    public void NoRun_PassesBytesThrough()
    {
        var rle = new Rle1Inverse();
        var input = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
        var output = new byte[input.Length];
        var (consumed, written) = rle.Decode(input, output);
        Assert.Equal(7, consumed);
        Assert.Equal(7, written);
        Assert.Equal(input, output);
    }

    [Fact]
    public void ThreeRepeats_NoExpansion()
    {
        // Three identical bytes (no run yet — bzip2 only RLEs 4+).
        var rle = new Rle1Inverse();
        var input = new byte[] { 0x42, 0x42, 0x42 };
        var output = new byte[3];
        var (consumed, written) = rle.Decode(input, output);
        Assert.Equal(3, consumed);
        Assert.Equal(3, written);
        Assert.Equal(input, output);
    }

    [Fact]
    public void FourRepeatsThenZeroExtra_EmitsExactlyFour()
    {
        // RLE1 encodes "AAAA" + 0 = 4 As (the 4 verbatim, plus 0 additional).
        var rle = new Rle1Inverse();
        var input = new byte[] { 0x41, 0x41, 0x41, 0x41, 0x00 };
        var output = new byte[4];
        var (consumed, written) = rle.Decode(input, output);
        Assert.Equal(5, consumed);
        Assert.Equal(4, written);
        Assert.Equal(new byte[] { 0x41, 0x41, 0x41, 0x41 }, output);
    }

    [Fact]
    public void FourRepeatsThenThreeExtra_EmitsSeven()
    {
        // "AAAA" + 3 = 7 As total.
        var rle = new Rle1Inverse();
        var input = new byte[] { 0x41, 0x41, 0x41, 0x41, 0x03 };
        var output = new byte[7];
        var (consumed, written) = rle.Decode(input, output);
        Assert.Equal(5, consumed);
        Assert.Equal(7, written);
        Assert.All(output, b => Assert.Equal(0x41, b));
    }

    [Fact]
    public void RunFollowedByOtherBytes_RunResetsBetween()
    {
        // "AAAA" + 2 + "B" + "B" + "B" = 6 As, then 3 Bs (no run because only 3 Bs).
        var rle = new Rle1Inverse();
        var input = new byte[] { 0x41, 0x41, 0x41, 0x41, 0x02, 0x42, 0x42, 0x42 };
        var output = new byte[9];
        var (consumed, written) = rle.Decode(input, output);
        Assert.Equal(8, consumed);
        Assert.Equal(9, written);
        var expected = new byte[] { 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x42, 0x42, 0x42 };
        Assert.Equal(expected, output);
    }

    [Fact]
    public void OutputBufferTooSmall_PendingRepeatsFlushOnNextCall()
    {
        // "AAAA" + 10 = 14 As, but caller only provides a 6-byte output buffer.
        // First call: consume the 5 input bytes, write 6 As, stash 8 pending repeats.
        // Second call: consume 0 input bytes, write the 8 pending As.
        var rle = new Rle1Inverse();
        var input = new byte[] { 0x41, 0x41, 0x41, 0x41, 0x0A };
        var firstOut = new byte[6];
        var (consumed1, written1) = rle.Decode(input, firstOut);
        Assert.Equal(5, consumed1);
        Assert.Equal(6, written1);

        var secondOut = new byte[8];
        var (consumed2, written2) = rle.Decode(ReadOnlySpan<byte>.Empty, secondOut);
        Assert.Equal(0, consumed2);
        Assert.Equal(8, written2);
        Assert.All(secondOut, b => Assert.Equal(0x41, b));
    }

    [Fact]
    public void Streaming_ChunkedInput_MatchesOneShot()
    {
        // Construct a stream with multiple runs; decode once in one chunk and again
        // in many small chunks; verify byte-identical outputs.
        var input = new byte[]
        {
            0x01, 0x02, 0x03,
            0x05, 0x05, 0x05, 0x05, 0x14, // "5×5" run × 4 + 20 extra = 24 fives
            0x99,
            0x07, 0x07, 0x07, 0x07, 0x00, // "7×4" + 0 extra = 4 sevens
            0x42, 0x42, 0x42,             // 3 forty-twos, no run
        };

        // One-shot reference.
        var reference = new byte[1024];
        var rleRef = new Rle1Inverse();
        var (cRef, wRef) = rleRef.Decode(input, reference);
        Assert.Equal(input.Length, cRef);

        // Streaming: feed one byte at a time, accumulate output bytes.
        var rleStream = new Rle1Inverse();
        var streamOut = new byte[wRef];
        int outIdx = 0;
        for (int i = 0; i < input.Length; i++)
        {
            int remaining = streamOut.Length - outIdx;
            var (c, w) = rleStream.Decode(input.AsSpan(i, 1), streamOut.AsSpan(outIdx, remaining));
            outIdx += w;
        }
        // Drain any remaining pending repeats.
        while (outIdx < streamOut.Length)
        {
            var (_, w) = rleStream.Decode(ReadOnlySpan<byte>.Empty, streamOut.AsSpan(outIdx));
            if (w == 0) break;
            outIdx += w;
        }

        Assert.Equal(reference.AsSpan(0, wRef).ToArray(), streamOut);
    }
}
