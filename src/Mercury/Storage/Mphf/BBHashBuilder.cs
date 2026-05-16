using System;
using System.Collections.Generic;
using System.Diagnostics;
using SkyOmega.Bcl.Collections;
using SkyOmega.Bcl.DataStructures;
using SkyOmega.Bcl.Hashing;
using SkyOmega.Mercury.Abstractions;

namespace SkyOmega.Mercury.Storage.Mphf;

/// <summary>
/// ADR-042 Part 4: Span-based key access callback. The caller fills <paramref name="scratch"/>
/// with the key bytes for <paramref name="inputIndex"/> and returns a slice of the written portion.
/// Eliminates the per-call <c>byte[]</c> allocation the old <c>Func&lt;long, byte[]&gt;</c> shape
/// caused — ~770 GB of GC churn across a 4 B-atom build at multiple BBHash passes.
/// </summary>
/// <remarks>
/// The scratch buffer is owned by <see cref="BBHashBuilder.Build(long, int, GetKeyDelegate, IObservabilityListener?)"/>
/// and is sized to the caller-declared <c>maxKeyByteLength</c>. The returned span is valid only
/// for the duration of the calling iteration; the next call overwrites the scratch contents.
/// </remarks>
internal delegate ReadOnlySpan<byte> GetKeyDelegate(long inputIndex, Span<byte> scratch);

