using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage.Mphf;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage.Mphf;

/// <summary>
/// ADR-039 Phase 2 / B3: BBHash MPHF round-trip tests. Covers correctness of
/// build + lookup + serialization, plus the translation table semantics.
/// </summary>
public class BBHashTests : IDisposable
{
    private readonly string _testDir;

    public BBHashTests()
    {
        var tempPath = TempPath.Test("bbhash");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    [Fact]
    public void Build_SmallSet_AllKeysLookupToUniquePositions()
    {
        // Build BBHash over 1000 distinct keys; assert every key gets a unique
        // position in [0, 1000) AND the translation table correctly inverts.
        const int N = 1000;
        var keys = new byte[N + 1][];  // 1-based indexing per builder convention
        for (int i = 1; i <= N; i++)
            keys[i] = Encoding.UTF8.GetBytes($"http://example.org/atom/{i:D6}");

        var builder = new BBHashBuilder();
        var result = builder.Build(N, idx => keys[idx]);

        Assert.Equal(N, result.Mphf.NumKeys);
        // Translation table: mphf_pos[0..N-1] → input_index[1..N]
        Assert.Equal(N, result.Translation.Length);

        // Every key maps to a unique position in [0, N).
        var seen = new HashSet<long>();
        for (int i = 1; i <= N; i++)
        {
            long mphfPos = result.Mphf.Lookup(keys[i]);
            Assert.InRange(mphfPos, 0, N - 1);
            Assert.True(seen.Add(mphfPos), $"Duplicate mphf_pos={mphfPos} for key {i}");
            // Translation should round-trip
            Assert.Equal(i, result.Translation[mphfPos]);
        }
        Assert.Equal(N, seen.Count);
    }

    [Fact]
    public void Build_SortedWikidataLikeKeys_RoundTrips()
    {
        // 5000 sorted Wikidata-shape keys (long shared-prefix URIs) — closer to
        // the actual atom-store input.
        const int N = 5000;
        var keys = new byte[N + 1][];
        for (int i = 1; i <= N; i++)
            keys[i] = Encoding.UTF8.GetBytes($"http://www.wikidata.org/entity/Q{i:D8}");

        var builder = new BBHashBuilder();
        var result = builder.Build(N, idx => keys[idx]);

        Assert.Equal(N, result.Mphf.NumKeys);
        // All keys must round-trip via lookup → translation → input_index
        for (int i = 1; i <= N; i++)
        {
            long mphfPos = result.Mphf.Lookup(keys[i]);
            Assert.Equal(i, result.Translation[mphfPos]);
        }
    }

    [Fact]
    public void Lookup_OutOfSetKey_DoesNotCrash()
    {
        // MPHF gives *some* position for out-of-set keys (collision with a real key).
        // The verification step (compare reconstructed bytes to query) catches it
        // upstream. Test only asserts no crash here.
        const int N = 200;
        var keys = new byte[N + 1][];
        for (int i = 1; i <= N; i++)
            keys[i] = Encoding.UTF8.GetBytes($"<http://ex/k{i}>");

        var builder = new BBHashBuilder();
        var result = builder.Build(N, idx => keys[idx]);

        // Out-of-set queries should return either -1 (no level matched) or some
        // valid position in [0, N) — either is acceptable, both are non-crash.
        var oos1 = Encoding.UTF8.GetBytes("<http://ex/NOT_IN_SET>");
        var oos2 = Encoding.UTF8.GetBytes("");
        var oos3 = new byte[1024];  // all zeros, not in set
        long _ = result.Mphf.Lookup(oos1);
        long _2 = result.Mphf.Lookup(oos2);
        long _3 = result.Mphf.Lookup(oos3);
        // Never throw; verification step is the caller's responsibility.
    }

    [Fact]
    public void Serialize_RoundTripsPreservesBehavior()
    {
        const int N = 2000;
        var keys = new byte[N + 1][];
        for (int i = 1; i <= N; i++)
            keys[i] = Encoding.UTF8.GetBytes($"<http://ex/serialize/k{i:D5}>");

        var builder = new BBHashBuilder();
        var result = builder.Build(N, idx => keys[idx]);

        var path = Path.Combine(_testDir, "test.mphf");
        result.Mphf.WriteTo(path);

        var loaded = BBHash.ReadFrom(path);
        Assert.Equal(result.Mphf.NumKeys, loaded.NumKeys);
        Assert.Equal(result.Mphf.BaseSeed, loaded.BaseSeed);
        Assert.Equal(result.Mphf.Levels.Length, loaded.Levels.Length);

        for (int i = 1; i <= N; i++)
        {
            long expected = result.Mphf.Lookup(keys[i]);
            long actual = loaded.Lookup(keys[i]);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Build_EmptySet_DoesNotCrash()
    {
        var builder = new BBHashBuilder();
        var result = builder.Build(0, idx => throw new InvalidOperationException("Should not be called for empty set"));
        Assert.Equal(0, result.Mphf.NumKeys);
        Assert.Equal(0L, result.Translation.Length);
    }

    [Fact]
    public void TranslationTable_RoundTrips()
    {
        // Build an MPHF, persist its translation array via MphfTranslationTable.WriteTo,
        // re-open via Open, verify Get() returns same values.
        const int N = 500;
        var keys = new byte[N + 1][];
        for (int i = 1; i <= N; i++)
            keys[i] = Encoding.UTF8.GetBytes($"<http://ex/idx/k{i:D4}>");

        var builder = new BBHashBuilder();
        var result = builder.Build(N, idx => keys[idx]);

        var path = Path.Combine(_testDir, "test.idx");
        MphfTranslationTable.WriteTo(path, result.Translation);

        using var table = MphfTranslationTable.Open(path);
        Assert.Equal(N, table.EntryCount);
        for (long i = 0; i < N; i++)
            Assert.Equal(result.Translation[i], table.Get(i));

        // Composed lookup: query → mphf → translation → input_index. Round-trip
        // matches the original input_index (1-based).
        for (int i = 1; i <= N; i++)
        {
            long mphfPos = result.Mphf.Lookup(keys[i]);
            long sortedPos = table.Get(mphfPos);
            Assert.Equal(i, sortedPos);
        }
    }

    [Fact]
    public void Build_DistributionAcrossLevels_ReasonableConvergence()
    {
        // For 10000 keys at gamma=2.0, BBHash should converge in 3-5 levels.
        // Bumped-set should shrink by ~50% per level. Sanity check the convergence.
        const int N = 10_000;
        var keys = new byte[N + 1][];
        for (int i = 1; i <= N; i++)
            keys[i] = Encoding.UTF8.GetBytes($"<http://ex/conv/k{i:D6}>");

        var builder = new BBHashBuilder();
        var result = builder.Build(N, idx => keys[idx]);

        // Convergence within MaxLevels. At gamma=2.0, per-level reduction is ~0.39;
        // for N=10000, expected to reach 0-key bumped in ~10-12 levels (depends on
        // hash distribution). Bound at MaxLevels (40) — anything else is OK.
        Assert.True(result.Mphf.Levels.Length <= BBHashBuilder.MaxLevels,
            $"Expected <={BBHashBuilder.MaxLevels} levels, got {result.Mphf.Levels.Length}");
        Assert.True(result.Mphf.Levels.Length >= 1);
    }

    [Fact]
    public void Build_DenseFinalLevel_HandlesUnconvergedKeys()
    {
        // Force the dense fallback path: low MaxLevels with enough keys that at least
        // some don't converge in the iterative phase. This exercises the dense-keys
        // capture, dense lookup, and translation-table entries for dense positions.
        // Cycle 10 Phase 3 (2026-05-10) hit this with 2 keys at MaxLevels=24 over 4 B
        // atoms; the dense fallback closes that gap.
        const int N = 2000;
        var keys = new byte[N + 1][];
        for (int i = 1; i <= N; i++)
            keys[i] = Encoding.UTF8.GetBytes($"<http://ex/dense/k{i:D5}>");

        // MaxLevels=4 with N=2000 at gamma=2.0 leaves ~2000 * 0.39^4 ≈ 47 keys for the
        // dense set — well within MaxDenseKeys (1024) but a meaningful test population.
        var builder = new BBHashBuilder(maxLevels: 4);
        var result = builder.Build(N, idx => keys[idx]);

        Assert.Equal(N, result.Mphf.NumKeys);
        Assert.True(result.Mphf.DenseKeys.Length > 0, "Expected dense fallback to capture some keys with MaxLevels=4");

        // Every key must lookup to a unique position in [0, N).
        var seen = new HashSet<long>();
        for (int i = 1; i <= N; i++)
        {
            long mphfPos = result.Mphf.Lookup(keys[i]);
            Assert.InRange(mphfPos, 0, N - 1);
            Assert.True(seen.Add(mphfPos), $"Duplicate mphf_pos={mphfPos} for key {i}");
            Assert.Equal(i, result.Translation[mphfPos]);
        }
        Assert.Equal(N, seen.Count);
    }

    [Fact]
    public void Build_DenseFallback_RoundTripsThroughSerialization()
    {
        // Force dense fallback via low MaxLevels, persist + reload, verify lookup
        // behavior matches across the serialization boundary including dense keys.
        const int N = 1500;
        var keys = new byte[N + 1][];
        for (int i = 1; i <= N; i++)
            keys[i] = Encoding.UTF8.GetBytes($"<http://ex/denser/k{i:D5}>");

        var builder = new BBHashBuilder(maxLevels: 3);
        var result = builder.Build(N, idx => keys[idx]);
        Assert.True(result.Mphf.DenseKeys.Length > 0);

        var path = Path.Combine(_testDir, "dense.mphf");
        result.Mphf.WriteTo(path);
        var loaded = BBHash.ReadFrom(path);

        Assert.Equal(result.Mphf.NumKeys, loaded.NumKeys);
        Assert.Equal(result.Mphf.Levels.Length, loaded.Levels.Length);
        Assert.Equal(result.Mphf.DenseKeys.Length, loaded.DenseKeys.Length);
        Assert.Equal(result.Mphf.DenseOffset, loaded.DenseOffset);

        for (int i = 1; i <= N; i++)
        {
            long expected = result.Mphf.Lookup(keys[i]);
            long actual = loaded.Lookup(keys[i]);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Build_WithListener_EmitsStartLevelAndCompleteEvents()
    {
        // 1.7.56 instrumentation: confirm the listener path fires start + per-level +
        // completed events. Dense-fallback path covered by the next test.
        const int N = 3000;
        var keys = new byte[N + 1][];
        for (int i = 1; i <= N; i++)
            keys[i] = Encoding.UTF8.GetBytes($"<http://ex/instr/k{i:D5}>");

        var sink = new RecordingListener();
        var builder = new BBHashBuilder();
        var result = builder.Build(N, idx => keys[idx], sink);

        Assert.Single(sink.Starts);
        Assert.Equal(N, sink.Starts[0].AtomCount);
        Assert.Equal(BBHashBuilder.DefaultGamma, sink.Starts[0].Gamma);
        Assert.Equal(BBHashBuilder.MaxLevels, sink.Starts[0].MaxLevels);

        Assert.Equal(result.Mphf.Levels.Length, sink.Levels.Count);
        // First level enters with all N keys; later levels strictly fewer.
        Assert.Equal(N, sink.Levels[0].RemainingAtEntry);
        for (int i = 1; i < sink.Levels.Count; i++)
            Assert.True(sink.Levels[i].RemainingAtEntry < sink.Levels[i - 1].RemainingAtEntry);
        // Placed + bumped at each level == RemainingAtEntry.
        foreach (var lvl in sink.Levels)
            Assert.Equal(lvl.RemainingAtEntry, lvl.Placed + lvl.Bumped);

        Assert.Empty(sink.DenseFallbacks);  // default MaxLevels=40, no dense engagement expected for 3K keys

        // MphfBuildCompletedEvent is emitted by SortedAtomStoreExternalBuilder.BuildMphfFiles
        // (which knows the on-disk file sizes), not by BBHashBuilder. Integration coverage
        // for that surface lives with the SortedAtomStore tests.
        Assert.Empty(sink.Completes);
    }

    [Fact]
    public void Build_WithListener_DenseFallback_EmitsDenseFallbackEvent()
    {
        // Force dense engagement via low MaxLevels; confirm the dense-fallback event
        // fires with the right counts.
        const int N = 2000;
        var keys = new byte[N + 1][];
        for (int i = 1; i <= N; i++)
            keys[i] = Encoding.UTF8.GetBytes($"<http://ex/dense_instr/k{i:D5}>");

        var sink = new RecordingListener();
        var builder = new BBHashBuilder(maxLevels: 4);
        var result = builder.Build(N, idx => keys[idx], sink);

        Assert.True(result.Mphf.DenseKeys.Length > 0);
        Assert.Single(sink.DenseFallbacks);
        Assert.Equal(result.Mphf.DenseKeys.Length, sink.DenseFallbacks[0].DenseKeysCount);
        Assert.Equal(result.Mphf.Levels.Length, sink.DenseFallbacks[0].LevelsUsed);
    }

    private sealed class RecordingListener : IObservabilityListener
    {
        public List<MphfBuildStartedEvent> Starts { get; } = new();
        public List<MphfLevelCompletedEvent> Levels { get; } = new();
        public List<MphfDenseFallbackEvent> DenseFallbacks { get; } = new();
        public List<MphfBuildCompletedEvent> Completes { get; } = new();

        public void OnMphfBuildStarted(in MphfBuildStartedEvent ev) => Starts.Add(ev);
        public void OnMphfLevelCompleted(in MphfLevelCompletedEvent ev) => Levels.Add(ev);
        public void OnMphfDenseFallback(in MphfDenseFallbackEvent ev) => DenseFallbacks.Add(ev);
        public void OnMphfBuildCompleted(in MphfBuildCompletedEvent ev) => Completes.Add(ev);
    }

    [Fact]
    public void Build_DenseFallback_ExceedsMaxDenseKeys_ThrowsWithDiagnostic()
    {
        // Pathological case: very low MaxLevels with many keys → > MaxDenseKeys remain.
        // Should fail fast with a diagnostic, not silently corrupt or hang.
        const int N = 50_000;
        var keys = new byte[N + 1][];
        for (int i = 1; i <= N; i++)
            keys[i] = Encoding.UTF8.GetBytes($"<http://ex/overflow/k{i:D6}>");

        // MaxLevels=1 over 50K keys at gamma=2.0 leaves ~50K * 0.39 = ~19,500 keys for
        // dense — vastly exceeds MaxDenseKeys (1024).
        var builder = new BBHashBuilder(maxLevels: 1);
        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build(N, idx => keys[idx]));
        Assert.Contains("dense final level overflow", ex.Message);
        Assert.Contains("MaxDenseKeys", ex.Message);
    }

    // ===== ADR-042 Parts 1 + 4 validation =====

    [Fact]
    public void Build_SpanApi_ProducesIdenticalResultToFuncApi()
    {
        // ADR-042 Parts 1 (range iterator at level 0) + 4 (Span-based GetKey API):
        // the new Build(keyCount, maxKeyByteLength, GetKeyDelegate) overload must
        // produce byte-identical output to the legacy Build(keyCount, Func<long, byte[]>)
        // overload on the same key set + same seed.
        const int N = 5000;
        var keys = new byte[N + 1][];
        for (int i = 1; i <= N; i++)
            keys[i] = Encoding.UTF8.GetBytes($"http://www.wikidata.org/entity/Q{i:D8}");

        // Legacy Func-based path
        var builderLegacy = new BBHashBuilder();
        var resultLegacy = builderLegacy.Build(N, idx => keys[idx]);

        // New Span-based path with explicit maxKeyByteLength
        var builderSpan = new BBHashBuilder();
        var resultSpan = builderSpan.Build(N, maxKeyByteLength: 64,
            (long idx, Span<byte> scratch) =>
            {
                keys[idx].AsSpan().CopyTo(scratch);
                return scratch.Slice(0, keys[idx].Length);
            });

        // Same MPHF structure: numkeys + level count + dense offset.
        Assert.Equal(resultLegacy.Mphf.NumKeys, resultSpan.Mphf.NumKeys);
        Assert.Equal(resultLegacy.Mphf.Levels.Length, resultSpan.Mphf.Levels.Length);
        Assert.Equal(resultLegacy.Mphf.DenseOffset, resultSpan.Mphf.DenseOffset);

        // Same translation array contents: every position maps to the same input index.
        Assert.Equal(resultLegacy.Translation.Length, resultSpan.Translation.Length);
        for (long pos = 0; pos < resultLegacy.Translation.Length; pos++)
            Assert.Equal(resultLegacy.Translation[pos], resultSpan.Translation[pos]);

        // Same per-key lookup results.
        for (int i = 1; i <= N; i++)
        {
            long legacyPos = resultLegacy.Mphf.Lookup(keys[i]);
            long spanPos = resultSpan.Mphf.Lookup(keys[i]);
            Assert.Equal(legacyPos, spanPos);
        }
    }

    [Fact]
    public void Build_SpanApi_ScratchBufferReuseDoesNotCorruptHash()
    {
        // Defensive: the Span GetKey delegate returns slices of the caller's scratch
        // buffer. The buffer is reused across every getKey call. Verify that the
        // builder neither caches keyBytes spans past their lifetime nor double-hashes
        // an aliased buffer.
        const int N = 2000;
        var keys = new byte[N + 1][];
        for (int i = 1; i <= N; i++)
            keys[i] = Encoding.UTF8.GetBytes($"<http://example.org/q/{i}>");

        var builder = new BBHashBuilder();
        var result = builder.Build(N, maxKeyByteLength: 256,
            (long idx, Span<byte> scratch) =>
            {
                // Intentionally overwrite scratch with garbage BEFORE filling the
                // actual key — verifies that the builder's cached span (if any)
                // hasn't been retained from a prior call.
                scratch.Fill(0xFF);
                keys[idx].AsSpan().CopyTo(scratch);
                return scratch.Slice(0, keys[idx].Length);
            });

        Assert.Equal(N, result.Mphf.NumKeys);
        // Every key should still round-trip cleanly.
        for (int i = 1; i <= N; i++)
        {
            long mphfPos = result.Mphf.Lookup(keys[i]);
            Assert.Equal(i, result.Translation[mphfPos]);
        }
    }
}
