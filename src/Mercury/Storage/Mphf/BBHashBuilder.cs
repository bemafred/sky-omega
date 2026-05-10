using System;
using System.Collections.Generic;
using SkyOmega.Bcl.Collections;
using SkyOmega.Bcl.DataStructures;
using SkyOmega.Bcl.Hashing;

namespace SkyOmega.Mercury.Storage.Mphf;

/// <summary>
/// Constructs a <see cref="BBHash"/> over a known key set. Iterative algorithm:
/// each level allocates a bit vector and hashes remaining keys; collisions bump
/// to the next level. Convergence in 3–5 levels at gamma=2.0.
/// </summary>
/// <remarks>
/// <para>
/// <b>Capacity discipline.</b> All per-key collections use long-typed
/// <see cref="ChunkedList{T}"/> / <see cref="ChunkedArray{T}"/> from
/// <c>SkyOmega.Bcl</c>; no path is bounded by int32. Collision detection uses a
/// bit-vector pair (<c>seen</c> + <c>collided</c>) instead of
/// <c>Dictionary&lt;long,int&gt;</c> — both for int32 immunity and memory
/// economy: at 4 B atoms, the Dictionary alternative would have required &gt;96 GB
/// per level vs ~2 GB for the bit-vector pair.
/// </para>
/// </remarks>
internal sealed class BBHashBuilder
{
    /// <summary>Default gamma — bits-per-key per level. 2.0 is the BBHash-paper default.</summary>
    public const double DefaultGamma = 2.0;

    /// <summary>
    /// Maximum levels before failing — safety bound. At γ=2.0, per-level reduction
    /// is ~0.39 (40 % bumped). Convergence to &lt; 1 key remaining requires
    /// log(1/N) / log(0.39) ≈ 24 levels for N=4 B atoms. Actual implementation may
    /// converge in fewer levels by chance (lucky key distribution); adversarial
    /// inputs may need this many or more.
    /// </summary>
    public const int MaxLevels = 24;

    private readonly double _gamma;
    private readonly ulong _baseSeed;
    private readonly int _maxLevels;

    /// <summary>Default base seed for BBHash — arbitrary stable value.</summary>
    public const ulong DefaultBaseSeed = 0xC0FFEE_BB_3F_2026UL;

    public BBHashBuilder(double gamma = DefaultGamma, ulong baseSeed = DefaultBaseSeed, int maxLevels = MaxLevels)
    {
        if (gamma < 1.0) throw new ArgumentOutOfRangeException(nameof(gamma), "gamma must be >= 1.0");
        if (maxLevels < 1) throw new ArgumentOutOfRangeException(nameof(maxLevels));
        _gamma = gamma;
        _baseSeed = baseSeed;
        _maxLevels = maxLevels;
    }

    /// <summary>
    /// Build the MPHF over the given keys. Returns the constructed <see cref="BBHash"/>
    /// plus a translation array mapping <c>mphf_pos → input_index</c>. Caller can use the
    /// translation array to populate <c>atoms.idx</c>.
    /// </summary>
    /// <param name="keyCount">Total number of keys.</param>
    /// <param name="getKey">Callback to retrieve key bytes by input index (1-based on caller's
    /// convention; passed through here unchanged).</param>
    public BuildResult Build(long keyCount, Func<long, byte[]> getKey)
    {
        if (keyCount < 0) throw new ArgumentOutOfRangeException(nameof(keyCount));
        if (keyCount == 0)
            return new BuildResult(new BBHash(_baseSeed, Array.Empty<BitVector>(), Array.Empty<long>(), 0), new ChunkedArray<long>(0));

        // Track which keys remain to be placed (by input-index, 1-based).
        // ChunkedList — long-indexed, no doubling-copy on growth, no int32 cap.
        var remaining = new ChunkedList<long>();
        for (long i = 1; i <= keyCount; i++) remaining.Add(i);

        var levels = new List<BitVector>();
        var levelOffsets = new List<long>();
        // mphf_pos → input_index. ChunkedArray — fixed length, eager allocation.
        var translation = new ChunkedArray<long>(keyCount);

        long globalOffset = 0;
        for (int levelIdx = 0; levelIdx < _maxLevels; levelIdx++)
        {
            long remainingCount = remaining.Count;
            if (remainingCount == 0) break;

            long bitCount = (long)Math.Ceiling(_gamma * remainingCount);
            if (bitCount < 1) bitCount = 1;
            var bv = new BitVector(bitCount);
            ulong seed = _baseSeed + (ulong)levelIdx;

            // First pass: detect collisions via bit-vector pair. seen[pos]=1 once any
            // key hashes there; collided[pos]=1 once a second key hashes there. This
            // replaces the prior Dictionary<long,int> position counter — same semantics
            // (we only care about "0/1 keys" vs "2+ keys"), but bounded by ~bitCount
            // bits each instead of ~32 bytes per distinct position.
            var seen = new BitVector(bitCount);
            var collided = new BitVector(bitCount);
            var keyPositions = new ChunkedArray<long>(remainingCount);
            for (long k = 0; k < remainingCount; k++)
            {
                var keyBytes = getKey(remaining[k]);
                ulong h = SplitMix64Hash.Hash64(keyBytes, seed);
                long pos = (long)(h % (ulong)bitCount);
                keyPositions[k] = pos;
                if (seen.Get(pos)) collided.Set(pos);
                seen.Set(pos);
            }

            // Second pass: set bits for non-colliding positions; bump collided keys.
            var bumped = new ChunkedList<long>();
            for (long k = 0; k < remainingCount; k++)
            {
                long pos = keyPositions[k];
                if (!collided.Get(pos))
                {
                    bv.Set(pos);
                }
                else
                {
                    bumped.Add(remaining[k]);
                }
            }

            // Build rank table for this level so we can compute placement positions.
            bv.BuildRankTable();
            // Fill translation for placed keys: mphf_pos = globalOffset + rank(pos).
            for (long k = 0; k < remainingCount; k++)
            {
                long pos = keyPositions[k];
                if (!collided.Get(pos))
                {
                    long mphfPos = globalOffset + bv.Rank(pos);
                    translation[mphfPos] = remaining[k];
                }
            }

            levels.Add(bv);
            levelOffsets.Add(globalOffset);
            globalOffset += bv.PopCount();
            remaining = bumped;
        }

        if (remaining.Count > 0)
            throw new InvalidOperationException(
                $"BBHashBuilder did not converge: {remaining.Count} keys still bumped after {_maxLevels} levels. Increase MaxLevels or gamma.");

        return new BuildResult(
            new BBHash(_baseSeed, levels.ToArray(), levelOffsets.ToArray(), keyCount),
            translation);
    }

    /// <summary>Result of <see cref="Build"/>: the MPHF + a translation array <c>mphf_pos → input_index</c>.</summary>
    public sealed record BuildResult(BBHash Mphf, ChunkedArray<long> Translation);
}
