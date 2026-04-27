using System;
using System.Linq;
using System.Text;
using SkyOmega.Mercury.Compression;
using Xunit;

namespace SkyOmega.Mercury.Tests.Compression;

/// <summary>
/// ADR-036 Decision 2: Burrows-Wheeler Transform inverse correctness. The inverse must
/// reconstruct the original byte sequence from the L-column and origin pointer; verified
/// against hand-computed BWT outputs and via end-to-end round-trip from a forward BWT
/// reference computed at test time.
/// </summary>
public class BurrowsWheelerInverseTests
{
    /// <summary>
    /// Reference forward BWT: sort all rotations, return (lastColumn, originRow). Suitable
    /// for short test inputs only — O(n^2 log n).
    /// </summary>
    private static (byte[] LastColumn, int Origin) ForwardBwt(byte[] input)
    {
        int n = input.Length;
        var rotations = new int[n];
        for (int i = 0; i < n; i++) rotations[i] = i;

        // Sort rotation indices by the rotation's character sequence.
        Array.Sort(rotations, (a, b) =>
        {
            for (int k = 0; k < n; k++)
            {
                int ca = input[(a + k) % n];
                int cb = input[(b + k) % n];
                if (ca != cb) return ca - cb;
            }
            return 0;
        });

        var L = new byte[n];
        int origin = -1;
        for (int i = 0; i < n; i++)
        {
            // L = last char of rotation = input[(rotations[i] + n - 1) % n]
            L[i] = input[(rotations[i] + n - 1) % n];
            if (rotations[i] == 0) origin = i;
        }
        return (L, origin);
    }

    [Fact]
    public void HandComputed_AB_RoundTrip()
    {
        // For "ab": rotations are "ab", "ba"; sorted: "ab" (row 0), "ba" (row 1).
        // L[0] = 'b', L[1] = 'a'. Original "ab" is row 0 → origin = 0.
        var input = Encoding.ASCII.GetBytes("ab");
        var (L, origin) = ForwardBwt(input);
        Assert.Equal((byte)'b', L[0]);
        Assert.Equal((byte)'a', L[1]);
        Assert.Equal(0, origin);

        var output = new byte[2];
        var inverse = new BurrowsWheelerInverse();
        inverse.Decode(L, 2, origin, output);
        Assert.Equal(input, output);
    }

    [Fact]
    public void HandComputed_BANANA_RoundTrip()
    {
        // The classic Burrows-Wheeler example. Rotations of "banana":
        //   banana  ->  anana b  ->  sorted as: 'abnana' index sort.
        // The original paper uses BANANA; bzip2's BWT is byte-level so case doesn't matter.
        var input = Encoding.ASCII.GetBytes("banana");
        var (L, origin) = ForwardBwt(input);

        var output = new byte[input.Length];
        var inverse = new BurrowsWheelerInverse();
        inverse.Decode(L, input.Length, origin, output);
        Assert.Equal(input, output);
    }

    [Fact]
    public void RandomInputs_Various_Sizes_RoundTrip()
    {
        // Round-trip a range of sizes including small, medium, and odd boundaries.
        var inverse = new BurrowsWheelerInverse();
        var rng = new Random(7);
        int[] sizes = { 1, 2, 3, 7, 16, 100, 1000, 10000 };
        foreach (var size in sizes)
        {
            var input = new byte[size];
            rng.NextBytes(input);
            var (L, origin) = ForwardBwt(input);

            var output = new byte[size];
            inverse.Decode(L, size, origin, output);
            Assert.Equal(input, output);
        }
    }

    [Fact]
    public void LowEntropy_HighRepetition_RoundTrip()
    {
        // BWT excels on highly-repetitive input; verify the inverse handles it.
        var input = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("AAAABBBB", 100)));
        var (L, origin) = ForwardBwt(input);

        var output = new byte[input.Length];
        var inverse = new BurrowsWheelerInverse();
        inverse.Decode(L, input.Length, origin, output);
        Assert.Equal(input, output);
    }

    [Fact]
    public void AllSameByte_RoundTrip()
    {
        // Edge case: every byte identical. BWT output = same byte repeated; T is a
        // simple cycle; origin determines starting position (irrelevant since all
        // characters are identical).
        var input = new byte[1000];
        Array.Fill(input, (byte)0x42);
        var (L, origin) = ForwardBwt(input);

        var output = new byte[input.Length];
        var inverse = new BurrowsWheelerInverse();
        inverse.Decode(L, input.Length, origin, output);
        Assert.Equal(input, output);
    }

    [Fact]
    public void TwoDistinctBytes_RoundTrip()
    {
        // Edge case: alphabet size 2. Tests cumStart with sparse character distribution.
        var input = new byte[256];
        for (int i = 0; i < 256; i++) input[i] = (byte)((i & 1) == 0 ? 0x00 : 0xFF);
        var (L, origin) = ForwardBwt(input);

        var output = new byte[input.Length];
        var inverse = new BurrowsWheelerInverse();
        inverse.Decode(L, input.Length, origin, output);
        Assert.Equal(input, output);
    }

    [Fact]
    public void Reuse_SameInstance_AcrossMultipleBlocks()
    {
        // The inverse instance is reused per stream; rebuild scratch from each new
        // block without leaking state. Decode three different inputs and check each.
        var inverse = new BurrowsWheelerInverse();
        string[] texts = { "the quick brown fox", "abracadabra", "Hello, World!" };
        foreach (var text in texts)
        {
            var input = Encoding.ASCII.GetBytes(text);
            var (L, origin) = ForwardBwt(input);
            var output = new byte[input.Length];
            inverse.Decode(L, input.Length, origin, output);
            Assert.Equal(input, output);
        }
    }
}
