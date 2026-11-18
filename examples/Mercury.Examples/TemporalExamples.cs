using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;

namespace SkyOmega.Mercury.Examples;

/// <summary>
/// The store's clock, set by an example to show writes a day apart. A store's two times are readings of its own clock,
/// taken at the write (ADR-061 D1, I11): no writer states one.
/// </summary>
internal sealed class ExampleClock : TimeProvider
{
    public DateTimeOffset Reading = new(2031, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Reading;
}

/// <summary>
/// Examples of memory time on a Cognitive store (ADR-061). Each period of a fact has two times: when the store memorized
/// it and when it forgot it. When something happened in the world is content, a triple of its own (D3).
/// </summary>
public static class TemporalExamples
{
    public static void RunAll()
    {
        Console.WriteLine("=== Sky Omega Memory Time Examples ===");
        Console.WriteLine();

        Example_MemorizeAndForget();
        Example_WhatWasRememberedAtATime();
        Example_MemorizedSince();
        Example_RememberedAgain();
        Example_Persistence();
    }

    public static void Example_MemorizeAndForget()
    {
        Console.WriteLine("Example: Memorize and Forget");
        Console.WriteLine("----------------------------");

        var dbPath = TempPath.Example("memory-basic").FullPath;
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        var clock = new ExampleClock();
        using var store = new QuadStore(dbPath, null, null, new StorageOptions { Clock = clock });

        // A SPARQL INSERT memorizes; a DELETE forgets, and the period stays in the store with both times
        store.AddCurrent("<http://ex.org/alice>", "<http://ex.org/status>", "\"on leave\"");
        clock.Reading = clock.Reading.AddDays(10);
        store.DeleteCurrent("<http://ex.org/alice>", "<http://ex.org/status>", "\"on leave\"");
        store.AddCurrent("<http://ex.org/alice>", "<http://ex.org/status>", "\"at work\"");

        Console.WriteLine("\nWhat is remembered:");
        var current = store.QueryCurrent("<http://ex.org/alice>", "<http://ex.org/status>", ReadOnlySpan<char>.Empty);
        while (current.MoveNext())
            Console.WriteLine($"  {current.Current.Subject.ToString()} {current.Current.Object.ToString()}");
        current.Dispose();

        Console.WriteLine("\nEvery period, forgotten ones too:");
        var every = store.QueryMemories("<http://ex.org/alice>", "<http://ex.org/status>", ReadOnlySpan<char>.Empty,
            includingForgotten: true);
        while (every.MoveNext())
        {
            var memory = every.Current;
            var forgotten = memory.ForgottenAt is { } at ? $"{at:yyyy-MM-dd}" : "(remembered)";
            Console.WriteLine($"  {memory.Object.ToString()}: memorized {memory.MemorizedAt:yyyy-MM-dd}, forgotten {forgotten}");
        }
        every.Dispose();

        Console.WriteLine();
    }

    public static void Example_WhatWasRememberedAtATime()
    {
        Console.WriteLine("Example: What Was Remembered at a Time");
        Console.WriteLine("--------------------------------------");

        var dbPath = TempPath.Example("memory-at-a-time").FullPath;
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        var clock = new ExampleClock();
        using var store = new QuadStore(dbPath, null, null, new StorageOptions { Clock = clock });

        var day0 = clock.Reading;
        store.AddCurrent("<http://ex.org/bob>", "<http://ex.org/project>", "<http://ex.org/Alpha>");
        clock.Reading = day0.AddDays(30);
        store.DeleteCurrent("<http://ex.org/bob>", "<http://ex.org/project>", "<http://ex.org/Alpha>");
        store.AddCurrent("<http://ex.org/bob>", "<http://ex.org/project>", "<http://ex.org/Beta>");

        // What was remembered at t is two reads (ADR-061 Q10), with b one millisecond after t: what was memorized
        // before b and is remembered, and what was memorized before b and forgotten since b
        foreach (var t in new[] { day0.AddDays(15), day0.AddDays(45) })
        {
            Console.WriteLine($"\nWhat was remembered on {t:yyyy-MM-dd}?");
            var b = t.AddMilliseconds(1);
            var remembered = store.QueryMemories("<http://ex.org/bob>", "<http://ex.org/project>", ReadOnlySpan<char>.Empty,
                memorizedBefore: b);
            while (remembered.MoveNext())
                Console.WriteLine($"  {remembered.Current.Object.ToString()}");
            remembered.Dispose();
            var forgottenSince = store.QueryMemories("<http://ex.org/bob>", "<http://ex.org/project>", ReadOnlySpan<char>.Empty,
                memorizedBefore: b, forgottenSince: b);
            while (forgottenSince.MoveNext())
                Console.WriteLine($"  {forgottenSince.Current.Object.ToString()}");
            forgottenSince.Dispose();
        }

        Console.WriteLine();
    }

