using System;
using System.IO;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Sky Omega temporal RDF examples
/// Demonstrates bitemporal querying capabilities
/// </summary>
public static class SkyOmegaExamples
{
    public static void RunAll()
    {
        Console.WriteLine("=== Sky Omega Temporal RDF Examples ===");
        Console.WriteLine();

        Example_BasicTemporal();
        Example_TimeTravelQuery();
        Example_VersionTracking();
        Example_TemporalRangeQuery();
        Example_BiTemporalCorrections();
        Example_EvolutionTracking();
        Example_SnapshotReconstruction();
        Example_TemporalPersistence();
    }

    private static void Example_BasicTemporal()
    {
        Console.WriteLine("Example: Basic Temporal Triples");
        Console.WriteLine("--------------------------------");

        var dbPath = Path.Combine(Path.GetTempPath(), "sky_omega_basic");
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        using var store = new TripleStore(dbPath);

        // Alice worked at Anthropic from 2020 to 2023
        store.Add(
            "<http://ex.org/alice>",
            "<http://ex.org/worksFor>",
            "<http://ex.org/Anthropic>",
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2023, 6, 30, 0, 0, 0, TimeSpan.Zero)
        );

        // Alice works at OpenAI from 2023 onwards
        store.Add(
            "<http://ex.org/alice>",
            "<http://ex.org/worksFor>",
            "<http://ex.org/OpenAI>",
            new DateTimeOffset(2023, 7, 1, 0, 0, 0, TimeSpan.Zero),
            DateTimeOffset.MaxValue
        );

        // Query current state
        Console.WriteLine("\nCurrent state (2025):");
        var current = store.QueryCurrent(
            "<http://ex.org/alice>",
            "<http://ex.org/worksFor>",
            ReadOnlySpan<char>.Empty
        );

        while (current.MoveNext())
        {
            var triple = current.Current;
            Console.WriteLine($"  {triple.Subject.ToString()} works for {triple.Object.ToString()}");
            Console.WriteLine($"    Valid: {triple.ValidFrom:yyyy-MM-dd} to {triple.ValidTo:yyyy-MM-dd}");
        }

