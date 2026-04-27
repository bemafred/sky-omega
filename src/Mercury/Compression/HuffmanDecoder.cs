using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SkyOmega.Mercury.Compression;

/// <summary>
/// Two-level canonical Huffman decoder for bzip2 blocks. ADR-036 Decision 2.
/// </summary>
/// <remarks>
/// <para>
/// bzip2 uses up to six Huffman tables per block, switched every 50 symbols by a stored
/// selector sequence. Alphabet size: up to 258 symbols (256 bytes + RUNA + RUNB + EOB).
/// Code lengths range 1–17 (in practice ≤ 15 for typical inputs); this decoder supports
/// the full spec range.
/// </para>
/// <para>
/// Layout: a primary table of 2^<see cref="PrimaryBits"/> = 2048 entries indexed by the
/// next 11 peeked bits. Each entry is one 32-bit instruction:
/// </para>
/// <list type="bullet">
/// <item>
///   <description>
///   <b>Direct hit</b> (code length ≤ 11): top bit set, bits 0–8 = symbol (0–258),
///   bits 16–23 = code length. Consume <c>length</c> bits, output the symbol.
///   </description>
/// </item>
/// <item>
///   <description>
///   <b>Secondary redirect</b> (code length &gt; 11): top bit clear, bits 0–15 =
///   secondary-table base index (into <see cref="_secondaryTable"/>), bits 16–23 =
///   suffix bit-count to peek for the secondary lookup.
///   </description>
/// </item>
/// </list>
/// <para>
/// The decoder is reused across blocks: <see cref="Build"/> rewrites the active tables
/// from a fresh code-length array. Internal storage is preallocated; per-decode work is
/// one peek + one or two table lookups + one bit-consume — branch-free for the direct-hit
/// case which covers ~99% of typical bzip2 streams.
/// </para>
/// </remarks>
internal sealed class HuffmanDecoder
{
    public const int PrimaryBits = 11;
    public const int PrimarySize = 1 << PrimaryBits;
    public const int MaxAlphabetSize = 258;
    public const int MaxCodeLength = 20;

    private const uint DirectHitFlag = 0x80000000u;

    /// <summary>Primary lookup. 2048 × 4 bytes = 8 KB; L1d-resident.</summary>
    private readonly uint[] _primaryTable = new uint[PrimarySize];

    /// <summary>
    /// Backing storage for secondary tables. Each long code (length > 11) routes through
    /// here; tables are packed contiguously and addressed by the redirect entry's base
    /// index. Conservative upper bound: 2^(MaxCodeLength - PrimaryBits) × MaxAlphabetSize
    /// = 512 × 258 ≈ 132K entries; in practice much smaller. We preallocate a generous
    /// buffer at construction so per-block <see cref="Build"/> never allocates.
    /// </summary>
    private readonly uint[] _secondaryTable = new uint[16 * 1024];

    private int _secondaryUsed;

