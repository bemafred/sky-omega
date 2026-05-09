using System;
using System.Collections.Generic;

namespace SkyOmega.Mercury.Storage.Mphf;

/// <summary>
/// Constructs a <see cref="BBHash"/> over a known key set. Iterative algorithm:
/// each level allocates a bit vector and hashes remaining keys; collisions bump
/// to the next level. Convergence in 3–5 levels at gamma=2.0.
/// </summary>
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
    /// <remarks>
    /// 24 is comfortably above the worst case at 4 B atoms but below pathological
    /// failure mode where construction grinds. For production at 4 B atoms,
    /// consider future dense-final-level extension (the 1.6 bits/key headline from
    /// the BBHash paper assumes such a tail).
    /// </remarks>
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
    /// convention; mapped 0-based here).</param>
    public BuildResult Build(long keyCount, Func<long, byte[]> getKey)
    {
        if (keyCount < 0) throw new ArgumentOutOfRangeException(nameof(keyCount));
        if (keyCount == 0)
            return new BuildResult(new BBHash(_baseSeed, Array.Empty<BitVector>(), Array.Empty<long>(), 0), Array.Empty<long>());

        // Track which keys remain to be placed (by input-index, 1-based).
        var remaining = new List<long>(checked((int)keyCount));
        for (long i = 1; i <= keyCount; i++) remaining.Add(i);

        var levels = new List<BitVector>();
        var levelOffsets = new List<long>();
        // mphf_pos → input_index. Built incrementally as bits are placed.
        var translation = new long[keyCount];

        long globalOffset = 0;
        for (int levelIdx = 0; levelIdx < _maxLevels; levelIdx++)
        {
            if (remaining.Count == 0) break;

            long bitCount = (long)Math.Ceiling(_gamma * remaining.Count);
            if (bitCount < 1) bitCount = 1;
            var bv = new BitVector(bitCount);
            ulong seed = _baseSeed + (ulong)levelIdx;

            // First pass: count collisions per position. We use a sparse map since most
            // positions get 0 or 1 keys.
            var positionToCount = new Dictionary<long, int>(remaining.Count);
            var keyPositions = new long[remaining.Count];
            for (int k = 0; k < remaining.Count; k++)
            {
                var keyBytes = getKey(remaining[k]);
                ulong h = MphfHash.Hash64(keyBytes, seed);
                long pos = (long)(h % (ulong)bitCount);
                keyPositions[k] = pos;
                positionToCount[pos] = positionToCount.GetValueOrDefault(pos, 0) + 1;
            }

            // Second pass: set bits for non-colliding positions, place into translation,
            // collect bumped keys.
            var bumped = new List<long>();
            for (int k = 0; k < remaining.Count; k++)
            {
                long pos = keyPositions[k];
                if (positionToCount[pos] == 1)
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
            // Fill translation for placed keys: for each placed key, the mphf_pos =
            // globalOffset + rank(level, key_position).
            for (int k = 0; k < remaining.Count; k++)
            {
                long pos = keyPositions[k];
                if (positionToCount[pos] == 1)
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
    public sealed record BuildResult(BBHash Mphf, long[] Translation);
}
