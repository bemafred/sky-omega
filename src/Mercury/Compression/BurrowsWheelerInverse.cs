using System;
using System.Runtime.CompilerServices;

namespace SkyOmega.Mercury.Compression;

/// <summary>
/// Inverse Burrows-Wheeler Transform via the Adler/Malbrain fast algorithm. ADR-036
/// Decision 2. Single-pass O(n) reconstruction; the load-bearing algorithmic piece in
/// bzip2 decompression.
/// </summary>
/// <remarks>
/// <para>
/// Given the BWT-encoded last column <c>L</c> and the origin pointer (the row whose first
/// column is the original string's first character), this class produces the original
/// byte sequence. The algorithm builds a single index array <c>T</c> where <c>T[i]</c>
/// gives the row in the sorted matrix corresponding to the rotation one position later
/// in the original. Walking from <c>origin</c> through <c>T</c> emits the original
/// characters in order.
/// </para>
/// <para>
/// The inner walk is pointer-chasing through a 3.6 MB <see cref="int"/>[] at the maximum
/// 900 KB block size. Cache misses dominate; <c>unsafe</c> raw pointer access in the hot
/// loop elides bounds checks and frees the JIT to schedule the loads as densely as the
/// memory subsystem allows. Multi-walk SIMD parallelism (multiple lockstep walks at
/// independent positions) is a deferred optimization-pass candidate.
/// </para>
/// <para>
/// Buffers <c>_t</c> and <c>_f</c> are allocated once at construction, sized for the
/// maximum bzip2 block (900 KB), and reused across all blocks. The class is reused for
/// the lifetime of the enclosing stream.
/// </para>
/// </remarks>
internal sealed class BurrowsWheelerInverse
{
    /// <summary>Maximum bzip2 block size in bytes (block size 9 × 100,000).</summary>
    public const int MaxBlockSize = 900_000;

    /// <summary>
    /// Successor permutation in bzip2's canonical formulation. <c>T[j]</c> gives the row
    /// index <c>i</c> in <c>L</c> whose character is the same instance as the j-th character
    /// in the sorted F-column. The walk pattern is <c>i = T[i]; emit L[i]</c>, advancing
    /// the original sequence one position per step.
    /// </summary>
    private readonly int[] _t = new int[MaxBlockSize];

    /// <summary>
    /// Decode <paramref name="length"/> bytes of BWT output starting from <paramref name="origin"/>.
    /// </summary>
    /// <param name="lastColumn">The L-column (BWT output) of length <paramref name="length"/>.</param>
    /// <param name="length">Number of bytes in the block.</param>
    /// <param name="origin">Origin pointer from the bzip2 block header — the row in the sorted
    /// matrix corresponding to the original input's first rotation.</param>
    /// <param name="output">Destination buffer; must have at least <paramref name="length"/> bytes.</param>
    public void Decode(ReadOnlySpan<byte> lastColumn, int length, int origin, Span<byte> output)
    {
        if (length < 0 || length > MaxBlockSize)
            throw new ArgumentOutOfRangeException(nameof(length));
        if (lastColumn.Length < length)
            throw new ArgumentException("lastColumn shorter than length", nameof(lastColumn));
        if (output.Length < length)
            throw new ArgumentException("output shorter than length", nameof(output));
        if ((uint)origin >= (uint)length)
            throw new ArgumentOutOfRangeException(nameof(origin));

        // Histogram L's bytes; cumulative sum gives cumStart[c] = position in F where
        // characters equal to c begin. cumStart is then mutated as a per-character
        // monotone counter during T construction (each placement bumps cumStart[c]).
        Span<int> cumStart = stackalloc int[256];
        cumStart.Clear();
        for (int i = 0; i < length; i++)
            cumStart[lastColumn[i]]++;

        int running = 0;
        for (int c = 0; c < 256; c++)
        {
            int countC = cumStart[c];
            cumStart[c] = running;
            running += countC;
        }

        BuildPermutation(lastColumn, length, cumStart);
        WalkAndEmit(origin, length, lastColumn, output);
    }

    /// <summary>
    /// Build T using bzip2's canonical recurrence: <c>T[cumStart[L[i]]++] = i</c>. The
    /// resulting permutation lets the walk emit L[T[i]] in original-text order.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void BuildPermutation(ReadOnlySpan<byte> lastColumn, int length, Span<int> cumStart)
    {
        fixed (int* tBase = _t)
        fixed (byte* lBase = lastColumn)
        fixed (int* cumBase = cumStart)
        {
            for (int i = 0; i < length; i++)
            {
                int slot = cumBase[lBase[i]]++;
                tBase[slot] = i;
            }
        }
    }

    /// <summary>
    /// Walk: <c>i = T[origin]; emit L[i]; i = T[i]; emit L[i]; ...</c> The first <c>i = T[origin]</c>
    /// advances from the origin row (which holds the original's last character in L) to the
    /// row whose L is the original's first character. Each subsequent T-step advances one
    /// position in the original sequence.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void WalkAndEmit(int origin, int length, ReadOnlySpan<byte> lastColumn, Span<byte> output)
    {
        fixed (int* tBase = _t)
        fixed (byte* lBase = lastColumn)
        fixed (byte* outBase = output)
        {
            int i = tBase[origin];
            for (int j = 0; j < length; j++)
            {
                outBase[j] = lBase[i];
                i = tBase[i];
            }
        }
    }
}
