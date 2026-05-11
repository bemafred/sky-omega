using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using SkyOmega.Bcl.DataStructures;
using SkyOmega.Bcl.Hashing;

namespace SkyOmega.Mercury.Storage.Mphf;

/// <summary>
/// Minimal Perfect Hash Function over a fixed key set, BBHash variant
/// (Limasset, Rizk, Chikhi 2017) with a dense final-level fallback.
/// Maps each input key to a unique integer in <c>[0, N)</c> with no collisions.
/// </summary>
/// <remarks>
/// <para>
/// <b>Important:</b> the position returned by <see cref="Lookup"/> is BBHash's
/// natural assignment, *not* the input key's sorted position. Use
/// <see cref="MphfTranslationTable"/> to translate.
/// </para>
/// <para>
/// <b>Algorithm:</b> for each level i (up to <see cref="BBHashBuilder.MaxLevels"/>),
/// allocate a bit vector of γ × |remaining keys| bits. Hash each remaining key
/// with seed = baseSeed + i. If exactly one key hashes to a position, set the bit;
/// if multiple keys collide, clear the bit and bump the colliders to the next level.
/// At γ=2.0, expected convergence for N=4 × 10⁹ is ~24 levels; <see cref="BBHashBuilder.MaxLevels"/>
/// is 40 to give comfortable variance headroom.
/// </para>
/// <para>
/// <b>Dense final level.</b> Any keys still un-placed after <see cref="BBHashBuilder.MaxLevels"/>
/// (typically 0; rarely a small number) are stored in a flat byte-array set with
/// explicit byte comparison at lookup time. Bounded by <see cref="BBHashBuilder.MaxDenseKeys"/>;
/// beyond that the builder fails fast. The dense path makes the substrate's convergence
/// guarantee deterministic — there is no "pathological seed" failure mode at any N.
/// Cycle 10 Phase 3 (2026-05-10) hit the iterative-only failure mode with 2 keys
/// un-converged at 4 B atoms; the dense fallback closes that gap structurally.
/// </para>
/// </remarks>
internal sealed class BBHash
{
    /// <summary>File-format magic: "MPHF" big-endian.</summary>
    public const uint Magic = 0x4D504846u;

    /// <summary>File-format version. Bump on incompatible change.</summary>
    /// <remarks>
    /// Version 1 (1.7.52 / cycle 10 Phase 2): iterative levels only; no dense fallback.
    /// Version 2 (1.7.55 / cycle 10 Phase 3 fix): adds dense-final-level fields.
    /// Version 1 files are not produced and not read — the v1 codepath shipped briefly
    /// but never reached production; the convergence failure (cycle 10 Phase 3)
    /// motivated the v2 format before any v1 file persisted.
    /// </remarks>
    public const uint Version = 2;

    public ulong BaseSeed { get; }
    public BitVector[] Levels { get; }
    public long[] LevelOffsets { get; }   // cumulative popcount up to (but not including) level i
    public long NumKeys { get; }

    /// <summary>
    /// MPHF position assigned to dense key <c>i</c>: <c>DenseOffset + i</c>.
    /// Equals the sum of all iterative-level popcounts.
    /// </summary>
    public long DenseOffset { get; }

    /// <summary>
    /// Keys that the iterative phase could not place. Each entry is the full byte
    /// content of an input key (e.g., the UTF-8 atom URI). At lookup, queries are
    /// compared byte-for-byte against this array; an exact match yields a precise
    /// MPHF position (no false positives, no verification needed for dense hits).
    /// Empty array is the common case at γ=2.0 with MaxLevels=40.
    /// </summary>
    public byte[][] DenseKeys { get; }

    internal BBHash(
        ulong baseSeed,
        BitVector[] levels,
        long[] levelOffsets,
        long numKeys,
        long denseOffset,
        byte[][] denseKeys)
    {
        BaseSeed = baseSeed;
        Levels = levels;
        LevelOffsets = levelOffsets;
        NumKeys = numKeys;
        DenseOffset = denseOffset;
        DenseKeys = denseKeys ?? Array.Empty<byte[]>();
    }

    /// <summary>
    /// Compute the MPHF position for a key. Returns a value in <c>[0, NumKeys)</c> for
    /// keys in the input set. For keys NOT in the set: the iterative phase may
    /// false-positive (return a valid <c>[0, DenseOffset)</c> position pointing to a
    /// different in-set key — caller must verify via the translation table + atom-bytes
    /// compare). Queries that miss all iterative levels are checked against the dense
    /// set; a dense miss returns -1 (definitively not in set; dense path is exact).
    /// </summary>
    public long Lookup(ReadOnlySpan<byte> key)
    {
        for (int level = 0; level < Levels.Length; level++)
        {
            ulong seed = BaseSeed + (ulong)level;
            ulong h = SplitMix64Hash.Hash64(key, seed);
            long pos = (long)(h % (ulong)Levels[level].BitCount);
            if (Levels[level].Get(pos))
            {
                return LevelOffsets[level] + Levels[level].Rank(pos);
            }
        }
        // Iterative phase missed. Try the dense final level — exact byte comparison.
        for (int i = 0; i < DenseKeys.Length; i++)
        {
            if (key.SequenceEqual(DenseKeys[i]))
            {
                return DenseOffset + i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Serialize the BBHash blob to a binary writer. Format (v2):
    /// <code>
    /// [u32 magic = 0x4D504846]
    /// [u32 version = 2]
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
    /// [u64 dense_offset]
    /// [u32 dense_count]
    /// per dense key:
    ///   [u32 key_length]
    ///   [key_length bytes]
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

        // Dense final level
        BinaryPrimitives.WriteInt64LittleEndian(buf8, DenseOffset);
        fs.Write(buf8);
        BinaryPrimitives.WriteInt32LittleEndian(buf4, DenseKeys.Length);
        fs.Write(buf4);
        for (int i = 0; i < DenseKeys.Length; i++)
        {
            var k = DenseKeys[i];
            BinaryPrimitives.WriteInt32LittleEndian(buf4, k.Length);
            fs.Write(buf4);
            fs.Write(k);
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
        if (version != Version) throw new InvalidDataException($"MPHF: unsupported version {version} (expected {Version})");

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

        // Dense final level
        if (fs.Read(buf8) != 8) throw new InvalidDataException("MPHF: short dense_offset");
        long denseOffset = BinaryPrimitives.ReadInt64LittleEndian(buf8);
        if (fs.Read(buf4) != 4) throw new InvalidDataException("MPHF: short dense_count");
        int denseCount = BinaryPrimitives.ReadInt32LittleEndian(buf4);
        var denseKeys = new byte[denseCount][];
        for (int i = 0; i < denseCount; i++)
        {
            if (fs.Read(buf4) != 4) throw new InvalidDataException("MPHF: short dense_key_length");
            int kLen = BinaryPrimitives.ReadInt32LittleEndian(buf4);
            var k = new byte[kLen];
            int got = 0;
            while (got < kLen)
            {
                int n = fs.Read(k, got, kLen - got);
                if (n == 0) throw new InvalidDataException("MPHF: short dense_key_bytes");
                got += n;
            }
            denseKeys[i] = k;
        }

        return new BBHash(baseSeed, levels, levelOffsets, numKeys, denseOffset, denseKeys);
    }
}
