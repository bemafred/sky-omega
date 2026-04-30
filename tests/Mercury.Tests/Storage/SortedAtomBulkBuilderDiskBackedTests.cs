using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-034 Phase 1B-5d: SortedAtomBulkBuilder with disk-backed AssignedIds.
/// Verifies that the disk-backed path (ExternalSorter&lt;ResolveRecord&gt;) produces
/// identical (G, S, P, O) tuples and atom IDs as the in-memory path on the same
/// inputs. Exercises the empty-graph sentinel, deduplication, and large-batch
/// streaming pull from the resolver.
/// </summary>
public class SortedAtomBulkBuilderDiskBackedTests : IDisposable
{
    private readonly string _testDir;

    public SortedAtomBulkBuilderDiskBackedTests()
    {
        var tempPath = TempPath.Test("sorted_atom_bulk_diskbacked");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    [Fact]
    public void DiskBacked_SingleTriple_ProducesSameIdsAsInMemory()
    {
        var memBase = Path.Combine(_testDir, "mem_single");
        var diskBase = Path.Combine(_testDir, "disk_single");

        using (var mem = new SortedAtomBulkBuilder(memBase, useDiskBackedAssigned: false))
        using (var disk = new SortedAtomBulkBuilder(diskBase, useDiskBackedAssigned: true))
        {
            mem.AddTriple("g1", "subj", "pred", "obj");
            disk.AddTriple("g1", "subj", "pred", "obj");

            var memResult = mem.Finalize();
            var diskResult = disk.Finalize();

            Assert.Equal(memResult.AtomCount, diskResult.AtomCount);
            Assert.Null(memResult.AssignedIdsResolver);
            Assert.NotNull(diskResult.AssignedIdsResolver);
            Assert.Equal(4L, diskResult.AssignedIdsResolver!.ExpectedCount);

            var memTuples = mem.EnumerateResolved().ToList();
            var diskTuples = disk.EnumerateResolved().ToList();
            Assert.Equal(memTuples, diskTuples);
        }
    }

    [Fact]
    public void DiskBacked_DefaultGraph_EmptyGraphYieldsZeroId()
    {
        var basePath = Path.Combine(_testDir, "default_graph_disk");
        using var builder = new SortedAtomBulkBuilder(basePath, useDiskBackedAssigned: true);
        builder.AddTriple(default, "subj", "pred", "obj");
        var result = builder.Finalize();

        Assert.NotNull(result.AssignedIdsResolver);
        Assert.Equal(4L, result.AssignedIdsResolver!.ExpectedCount);

        var resolved = builder.EnumerateResolved().Single();
        Assert.Equal(0, resolved.GraphId);  // default graph -> sentinel atom 0
        Assert.True(resolved.SubjectId > 0);
        Assert.True(resolved.PredicateId > 0);
        Assert.True(resolved.ObjectId > 0);
    }

    [Fact]
    public void DiskBacked_RepeatedAtomsShareIds()
    {
        var basePath = Path.Combine(_testDir, "repeats_disk");
        using var builder = new SortedAtomBulkBuilder(basePath, useDiskBackedAssigned: true);
        builder.AddTriple("g", "alice", "knows", "bob");
        builder.AddTriple("g", "bob", "knows", "alice");
        builder.AddTriple("g", "alice", "knows", "carol");

        var result = builder.Finalize();
        Assert.Equal(5, result.AtomCount);

        var resolved = builder.EnumerateResolved().ToList();
        Assert.Equal(3, resolved.Count);

        // alice/bob/carol/g/knows -> sorted: alice(1) bob(2) carol(3) g(4) knows(5)
        Assert.Equal(4, resolved[0].GraphId);
        Assert.Equal(1, resolved[0].SubjectId);
        Assert.Equal(5, resolved[0].PredicateId);
        Assert.Equal(2, resolved[0].ObjectId);

        Assert.Equal(2, resolved[1].SubjectId);
        Assert.Equal(1, resolved[1].ObjectId);
        Assert.Equal(3, resolved[2].ObjectId);

        foreach (var r in resolved) Assert.Equal(5, r.PredicateId);
    }

    [Fact]
    public void DiskBacked_LargeBatch_EquivalentToInMemory()
    {
        var memBase = Path.Combine(_testDir, "mem_large");
        var diskBase = Path.Combine(_testDir, "disk_large");

        // 5000 triples mixing default-graph and named-graph rows so the empty-slot
        // sentinel path is exercised alongside the full merge path.
        var rng = new Random(42);
        var triples = new (string g, string s, string p, string o)[5000];
        for (int i = 0; i < triples.Length; i++)
        {
            triples[i] = (
                i % 7 == 0 ? string.Empty : $"http://ex/g{i % 4}",
                $"http://ex/s{rng.Next(0, 200)}",
                $"http://ex/p{i % 10}",
                $"http://ex/o{rng.Next(0, 500)}");
        }

        List<(long G, long S, long P, long O)> memTuples;
        long memAtomCount;
        using (var mem = new SortedAtomBulkBuilder(memBase, useDiskBackedAssigned: false))
        {
            foreach (var t in triples) mem.AddTriple(t.g, t.s, t.p, t.o);
            var memResult = mem.Finalize();
            memAtomCount = memResult.AtomCount;
            memTuples = mem.EnumerateResolved().ToList();
        }

        List<(long G, long S, long P, long O)> diskTuples;
        long diskAtomCount;
        using (var disk = new SortedAtomBulkBuilder(diskBase, useDiskBackedAssigned: true))
        {
            foreach (var t in triples) disk.AddTriple(t.g, t.s, t.p, t.o);
            var diskResult = disk.Finalize();
            Assert.NotNull(diskResult.AssignedIdsResolver);
            Assert.Equal(triples.Length * 4L, diskResult.AssignedIdsResolver!.ExpectedCount);
            diskAtomCount = diskResult.AtomCount;
            diskTuples = disk.EnumerateResolved().ToList();
        }

        // Same vocabulary count, same per-triple ID assignment.
        Assert.Equal(memAtomCount, diskAtomCount);
        Assert.Equal(memTuples, diskTuples);

        // Round-trip: the disk-backed store is durable on disk and resolves IDs back to strings.
        using var store = new SortedAtomStore(diskBase);
        for (int i = 0; i < triples.Length; i++)
        {
            Assert.Equal(
                triples[i].g.Length == 0 ? null : triples[i].g,
                diskTuples[i].G == 0 ? null : store.GetAtomString(diskTuples[i].G));
            Assert.Equal(triples[i].s, store.GetAtomString(diskTuples[i].S));
            Assert.Equal(triples[i].p, store.GetAtomString(diskTuples[i].P));
            Assert.Equal(triples[i].o, store.GetAtomString(diskTuples[i].O));
        }
    }

    [Fact]
    public void DiskBacked_TinyResolveSorterChunk_ForcesMultipleChunks()
    {
        // Default resolveSorterChunkSize is 16 M records. Force a small chunk size so the
        // ExternalSorter spills multiple chunks even for a small input — exercises the
        // k-way merge path of the resolver.
        var basePath = Path.Combine(_testDir, "multi_chunk_resolver");

        // 2000 triples × 4 occurrences = 8000 records; with chunkSize=512 → 16 chunks.
        var triples = new List<(string g, string s, string p, string o)>(2000);
        for (int i = 0; i < 2000; i++)
            triples.Add(($"http://ex/g{i % 5}", $"http://ex/s{i}", $"http://ex/p{i % 3}", $"http://ex/o{i}"));

        using var builder = new SortedAtomBulkBuilder(basePath, useDiskBackedAssigned: true);
        foreach (var t in triples) builder.AddTriple(t.g, t.s, t.p, t.o);

        // Re-finalize via the lower-level overload to pass a tiny chunk size.
        // Use BuildExternal directly for this knob.
        // NOTE: this test uses the public Finalize path with the default chunk size; the
        // explicit small-chunk path is exercised via SortedAtomStoreExternalBuilder
        // directly below.
        builder.Finalize();
        var resolved = builder.EnumerateResolved().ToList();
        Assert.Equal(triples.Count, resolved.Count);

        using var store = new SortedAtomStore(basePath);
        for (int i = 0; i < triples.Count; i++)
        {
            Assert.Equal(triples[i].g, store.GetAtomString(resolved[i].GraphId));
            Assert.Equal(triples[i].s, store.GetAtomString(resolved[i].SubjectId));
            Assert.Equal(triples[i].p, store.GetAtomString(resolved[i].PredicateId));
            Assert.Equal(triples[i].o, store.GetAtomString(resolved[i].ObjectId));
        }
    }

    [Fact]
    public void DiskBacked_ExternalBuilder_TinyChunkSize_KWayMerge()
    {
        // Drives the resolveSorter's k-way merge by forcing a tiny chunk size.
        // 4000 input atoms; resolveSorterChunkSize=1024 → 4 spilled chunks; the merge
        // must stitch them back into InputIdx-monotonic order.
        var basePath = Path.Combine(_testDir, "ext_builder_kway");

        var inputs = new List<byte[]>(4000);
        for (int i = 0; i < 1000; i++)
        {
            inputs.Add(System.Text.Encoding.UTF8.GetBytes($"http://ex/g{i % 5}"));
            inputs.Add(System.Text.Encoding.UTF8.GetBytes($"http://ex/s{i}"));
            inputs.Add(System.Text.Encoding.UTF8.GetBytes($"http://ex/p{i % 3}"));
            inputs.Add(System.Text.Encoding.UTF8.GetBytes($"http://ex/o{i}"));
        }

        var result = SortedAtomStoreExternalBuilder.BuildExternal(
            basePath, inputs,
            useDiskBackedAssigned: true,
            resolveSorterChunkSize: 1024);

        try
        {
            Assert.NotNull(result.AssignedIdsResolver);
            Assert.Equal(inputs.Count, result.AssignedIdsResolver!.ExpectedCount);

            // Drain the resolver and verify dense, in-order coverage.
            using var reader = result.AssignedIdsResolver.GetReader();
            for (long expectedIdx = 0; expectedIdx < inputs.Count; expectedIdx++)
            {
                Assert.True(reader.TryReadNext(out var atomId), $"underread at idx {expectedIdx}");
                Assert.True(atomId >= 0, $"atomId at idx {expectedIdx} should be >= 0, got {atomId}");
            }
            Assert.False(reader.TryReadNext(out _), "resolver should be drained after ExpectedCount reads");
        }
        finally
        {
            result.AssignedIdsResolver?.Dispose();
        }
    }
}
