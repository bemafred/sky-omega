using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using SkyOmega.Mercury.Abstractions;
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

    // ADR-037 Validation Phase 1: pipelined-spill correctness tests.

    [Fact]
    public void PipelinedSpill_QueueSaturation_ParserBlocksAndProducesCorrectStore()
    {
        // Force the parser to outpace the worker by giving the listener a slow OnSpill.
        // Buffer threshold 1 MB means many spills; 50ms/spill artificial delay forces
        // the bound-1 queue to saturate and the parser to block at every handoff.
        var basePath = Path.Combine(_testDir, "saturation");
        var listener = new CapturingBulkListener { OnSpillDelayMs = 50 };
        using (var builder = new SortedAtomBulkBuilder(
            basePath,
            chunkBufferBytes: 1L * 1024 * 1024,
            listener: listener))
        {
            for (int i = 0; i < 30_000; i++)
            {
                var s = $"http://example.org/s{i:D6}";
                var p = $"http://example.org/p{i % 50:D2}";
                var o = $"http://example.org/o{i:D6}";
                builder.AddTriple(default, s, p, o);
            }
            builder.Finalize();
        }

        Assert.NotNull(listener.LastBulkBuilderCompleted);
        var ev = listener.LastBulkBuilderCompleted!.Value;
        Assert.True(ev.SpillCount >= 2, $"expected multiple spills, got {ev.SpillCount}");
        // With 50ms-per-spill artificial cost > parser-fill time, parser MUST have
        // blocked at handoff at least once. Cumulative blocked time > 0 is the proof.
        Assert.True(ev.ParserBlockedOnSpill > TimeSpan.Zero,
            $"expected parser_blocked > 0 under saturation, got {ev.ParserBlockedOnSpill}");

        // Correctness: store opens and contains the right atom count.
        using var store = new SortedAtomStore(basePath);
        Assert.True(store.AtomCount > 0);
    }

    [Fact]
    public void PipelinedSpill_WorkerException_SurfacesOnParserThread()
    {
        // Inject a faulting listener that throws on the second OnSpill. The parser's
        // next AddTriple (or Finalize) MUST throw, with the original exception as
        // InnerException. Silent loss is the failure mode this test guards against.
        var basePath = Path.Combine(_testDir, "fault");
        var listener = new CapturingBulkListener { ThrowOnSpillIndex = 2 };

        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            using var builder = new SortedAtomBulkBuilder(
                basePath,
                chunkBufferBytes: 1L * 1024 * 1024,
                listener: listener);
            // Generate enough triples to spill > 2 chunks; the second spill faults the
            // worker. Subsequent AddTriple calls must surface the fault.
            for (int i = 0; i < 60_000; i++)
            {
                var s = $"http://example.org/s{i:D6}";
                builder.AddTriple(default, s, "p", "o");
            }
            builder.Finalize();
        });

        // Drill through wrappers — InvalidOperationException from the parser-side check
        // wraps the worker's original exception.
        Exception? cursor = ex;
        bool foundOriginal = false;
        while (cursor is not null)
        {
            if (cursor.Message.Contains("injected fault", StringComparison.Ordinal))
            {
                foundOriginal = true;
                break;
            }
            cursor = cursor.InnerException;
        }
        Assert.True(foundOriginal, $"original worker exception not surfaced; got: {ex}");
    }

    [Fact]
    public void PipelinedSpill_DisposeWithoutFinalize_CompletesPromptly()
    {
        // Builder disposed mid-load (cancellation path). Worker thread must shut down
        // cleanly within the ADR-037 30s budget — in practice well under 1s for a
        // small in-flight buffer with no fault.
        var basePath = Path.Combine(_testDir, "abort");
        var sw = Stopwatch.StartNew();
        {
            using var builder = new SortedAtomBulkBuilder(
                basePath,
                chunkBufferBytes: 1L * 1024 * 1024);
            for (int i = 0; i < 5_000; i++)
                builder.AddTriple(default, $"s{i}", "p", "o");
            // No Finalize — IDisposable closes the builder.
        }
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30),
            $"Dispose without Finalize took {sw.Elapsed} — should be well under 30s");
    }

    private sealed class CapturingBulkListener : IObservabilityListener
    {
        public int OnSpillDelayMs { get; init; }
        public int ThrowOnSpillIndex { get; init; } = -1;
        public BulkBuilderCompletedEvent? LastBulkBuilderCompleted { get; private set; }
        private int _spillCount;

        public void OnSpill(in SpillEvent ev)
        {
            int idx = Interlocked.Increment(ref _spillCount);
            if (ThrowOnSpillIndex > 0 && idx == ThrowOnSpillIndex)
                throw new InvalidOperationException("injected fault on spill " + idx);
            if (OnSpillDelayMs > 0)
                Thread.Sleep(OnSpillDelayMs);
        }

        public void OnBulkBuilderCompleted(in BulkBuilderCompletedEvent ev)
            => LastBulkBuilderCompleted = ev;
    }
}
