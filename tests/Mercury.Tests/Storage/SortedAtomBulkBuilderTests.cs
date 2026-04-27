using System;
using System.IO;
using System.Linq;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-034 Phase 1B-5a: SortedAtomBulkBuilder correctness. Builds vocabulary + per-triple
/// atom-ID resolution end-to-end, verifying input-order is preserved and atom IDs follow
/// dense alphabetical order.
/// </summary>
public class SortedAtomBulkBuilderTests : IDisposable
{
    private readonly string _testDir;

    public SortedAtomBulkBuilderTests()
    {
        var tempPath = TempPath.Test("sorted_atom_bulk");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    [Fact]
    public void EmptyBuilder_FinalizeProducesZeroAtoms()
    {
        var basePath = Path.Combine(_testDir, "empty");
        using var builder = new SortedAtomBulkBuilder(basePath);
        var result = builder.Finalize();
        Assert.Equal(0, result.AtomCount);

        Assert.Empty(builder.EnumerateResolved());
    }

    [Fact]
    public void SingleTriple_RoundTrips()
    {
        var basePath = Path.Combine(_testDir, "single");
        using var builder = new SortedAtomBulkBuilder(basePath);
        builder.AddTriple("g1", "subj", "pred", "obj");

        var result = builder.Finalize();
        Assert.Equal(4, result.AtomCount);  // 4 distinct strings

        // Sorted byte order: g1 < obj < pred < subj
        // Expected IDs: g1=1, obj=2, pred=3, subj=4
        var resolved = builder.EnumerateResolved().ToList();
        Assert.Single(resolved);
        Assert.Equal(1, resolved[0].GraphId);
        Assert.Equal(4, resolved[0].SubjectId);
        Assert.Equal(3, resolved[0].PredicateId);
        Assert.Equal(2, resolved[0].ObjectId);

        // Files are durable; SortedAtomStore opens and reads them.
        using var store = new SortedAtomStore(basePath);
        Assert.Equal("g1", store.GetAtomString(1));
        Assert.Equal("obj", store.GetAtomString(2));
        Assert.Equal("pred", store.GetAtomString(3));
        Assert.Equal("subj", store.GetAtomString(4));
    }

    [Fact]
    public void DefaultGraph_EmptyGraphYieldsZeroId()
    {
        var basePath = Path.Combine(_testDir, "default_graph");
        using var builder = new SortedAtomBulkBuilder(basePath);
        builder.AddTriple(default, "subj", "pred", "obj");
        builder.Finalize();

        var resolved = builder.EnumerateResolved().Single();
        Assert.Equal(0, resolved.GraphId);  // default graph -> sentinel atom 0
        Assert.True(resolved.SubjectId > 0);
    }

    [Fact]
    public void RepeatedAtomsShareIds()
    {
        var basePath = Path.Combine(_testDir, "repeats");
        using var builder = new SortedAtomBulkBuilder(basePath);
        builder.AddTriple("g", "alice", "knows", "bob");
        builder.AddTriple("g", "bob", "knows", "alice");
        builder.AddTriple("g", "alice", "knows", "carol");

        var result = builder.Finalize();
        Assert.Equal(5, result.AtomCount);  // alice, bob, carol, g, knows

        var resolved = builder.EnumerateResolved().ToList();
        Assert.Equal(3, resolved.Count);

        // alice/bob/g/knows/carol → sorted: alice(1) bob(2) carol(3) g(4) knows(5)
        Assert.Equal(4, resolved[0].GraphId);  // "g"
        Assert.Equal(1, resolved[0].SubjectId);  // "alice"
        Assert.Equal(5, resolved[0].PredicateId);  // "knows"
        Assert.Equal(2, resolved[0].ObjectId);  // "bob"

        Assert.Equal(2, resolved[1].SubjectId);  // "bob"
        Assert.Equal(1, resolved[1].ObjectId);  // "alice"

        Assert.Equal(3, resolved[2].ObjectId);  // "carol"

        // Verify all rows reference the same predicate ID for "knows"
        foreach (var r in resolved) Assert.Equal(5, r.PredicateId);
    }

    [Fact]
    public void LargeBatch_PreservesInputOrderAndDeduplicates()
    {
        var basePath = Path.Combine(_testDir, "large");
        using var builder = new SortedAtomBulkBuilder(basePath);

        // 1000 triples; each reuses one of 100 subjects. Vocabulary should collapse.
        var rng = new Random(7);
        var triples = new (string g, string s, string p, string o)[1000];
        for (int i = 0; i < triples.Length; i++)
        {
            triples[i] = (
                "default",
                $"http://ex/s{rng.Next(0, 100)}",
                $"http://ex/p{i % 5}",
                $"http://ex/o{rng.Next(0, 200)}");
        }
        foreach (var t in triples) builder.AddTriple(t.g, t.s, t.p, t.o);

        var result = builder.Finalize();
        // ~100 subjects + 5 predicates + ~200 objects + 1 graph = ~306 atoms
        Assert.True(result.AtomCount < 320);
        Assert.True(result.AtomCount > 200);

        // Resolved tuples are in input order; same input string maps to the same ID
        // across all occurrences.
        var resolved = builder.EnumerateResolved().ToList();
        Assert.Equal(1000, resolved.Count);

        using var store = new SortedAtomStore(basePath);
        for (int i = 0; i < triples.Length; i++)
        {
            Assert.Equal(triples[i].g, store.GetAtomString(resolved[i].GraphId));
            Assert.Equal(triples[i].s, store.GetAtomString(resolved[i].SubjectId));
            Assert.Equal(triples[i].p, store.GetAtomString(resolved[i].PredicateId));
            Assert.Equal(triples[i].o, store.GetAtomString(resolved[i].ObjectId));
        }
    }

    [Fact]
    public void Finalize_Idempotent()
    {
        var basePath = Path.Combine(_testDir, "idempotent");
        using var builder = new SortedAtomBulkBuilder(basePath);
        builder.AddTriple("g", "s", "p", "o");
        var first = builder.Finalize();
        var second = builder.Finalize();
        Assert.Equal(first, second);
    }

    [Fact]
    public void AddTriple_AfterFinalize_Throws()
    {
        var basePath = Path.Combine(_testDir, "frozen");
        using var builder = new SortedAtomBulkBuilder(basePath);
        builder.AddTriple("g", "s", "p", "o");
        builder.Finalize();
        Assert.Throws<InvalidOperationException>(() => builder.AddTriple("g", "x", "y", "z"));
    }

    [Fact]
    public void EnumerateResolved_BeforeFinalize_Throws()
    {
        var basePath = Path.Combine(_testDir, "premature");
        using var builder = new SortedAtomBulkBuilder(basePath);
        builder.AddTriple("g", "s", "p", "o");
        Assert.Throws<InvalidOperationException>(() => builder.EnumerateResolved().ToList());
    }
}