    public static void Example_MemorizedSince()
    {
        Console.WriteLine("Example: What Was Memorized Since a Time");
        Console.WriteLine("----------------------------------------");

        var dbPath = TempPath.Example("memory-since").FullPath;
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        var clock = new ExampleClock();
        using var store = new QuadStore(dbPath, null, null, new StorageOptions { Clock = clock });

        var day0 = clock.Reading;
        for (int day = 0; day < 5; day++)
        {
            clock.Reading = day0.AddDays(day);
            store.AddCurrent("<http://ex.org/notes>", "<http://ex.org/entry>", $"\"note {day}\"");
        }

        var since = day0.AddDays(3);
        Console.WriteLine($"\nMemorized since {since:yyyy-MM-dd}:");
        var recent = store.QueryMemories("<http://ex.org/notes>", "<http://ex.org/entry>", ReadOnlySpan<char>.Empty,
            memorizedSince: since);
        while (recent.MoveNext())
            Console.WriteLine($"  {recent.Current.Object.ToString()} ({recent.Current.MemorizedAt:yyyy-MM-dd})");
        recent.Dispose();

        Console.WriteLine();
    }

    public static void Example_RememberedAgain()
    {
        Console.WriteLine("Example: A Fact Remembered Again");
        Console.WriteLine("--------------------------------");

        var dbPath = TempPath.Example("memory-again").FullPath;
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        var clock = new ExampleClock();
        using var store = new QuadStore(dbPath, null, null, new StorageOptions { Clock = clock });

        // Memorized, forgotten at a later reading and memorized again at a reading after that: two periods
        store.AddCurrent("<http://ex.org/carol>", "<http://ex.org/likes>", "<http://ex.org/tea>");
        clock.Reading = clock.Reading.AddDays(7);
        store.DeleteCurrent("<http://ex.org/carol>", "<http://ex.org/likes>", "<http://ex.org/tea>");
        clock.Reading = clock.Reading.AddDays(7);
        store.AddCurrent("<http://ex.org/carol>", "<http://ex.org/likes>", "<http://ex.org/tea>");

        Console.WriteLine("\nThe periods of one fact:");
        var periods = store.QueryMemories("<http://ex.org/carol>", "<http://ex.org/likes>", "<http://ex.org/tea>",
            includingForgotten: true);
        while (periods.MoveNext())
        {
            var memory = periods.Current;
            var forgotten = memory.ForgottenAt is { } at ? $"{at:yyyy-MM-dd}" : "(remembered)";
            Console.WriteLine($"  memorized {memory.MemorizedAt:yyyy-MM-dd}, forgotten {forgotten}");
        }
        periods.Dispose();

        Console.WriteLine();
    }

    public static void Example_Persistence()
    {
        Console.WriteLine("Example: Memory Time Persistence");
        Console.WriteLine("--------------------------------");

        var dbPath = TempPath.Example("memory-persist").FullPath;
        if (Directory.Exists(dbPath))
            Directory.Delete(dbPath, true);

        var clock = new ExampleClock();
        Console.WriteLine("Phase 1: Creating the store...");
        using (var store = new QuadStore(dbPath, null, null, new StorageOptions { Clock = clock }))
        {
            for (int day = 0; day < 5; day++)
            {
                clock.Reading = clock.Reading.AddDays(1);
                store.AddCurrent("<http://ex.org/metric>", "<http://ex.org/value>", $"\"{day * 100}\"");
            }
            Console.WriteLine($"  Memories: {store.GetStatistics().QuadCount:N0}");
        }
        Console.WriteLine("  Store closed");

        Console.WriteLine("\nPhase 2: Reopening...");
        using (var store = new QuadStore(dbPath, null, null, new StorageOptions { Clock = clock }))
        {
            var first = store.QueryMemories("<http://ex.org/metric>", "<http://ex.org/value>", "\"0\"");
            if (first.MoveNext())
                Console.WriteLine($"  \"0\" was memorized {first.Current.MemorizedAt:yyyy-MM-dd}, as before");
            first.Dispose();
        }

        Console.WriteLine("  Persistence verified\n");
    }

    public static void DemoCapabilities()
    {
        Console.WriteLine("=== Sky Omega Memory Time ===");
        Console.WriteLine();

        Console.WriteLine("Memory time (ADR-061):");
        Console.WriteLine("  - Memorized: the store's clock reading when it took a fact in");
        Console.WriteLine("  - Forgotten: the reading when it let the fact go; absent while it is remembered");
        Console.WriteLine("  - When something happened in the world is content: a triple of its own");
        Console.WriteLine();

        Console.WriteLine("Cognitive SPARQL:");
        Console.WriteLine("  - ?s ?p ?o MEMORIZED ?t SINCE a BEFORE b");
        Console.WriteLine("  - ?s ?p ?o FORGOTTEN ?t SINCE a BEFORE b");
        Console.WriteLine("  - ... INCLUDING FORGOTTEN, after the query");
        Console.WriteLine("  - Without the words: what is remembered");
        Console.WriteLine();

        Console.WriteLine("Use Cases:");
        Console.WriteLine("  - What did we memorize last week?");
        Console.WriteLine("  - What was remembered on a given day?");
        Console.WriteLine("  - What was forgotten, and when?");
        Console.WriteLine();

        Console.WriteLine("Storage:");
        Console.WriteLine("  - 4 indexes: GSPO, GPOS, GOSP, TGSP");
        Console.WriteLine("  - Memory-mapped B+Trees (persistent)");
        Console.WriteLine();
    }
}
