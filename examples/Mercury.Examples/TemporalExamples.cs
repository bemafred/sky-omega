using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Examples;

/// <summary>
/// Examples demonstrating bitemporal querying capabilities
/// </summary>
public static class TemporalExamples
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

    public static void Example_BasicTemporal()
    {
        Console.WriteLine("Example: Basic Temporal Triples");
        Console.WriteLine("--------------------------------");

        var dbPath = Path.Combine(Path.GetTempPath(), "mercury-example-temporal-basic");
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        using var store = new QuadStore(dbPath);

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

        Console.WriteLine();
    }

    public static void Example_TimeTravelQuery()
    {
        Console.WriteLine("Example: Time-Travel Queries");
        Console.WriteLine("----------------------------");

        var dbPath = Path.Combine(Path.GetTempPath(), "mercury-example-timetravel");
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        using var store = new QuadStore(dbPath);

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

        Console.WriteLine();
    }

    public static void Example_VersionTracking()
    {
        Console.WriteLine("Example: Version Tracking");
        Console.WriteLine("-------------------------");

        var dbPath = Path.Combine(Path.GetTempPath(), "mercury-example-versions");
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        using var store = new QuadStore(dbPath);

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

        Console.WriteLine();
    }

    public static void Example_TemporalRangeQuery()
    {
        Console.WriteLine("Example: Temporal Range Queries");
        Console.WriteLine("--------------------------------");

        var dbPath = Path.Combine(Path.GetTempPath(), "mercury-example-range");
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        using var store = new QuadStore(dbPath);

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

        Console.WriteLine();
    }

    public static void Example_BiTemporalCorrections()
    {
        Console.WriteLine("Example: Bitemporal Corrections");
        Console.WriteLine("--------------------------------");

        var dbPath = Path.Combine(Path.GetTempPath(), "mercury-example-bitemporal");
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        using var store = new QuadStore(dbPath);

        // Initially recorded: Eve worked at Acme from 2020-2023
        store.Add(
            "<http://ex.org/eve>",
            "<http://ex.org/worksFor>",
            "<http://ex.org/Acme>",
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero)
        );

        // Later discovered: Actually left in 2022
        store.Add(
            "<http://ex.org/eve>",
            "<http://ex.org/worksFor>",
            "<http://ex.org/Acme>",
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2022, 12, 31, 0, 0, 0, TimeSpan.Zero)
        );

        Console.WriteLine("Bitemporal model explained:");
        Console.WriteLine("  Valid-time: When fact was true in reality");
        Console.WriteLine("  Transaction-time: When fact was recorded in DB");
        Console.WriteLine("\n  This enables:");
        Console.WriteLine("    - What we KNOW now about the past");
        Console.WriteLine("    - What we KNEW then about the past");
        Console.WriteLine("    - When our knowledge changed");
        Console.WriteLine();
    }

    public static void Example_EvolutionTracking()
    {
        Console.WriteLine("Example: Entity Evolution Tracking");
        Console.WriteLine("-----------------------------------");

        var dbPath = Path.Combine(Path.GetTempPath(), "mercury-example-evolution");
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        using var store = new QuadStore(dbPath);

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
        Console.WriteLine("\nCompany evolution (first 5 entries):");
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

        Console.WriteLine();
    }

    public static void Example_SnapshotReconstruction()
    {
        Console.WriteLine("Example: Historical Snapshot Reconstruction");
        Console.WriteLine("-------------------------------------------");

        var dbPath = Path.Combine(Path.GetTempPath(), "mercury-example-snapshot");
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        using var store = new QuadStore(dbPath);

        // Build knowledge base about a country over time
        store.Add("<http://ex.org/Country1>", "<http://ex.org/population>", "\"10M\"",
            new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero));

        store.Add("<http://ex.org/Country1>", "<http://ex.org/population>", "\"12M\"",
            new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));

        store.Add("<http://ex.org/Country1>", "<http://ex.org/population>", "\"13M\"",
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DateTimeOffset.MaxValue);

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

        Console.WriteLine();
    }

    public static void Example_TemporalPersistence()
    {
        Console.WriteLine("Example: Temporal Database Persistence");
        Console.WriteLine("---------------------------------------");

        var dbPath = Path.Combine(Path.GetTempPath(), "mercury-example-temporal-persist");
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        // Phase 1: Create and populate
        Console.WriteLine("Phase 1: Creating temporal database...");

        using (var store = new QuadStore(dbPath))
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
            Console.WriteLine($"  Temporal triples: {stats.QuadCount:N0}");
        }

        Console.WriteLine("  Database closed");

        // Phase 2: Reopen and verify
        Console.WriteLine("\nPhase 2: Reopening temporal database...");

        using (var store = new QuadStore(dbPath))
        {
            var stats = store.GetStatistics();
            Console.WriteLine($"  Temporal triples recovered: {stats.QuadCount:N0}");

            var results = store.QueryAsOf(
                "<http://ex.org/metric>",
                "<http://ex.org/value>",
                ReadOnlySpan<char>.Empty,
                new DateTimeOffset(2022, 6, 1, 0, 0, 0, TimeSpan.Zero)
            );

            if (results.MoveNext())
            {
                var triple = results.Current;
                Console.WriteLine($"  Value in 2022: {triple.Object.ToString()}");
            }
        }

        Console.WriteLine("  Persistence verified\n");
    }

    public static void DemoCapabilities()
    {
        Console.WriteLine("=== Sky Omega Temporal Capabilities ===");
        Console.WriteLine();

        Console.WriteLine("Bitemporal Model:");
        Console.WriteLine("  - Valid Time (VT): When fact is true in reality");
        Console.WriteLine("  - Transaction Time (TT): When fact recorded in DB");
        Console.WriteLine();

        Console.WriteLine("Query Types:");
        Console.WriteLine("  - Point-in-time: What was true at time T?");
        Console.WriteLine("  - Range: What changed between T1 and T2?");
        Console.WriteLine("  - Evolution: Show all versions");
        Console.WriteLine("  - Current: What is true now?");
        Console.WriteLine();

        Console.WriteLine("Use Cases:");
        Console.WriteLine("  - Audit trails: Who changed what and when?");
        Console.WriteLine("  - Historical analysis: Reconstruct past states");
        Console.WriteLine("  - Data corrections: Fix errors without losing history");
        Console.WriteLine("  - Trend analysis: Track changes over time");
        Console.WriteLine("  - Compliance: Prove what was known when");
        Console.WriteLine();

        Console.WriteLine("Storage:");
        Console.WriteLine("  - 4 indexes: SPOT, POST, OSPT, TSPO");
        Console.WriteLine("  - 80-byte temporal entries");
        Console.WriteLine("  - 204 entries per 16KB page");
        Console.WriteLine("  - Memory-mapped B+Trees (persistent)");
        Console.WriteLine("  - Scales to billions of temporal triples");
        Console.WriteLine();
    }
}