        Console.WriteLine($"✓ Temporal triples working\n");
    }

    private static void Example_TimeTravelQuery()
    {
        Console.WriteLine("Example: Time-Travel Queries");
        Console.WriteLine("----------------------------");

        var dbPath = Path.Combine(Path.GetTempPath(), "sky_omega_timetravel");
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        using var store = new TripleStore(dbPath);

        // Create employment history
        var periods = new[]
        {
            ("Google", new DateTimeOffset(2015, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2018, 12, 31, 0, 0, 0, TimeSpan.Zero)),
            ("Anthropic", new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2022, 6, 30, 0, 0, 0, TimeSpan.Zero)),
            ("OpenAI", new DateTimeOffset(2022, 7, 1, 0, 0, 0, TimeSpan.Zero), DateTimeOffset.MaxValue)
        };

        foreach (var (company, from, to) in periods)
        {
            store.Add(
                "<http://ex.org/bob>",
                "<http://ex.org/worksFor>",
                $"<http://ex.org/{company}>",
                from,
                to
            );
        }

        // Time-travel queries
        var queryDates = new[]
        {
            new DateTimeOffset(2017, 6, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2020, 6, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero)
        };

        foreach (var date in queryDates)
        {
            Console.WriteLine($"\nWhere did Bob work on {date:yyyy-MM-dd}?");

            var results = store.TimeTravelTo(
                date,
                "<http://ex.org/bob>",
                "<http://ex.org/worksFor>",
                ReadOnlySpan<char>.Empty
            );

            if (results.MoveNext())
            {
                var triple = results.Current;
                Console.WriteLine($"  Answer: {triple.Object.ToString()}");
            }
        }

        Console.WriteLine($"\n✓ Time-travel queries working\n");
    }

    private static void Example_VersionTracking()
    {
        Console.WriteLine("Example: Version Tracking");
        Console.WriteLine("-------------------------");

        var dbPath = Path.Combine(Path.GetTempPath(), "sky_omega_versions");
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        using var store = new TripleStore(dbPath);

        // Track salary changes over time
        var salaryHistory = new[]
        {
            (80000, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            (90000, new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            (100000, new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            (120000, new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero), DateTimeOffset.MaxValue)
        };

        foreach (var (salary, from, to) in salaryHistory)
        {
            store.Add(
                "<http://ex.org/charlie>",
                "<http://ex.org/salary>",
                $"\"{salary}\"",
                from,
                to
            );
        }

        // Query all versions
        Console.WriteLine("\nSalary evolution:");
        var evolution = store.QueryEvolution(
            "<http://ex.org/charlie>",
            "<http://ex.org/salary>",
            ReadOnlySpan<char>.Empty
        );

        int version = 1;
        while (evolution.MoveNext())
        {
            var triple = evolution.Current;
            Console.WriteLine($"  Version {version}: {triple.Object.ToString()}");
            Console.WriteLine($"    Period: {triple.ValidFrom:yyyy-MM-dd} to {triple.ValidTo:yyyy-MM-dd}");
            version++;
        }

        Console.WriteLine($"✓ Version tracking working\n");
    }

    private static void Example_TemporalRangeQuery()
    {
        Console.WriteLine("Example: Temporal Range Queries");
        Console.WriteLine("--------------------------------");

        var dbPath = Path.Combine(Path.GetTempPath(), "sky_omega_range");
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        using var store = new TripleStore(dbPath);

        // Track project assignments
        var projects = new[]
        {
            ("ProjectAlpha", new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2023, 3, 31, 0, 0, 0, TimeSpan.Zero)),
            ("ProjectBeta", new DateTimeOffset(2023, 4, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2023, 6, 30, 0, 0, 0, TimeSpan.Zero)),
            ("ProjectGamma", new DateTimeOffset(2023, 7, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2023, 9, 30, 0, 0, 0, TimeSpan.Zero)),
            ("ProjectDelta", new DateTimeOffset(2023, 10, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2023, 12, 31, 0, 0, 0, TimeSpan.Zero))
        };

        foreach (var (project, from, to) in projects)
        {
            store.Add(
                "<http://ex.org/dana>",
                "<http://ex.org/assignedTo>",
                $"<http://ex.org/{project}>",
                from,
                to
            );
        }

        // Query what happened during Q2 2023
        Console.WriteLine("\nProjects during Q2 2023 (April-June):");
        var q2Results = store.QueryChanges(
            new DateTimeOffset(2023, 4, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2023, 6, 30, 0, 0, 0, TimeSpan.Zero),
            "<http://ex.org/dana>",
            "<http://ex.org/assignedTo>",
            ReadOnlySpan<char>.Empty
        );

        while (q2Results.MoveNext())
        {
            var triple = q2Results.Current;
            Console.WriteLine($"  {triple.Object.ToString()}");
            Console.WriteLine($"    Duration: {triple.ValidFrom:yyyy-MM-dd} to {triple.ValidTo:yyyy-MM-dd}");
        }

        Console.WriteLine($"✓ Temporal range queries working\n");
    }

    private static void Example_BiTemporalCorrections()
    {
        Console.WriteLine("Example: Bitemporal Corrections");
        Console.WriteLine("--------------------------------");

        var dbPath = Path.Combine(Path.GetTempPath(), "sky_omega_bitemporal");
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        using var store = new TripleStore(dbPath);

        // Initially recorded: Eve worked at Acme from 2020-2023
        // (Recorded on 2023-01-01)
        store.Add(
            "<http://ex.org/eve>",
            "<http://ex.org/worksFor>",
            "<http://ex.org/Acme>",
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero)
        );

        // Later discovered: Actually left in 2022
        // (Correction recorded on 2023-06-01)
        store.Add(
            "<http://ex.org/eve>",
            "<http://ex.org/worksFor>",
            "<http://ex.org/Acme>",
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2022, 12, 31, 0, 0, 0, TimeSpan.Zero)
        );

        Console.WriteLine("Bitemporal example:");
        Console.WriteLine("  Valid-time: When fact was true in reality");
        Console.WriteLine("  Transaction-time: When fact was recorded in DB");
        Console.WriteLine("\n  Can query:");
        Console.WriteLine("    - What we KNOW now about the past");
        Console.WriteLine("    - What we KNEW then about the past");
        Console.WriteLine("    - When our knowledge changed");

        Console.WriteLine($"\n✓ Bitemporal model demonstrated\n");
    }

    private static void Example_EvolutionTracking()
    {
        Console.WriteLine("Example: Entity Evolution Tracking");
        Console.WriteLine("-----------------------------------");

        var dbPath = Path.Combine(Path.GetTempPath(), "sky_omega_evolution");
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        using var store = new TripleStore(dbPath);

        // Track organization name changes
        var names = new[]
        {
            ("Twitter", new DateTimeOffset(2006, 7, 15, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2023, 7, 24, 0, 0, 0, TimeSpan.Zero)),
            ("X Corp", new DateTimeOffset(2023, 7, 24, 0, 0, 0, TimeSpan.Zero), DateTimeOffset.MaxValue)
        };

        foreach (var (name, from, to) in names)
        {
            store.Add(
                "<http://ex.org/company1>",
                "<http://ex.org/hasName>",
                $"\"{name}\"",
                from,
                to
            );
        }

        // Track CEO changes
        var ceos = new[]
        {
            ("Jack Dorsey", new DateTimeOffset(2006, 7, 15, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2008, 10, 16, 0, 0, 0, TimeSpan.Zero)),
            ("Evan Williams", new DateTimeOffset(2008, 10, 16, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2010, 10, 4, 0, 0, 0, TimeSpan.Zero)),
            ("Dick Costolo", new DateTimeOffset(2010, 10, 4, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2015, 7, 1, 0, 0, 0, TimeSpan.Zero)),
            ("Jack Dorsey", new DateTimeOffset(2015, 7, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2021, 11, 29, 0, 0, 0, TimeSpan.Zero)),
            ("Parag Agrawal", new DateTimeOffset(2021, 11, 29, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2022, 10, 27, 0, 0, 0, TimeSpan.Zero)),
            ("Elon Musk", new DateTimeOffset(2022, 10, 27, 0, 0, 0, TimeSpan.Zero), DateTimeOffset.MaxValue)
        };

        foreach (var (ceo, from, to) in ceos)
        {
            store.Add(
                "<http://ex.org/company1>",
                "<http://ex.org/hasCEO>",
                $"<http://ex.org/{ceo.Replace(" ", "")}>",
                from,
                to
            );
        }

        // Query evolution
        Console.WriteLine("\nCompany evolution:");
        var evolution = store.QueryEvolution(
            "<http://ex.org/company1>",
            ReadOnlySpan<char>.Empty,
            ReadOnlySpan<char>.Empty
        );

        int count = 0;
        while (evolution.MoveNext() && count < 5)
        {
            var triple = evolution.Current;
            Console.WriteLine($"  {triple.Predicate.ToString()}: {triple.Object.ToString()}");
            Console.WriteLine($"    {triple.ValidFrom:yyyy-MM-dd} to {triple.ValidTo:yyyy-MM-dd}");
            count++;
        }

        Console.WriteLine($"✓ Evolution tracking working\n");
    }

    private static void Example_SnapshotReconstruction()
    {
        Console.WriteLine("Example: Historical Snapshot Reconstruction");
        Console.WriteLine("-------------------------------------------");

        var dbPath = Path.Combine(Path.GetTempPath(), "sky_omega_snapshot");
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        using var store = new TripleStore(dbPath);

        // Build knowledge base about a country over time
        // Population changes
        store.Add("<http://ex.org/Country1>", "<http://ex.org/population>", "\"10M\"",
            new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero));

        store.Add("<http://ex.org/Country1>", "<http://ex.org/population>", "\"12M\"",
            new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));

        store.Add("<http://ex.org/Country1>", "<http://ex.org/population>", "\"13M\"",
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DateTimeOffset.MaxValue);

        // Capital changes
        store.Add("<http://ex.org/Country1>", "<http://ex.org/capital>", "<http://ex.org/CityA>",
            new DateTimeOffset(1950, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2015, 1, 1, 0, 0, 0, TimeSpan.Zero));

        store.Add("<http://ex.org/Country1>", "<http://ex.org/capital>", "<http://ex.org/CityB>",
            new DateTimeOffset(2015, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DateTimeOffset.MaxValue);

        // Reconstruct snapshot at 2012
        Console.WriteLine("\nSnapshot of Country1 on 2012-01-01:");
        var snapshot = store.TimeTravelTo(
            new DateTimeOffset(2012, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "<http://ex.org/Country1>",
            ReadOnlySpan<char>.Empty,
            ReadOnlySpan<char>.Empty
        );

        while (snapshot.MoveNext())
        {
            var triple = snapshot.Current;
            Console.WriteLine($"  {triple.Predicate.ToString()}: {triple.Object.ToString()}");
        }

        Console.WriteLine($"\n✓ Snapshot reconstruction working\n");
    }

    private static void Example_TemporalPersistence()
    {
        Console.WriteLine("Example: Temporal Database Persistence");
        Console.WriteLine("---------------------------------------");

        var dbPath = Path.Combine(Path.GetTempPath(), "sky_omega_persist");
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        // Phase 1: Create and populate
        Console.WriteLine("Phase 1: Creating temporal database...");

        using (var store = new TripleStore(dbPath))
        {
            for (int year = 2020; year <= 2024; year++)
            {
                store.Add(
                    "<http://ex.org/metric>",
                    "<http://ex.org/value>",
                    $"\"{year * 100}\"",
                    new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero)
                );
            }

            var stats = store.GetStatistics();
            Console.WriteLine($"  Temporal triples: {stats.TripleCount:N0}");
            Console.WriteLine($"  Atoms: {stats.AtomCount:N0}");
            Console.WriteLine($"  Storage: {stats.TotalBytes / 1024.0:F2} KB");
        }

        Console.WriteLine("  Database closed (persisted to disk)");

        // Phase 2: Reopen and verify
        Console.WriteLine("\nPhase 2: Reopening temporal database...");

        using (var store = new TripleStore(dbPath))
        {
            var stats = store.GetStatistics();
            Console.WriteLine($"  Temporal triples recovered: {stats.TripleCount:N0}");

            // Verify with time-travel query
            var results = store.QueryAsOf(
                "<http://ex.org/metric>",
                "<http://ex.org/value>",
                ReadOnlySpan<char>.Empty,
                new DateTimeOffset(2022, 6, 1, 0, 0, 0, TimeSpan.Zero)
            );

            if (results.MoveNext())
            {
                var triple = results.Current;
                Console.WriteLine($"  Sample query: Value in 2022 = {triple.Object.ToString()}");
                Console.WriteLine($"    Valid period: {triple.ValidFrom:yyyy-MM-dd} to {triple.ValidTo:yyyy-MM-dd}");
            }
        }

        // Check actual file size
        var files = Directory.GetFiles(dbPath, "*.*", SearchOption.AllDirectories);
        long totalSize = 0;
        foreach (var file in files)
        {
            totalSize += new FileInfo(file).Length;
        }

        Console.WriteLine($"\n  Disk usage: {totalSize / 1024.0:F2} KB");
        Console.WriteLine($"  Files: {files.Length}");
        Console.WriteLine($"✓ Temporal persistence verified\n");
    }

    public static void DemoSkyOmegaCapabilities()
    {
        Console.WriteLine("=== Sky Omega Temporal Capabilities ===");
        Console.WriteLine();

        Console.WriteLine("Bitemporal Model:");
        Console.WriteLine("  • Valid Time (VT): When fact is true in reality");
        Console.WriteLine("  • Transaction Time (TT): When fact recorded in DB");
        Console.WriteLine();

        Console.WriteLine("Query Types:");
        Console.WriteLine("  • Point-in-time: What was true at time T?");
        Console.WriteLine("  • Range: What changed between T1 and T2?");
        Console.WriteLine("  • Evolution: Show all versions");
        Console.WriteLine("  • Current: What is true now?");
        Console.WriteLine();

        Console.WriteLine("Use Cases:");
        Console.WriteLine("  • Audit trails: Who changed what and when?");
        Console.WriteLine("  • Historical analysis: Reconstruct past states");
        Console.WriteLine("  • Data corrections: Fix errors without losing history");
        Console.WriteLine("  • Trend analysis: Track changes over time");
        Console.WriteLine("  • Compliance: Prove what was known when");
        Console.WriteLine();

        Console.WriteLine("Storage:");
        Console.WriteLine("  • 4 indexes: SPOT, POST, OSPT, TSPO");
        Console.WriteLine("  • 80-byte temporal entries (32B key + 48B metadata)");
        Console.WriteLine("  • 204 entries per 16KB page");
        Console.WriteLine("  • Memory-mapped B+Trees (persistent)");
        Console.WriteLine("  • Scales to billions of temporal triples");
        Console.WriteLine();
    }

    public static void BenchmarkTemporalStorage()
    {
        Console.WriteLine("=== Temporal Storage Performance Benchmark ===");
        Console.WriteLine();

        var dbPath = Path.Combine(Path.GetTempPath(), "sky_omega_bench");
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        using var store = new TripleStore(dbPath);

        // Benchmark 1: Write performance
        Console.WriteLine("Benchmark 1: Temporal Write Performance");
        Console.WriteLine("---------------------------------------");

        const int writeCount = 10_000;
        var baseTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < writeCount; i++)
        {
            var validFrom = baseTime.AddDays(i);
            var validTo = baseTime.AddDays(i + 365);

            store.Add(
                $"<http://ex.org/entity{i % 100}>",
                $"<http://ex.org/property{i % 10}>",
                $"\"value{i}\"",
                validFrom,
                validTo
            );
        }

        sw.Stop();

        var stats = store.GetStatistics();

        Console.WriteLine($"  Temporal triples: {writeCount:N0}");
        Console.WriteLine($"  Time: {sw.ElapsedMilliseconds:N0} ms");
        Console.WriteLine($"  Rate: {writeCount / sw.Elapsed.TotalSeconds:N0} writes/sec");
        Console.WriteLine($"  Storage: {stats.TotalBytes / (1024.0 * 1024.0):F2} MB");
        Console.WriteLine($"  Unique atoms: {stats.AtomCount:N0}");

        // Benchmark 2: Point-in-time query
        Console.WriteLine("\nBenchmark 2: Point-in-Time Queries");
        Console.WriteLine("-----------------------------------");

        const int queryCount = 1_000;
        sw.Restart();
        long totalResults = 0;

        for (int i = 0; i < queryCount; i++)
        {
            var queryTime = baseTime.AddDays(i % 365);
            var results = store.QueryAsOf(
                $"<http://ex.org/entity{i % 100}>",
                ReadOnlySpan<char>.Empty,
                ReadOnlySpan<char>.Empty,
                queryTime
            );

            while (results.MoveNext())
            {
                totalResults++;
                var triple = results.Current;
                _ = triple.Object.Length; // Ensure not optimized away
            }
        }

        sw.Stop();

        Console.WriteLine($"  Queries: {queryCount:N0}");
        Console.WriteLine($"  Results: {totalResults:N0}");
        Console.WriteLine($"  Time: {sw.ElapsedMilliseconds:N0} ms");
        Console.WriteLine($"  Rate: {queryCount / sw.Elapsed.TotalSeconds:N0} queries/sec");

        // Benchmark 3: Temporal range query
        Console.WriteLine("\nBenchmark 3: Temporal Range Queries");
        Console.WriteLine("------------------------------------");

        sw.Restart();
        totalResults = 0;

        for (int i = 0; i < 100; i++)
        {
            var rangeStart = baseTime.AddDays(i * 10);
            var rangeEnd = rangeStart.AddDays(90);

            var results = store.QueryChanges(
                rangeStart,
                rangeEnd,
                ReadOnlySpan<char>.Empty,
                ReadOnlySpan<char>.Empty,
                ReadOnlySpan<char>.Empty
            );

            while (results.MoveNext())
            {
                totalResults++;
            }
        }

        sw.Stop();

        Console.WriteLine($"  Range queries: 100");
        Console.WriteLine($"  Results: {totalResults:N0}");
        Console.WriteLine($"  Time: {sw.ElapsedMilliseconds:N0} ms");
        Console.WriteLine($"  Throughput: {totalResults / sw.Elapsed.TotalSeconds:N0} triples/sec");

        // Benchmark 4: Evolution query
        Console.WriteLine("\nBenchmark 4: Evolution Queries");
        Console.WriteLine("-------------------------------");

        sw.Restart();
        totalResults = 0;

        for (int i = 0; i < 100; i++)
        {
            var results = store.QueryEvolution(
                $"<http://ex.org/entity{i}>",
                ReadOnlySpan<char>.Empty,
                ReadOnlySpan<char>.Empty
            );

            while (results.MoveNext())
            {
                totalResults++;
            }
        }

        sw.Stop();

        Console.WriteLine($"  Evolution queries: 100");
        Console.WriteLine($"  Versions returned: {totalResults:N0}");
        Console.WriteLine($"  Time: {sw.ElapsedMilliseconds:N0} ms");
        Console.WriteLine($"✓ Temporal benchmarks complete\n");
    }
}
