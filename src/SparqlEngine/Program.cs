using System;
using System.Diagnostics;
using System.IO;
using System.Runtime;
using SparqlEngine;

namespace SparqlEngine;

public class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("SPARQL Zero-GC Streaming Query Engine");
        Console.WriteLine("======================================");
        Console.WriteLine($".NET Version: {Environment.Version}");
        Console.WriteLine($"GC Mode: {(GCSettings.IsServerGC ? "Server" : "Workstation")}");
        Console.WriteLine();

        // Configure for zero-GC operation
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        
        // Run tests first
        Tests.Tests.RunAllTests();
        Console.WriteLine();
        
        // Run comprehensive examples
        Examples.ComprehensiveExamples.RunAll();
        Console.WriteLine();
        
        // Run file storage examples
        Examples.FileStorageExamples.RunAll();
        Examples.FileStorageExamples.DemoTBScaleCapability();
        Console.WriteLine();
        
        // Run Sky Omega temporal examples
        Examples.SkyOmegaExamples.RunAll();
        Examples.SkyOmegaExamples.DemoSkyOmegaCapabilities();
        Examples.SkyOmegaExamples.BenchmarkTemporalStorage();
        Console.WriteLine();
        
        RunExamples();
        RunBenchmarks();
    }

    private static void RunExamples()
    {
        Console.WriteLine("=== Basic Examples ===");
        Console.WriteLine();

        // Example 1: Parse and execute simple SPARQL query
        Example1_BasicQuery();
        
        // Example 2: Load and query RDF data
        Example2_RdfData();
        
        // Example 3: Streaming large dataset
        Example3_StreamingQuery();
    }

    private static void Example1_BasicQuery()
    {
        Console.WriteLine("Example 1: Basic SPARQL Query Parsing");
        Console.WriteLine("--------------------------------------");

        const string query = "SELECT * WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        
        try
        {
            var parsed = parser.ParseQuery();
            Console.WriteLine($"Query Type: {parsed.Type}");
            Console.WriteLine($"Select All: {parsed.SelectClause.SelectAll}");
            Console.WriteLine($"Distinct: {parsed.SelectClause.Distinct}");
            Console.WriteLine("✓ Query parsed successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Parse error: {ex.Message}");
        }
        
        Console.WriteLine();
    }

    private static void Example2_RdfData()
    {
        Console.WriteLine("Example 2: RDF Triple Store");
        Console.WriteLine("---------------------------");

        using var store = new StreamingTripleStore();
        
        // Add some triples
        store.Add("<http://example.org/person/1>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\"");
        store.Add("<http://example.org/person/1>", "<http://xmlns.com/foaf/0.1/age>", "\"30\"");
        store.Add("<http://example.org/person/2>", "<http://xmlns.com/foaf/0.1/name>", "\"Bob\"");
        store.Add("<http://example.org/person/2>", "<http://xmlns.com/foaf/0.1/age>", "\"25\"");
        
        Console.WriteLine("Added 4 triples to store");
        
        // Query all triples
        Console.WriteLine("\nQuerying all triples:");
        var enumerator = store.Query(
            ReadOnlySpan<char>.Empty,
            ReadOnlySpan<char>.Empty,
            ReadOnlySpan<char>.Empty
        );
        
        int count = 0;
        while (enumerator.MoveNext())
        {
            var triple = enumerator.Current;
            Console.WriteLine($"  {triple.Subject.ToString()} {triple.Predicate.ToString()} {triple.Object.ToString()}");
            count++;
        }
        
        Console.WriteLine($"✓ Retrieved {count} triples");
        Console.WriteLine();
    }

    private static void Example3_StreamingQuery()
    {
        Console.WriteLine("Example 3: Streaming Query Execution");
        Console.WriteLine("------------------------------------");

        using var store = new StreamingTripleStore();
        var executor = new QueryExecutor(store);
        
        // Populate with test data
        for (int i = 0; i < 100; i++)
        {
            store.Add(
                $"<http://example.org/person/{i}>",
                "<http://xmlns.com/foaf/0.1/name>",
                $"\"Person{i}\""
            );
        }
        
        Console.WriteLine("Added 100 triples");
        
        // Create and execute query
        var query = new Query
        {
            Type = QueryType.Select,
            SelectClause = new SelectClause { SelectAll = true }
        };
        
        var results = executor.Execute(query);
        
        int resultCount = 0;
        while (results.MoveNext())
        {
            resultCount++;
        }
        
        Console.WriteLine($"✓ Streamed {resultCount} results without allocation");
        Console.WriteLine();
    }

    private static void RunBenchmarks()
    {
        Console.WriteLine("=== Performance Benchmarks ===");
        Console.WriteLine();

        Benchmark_TripleInsertion();
        Benchmark_QueryExecution();
        Benchmark_ZeroGCVerification();
    }

    private static void Benchmark_TripleInsertion()
    {
        Console.WriteLine("Benchmark: Triple Insertion");
        Console.WriteLine("---------------------------");

        const int tripleCount = 100_000;
        
        using var store = new StreamingTripleStore();
        
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        
        var sw = Stopwatch.StartNew();
        
        for (int i = 0; i < tripleCount; i++)
        {
            store.Add(
                $"<http://example.org/s{i}>",
                $"<http://example.org/p{i % 10}>",
                $"<http://example.org/o{i % 100}>"
            );
        }
        
        sw.Stop();
        
        var gen0After = GC.CollectionCount(0);
        var gen1After = GC.CollectionCount(1);
        var gen2After = GC.CollectionCount(2);
        
        Console.WriteLine($"Inserted: {tripleCount:N0} triples");
        Console.WriteLine($"Time: {sw.ElapsedMilliseconds:N0} ms");
        Console.WriteLine($"Rate: {tripleCount / sw.Elapsed.TotalSeconds:N0} triples/sec");
        Console.WriteLine($"Gen0 GC: {gen0After - gen0Before}");
        Console.WriteLine($"Gen1 GC: {gen1After - gen1Before}");
        Console.WriteLine($"Gen2 GC: {gen2After - gen2Before}");
        Console.WriteLine();
    }

    private static void Benchmark_QueryExecution()
    {
        Console.WriteLine("Benchmark: Query Execution");
        Console.WriteLine("--------------------------");

        const int tripleCount = 50_000;
        const int queryCount = 1_000;
        
        using var store = new StreamingTripleStore();
        
        // Populate store
        for (int i = 0; i < tripleCount; i++)
        {
            store.Add(
                $"<http://example.org/s{i}>",
                $"<http://example.org/p{i % 10}>",
                $"<http://example.org/o{i % 100}>"
            );
        }
        
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        
        var sw = Stopwatch.StartNew();
        long totalResults = 0;
        
        for (int i = 0; i < queryCount; i++)
        {
            var enumerator = store.Query(
                ReadOnlySpan<char>.Empty,
                $"<http://example.org/p{i % 10}>",
                ReadOnlySpan<char>.Empty
            );
            
            while (enumerator.MoveNext())
            {
                totalResults++;
            }
        }
        
        sw.Stop();
        
        var gen0After = GC.CollectionCount(0);
        var gen1After = GC.CollectionCount(1);
        var gen2After = GC.CollectionCount(2);
        
        Console.WriteLine($"Queries: {queryCount:N0}");
        Console.WriteLine($"Results: {totalResults:N0}");
        Console.WriteLine($"Time: {sw.ElapsedMilliseconds:N0} ms");
        Console.WriteLine($"Rate: {queryCount / sw.Elapsed.TotalSeconds:N0} queries/sec");
        Console.WriteLine($"Gen0 GC: {gen0After - gen0Before}");
        Console.WriteLine($"Gen1 GC: {gen1After - gen1Before}");
        Console.WriteLine($"Gen2 GC: {gen2After - gen2Before}");
        Console.WriteLine();
    }

    private static void Benchmark_ZeroGCVerification()
    {
        Console.WriteLine("Benchmark: Zero-GC Verification");
        Console.WriteLine("--------------------------------");

        const int iterations = 10_000;
        
        using var store = new StreamingTripleStore();
        
        // Pre-populate to ensure pool is warmed up
        for (int i = 0; i < 1000; i++)
        {
            store.Add(
                $"<http://example.org/s{i}>",
                "<http://example.org/p>",
                $"<http://example.org/o{i}>"
            );
        }
        
        // Force a collection to start clean
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        var memoryBefore = GC.GetTotalMemory(false);
        
        var sw = Stopwatch.StartNew();
        
        for (int i = 0; i < iterations; i++)
        {
            var enumerator = store.Query(
                ReadOnlySpan<char>.Empty,
                "<http://example.org/p>",
                ReadOnlySpan<char>.Empty
            );
            
            while (enumerator.MoveNext())
            {
                var triple = enumerator.Current;
                // Access the triple data
                _ = triple.Subject.Length + triple.Predicate.Length + triple.Object.Length;
            }
        }
        
        sw.Stop();
        
        var gen0After = GC.CollectionCount(0);
        var gen1After = GC.CollectionCount(1);
        var gen2After = GC.CollectionCount(2);
        var memoryAfter = GC.GetTotalMemory(false);
        
        var gen0Delta = gen0After - gen0Before;
        var gen1Delta = gen1After - gen1Before;
        var gen2Delta = gen2After - gen2Before;
        var memoryDelta = memoryAfter - memoryBefore;
        
        Console.WriteLine($"Iterations: {iterations:N0}");
        Console.WriteLine($"Time: {sw.ElapsedMilliseconds:N0} ms");
        Console.WriteLine($"Gen0 GC: {gen0Delta} (Target: 0)");
        Console.WriteLine($"Gen1 GC: {gen1Delta} (Target: 0)");
        Console.WriteLine($"Gen2 GC: {gen2Delta} (Target: 0)");
        Console.WriteLine($"Memory Delta: {memoryDelta:N0} bytes");
        
        var isZeroGC = gen0Delta == 0 && gen1Delta == 0 && gen2Delta == 0;
        Console.WriteLine($"Zero-GC Status: {(isZeroGC ? "✓ PASSED" : "✗ FAILED")}");
        Console.WriteLine();
    }
}
