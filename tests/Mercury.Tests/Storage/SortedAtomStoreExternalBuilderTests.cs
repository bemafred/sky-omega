using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-034 Phase 1B-4: external-merge-sort builder. Verifies correctness against the
/// in-memory <see cref="SortedAtomStoreBuilder"/> reference, including the chunked spill
/// path (small <c>chunkSizeBytes</c> forces multiple chunks).
/// </summary>
public class SortedAtomStoreExternalBuilderTests : IDisposable
{
    private readonly string _testDir;

    public SortedAtomStoreExternalBuilderTests()
    {
        var tempPath = TempPath.Test("sorted_atom_external");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    [Fact]
    public void External_Empty_ProducesZeroAtomStore()
    {
        var basePath = Path.Combine(_testDir, "ext_empty");
        var result = SortedAtomStoreExternalBuilder.BuildExternal(basePath, Array.Empty<string>(),
            tempDir: Path.Combine(_testDir, "tmp_empty"));
        Assert.Equal(0, result.AtomCount);

        using var store = new SortedAtomStore(basePath);
        Assert.Equal(0, store.AtomCount);
    }

    [Fact]
    public void External_FewAtoms_RoundTripsLikeInMemory()
    {
        var basePath = Path.Combine(_testDir, "ext_small");
        var inputs = new[] { "charlie", "alpha", "bravo", "alpha" };
        var result = SortedAtomStoreExternalBuilder.BuildExternal(basePath, inputs,
            tempDir: Path.Combine(_testDir, "tmp_small"));

        Assert.Equal(3, result.AtomCount);  // 3 distinct
        Assert.Equal(new long[] { 3, 1, 2, 1 }, result.AssignedIds);

        using var store = new SortedAtomStore(basePath);
        Assert.Equal("alpha", store.GetAtomString(1));
        Assert.Equal("bravo", store.GetAtomString(2));
        Assert.Equal("charlie", store.GetAtomString(3));
    }

    [Fact]
    public void External_ChunkSpillForced_ProducesIdenticalOutputAsInMemory()
    {
        // Force several chunk spills with a tiny chunkSizeBytes; verify the result is
        // identical to the in-memory build over the same input.
        var basePath = Path.Combine(_testDir, "ext_chunked");
        var inMemPath = Path.Combine(_testDir, "in_mem");

        var rng = new Random(42);
        var inputs = new string[5000];
        for (int i = 0; i < inputs.Length; i++)
            inputs[i] = $"http://wikidata.org/entity/Q{rng.Next(0, 200_000)}";

        // External: 1 MB chunks across ~50 KB total → many chunk spills.
        var ext = SortedAtomStoreExternalBuilder.BuildExternal(basePath, inputs,
            tempDir: Path.Combine(_testDir, "tmp_chunked"),
            chunkSizeBytes: 1L * 1024 * 1024);

        // In-memory: same input.
        var mem = SortedAtomStoreBuilder.Build(inMemPath, inputs);

        Assert.Equal(mem.AtomCount, ext.AtomCount);
        Assert.Equal(mem.DataBytes, ext.DataBytes);
        Assert.Equal(mem.AssignedIds, ext.AssignedIds);

        // Open both and verify identical lookup behavior on every input.
        using var extStore = new SortedAtomStore(basePath);
        using var memStore = new SortedAtomStore(inMemPath);
        for (int i = 0; i < inputs.Length; i++)
        {
            long extId = extStore.GetAtomId(inputs[i]);
            long memId = memStore.GetAtomId(inputs[i]);
            Assert.Equal(memId, extId);
            Assert.Equal(inputs[i], extStore.GetAtomString(extId));
        }
    }

    [Fact]
    public void External_DenseDuplicates_CollapseAcrossChunks()
    {
        // Many duplicates of the same string spread across chunks — exercises the
        // dedup-during-merge path.
        var basePath = Path.Combine(_testDir, "ext_dups");
        var inputs = new string[10000];
        for (int i = 0; i < inputs.Length; i++)
            inputs[i] = $"key{i % 10}";

        var result = SortedAtomStoreExternalBuilder.BuildExternal(basePath, inputs,
            tempDir: Path.Combine(_testDir, "tmp_dups"),
            chunkSizeBytes: 1L * 1024 * 1024);

        Assert.Equal(10, result.AtomCount);  // exactly 10 distinct keys

        using var store = new SortedAtomStore(basePath);
        // Sort order: "key0", "key1", "key2", ... "key9"
        for (int i = 0; i < 10; i++)
            Assert.Equal($"key{i}", store.GetAtomString(i + 1));
    }

    [Fact]
    public void External_TempDirCleanupOnSuccess()
    {
        var tempDir = Path.Combine(_testDir, "tmp_cleanup");
        var basePath = Path.Combine(_testDir, "ext_cleanup");
        SortedAtomStoreExternalBuilder.BuildExternal(basePath, new[] { "a", "b" }, tempDir: tempDir);
        Assert.False(Directory.Exists(tempDir), "temp directory should be deleted after successful build");
    }

    [Fact]
    public void External_ManyChunks_ExceedsPoolCapacity_StillCorrect()
    {
        // ADR-034 Round 2 follow-up: at 21.3B Wikidata scale the merge processes ~13K
        // chunk files; without a bounded file-stream pool, macOS ulimit (256-1024) is
        // exceeded and FlushToDisk crashes. The fix wires BoundedFileStreamPool into
        // ChunkReader. This test forces >MergeFileStreamPoolSize (64) chunks at small
        // chunkSizeBytes so the merge's LRU eviction path is exercised end-to-end.
        var basePath = Path.Combine(_testDir, "ext_many_chunks");
        var inMemPath = Path.Combine(_testDir, "in_mem_many");

        // ~80 chunks at 1 MB each. 1.5M strings × ~50 bytes = ~75 MB of raw input;
        // with 16-byte per-record overhead the spill buffer ticks over at 1 MB roughly
        // every ~16K records.
        var inputs = new string[1_500_000];
        for (int i = 0; i < inputs.Length; i++)
            inputs[i] = $"http://wikidata.org/entity/Q{i:D9}";

        var ext = SortedAtomStoreExternalBuilder.BuildExternal(basePath, inputs,
            tempDir: Path.Combine(_testDir, "tmp_many_chunks"),
            chunkSizeBytes: 1L * 1024 * 1024);

        Assert.Equal(inputs.Length, ext.AtomCount);

        using var extStore = new SortedAtomStore(basePath);
        // Spot-check first, last, and 100 random inputs — round-trips through the
        // pool/eviction logic for each lookup.
        Assert.Equal(inputs[0], extStore.GetAtomString(extStore.GetAtomId(inputs[0])));
        Assert.Equal(inputs[^1], extStore.GetAtomString(extStore.GetAtomId(inputs[^1])));
        var rng = new Random(13);
        for (int trial = 0; trial < 100; trial++)
        {
            int i = rng.Next(0, inputs.Length);
            long id = extStore.GetAtomId(inputs[i]);
            Assert.True(id > 0);
            Assert.Equal(inputs[i], extStore.GetAtomString(id));
        }
    }

    [Fact]
    public void External_LargeAlphabet_RoundTrip()
    {
        // 50 K distinct strings, multiple chunks — exercises the priority-queue merge
        // at non-trivial scale.
        var basePath = Path.Combine(_testDir, "ext_large");
        var inputs = new string[50_000];
        for (int i = 0; i < inputs.Length; i++)
            inputs[i] = $"http://wikidata.org/entity/Q{i:D8}";

        var result = SortedAtomStoreExternalBuilder.BuildExternal(basePath, inputs,
            tempDir: Path.Combine(_testDir, "tmp_large"),
            chunkSizeBytes: 4L * 1024 * 1024);

        Assert.Equal(50_000, result.AtomCount);

        using var store = new SortedAtomStore(basePath);
        // Spot-check 100 random inputs.
        var rng = new Random(7);
        for (int trial = 0; trial < 100; trial++)
        {
            int i = rng.Next(0, inputs.Length);
            long id = store.GetAtomId(inputs[i]);
            Assert.True(id > 0);
            Assert.Equal(inputs[i], store.GetAtomString(id));
        }
    }

    [Fact]
    public void MergeAndWrite_DeletesConsumedChunks_AndReportsCounts()
    {
        // Round 2 phase-transition cleanup: chunks must be deleted by the end of
        // MergeAndWrite, and the MergeCompletedEvent must report counts that match.
        // Limits: docs/limits/intermediate-cleanup-deferred-to-run-end.md
        var bulkTempDir = Path.Combine(_testDir, "tmp_cleanup_event");
        var basePath = Path.Combine(_testDir, "ext_cleanup_event");
        var listener = new CapturingMergeListener();

        using (var builder = new SortedAtomBulkBuilder(
            basePath,
            tempDir: bulkTempDir,
            useDiskBackedAssigned: false,
            chunkBufferBytes: 1L * 1024 * 1024,  // 1 MB → forces multiple chunks
            listener: listener))
        {
            // ~30K atoms × ~40 bytes ≈ 1.2 MB raw; with overhead we cross the 1 MB
            // chunk threshold a few times.
            var rng = new Random(11);
            for (int i = 0; i < 30_000; i++)
            {
                var s = $"http://example.org/s{i:D6}";
                var p = $"http://example.org/p{rng.Next(50):D2}";
                var o = $"http://example.org/o{i:D6}";
                builder.AddTriple(default, s, p, o);
            }
            builder.Finalize();
        }

        Assert.NotNull(listener.LastCompleted);
        var ev = listener.LastCompleted!.Value;
        Assert.True(ev.ChunkCount >= 2, $"expected multiple chunks, got {ev.ChunkCount}");
        Assert.Equal(ev.ChunkCount, ev.ChunksDeleted);
        Assert.True(ev.ChunkBytesReclaimed > 0);

        var occurrencesDir = Path.Combine(bulkTempDir, "occurrences");
        if (Directory.Exists(occurrencesDir))
        {
            var leftover = Directory.GetFiles(occurrencesDir, "chunk-*.bin");
            Assert.Empty(leftover);
        }
    }

    private sealed class CapturingMergeListener : IObservabilityListener
    {
        public MergeCompletedEvent? LastCompleted { get; private set; }
        public void OnMergeCompleted(in MergeCompletedEvent ev) => LastCompleted = ev;
    }
}
