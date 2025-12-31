using System.Diagnostics;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;

namespace SkyOmega.Mercury.Examples;

/// <summary>
/// Examples demonstrating TB-scale temporal quad storage
/// </summary>
public static class StorageExamples
{
    public static void RunAll()
    {
        Console.WriteLine("=== TB-Scale File-Based Storage Examples ===");
        Console.WriteLine();

        Example_BasicFileStorage();
        Example_MultiIndexQueries();
        Example_LargeDatasetBulkLoad();
        Example_PersistenceAndRecovery();
    }

    public static void Example_BasicFileStorage()
    {
        Console.WriteLine("Example: Basic File Storage");
        Console.WriteLine("---------------------------");

        var dbPath = TempPath.Example("basic").FullPath;
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        using var store = new QuadStore(dbPath);

        // Add triples
        Console.WriteLine("Inserting triples...");
        for (int i = 0; i < 1000; i++)
        {
            store.AddCurrent(
                $"<http://example.org/person{i}>",
                "<http://xmlns.com/foaf/0.1/name>",
                $"\"Person {i}\""
            );
        }

        var stats = store.GetStatistics();
        Console.WriteLine($"Triples: {stats.QuadCount:N0}");
        Console.WriteLine($"Atoms: {stats.AtomCount:N0}");
        Console.WriteLine($"Storage: {stats.TotalBytes:N0} bytes");

        // Query current state
        var results = store.QueryCurrent(
            ReadOnlySpan<char>.Empty,
            "<http://xmlns.com/foaf/0.1/name>",
            ReadOnlySpan<char>.Empty
        );

        int count = 0;
        while (results.MoveNext() && count < 5)
        {
            var triple = results.Current;
            Console.WriteLine($"  {triple.Subject.ToString()} -> {triple.Object.ToString()}");
            count++;
        }

        Console.WriteLine($"... and {stats.QuadCount - count} more\n");
    }

    public static void Example_MultiIndexQueries()
    {
        Console.WriteLine("Example: Multi-Index Query Optimization");
        Console.WriteLine("---------------------------------------");

        var dbPath = TempPath.Example("multiindex").FullPath;
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        using var store = new QuadStore(dbPath);

        // Add diverse data
        Console.WriteLine("Loading data with multiple patterns...");
        for (int i = 0; i < 1000; i++)
        {
            store.AddCurrent(
                $"<http://ex.org/person{i}>",
                "<http://xmlns.com/foaf/0.1/knows>",
                $"<http://ex.org/person{(i + 1) % 1000}>"
            );

            store.AddCurrent(
                $"<http://ex.org/person{i}>",
                "<http://xmlns.com/foaf/0.1/age>",
                $"\"{20 + (i % 50)}\""
            );
        }

        Console.WriteLine("\nQuery 1: Subject bound (uses SPO index)");
        var sw = Stopwatch.StartNew();
        var results1 = store.QueryCurrent(
            "<http://ex.org/person42>",
            ReadOnlySpan<char>.Empty,
            ReadOnlySpan<char>.Empty
        );
        int count1 = 0;
        while (results1.MoveNext()) count1++;
        sw.Stop();
        Console.WriteLine($"  Results: {count1}, Time: {sw.ElapsedMilliseconds}ms");

        Console.WriteLine("\nQuery 2: Predicate bound (uses POS index)");
        sw.Restart();
        var results2 = store.QueryCurrent(
            ReadOnlySpan<char>.Empty,
            "<http://xmlns.com/foaf/0.1/knows>",
            ReadOnlySpan<char>.Empty
        );
        int count2 = 0;
        while (results2.MoveNext()) count2++;
        sw.Stop();
        Console.WriteLine($"  Results: {count2}, Time: {sw.ElapsedMilliseconds}ms");

        Console.WriteLine("\nQuery 3: Object bound (uses OSP index)");
        sw.Restart();
        var results3 = store.QueryCurrent(
            ReadOnlySpan<char>.Empty,
            ReadOnlySpan<char>.Empty,
            "\"25\""
        );
        int count3 = 0;
        while (results3.MoveNext()) count3++;
        sw.Stop();
        Console.WriteLine($"  Results: {count3}, Time: {sw.ElapsedMilliseconds}ms\n");
    }