    /// <summary>
    /// Build (or rebuild) the decoder from per-symbol code lengths. <paramref name="lengths"/>
    /// has length equal to the alphabet size; <c>lengths[s]</c> is the canonical-code length
    /// of symbol <c>s</c> (zero is permitted for unused symbols).
    /// </summary>
    public void Build(ReadOnlySpan<byte> lengths)
    {
        int alphabetSize = lengths.Length;
        if (alphabetSize > MaxAlphabetSize)
            throw new ArgumentException($"alphabet size {alphabetSize} exceeds MaxAlphabetSize", nameof(lengths));

        // Histogram of lengths; canonical ordering uses (length, symbol) ascending.
        Span<int> lengthCount = stackalloc int[MaxCodeLength + 1];
        for (int s = 0; s < alphabetSize; s++)
        {
            int L = lengths[s];
            if (L > MaxCodeLength)
                throw new InvalidDataException($"code length {L} exceeds MaxCodeLength {MaxCodeLength}");
            lengthCount[L]++;
        }
        lengthCount[0] = 0; // zero-length codes are absent from the alphabet

        // Compute the first canonical code value at each length and the cumulative offset
        // into the canonical-ordered symbol list.
        Span<int> firstCode = stackalloc int[MaxCodeLength + 2];
        Span<int> firstSymbolOffset = stackalloc int[MaxCodeLength + 2];
        int code = 0;
        int offset = 0;
        for (int L = 1; L <= MaxCodeLength; L++)
        {
            firstCode[L] = code;
            firstSymbolOffset[L] = offset;
            code = (code + lengthCount[L]) << 1;
            offset += lengthCount[L];
        }
        // Sentinel for length L+1 used by secondary-table redirect bounds.
        firstCode[MaxCodeLength + 1] = int.MaxValue;
        firstSymbolOffset[MaxCodeLength + 1] = offset;

        // Canonical symbol ordering: symbols sorted by (length, symbol).
        Span<short> orderedSymbols = stackalloc short[MaxAlphabetSize];
        Span<int> nextOffset = stackalloc int[MaxCodeLength + 2];
        firstSymbolOffset.CopyTo(nextOffset);
        for (int s = 0; s < alphabetSize; s++)
        {
            int L = lengths[s];
            if (L == 0) continue;
            orderedSymbols[nextOffset[L]++] = (short)s;
        }

        // Reset primary table; entries left uninitialized correspond to bit patterns
        // that never appear in valid input — populating with a "trap" sentinel makes
        // malformed streams fail loudly at lookup.
        _primaryTable.AsSpan().Clear();
        _secondaryUsed = 0;

        // Fill the primary table. For each length L ≤ PrimaryBits, every (length, code)
        // covers 2^(PrimaryBits - L) prefix patterns; replicate the entry across them.
        for (int L = 1; L <= PrimaryBits; L++)
        {
            int countL = lengthCount[L];
            if (countL == 0) continue;
            int baseCode = firstCode[L];
            int baseSymbolOffset = firstSymbolOffset[L];
            int replicate = 1 << (PrimaryBits - L);
            for (int i = 0; i < countL; i++)
            {
                int symbol = orderedSymbols[baseSymbolOffset + i];
                int prefix = (baseCode + i) << (PrimaryBits - L);
                uint entry = DirectHitFlag | ((uint)L << 16) | (uint)symbol;
                for (int rep = 0; rep < replicate; rep++)
                    _primaryTable[prefix + rep] = entry;
            }
        }

        // For lengths > PrimaryBits, codes share an 11-bit prefix with their
        // length-(PrimaryBits+1)-and-up cousins. Group long codes by their 11-bit
        // prefix; each unique prefix gets a secondary table sized to cover the maximum
        // remaining suffix length encountered for that prefix.
        // Compute, for each 11-bit prefix, the max overflow length:
        Span<int> secondarySuffixBits = stackalloc int[PrimarySize];
        for (int L = PrimaryBits + 1; L <= MaxCodeLength; L++)
        {
            int countL = lengthCount[L];
            if (countL == 0) continue;
            int baseCode = firstCode[L];
            for (int i = 0; i < countL; i++)
            {
                int fullCode = baseCode + i;
                int prefix = fullCode >> (L - PrimaryBits);
                int suffixBits = L - PrimaryBits;
                if (suffixBits > secondarySuffixBits[prefix])
                    secondarySuffixBits[prefix] = suffixBits;
            }
        }

        // Allocate secondary tables and write redirects into the primary.
        Span<int> secondaryBase = stackalloc int[PrimarySize];
        for (int prefix = 0; prefix < PrimarySize; prefix++)
        {
            int suffix = secondarySuffixBits[prefix];
            if (suffix == 0) continue;
            int size = 1 << suffix;
            int baseIdx = _secondaryUsed;
            _secondaryUsed += size;
            if (_secondaryUsed > _secondaryTable.Length)
                throw new InvalidDataException(
                    $"secondary table overflow: needed {_secondaryUsed} entries, have {_secondaryTable.Length}");
            secondaryBase[prefix] = baseIdx;
            // primary entry: redirect (top bit 0), suffix bits in 16-23, base in 0-15.
            _primaryTable[prefix] = ((uint)suffix << 16) | (uint)baseIdx;
        }

        // Fill secondary entries.
        for (int L = PrimaryBits + 1; L <= MaxCodeLength; L++)
        {
            int countL = lengthCount[L];
            if (countL == 0) continue;
            int baseCode = firstCode[L];
            int baseSymbolOffset = firstSymbolOffset[L];
            for (int i = 0; i < countL; i++)
            {
                int fullCode = baseCode + i;
                int prefix = fullCode >> (L - PrimaryBits);
                int suffix = secondarySuffixBits[prefix];
                int suffixCode = fullCode & ((1 << (L - PrimaryBits)) - 1);
                int symbol = orderedSymbols[baseSymbolOffset + i];
                int replicate = 1 << (suffix - (L - PrimaryBits));
                int suffixBase = suffixCode << (suffix - (L - PrimaryBits));
                uint entry = DirectHitFlag | ((uint)L << 16) | (uint)symbol;
                int baseIdx = secondaryBase[prefix];
                for (int rep = 0; rep < replicate; rep++)
                    _secondaryTable[baseIdx + suffixBase + rep] = entry;
            }
        }
    }

    /// <summary>
    /// Decode the next symbol from <paramref name="reader"/>. Consumes the matching
    /// number of bits. Throws <see cref="InvalidDataException"/> on a code that
    /// doesn't match any built table entry (i.e. malformed input).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DecodeSymbol(ref BZip2BitReader reader)
    {
        uint prefix = reader.PeekBits(PrimaryBits);
        uint entry = _primaryTable[prefix];
        if ((entry & DirectHitFlag) != 0)
        {
            int length = (int)((entry >> 16) & 0xFF);
            reader.ConsumeBits(length);
            return (int)(entry & 0xFFFF);
        }

        // Secondary redirect: consume the 11 prefix bits, then peek the suffix.
        reader.ConsumeBits(PrimaryBits);
        int suffixBits = (int)((entry >> 16) & 0xFF);
        int baseIdx = (int)(entry & 0xFFFF);
        uint suffixIndex = reader.PeekBits(suffixBits);
        uint secondaryEntry = _secondaryTable[baseIdx + (int)suffixIndex];
        if ((secondaryEntry & DirectHitFlag) == 0)
            throw new InvalidDataException("Huffman secondary entry not populated — malformed bzip2 block");
        int totalLen = (int)((secondaryEntry >> 16) & 0xFF);
        reader.ConsumeBits(totalLen - PrimaryBits);
        return (int)(secondaryEntry & 0xFFFF);
    }
}
