using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace SkyOmega.Mercury.Storage.Mphf;

/// <summary>
/// Minimal Perfect Hash Function over a fixed key set, BBHash variant
/// (Limasset, Rizk, Chikhi 2017). Maps each input key to a unique
/// integer in <c>[0, N)</c> with no collisions. Storage overhead ~1.6 bits/key
/// at gamma 2.0 across 3–5 levels.
/// </summary>
/// <remarks>
/// <para>
/// <b>Important:</b> the position returned by <see cref="Lookup"/> is BBHash's
/// natural assignment, *not* the input key's sorted position. Use
/// <see cref="MphfTranslationTable"/> to translate.
/// </para>
/// <para>
/// <b>Algorithm:</b> for each level i, allocate a bit vector of γ × |remaining keys| bits.
/// Hash each remaining key with seed = baseSeed + i. If exactly one key hashes to a
/// position, set the bit; if multiple keys collide, clear the bit and bump the colliders
/// to the next level. After 3–5 levels the bumped set is empty (tail keys → final level
/// stores them densely).
/// </para>
/// </remarks>
internal sealed class BBHash
{
    /// <summary>File-format magic: "MPHF" big-endian.</summary>
    public const uint Magic = 0x4D504846u;

    /// <summary>File-format version. Bump on incompatible change.</summary>
    public const uint Version = 1;

    public ulong BaseSeed { get; }
    public BitVector[] Levels { get; }
    public long[] LevelOffsets { get; }   // cumulative popcount up to (but not including) level i
    public long NumKeys { get; }

    internal BBHash(ulong baseSeed, BitVector[] levels, long[] levelOffsets, long numKeys)
    {
        BaseSeed = baseSeed;
        Levels = levels;
        LevelOffsets = levelOffsets;
        NumKeys = numKeys;
    }

    /// <summary>
    /// Compute the MPHF position for a key. Returns a value in <c>[0, NumKeys)</c> for
    /// keys in the input set; for keys NOT in the set, returns <c>some</c> value in the
    /// same range (verification via the translation table + atom-bytes compare is
    /// required to detect not-in-set).
    /// </summary>
    public long Lookup(ReadOnlySpan<byte> key)
    {
        for (int level = 0; level < Levels.Length; level++)
        {
            ulong seed = BaseSeed + (ulong)level;
            ulong h = MphfHash.Hash64(key, seed);
            long pos = (long)(h % (ulong)Levels[level].BitCount);
            if (Levels[level].Get(pos))
            {
                return LevelOffsets[level] + Levels[level].Rank(pos);
            }
        }
        // Should not happen for in-set keys when construction is correct.
        // Out-of-set keys may fall through; return -1 to signal "not in any level."
        return -1;
    }

    /// <summary>
    /// Serialize the BBHash blob to a binary writer. Format:
    /// <code>
    /// [u32 magic = 0x4D504846]
    /// [u32 version = 1]
    /// [u64 num_keys]
    /// [u64 base_seed]
    /// [u32 level_count]
    /// per level (in order):
    ///   [u64 bit_count]
    ///   [u64 word_count]
    ///   [u32 rank_table_count]
    ///   [u64 level_offset]
    ///   [word_count × u64 words]
    ///   [rank_table_count × u32 rank_table]
    /// </code>
    /// </summary>
    public void WriteTo(string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
        Span<byte> buf16 = stackalloc byte[16];
        Span<byte> buf8 = stackalloc byte[8];
        Span<byte> buf4 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf16.Slice(0, 4), Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(buf16.Slice(4, 4), Version);
        BinaryPrimitives.WriteInt64LittleEndian(buf16.Slice(8, 8), NumKeys);
        fs.Write(buf16);

        BinaryPrimitives.WriteUInt64LittleEndian(buf8, BaseSeed);
        fs.Write(buf8);
        BinaryPrimitives.WriteInt32LittleEndian(buf4, Levels.Length);
        fs.Write(buf4);

        for (int i = 0; i < Levels.Length; i++)
        {
            var level = Levels[i];
            var rank = level.RankTable ?? throw new InvalidOperationException($"Level {i} rank table not built");
            BinaryPrimitives.WriteInt64LittleEndian(buf8, level.BitCount);
            fs.Write(buf8);
            BinaryPrimitives.WriteInt64LittleEndian(buf8, level.Words.LongLength);
            fs.Write(buf8);
            BinaryPrimitives.WriteInt32LittleEndian(buf4, rank.Length);
            fs.Write(buf4);
            BinaryPrimitives.WriteInt64LittleEndian(buf8, LevelOffsets[i]);
            fs.Write(buf8);

            // Words
            var wordBytes = new byte[level.Words.Length * 8];
            for (int w = 0; w < level.Words.Length; w++)
                BinaryPrimitives.WriteUInt64LittleEndian(wordBytes.AsSpan(w * 8, 8), level.Words[w]);
            fs.Write(wordBytes);
            // Rank table
            var rankBytes = new byte[rank.Length * 4];
            for (int r = 0; r < rank.Length; r++)
                BinaryPrimitives.WriteUInt32LittleEndian(rankBytes.AsSpan(r * 4, 4), rank[r]);
            fs.Write(rankBytes);
        }
    }

