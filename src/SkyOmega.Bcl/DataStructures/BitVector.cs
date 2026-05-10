using System;
using System.Numerics;

namespace SkyOmega.Bcl.DataStructures;

/// <summary>
/// Compact bit array backed by <c>ulong[]</c>. Supports get/set + precomputed
/// rank queries. Used by minimal-perfect-hash data structures (BBHash) and other
/// substrate-level workloads that need >int.MaxValue logical bit addressing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layout.</b> Bit <c>i</c> lives in word <c>i / 64</c>, bit position <c>i % 64</c>
/// (LSB-first). <c>BitCount</c> is the logical length; the backing array may be
/// 1 word longer to accommodate the off-by-one for whole-word reads.
/// </para>
/// <para>
/// <b>Capacity.</b> The backing <c>ulong[]</c> is bounded by .NET's int.MaxValue
/// element cap, giving a maximum of ~2.15 G ulongs = ~137 G bits. For substrate
/// workloads at 4 B atoms × γ=2.0 = 8 B bits, this is well within bounds.
/// </para>
/// <para>
/// <b>Rank table.</b> <see cref="BuildRankTable"/> precomputes cumulative popcount
/// every <see cref="RankBlockSize"/> bits. <see cref="Rank"/> returns the count of
/// set bits in [0, position] in O(1) — one rank-table lookup + one popcount over the
/// remainder of the active word's preceding bits.
/// </para>
/// </remarks>
public sealed class BitVector
{
    /// <summary>Bits per rank-table block. 512 = 8 words; rank lookup table is N / 512 entries.</summary>
    public const int RankBlockSize = 512;

    private readonly ulong[] _words;
    private uint[]? _rank;
    public long BitCount { get; }

    public BitVector(long bitCount)
    {
        if (bitCount < 0) throw new ArgumentOutOfRangeException(nameof(bitCount));
        BitCount = bitCount;
        long wordCount = (bitCount + 63) / 64;
        _words = new ulong[wordCount];
    }

    public bool Get(long bit)
    {
        long word = bit >> 6;
        int b = (int)(bit & 63);
        return ((_words[word] >> b) & 1UL) != 0;
    }

    public void Set(long bit)
    {
        long word = bit >> 6;
        int b = (int)(bit & 63);
        _words[word] |= 1UL << b;
    }

    public void Clear(long bit)
    {
        long word = bit >> 6;
        int b = (int)(bit & 63);
        _words[word] &= ~(1UL << b);
    }

    public ulong[] Words => _words;

    /// <summary>
    /// Total number of set bits in the bit vector.
    /// </summary>
    public long PopCount()
    {
        long total = 0;
        for (int i = 0; i < _words.Length; i++)
            total += BitOperations.PopCount(_words[i]);
        return total;
    }

    /// <summary>
    /// Build the rank table. Must be called before <see cref="Rank"/>.
    /// </summary>
    public void BuildRankTable()
    {
        long blockCount = (BitCount + RankBlockSize - 1) / RankBlockSize;
        _rank = new uint[blockCount + 1];
        uint cumulative = 0;
        long bitsPerWord = 64;
        long wordsPerBlock = RankBlockSize / bitsPerWord;
        for (long block = 0; block < blockCount; block++)
        {
            _rank[block] = cumulative;
            long startWord = block * wordsPerBlock;
            long endWord = Math.Min(startWord + wordsPerBlock, _words.Length);
            for (long w = startWord; w < endWord; w++)
            {
                cumulative += (uint)BitOperations.PopCount(_words[w]);
            }
        }
        _rank[blockCount] = cumulative;
    }

    /// <summary>
    /// Number of set bits in <c>[0, position)</c>. Position is exclusive.
    /// O(1) via rank table + popcount over partial word.
    /// </summary>
    public long Rank(long position)
    {
        if (_rank is null) throw new InvalidOperationException("Rank table not built. Call BuildRankTable first.");
        if (position <= 0) return 0;
        if (position > BitCount) position = BitCount;

        long blockIdx = position / RankBlockSize;
        long blockStartBit = blockIdx * RankBlockSize;
        long bitsToScan = position - blockStartBit;

        long rank = _rank[blockIdx];
        long wordsPerBlock = RankBlockSize / 64;
        long startWord = blockIdx * wordsPerBlock;
        long fullWords = bitsToScan / 64;
        for (long w = 0; w < fullWords && (startWord + w) < _words.Length; w++)
        {
            rank += BitOperations.PopCount(_words[startWord + w]);
        }
        long partialBits = bitsToScan % 64;
        if (partialBits > 0)
        {
            long partialWordIdx = startWord + fullWords;
            if (partialWordIdx < _words.Length)
            {
                ulong mask = (1UL << (int)partialBits) - 1;
                rank += BitOperations.PopCount(_words[partialWordIdx] & mask);
            }
        }
        return rank;
    }

    public uint[]? RankTable => _rank;

    public void RestoreRankTable(uint[] table)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));
        _rank = table;
    }
}
