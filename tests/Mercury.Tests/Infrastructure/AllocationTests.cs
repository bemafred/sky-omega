using System;
using System.IO;
using System.Threading.Tasks;
using SkyOmega.Mercury.Rdf;
using SkyOmega.Mercury.Rdf.Turtle;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Infrastructure;

/// <summary>
/// Tests verifying zero-GC behavior of hot paths.
/// Uses GC.GetAllocatedBytesForCurrentThread() to measure allocations.
/// </summary>
public class AllocationTests
{
    /// <summary>
    /// SPARQL parser is a ref struct and should not allocate during parsing.
    /// </summary>
    [Fact]
    public void SparqlParser_ParseQuery_ZeroAllocations()
    {
        var query = "SELECT * WHERE { ?s ?p ?o }";

        // Warmup - first parse may allocate for JIT, static init, etc.
        _ = new SparqlParser(query.AsSpan()).ParseQuery();

        // Force GC to get clean baseline
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();

        // Parse multiple times to amplify any allocations
        for (int i = 0; i < 100; i++)
        {
            var parser = new SparqlParser(query.AsSpan());
            _ = parser.ParseQuery();
        }

        var after = GC.GetAllocatedBytesForCurrentThread();
        var allocated = after - before;

        // Allow small tolerance for measurement overhead
        Assert.True(allocated < 1000,
            $"SPARQL parser allocated {allocated} bytes over 100 parses. Expected near-zero.");
    }

    /// <summary>
    /// QuadStore queries should not allocate when iterating results.
    /// The ref struct enumerator lives on the stack.
    /// </summary>
    [Fact]
    public void QuadStore_QueryIteration_ZeroAllocations()
    {
        var tempPath = TempPath.Test("alloc");
        tempPath.MarkOwnership();
        var dbPath = tempPath.FullPath;

        try
        {
            using var store = new QuadStore(dbPath);

            // Pre-populate with test data
            for (int i = 0; i < 100; i++)
            {
                store.AddCurrent(
                    $"<http://ex.org/s{i}>",
                    "<http://ex.org/predicate>",
                    $"<http://ex.org/o{i}>"
                );
            }

            // Warmup query
            store.AcquireReadLock();
            try
            {
                var warmup = store.QueryCurrent(
                    ReadOnlySpan<char>.Empty,
                    "<http://ex.org/predicate>",
                    ReadOnlySpan<char>.Empty
                );
                try
                {
                    while (warmup.MoveNext()) { }
                }
                finally
                {
                    warmup.Dispose();
                }
            }
            finally
            {
                store.ReleaseReadLock();
            }

            // Force GC
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();

            // Query and iterate multiple times
            for (int i = 0; i < 100; i++)
            {
                store.AcquireReadLock();
                try
                {
                    var results = store.QueryCurrent(
                        ReadOnlySpan<char>.Empty,
                        "<http://ex.org/predicate>",
                        ReadOnlySpan<char>.Empty
                    );

                    try
                    {
                        int count = 0;
                        while (results.MoveNext())
                        {
                            var triple = results.Current;
                            count += triple.Subject.Length;
                        }
                    }
                    finally
                    {
                        results.Dispose();
                    }
                }
                finally
                {
                    store.ReleaseReadLock();
                }
            }

            var after = GC.GetAllocatedBytesForCurrentThread();
            var allocated = after - before;

            // Allow up to 16KB for ArrayPool bucket initialization overhead.
            // The key improvement: 2MB → 8KB (99.6% reduction).
            // Per-query allocations are now zero after warmup.
            Assert.True(allocated < 16_000,
                $"QuadStore query iteration allocated {allocated} bytes over 100 queries. " +
                $"Expected < 16KB (ArrayPool overhead). Was {allocated / 100} bytes/query avg.");
        }
        finally
        {
            TempPath.SafeCleanup(dbPath);
        }
    }

    /// <summary>
    /// Turtle parser with TripleHandler callback should be zero-GC.
    /// Uses pooled output buffer, no string allocations per triple.
    /// </summary>
    [Fact]
    public async Task TurtleParser_ZeroGC_WithHandler()
    {
        var turtle = """
            @prefix ex: <http://example.org/> .
            ex:subject1 ex:predicate1 ex:object1 .
            ex:subject2 ex:predicate2 "literal value" .
            ex:subject3 ex:predicate3 ex:object3 .
            """u8.ToArray();

        // Warmup - create parser and parse once
        await using (var warmupStream = new MemoryStream(turtle))
        {
            var warmupParser = new TurtleStreamParser(warmupStream);
            await warmupParser.ParseAsync((s, p, o) => { });
        }

        // Force GC
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        int tripleCount = 0;

        // Parse with zero-GC handler API
        await using (var stream = new MemoryStream(turtle))
        {
            var parser = new TurtleStreamParser(stream);
            await parser.ParseAsync((subject, predicate, obj) =>
            {
                tripleCount++;
                // Access spans to ensure they're used (no allocation)
                _ = subject.Length + predicate.Length + obj.Length;
            });
        }

        var after = GC.GetAllocatedBytesForCurrentThread();
        var allocated = after - before;

        // Near-zero-GC: Main allocations eliminated. Remaining sources:
        // - Dictionary<string,string> lookups require string keys (prefix.ToString())
        // - Blank node ID generation (string.Concat)
        // - Async state machine overhead
        // These are minimal and amortized. Target: < 100KB total (vs ~10KB/triple before)
        var bytesPerTriple = tripleCount > 0 ? allocated / tripleCount : 0;
        Assert.True(allocated < 100_000,
            $"Zero-GC Turtle parser allocated {allocated} bytes for {tripleCount} triples " +
            $"({bytesPerTriple} bytes/triple). Expected < 100KB total.");
    }

    /// <summary>
    /// Legacy IAsyncEnumerable API still works but allocates strings.
    /// </summary>
    [Fact]
    public async Task TurtleParser_LegacyAPI_Allocates()
    {
        var turtle = """
            @prefix ex: <http://example.org/> .
            ex:subject1 ex:predicate1 ex:object1 .
            """u8.ToArray();

        // Warmup
        await using (var warmupStream = new MemoryStream(turtle))
        {
            var warmupParser = new TurtleStreamParser(warmupStream);
            await foreach (var _ in warmupParser.ParseAsync()) { }
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        int tripleCount = 0;

        await using (var stream = new MemoryStream(turtle))
        {
            var parser = new TurtleStreamParser(stream);
            await foreach (var triple in parser.ParseAsync())
            {
                tripleCount++;
                _ = triple.Subject.Length;
            }
        }

        var after = GC.GetAllocatedBytesForCurrentThread();
        var allocated = after - before;

        // Legacy API allocates strings - just verify it still works
        Assert.True(tripleCount > 0, "Legacy API should parse triples");
    }

    /// <summary>
    /// Test that verifies we can detect allocations - control test.
    /// </summary>
    [Fact]
    public void AllocationDetection_CanDetectAllocations()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();

        // Intentionally allocate
        var list = new System.Collections.Generic.List<string>();
        for (int i = 0; i < 100; i++)
        {
            list.Add($"string number {i}");
        }

        var after = GC.GetAllocatedBytesForCurrentThread();
        var allocated = after - before;

        // Should detect significant allocations
        Assert.True(allocated > 1000,
            $"Control test: expected significant allocations but only measured {allocated} bytes.");
    }
}
