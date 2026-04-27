using System;
using System.Runtime.CompilerServices;

namespace SkyOmega.Mercury.Compression;

/// <summary>
/// Move-to-front inverse for bzip2 block decode. ADR-036 Decision 2.
/// </summary>
/// <remarks>
/// <para>
/// MTF maintains a 256-entry symbol list initialized to identity (<c>list[i] = i</c>).
/// For each input index <c>k</c>, the inverse outputs <c>list[k]</c>, then moves that
/// byte to position 0 (shifting positions <c>0..k-1</c> right by one).
/// </para>
/// <para>
/// Implemented as a tight loop with byte-level shifts; the 256-byte list fits in 4 cache
/// lines and the per-symbol cost is dominated by the shift, which is O(k) on average.
/// On bzip2 streams of typical input, k tends to be small (the literal alphabet is
/// frequency-biased after Huffman + RLE2), so the linear shift is competitive with
/// linked-list approaches and substantially more cache-friendly.
/// </para>
/// <para>
/// The struct is mutable and reused across symbols within a block. Reset between blocks
/// via <see cref="Reset"/>; do not allocate per-symbol.
/// </para>
/// </remarks>
internal struct MoveToFrontInverse
{
    /// <summary>
    /// 256-entry inline buffer. <c>[InlineArray]</c> places the bytes directly inside
    /// the struct — zero heap allocation, zero indirection, fits in 4 cache lines.
    /// Span access via the implicit conversion gives pointer-equivalent codegen.
    /// </summary>
    private MtfBuffer _list;

    [InlineArray(256)]
    private struct MtfBuffer
    {
        private byte _e0;
    }

    /// <summary>
    /// Initialize the MTF list from the block's symbol map. <paramref name="alphabet"/>
    /// holds the bytes present in the block in ascending byte order; entries beyond
    /// <paramref name="length"/> are zeroed (correct programs never read them).
    /// </summary>
    public void Initialize(ReadOnlySpan<byte> alphabet, int length)
    {
        if ((uint)length > 256)
            throw new ArgumentOutOfRangeException(nameof(length));

        Span<byte> list = _list;
        for (int i = 0; i < length; i++) list[i] = alphabet[i];
        if (length < 256) list.Slice(length).Clear();
    }

    /// <summary>Reset to identity (list[i] = i for all i).</summary>
    public void Reset()
    {
        Span<byte> list = _list;
        for (int i = 0; i < 256; i++) list[i] = (byte)i;
    }

    /// <summary>
    /// Output the byte at position <paramref name="index"/>, then move that entry
    /// to position 0 (shifting <c>0..index-1</c> right). Returns the output byte.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Decode(int index)
    {
        if ((uint)index >= 256)
            throw new ArgumentOutOfRangeException(nameof(index));

        Span<byte> list = _list;
        byte symbol = list[index];

        // Forward-overlapping move of [0, index) into [1, index+1). Span<byte>.CopyTo
        // handles the overlap correctly via memmove semantics.
        if (index > 0)
            list.Slice(0, index).CopyTo(list.Slice(1, index));
        list[0] = symbol;

        return symbol;
    }

    /// <summary>Copy the current list into <paramref name="destination"/> (for tests/diagnostics).</summary>
    public void CopyListTo(Span<byte> destination)
    {
        Span<byte> list = _list;
        list.Slice(0, Math.Min(256, destination.Length)).CopyTo(destination);
    }
}