    /// <summary>Read BBHash blob from disk.</summary>
    public static BBHash ReadFrom(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        Span<byte> hdr = stackalloc byte[16];
        if (fs.Read(hdr) != 16) throw new InvalidDataException("MPHF: short header");
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(hdr.Slice(0, 4));
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(4, 4));
        long numKeys = BinaryPrimitives.ReadInt64LittleEndian(hdr.Slice(8, 8));
        if (magic != Magic) throw new InvalidDataException($"MPHF: bad magic 0x{magic:X8}");
        if (version != Version) throw new InvalidDataException($"MPHF: unsupported version {version}");

        Span<byte> buf8 = stackalloc byte[8];
        Span<byte> buf4 = stackalloc byte[4];
        if (fs.Read(buf8) != 8) throw new InvalidDataException("MPHF: short base_seed");
        ulong baseSeed = BinaryPrimitives.ReadUInt64LittleEndian(buf8);
        if (fs.Read(buf4) != 4) throw new InvalidDataException("MPHF: short level_count");
        int levelCount = BinaryPrimitives.ReadInt32LittleEndian(buf4);

        var levels = new BitVector[levelCount];
        var levelOffsets = new long[levelCount];

        for (int i = 0; i < levelCount; i++)
        {
            if (fs.Read(buf8) != 8) throw new InvalidDataException("MPHF: short level header");
            long bitCount = BinaryPrimitives.ReadInt64LittleEndian(buf8);
            if (fs.Read(buf8) != 8) throw new InvalidDataException("MPHF: short word_count");
            long wordCount = BinaryPrimitives.ReadInt64LittleEndian(buf8);
            if (fs.Read(buf4) != 4) throw new InvalidDataException("MPHF: short rank_table_count");
            int rankCount = BinaryPrimitives.ReadInt32LittleEndian(buf4);
            if (fs.Read(buf8) != 8) throw new InvalidDataException("MPHF: short level_offset");
            long levelOffset = BinaryPrimitives.ReadInt64LittleEndian(buf8);

            var bv = new BitVector(bitCount);
            int wordBytesLen = checked((int)(wordCount * 8));
            var wordBytes = new byte[wordBytesLen];
            int got = 0;
            while (got < wordBytesLen)
            {
                int n = fs.Read(wordBytes, got, wordBytesLen - got);
                if (n == 0) throw new InvalidDataException("MPHF: short level words");
                got += n;
            }
            for (int w = 0; w < wordCount; w++)
            {
                bv.Words[w] = BinaryPrimitives.ReadUInt64LittleEndian(wordBytes.AsSpan(w * 8, 8));
            }
            int rankBytesLen = checked(rankCount * 4);
            var rankBytes = new byte[rankBytesLen];
            got = 0;
            while (got < rankBytesLen)
            {
                int n = fs.Read(rankBytes, got, rankBytesLen - got);
                if (n == 0) throw new InvalidDataException("MPHF: short level rank");
                got += n;
            }
            var rank = new uint[rankCount];
            for (int r = 0; r < rankCount; r++)
                rank[r] = BinaryPrimitives.ReadUInt32LittleEndian(rankBytes.AsSpan(r * 4, 4));
            bv.RestoreRankTable(rank);
            levels[i] = bv;
            levelOffsets[i] = levelOffset;
        }

        return new BBHash(baseSeed, levels, levelOffsets, numKeys);
    }
}
