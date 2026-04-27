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
    /// Packed successor + emit-byte. <c>_packed[j]</c> encodes <c>(L[T[j]] &lt;&lt; 24) | T[j]</c>
    /// — the byte to emit at this row plus the next row in the walk, in one 32-bit load.
    /// Block length is bounded by 900,000 so 24 bits is sufficient for the row index;
    /// the upper byte carries the character. Halves the load count per emitted byte vs
    /// separate T[] and L[] reads, the dominant cost on cache-miss-bound walks.
    /// </summary>
    private readonly int[] _packed = new int[MaxBlockSize];

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
    /// Build the packed successor table. Two passes:
    /// 1. <c>_packed[cumStart[L[i]]++] = i</c> places the source row index in each slot
    ///    (T[j] = i). Encoding identical to bzip2's canonical T-permutation.
    /// 2. Walk every slot j; replace <c>_packed[j]</c> with <c>(L[T[j]] &lt;&lt; 24) | T[j]</c>.
    ///    After this pass, one load per emitted byte covers both "what to emit" and
    ///    "where to go next" — eliminating the L[i] read from the walk hot path.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void BuildPermutation(ReadOnlySpan<byte> lastColumn, int length, Span<int> cumStart)
    {
        fixed (int* pBase = _packed)
        fixed (byte* lBase = lastColumn)
        fixed (int* cumBase = cumStart)
        {
            // Pass 1: T[j] = i where row j's F-column character is L[i].
            for (int i = 0; i < length; i++)
            {
                int slot = cumBase[lBase[i]]++;
                pBase[slot] = i;
            }
            // Pass 2: pack (L[j] << 24) | T[j] so one load at row j gives both the
            // character to emit at row j and the next row in the walk.
            for (int j = 0; j < length; j++)
            {
                int t = pBase[j];
                pBase[j] = ((int)lBase[j] << 24) | t;
            }
        }
    }

    /// <summary>
    /// Walk via the packed successor table: each iteration is one 32-bit load + a
    /// shift + a mask + one byte store. Pointer-chasing latency dominates on cold
    /// blocks (the packed table is 3.6 MB at maximum block size, larger than typical
    /// L2); on warm blocks the JIT can issue back-to-back loads for memory-level
    /// parallelism within the dependency chain.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void WalkAndEmit(int origin, int length, ReadOnlySpan<byte> lastColumn, Span<byte> output)
    {
        fixed (int* pBase = _packed)
        fixed (byte* outBase = output)
        {
            // Initial step: i = T[origin]. _packed[origin] = (L[T[origin]] << 24) | T[origin],
            // so i = _packed[origin] & 0xFFFFFF. The first emitted character is L[T[origin]],
            // which is the high byte of _packed[origin].
            int packed = pBase[origin];
            int i = packed & 0xFFFFFF;
            for (int j = 0; j < length; j++)
            {
                int p = pBase[i];
                outBase[j] = (byte)((uint)p >> 24);
                i = p & 0xFFFFFF;
            }
        }
    }
}