    public static void Example_LargeDatasetBulkLoad()
    {
        Console.WriteLine("Example: Large Dataset Bulk Load");
        Console.WriteLine("--------------------------------");

        var dbPath = TempPath.Example("bulk").FullPath;
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        using var store = new QuadStore(dbPath);

        const int tripleCount = 100_000;

        Console.WriteLine($"Bulk loading {tripleCount:N0} triples...");

        var sw = Stopwatch.StartNew();

        for (int i = 0; i < tripleCount; i++)
        {
            store.AddCurrent(
                $"<http://ex.org/s{i}>",
                $"<http://ex.org/p{i % 100}>",
                $"<http://ex.org/o{i % 1000}>"
            );

            if ((i + 1) % 25000 == 0)
            {
                Console.Write($"\r  Progress: {i + 1:N0} / {tripleCount:N0}");
            }
        }

        sw.Stop();
        Console.WriteLine();

        var stats = store.GetStatistics();

        Console.WriteLine($"\nBulk load complete:");
        Console.WriteLine($"  Triples: {stats.QuadCount:N0}");
        Console.WriteLine($"  Atoms: {stats.AtomCount:N0}");
        Console.WriteLine($"  Storage: {stats.TotalBytes / (1024.0 * 1024.0):F2} MB");
        Console.WriteLine($"  Time: {sw.ElapsedMilliseconds:N0} ms");
        Console.WriteLine($"  Rate: {tripleCount / sw.Elapsed.TotalSeconds:N0} triples/sec\n");
    }

    public static void Example_PersistenceAndRecovery()
    {
        Console.WriteLine("Example: Persistence and Recovery");
        Console.WriteLine("---------------------------------");

        var dbPath = TempPath.Example("persist").FullPath;
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        // Phase 1: Create and populate database
        Console.WriteLine("Phase 1: Creating database...");

        using (var store = new QuadStore(dbPath))
        {
            for (int i = 0; i < 1000; i++)
            {
                store.AddCurrent(
                    $"<http://ex.org/s{i}>",
                    "<http://ex.org/p>",
                    $"\"Value {i}\""
                );
            }

            var stats = store.GetStatistics();
            Console.WriteLine($"  Triples inserted: {stats.QuadCount:N0}");
        }

        Console.WriteLine("  Database closed");

        // Phase 2: Reopen and verify
        Console.WriteLine("\nPhase 2: Reopening database...");

        using (var store = new QuadStore(dbPath))
        {
            var stats = store.GetStatistics();
            Console.WriteLine($"  Triples recovered: {stats.QuadCount:N0}");

            // Verify data
            var results = store.QueryCurrent(
                "<http://ex.org/s42>",
                "<http://ex.org/p>",
                ReadOnlySpan<char>.Empty
            );

            if (results.MoveNext())
            {
                var triple = results.Current;
                Console.WriteLine($"  Sample triple: {triple.Subject.ToString()} -> {triple.Object.ToString()}");
            }
        }

        Console.WriteLine($"  Persistence verified\n");
    }

    public static void DemoCapabilities()
    {
        Console.WriteLine("=== TB-Scale Capability Overview ===");
        Console.WriteLine();

        Console.WriteLine("Storage Architecture:");
        Console.WriteLine("  - Memory-mapped B+Tree with 16KB pages");
        Console.WriteLine("  - Four temporal indexes: SPOT, POST, OSPT, TSPO");
        Console.WriteLine("  - Bitemporal data model (valid-time + transaction-time)");
        Console.WriteLine("  - Atom storage with hash-based deduplication");
        Console.WriteLine("  - 64-bit atom IDs for TB-scale capacity");
        Console.WriteLine();

        Console.WriteLine("Temporal Capabilities:");
        Console.WriteLine("  - Time-travel queries (as-of semantics)");
        Console.WriteLine("  - Temporal range queries");
        Console.WriteLine("  - Evolution tracking (full history)");
        Console.WriteLine("  - Transaction-time auditing");
        Console.WriteLine();

        // B+Tree capacity calculation
        const int entriesPerPage = 204;
        const int pageSize = 16384;
        var heightFor1T = Math.Ceiling(Math.Log(1_000_000_000_000.0, entriesPerPage));

        Console.WriteLine("Theoretical Capacity:");
        Console.WriteLine($"  - Page size: {pageSize:N0} bytes");
        Console.WriteLine($"  - Entries per page: {entriesPerPage:N0}");
        Console.WriteLine($"  - Tree height for 1T triples: ~{heightFor1T:F0} levels");
        Console.WriteLine($"  - Disk seeks for lookup: {heightFor1T:F0}");
        Console.WriteLine($"  - With 95% cache hit rate: ~{heightFor1T * 0.05:F1} disk seeks");
        Console.WriteLine();
    }
}
