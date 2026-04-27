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
internal sealed class MoveToFrontInverse
{
    /// <summary>
    /// 256-entry permutation list. Allocated once per stream instance and reused
    /// across blocks via <see cref="Initialize"/> / <see cref="Reset"/>. One byte[]
    /// per active stream is the correct allocation footprint — fits in 4 cache lines.
    /// </summary>
    private readonly byte[] _list = new byte[256];

    /// <summary>
    /// Initialize the MTF list from the block's symbol map. <paramref name="alphabet"/>
    /// holds the bytes present in the block in ascending byte order; the MTF list is
    /// initialized to that alphabet (entries beyond <paramref name="length"/> are
    /// unused and set to zero — correct programs never read them).
    /// </summary>
    public void Initialize(ReadOnlySpan<byte> alphabet, int length)
    {
        if ((uint)length > 256)
            throw new ArgumentOutOfRangeException(nameof(length));

        for (int i = 0; i < length; i++) _list[i] = alphabet[i];
        for (int i = length; i < 256; i++) _list[i] = 0;
    }

    /// <summary>
    /// Reset to identity (list[i] = i for all i). Useful for tests; bzip2 blocks
    /// always initialize from the symbol map so this is rarely called in production.
    /// </summary>
    public void Reset()
    {
        for (int i = 0; i < 256; i++) _list[i] = (byte)i;
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

        byte symbol = _list[index];

        // Shift entries [0, index) right by one; place symbol at 0. Buffer.BlockCopy
        // handles overlapping forward moves correctly via memmove semantics.
        if (index > 0)
            Buffer.BlockCopy(_list, 0, _list, 1, index);
        _list[0] = symbol;

        return symbol;
    }

    /// <summary>Read-only view of the current list state — for tests and diagnostics.</summary>
    public ReadOnlySpan<byte> ListView => _list;
}
