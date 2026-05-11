using System;
using System.Collections.Generic;
using SkyOmega.Bcl.Collections;
using SkyOmega.Bcl.DataStructures;
using SkyOmega.Bcl.Hashing;

namespace SkyOmega.Mercury.Storage.Mphf;

/// <summary>
/// Constructs a <see cref="BBHash"/> over a known key set. Iterative algorithm
/// with a dense final-level fallback for any keys the iterative phase cannot place.
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
/// <para>
/// <b>Convergence guarantee.</b> The iterative phase is probabilistic — at γ=2.0,
/// expected levels for N=4 × 10⁹ is ~24, but variance puts ~50 % of runs over that
/// threshold. <see cref="MaxLevels"/> = 40 gives comfortable headroom; any keys
/// still un-converged after the iterative phase fall through to the
/// <see cref="BBHash.DenseKeys"/> array (bounded by <see cref="MaxDenseKeys"/>).
/// Together they make convergence deterministic at any N.
/// </para>
/// </remarks>
internal sealed class BBHashBuilder
{
    /// <summary>Default gamma — bits-per-key per level. 2.0 is the BBHash-paper default.</summary>
    public const double DefaultGamma = 2.0;

    /// <summary>
    /// Maximum iterative levels. At γ=2.0, expected convergence for N=4 × 10⁹ is ~24
    /// levels; <see cref="MaxLevels"/> = 40 gives comfortable variance headroom.
    /// Cycle 10 Phase 3 (2026-05-10) failed at the prior limit of 24 with 2 keys
    /// un-converged — coin-flip from the substrate's own expected boundary. The dense
    /// final-level fallback in <see cref="BBHash"/> handles any keys still un-placed
    /// at this limit, so this constant is a performance knob, not a correctness one.
    /// </summary>
    public const int MaxLevels = 40;

    /// <summary>
    /// Maximum size of the dense final level. If more than this many keys remain after
    /// the iterative phase, construction fails fast — beyond ~1024 keys, the BBHash
    /// is mis-tuned (γ too low or MaxLevels too low) and a dense set this large
    /// indicates a deeper problem rather than ordinary variance.
    /// </summary>
    public const int MaxDenseKeys = 1024;

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
            return new BuildResult(
                new BBHash(_baseSeed, Array.Empty<BitVector>(), Array.Empty<long>(), 0, 0, Array.Empty<byte[]>()),
                new ChunkedArray<long>(0));

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

        // Dense final level: any keys the iterative phase couldn't place go here.
        // At γ=2.0 with MaxLevels=40, this is empty on the overwhelming majority of
        // runs at any N. Cycle 10 Phase 3 (24 levels) hit non-empty dense set with
        // 2 keys remaining — the dense path makes that case routine instead of fatal.
        long denseOffset = globalOffset;
        var denseKeys = Array.Empty<byte[]>();
        if (remaining.Count > 0)
        {
            if (remaining.Count > MaxDenseKeys)
            {
                throw new InvalidOperationException(
                    $"BBHashBuilder: dense final level overflow — {remaining.Count} keys still bumped after {_maxLevels} levels, " +
                    $"exceeds MaxDenseKeys={MaxDenseKeys}. Increase gamma or MaxLevels; or raise MaxDenseKeys if the workload genuinely needs it.");
            }
            int denseCount = (int)remaining.Count;
            denseKeys = new byte[denseCount][];
            for (int i = 0; i < denseCount; i++)
            {
                long inputIdx = remaining[i];
                denseKeys[i] = getKey(inputIdx);
                long mphfPos = denseOffset + i;
                translation[mphfPos] = inputIdx;
            }
        }

        return new BuildResult(
            new BBHash(_baseSeed, levels.ToArray(), levelOffsets.ToArray(), keyCount, denseOffset, denseKeys),
            translation);
    }

    /// <summary>Result of <see cref="Build"/>: the MPHF + a translation array <c>mphf_pos → input_index</c>.</summary>
    public sealed record BuildResult(BBHash Mphf, ChunkedArray<long> Translation);
}
