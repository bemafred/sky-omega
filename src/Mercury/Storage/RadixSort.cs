using System;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// LSD radix sort for fixed-width composite keys used by Mercury's secondary
/// index rebuild paths (ADR-032). Comparator-free byte-bucketing produces sorted
/// output with stable ordering, no per-comparison overhead, and zero allocations
/// inside the sort.
/// </summary>
/// <remarks>
/// <para><strong>Caller-owned scratch.</strong> The <c>scratch</c> span must be
/// the same length as <c>data</c>. The sort uses it as a ping-pong buffer and
/// leaves it in unspecified state on return. Allocate once per rebuild
/// (typically 16 M entries — 512 MB on the LOH for ReferenceKey, 192 MB for
/// TrigramEntry) and reuse across all chunks.</para>
///
/// <para><strong>Zero allocations inside the sort.</strong> 256-bucket histogram
/// and prefix-sum offset arrays live on the stack via <c>stackalloc uint[256]</c>
/// (1 KB each, well below stack-overflow limits per ADR-009).</para>
///
/// <para><strong>Signed-long handling.</strong> Mercury composite keys carry
/// signed <c>long</c> fields. Naïve unsigned byte comparison would order
/// negatives after positives, breaking the lexicographic Compare semantics. The
/// MSB byte of each signed field is XOR'd with 0x80 during extraction (the
/// standard biased-radix trick), producing a byte-order-equals-signed-order
/// mapping. The bias is symmetric on both histogram and distribute passes;
/// stored data is never modified.</para>
///
/// <para><strong>Skip-trivial-passes optimization.</strong> If a histogram puts
/// all entries in a single bucket (i.e., the byte is identical across all
/// entries), the distribute pass is a no-op and is skipped. Mercury atom IDs
/// are typically 30-40 bits, so the high bytes of each long field are zero
/// across all entries; this typically reduces 32 worst-case ReferenceKey passes
/// to 12-16 effective passes.</para>
/// </remarks>
internal static class RadixSort
{
    /// <summary>
    /// Sort <paramref name="data"/> lexicographically by Graph, Primary, Secondary,
    /// Tertiary (most-significant-first), matching <c>ReferenceQuadIndex.ReferenceKey.Compare</c>.
    /// On return <paramref name="data"/> contains the sorted entries; <paramref name="scratch"/>
    /// holds undefined contents.
    /// </summary>
    internal static unsafe void SortInPlace(
        Span<ReferenceQuadIndex.ReferenceKey> data,
        Span<ReferenceQuadIndex.ReferenceKey> scratch)
    {
        if (data.Length != scratch.Length)
            throw new ArgumentException("scratch must be the same length as data", nameof(scratch));
        if (data.Length < 2) return;

        Span<uint> histogram = stackalloc uint[256];
        Span<uint> offsets = stackalloc uint[256];

        // ReferenceKey field byte offsets in the struct: Graph @ 0, Primary @ 8,
        // Secondary @ 16, Tertiary @ 24. Sort priority (high to low): Graph,
        // Primary, Secondary, Tertiary. LSD pass order processes lowest-priority
        // field first, highest last → Tertiary, Secondary, Primary, Graph.
        // The array-initializer form `stackalloc int[N] { ... }` produces a heap
        // allocation per call on .NET 10; explicit indexed init keeps it on the stack.
        Span<int> fieldBaseOffsets = stackalloc int[4];
        fieldBaseOffsets[0] = 24;
        fieldBaseOffsets[1] = 16;
        fieldBaseOffsets[2] = 8;
        fieldBaseOffsets[3] = 0;

        Span<ReferenceQuadIndex.ReferenceKey> src = data;
        Span<ReferenceQuadIndex.ReferenceKey> dst = scratch;
        int distributes = 0;

        for (int field = 0; field < 4; field++)
        {
            int fieldBase = fieldBaseOffsets[field];
            for (int byteIdx = 0; byteIdx < 8; byteIdx++)
            {
                int absByteIdx = fieldBase + byteIdx;
                bool isMsb = byteIdx == 7;
                int n = src.Length;

                // Histogram pass
                histogram.Clear();
                fixed (ReferenceQuadIndex.ReferenceKey* srcPtr = src)
                {
                    byte* srcBase = (byte*)srcPtr;
                    if (isMsb)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            byte b = (byte)(srcBase[i * 32 + absByteIdx] ^ 0x80);
                            histogram[b]++;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < n; i++)
                        {
                            histogram[srcBase[i * 32 + absByteIdx]]++;
                        }
                    }
                }

                // Skip-trivial: a single bucket holding all entries means this byte is constant
                if (IsTrivialHistogram(histogram, (uint)n)) continue;

                // Prefix sum → write offsets
                uint sum = 0;
                for (int b = 0; b < 256; b++)
                {
                    offsets[b] = sum;
                    sum += histogram[b];
                }

                // Distribute pass
                fixed (ReferenceQuadIndex.ReferenceKey* srcPtr = src)
                fixed (ReferenceQuadIndex.ReferenceKey* dstPtr = dst)
                {
                    byte* srcBase = (byte*)srcPtr;
                    if (isMsb)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            byte b = (byte)(srcBase[i * 32 + absByteIdx] ^ 0x80);
                            dstPtr[offsets[b]++] = srcPtr[i];
                        }
                    }
                    else
                    {
                        for (int i = 0; i < n; i++)
                        {
                            dstPtr[offsets[srcBase[i * 32 + absByteIdx]]++] = srcPtr[i];
                        }
                    }
                }

                distributes++;
                var tmp = src; src = dst; dst = tmp;
            }
        }

        // Odd number of distributes leaves the result in scratch; copy back to data.
        if ((distributes & 1) != 0)
            src.CopyTo(data);
    }

    /// <summary>
    /// Sort <paramref name="data"/> by Hash (unsigned uint32), then by AtomId
    /// (signed long). On return <paramref name="data"/> contains the sorted
    /// entries; <paramref name="scratch"/> holds undefined contents.
    /// </summary>
    internal static unsafe void SortInPlace(
        Span<TrigramEntry> data,
        Span<TrigramEntry> scratch)
    {
        if (data.Length != scratch.Length)
            throw new ArgumentException("scratch must be the same length as data", nameof(scratch));
        if (data.Length < 2) return;

        Span<uint> histogram = stackalloc uint[256];
        Span<uint> offsets = stackalloc uint[256];

        // TrigramEntry: [Hash @ 0..3 (uint32, unsigned)][AtomId @ 4..11 (long, signed)].
        // Sort priority: Hash (high), AtomId (low). LSD order: AtomId 4..11 first,
        // then Hash 0..3. Only struct offset 11 (AtomId MSB) needs signed bias —
        // Hash MSB at offset 3 is unsigned, no bias.
        // The array-initializer form `stackalloc int[N] { ... }` produces a heap
        // allocation per call on .NET 10; explicit indexed init keeps it on the stack.
        Span<int> processOrder = stackalloc int[12];
        processOrder[0] = 4; processOrder[1] = 5; processOrder[2] = 6; processOrder[3] = 7;
        processOrder[4] = 8; processOrder[5] = 9; processOrder[6] = 10; processOrder[7] = 11;
        processOrder[8] = 0; processOrder[9] = 1; processOrder[10] = 2; processOrder[11] = 3;

        Span<TrigramEntry> src = data;
        Span<TrigramEntry> dst = scratch;
        int distributes = 0;

        for (int passIdx = 0; passIdx < 12; passIdx++)
        {
            int absByteIdx = processOrder[passIdx];
            bool isSignedMsb = absByteIdx == 11;
            int n = src.Length;

            histogram.Clear();
            fixed (TrigramEntry* srcPtr = src)
            {
                byte* srcBase = (byte*)srcPtr;
                if (isSignedMsb)
                {
                    for (int i = 0; i < n; i++)
                    {
                        byte b = (byte)(srcBase[i * 12 + absByteIdx] ^ 0x80);
                        histogram[b]++;
                    }
                }
                else
                {
                    for (int i = 0; i < n; i++)
                    {
                        histogram[srcBase[i * 12 + absByteIdx]]++;
                    }
                }
            }

            if (IsTrivialHistogram(histogram, (uint)n)) continue;

            uint sum = 0;
            for (int b = 0; b < 256; b++)
            {
                offsets[b] = sum;
                sum += histogram[b];
            }

            fixed (TrigramEntry* srcPtr = src)
            fixed (TrigramEntry* dstPtr = dst)
            {
                byte* srcBase = (byte*)srcPtr;
                if (isSignedMsb)
                {
                    for (int i = 0; i < n; i++)
                    {
                        byte b = (byte)(srcBase[i * 12 + absByteIdx] ^ 0x80);
                        dstPtr[offsets[b]++] = srcPtr[i];
                    }
                }
                else
                {
                    for (int i = 0; i < n; i++)
                    {
                        dstPtr[offsets[srcBase[i * 12 + absByteIdx]]++] = srcPtr[i];
                    }
                }
            }

            distributes++;
            var tmp = src; src = dst; dst = tmp;
        }

        if ((distributes & 1) != 0)
            src.CopyTo(data);
    }

    private static bool IsTrivialHistogram(ReadOnlySpan<uint> histogram, uint total)
    {
        for (int b = 0; b < 256; b++)
        {
            if (histogram[b] == total) return true;
        }
        return false;
    }
}