/// <summary>
/// ADR-042 Part 2: pluggable translation-table sink. Lets <see cref="BBHashBuilder"/>
/// write each <c>(mphf_pos, input_idx)</c> entry as it's computed — either into an
/// in-memory <see cref="ChunkedArray{T}"/> (legacy/test path) or directly into an mmap'd
/// <c>atoms.idx</c> file (production path). Eliminates the 32 GB persistent in-memory
/// translation allocation at N=4 B atoms.
/// </summary>
internal interface IMphfTranslationSink
{
    /// <summary>Write a single translation entry. Called once per placed key per level + once per dense key.</summary>
    void Set(long mphfPos, long inputIdx);
}

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
    /// Build the MPHF over the given keys (legacy <c>byte[]</c>-allocating overload).
    /// Wraps the Span-based <see cref="Build(long, int, GetKeyDelegate, IObservabilityListener?)"/>
    /// for callers that don't need the zero-allocation hot path. Each <paramref name="getKey"/>
    /// call's returned <c>byte[]</c> is copied into the scratch buffer; the underlying allocation
    /// is unchanged from the pre-ADR-042 behavior.
    /// </summary>
    /// <param name="keyCount">Total number of keys.</param>
    /// <param name="getKey">Callback to retrieve key bytes by input index (1-based on caller's
    /// convention; passed through here unchanged).</param>
    /// <param name="listener">Optional observability sink.</param>
    public BuildResult Build(long keyCount, Func<long, byte[]> getKey, IObservabilityListener? listener = null)
    {
        // Scratch sizing: 64 KB covers any practical RDF atom byte length — Mercury's
        // parsers cap term-output buffers at 16K chars × max 4 bytes/char UTF-8 = 64 KB.
        // Outlier path: if the caller's getKey returns a byte[] larger than the scratch
        // (in-practice impossible at parser-bounded scales but theoretically possible
        // for synthesized test inputs), we fall back to returning the byte[] directly
        // via implicit conversion to ReadOnlySpan<byte>. No truncation; no surprise.
        return Build(keyCount, maxKeyByteLength: 64 * 1024,
            (long inputIdx, Span<byte> scratch) =>
            {
                var keyBytes = getKey(inputIdx);
                if (keyBytes.Length <= scratch.Length)
                {
                    keyBytes.AsSpan().CopyTo(scratch);
                    return (ReadOnlySpan<byte>)scratch.Slice(0, keyBytes.Length);
                }
                return keyBytes;
            }, listener);
    }

    /// <summary>
    /// Build the MPHF over the given keys (Span-based zero-allocation overload).
    /// Returns the constructed <see cref="BBHash"/> plus a translation array mapping
    /// <c>mphf_pos → input_index</c>. Caller can use the translation array to populate
    /// <c>atoms.idx</c>.
    /// </summary>
    /// <param name="keyCount">Total number of keys.</param>
    /// <param name="maxKeyByteLength">Maximum byte length of any key returned by <paramref name="getKey"/>.
    /// Used to size the scratch buffer once. For Wikidata-shape atoms, 4096 is a safe upper bound.</param>
    /// <param name="getKey">Callback that fills the supplied scratch buffer with key bytes for the
    /// given input index and returns a slice of the written portion. Per ADR-042 Part 4 — eliminates
    /// the per-call <c>byte[]</c> allocation the prior Func-shape caused.</param>
    /// <param name="listener">Optional observability sink. When non-null, emits per-level
    /// + dense-fallback + start/complete events for ADR-039 attribution. Default null
    /// preserves the original signature for tests and standalone callers.</param>
    public BuildResult Build(long keyCount, int maxKeyByteLength, GetKeyDelegate getKey, IObservabilityListener? listener = null)
    {
        if (keyCount < 0) throw new ArgumentOutOfRangeException(nameof(keyCount));
        if (maxKeyByteLength < 1) throw new ArgumentOutOfRangeException(nameof(maxKeyByteLength));
        if (keyCount == 0)
            return new BuildResult(
                new BBHash(_baseSeed, Array.Empty<BitVector>(), Array.Empty<long>(), 0, 0, Array.Empty<byte[]>()),
                new ChunkedArray<long>(0));

        // ADR-042 Part 2: pre-ADR path allocates a 32 GB in-memory ChunkedArray and
        // returns it as BuildResult.Translation. The sink-based path internally
        // produces the same result by accumulating writes into a ChunkedArraySink.
        // Tests rely on this shape; production paths should use the IMphfTranslationSink
        // overload directly to avoid the 32 GB peak.
        var translation = new ChunkedArray<long>(keyCount);
        var sink = new ChunkedArraySink(translation);
        var mphf = BuildToSink(keyCount, maxKeyByteLength, getKey, sink, listener);
        return new BuildResult(mphf, translation);
    }

    /// <summary>
    /// Build the MPHF over the given keys, writing each translation entry directly
    /// to the caller-provided <paramref name="sink"/>. ADR-042 Part 2 production
    /// path: caller wraps an mmap'd <c>atoms.idx</c> view as the sink, eliminating
    /// the 32 GB in-memory <see cref="ChunkedArray{T}"/> translation at N=4 B atoms.
    /// </summary>
    /// <param name="keyCount">Total number of keys.</param>
    /// <param name="maxKeyByteLength">Maximum byte length of any key returned by <paramref name="getKey"/>.</param>
    /// <param name="getKey">Span-based key-access callback (see <see cref="GetKeyDelegate"/>).</param>
    /// <param name="sink">Receives each <c>(mphf_pos, input_idx)</c> pair as it's computed.</param>
    /// <param name="listener">Optional observability sink for MPHF construction events.</param>
    /// <returns>The constructed <see cref="BBHash"/>. The translation entries are in the
    /// caller-provided <paramref name="sink"/>.</returns>
    public BBHash Build(long keyCount, int maxKeyByteLength, GetKeyDelegate getKey, IMphfTranslationSink sink, IObservabilityListener? listener = null)
    {
        if (keyCount < 0) throw new ArgumentOutOfRangeException(nameof(keyCount));
        if (maxKeyByteLength < 1) throw new ArgumentOutOfRangeException(nameof(maxKeyByteLength));
        if (sink is null) throw new ArgumentNullException(nameof(sink));
        if (keyCount == 0)
            return new BBHash(_baseSeed, Array.Empty<BitVector>(), Array.Empty<long>(), 0, 0, Array.Empty<byte[]>());
        return BuildToSink(keyCount, maxKeyByteLength, getKey, sink, listener);
    }

    /// <summary>
    /// Build the MPHF over the given keys, writing each translation entry directly
    /// to the caller-provided <paramref name="sink"/>. Internal worker shared by
    /// the <see cref="BuildResult"/>-returning <c>Build</c> overloads and the
    /// sink-only <see cref="Build(long, int, GetKeyDelegate, IMphfTranslationSink, IObservabilityListener?)"/>.
    /// </summary>
    private BBHash BuildToSink(long keyCount, int maxKeyByteLength, GetKeyDelegate getKey, IMphfTranslationSink sink, IObservabilityListener? listener)
    {
        listener?.OnMphfBuildStarted(new MphfBuildStartedEvent(
            DateTimeOffset.UtcNow, keyCount, _gamma, _maxLevels, MaxDenseKeys, _baseSeed));

        // ADR-042 Part 4: single scratch buffer reused across every key access for the
        // entire build. Sized to the caller-declared maxKeyByteLength; in practice 64 KB
        // for Wikidata-shape atoms. The buffer's contents are valid only until the next
        // getKey call — none of the hot paths cache the keyBytes span beyond hashing.
        byte[] scratchBuffer = new byte[maxKeyByteLength];

        // ADR-042 Part 1: range-iterator at level 0. The level-0 input is always the
        // dense range [1..keyCount]; materializing it into a ChunkedList costs 32 GB
        // at N=4B atoms (8 bytes × N). Instead, we iterate the range directly at
        // level 0 and only allocate `remaining` lazily when level 0 produces a
        // non-empty bumped set (i.e., entering level 1+). The hot loops branch on
        // levelIdx to select the input-index source — branch is constant per level,
        // perfectly predicted, zero per-iteration cost.
        ChunkedList<long>? remaining = null;

        var levels = new List<BitVector>();
        var levelOffsets = new List<long>();

        long globalOffset = 0;
        for (int levelIdx = 0; levelIdx < _maxLevels; levelIdx++)
        {
            long remainingCount = levelIdx == 0 ? keyCount : remaining!.Count;
            if (remainingCount == 0) break;

            var levelStart = Stopwatch.GetTimestamp();
            long bitCount = (long)Math.Ceiling(_gamma * remainingCount);
            if (bitCount < 1) bitCount = 1;
            ulong seed = _baseSeed + (ulong)levelIdx;

            // ADR-042 Part 3: re-hash second pass eliminates per-level keyPositions
            // ChunkedArray (32 GB at level 0 in the pre-1.7.62 implementation). New
            // shape: two passes over keys instead of three. Pass 1 only populates
            // collision-detection bit vectors (no positions stored); the placement
            // bit-vector `bv` is derived via bit-vector arithmetic (`seen AND NOT
            // collided`) in one linear scan; Pass 2 re-hashes each key and either
            // writes its translation entry or bumps it. Re-hash cost ≈ 3 ns/key per
            // pass ≈ 12 s extra CPU at level 0 for N=4 B keys — negligible vs the
            // ~50 min level-0 wall-clock.
            //
            // First pass: detect collisions via bit-vector pair. seen[pos]=1 once any
            // key hashes there; collided[pos]=1 once a second key hashes there. This
            // replaces the prior Dictionary<long,int> position counter — same semantics
            // (we only care about "0/1 keys" vs "2+ keys"), but bounded by ~bitCount
            // bits each instead of ~32 bytes per distinct position.
            var seen = new BitVector(bitCount);
            var collided = new BitVector(bitCount);
            bool isLevel0 = levelIdx == 0;
            for (long k = 0; k < remainingCount; k++)
            {
                long inputIdx = isLevel0 ? (k + 1) : remaining![k];
                var keyBytes = getKey(inputIdx, scratchBuffer);
                ulong h = SplitMix64Hash.Hash64(keyBytes, seed);
                long pos = (long)(h % (ulong)bitCount);
                if (seen.Get(pos)) collided.Set(pos);
                seen.Set(pos);
            }

            // ADR-042 Part 3: derive bv via bit-vector arithmetic. bv[pos] is set iff
            // exactly one key hashes to pos — i.e., seen[pos] AND NOT collided[pos].
            // Single linear scan over the underlying ulong[] words; O(bitCount/64).
            var bv = new BitVector(bitCount);
            var bvWords = bv.Words;
            var seenWords = seen.Words;
            var collidedWords = collided.Words;
            int wordCount = bvWords.Length;
            for (int w = 0; w < wordCount; w++)
            {
                bvWords[w] = seenWords[w] & ~collidedWords[w];
            }
            // seen + collided no longer needed past this point. (GC can collect when
            // the local-scope references drop at the next level iteration.)

            // Build rank table for this level so we can compute placement positions.
            bv.BuildRankTable();

            // Second pass: re-hash each key, place or bump. For placed keys, compute
            // mphfPos via the just-built rank table and write the translation entry.
            // The re-hash is the cost ADR-042 Part 3 trades against the 32 GB savings.
            var bumped = new ChunkedList<long>();
            for (long k = 0; k < remainingCount; k++)
            {
                long inputIdx = isLevel0 ? (k + 1) : remaining![k];
                var keyBytes = getKey(inputIdx, scratchBuffer);
                ulong h = SplitMix64Hash.Hash64(keyBytes, seed);
                long pos = (long)(h % (ulong)bitCount);
                if (!collided.Get(pos))
                {
                    long mphfPos = globalOffset + bv.Rank(pos);
                    sink.Set(mphfPos, inputIdx);
                }
                else
                {
                    bumped.Add(inputIdx);
                }
            }

            levels.Add(bv);
            levelOffsets.Add(globalOffset);
            long placed = bv.PopCount();
            globalOffset += placed;
            long bumpedCount = bumped.Count;
            listener?.OnMphfLevelCompleted(new MphfLevelCompletedEvent(
                DateTimeOffset.UtcNow,
                levelIdx,
                remainingCount,
                bitCount,
                placed,
                bumpedCount,
                Stopwatch.GetElapsedTime(levelStart)));
            remaining = bumped;
        }

        // Dense final level: any keys the iterative phase couldn't place go here.
        // At γ=2.0 with MaxLevels=40, this is empty on the overwhelming majority of
        // runs at any N. Cycle 10 Phase 3 (24 levels) hit non-empty dense set with
        // 2 keys remaining — the dense path makes that case routine instead of fatal.
        //
        // ADR-042 Part 1: `remaining` is null when the iterative phase converged with
        // an empty bumped set at every level — including the level-0-converges case
        // (every key placed at level 0, no bumps). Treat null as empty.
        long denseRemaining = remaining?.Count ?? 0;
        long denseOffset = globalOffset;
        var denseKeys = Array.Empty<byte[]>();
        if (denseRemaining > 0)
        {
            if (denseRemaining > MaxDenseKeys)
            {
                throw new InvalidOperationException(
                    $"BBHashBuilder: dense final level overflow — {denseRemaining} keys still bumped after {_maxLevels} levels, " +
                    $"exceeds MaxDenseKeys={MaxDenseKeys}. Increase gamma or MaxLevels; or raise MaxDenseKeys if the workload genuinely needs it.");
            }
            int denseCount = (int)denseRemaining;
            denseKeys = new byte[denseCount][];
            for (int i = 0; i < denseCount; i++)
            {
                long inputIdx = remaining![i];
                // Dense keys must be stored persistently in the BBHash for membership
                // checks on lookup; copy from scratch into a fresh per-key byte[].
                // Bounded by MaxDenseKeys (1024), so the copy cost is trivial.
                var keyBytes = getKey(inputIdx, scratchBuffer);
                denseKeys[i] = keyBytes.ToArray();
                long mphfPos = denseOffset + i;
                sink.Set(mphfPos, inputIdx);
            }
            listener?.OnMphfDenseFallback(new MphfDenseFallbackEvent(
                DateTimeOffset.UtcNow, denseCount, levels.Count));
        }

        return new BBHash(_baseSeed, levels.ToArray(), levelOffsets.ToArray(), keyCount, denseOffset, denseKeys);
    }

    /// <summary>
    /// <see cref="IMphfTranslationSink"/> backed by an in-memory <see cref="ChunkedArray{T}"/>.
    /// Used by the <see cref="BuildResult"/>-returning <c>Build</c> overloads to preserve
    /// the pre-ADR-042 API surface (tests + simpler callers).
    /// </summary>
    private sealed class ChunkedArraySink : IMphfTranslationSink
    {
        private readonly ChunkedArray<long> _translation;
        public ChunkedArraySink(ChunkedArray<long> translation) => _translation = translation;
        public void Set(long mphfPos, long inputIdx) => _translation[mphfPos] = inputIdx;
    }

    /// <summary>Result of <see cref="Build"/>: the MPHF + a translation array <c>mphf_pos → input_index</c>.</summary>
    public sealed record BuildResult(BBHash Mphf, ChunkedArray<long> Translation);
}
