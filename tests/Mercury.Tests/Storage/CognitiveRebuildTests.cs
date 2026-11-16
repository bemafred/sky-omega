using System;
using System.Collections.Generic;
using System.IO;
using SkyOmega.Mercury.Diagnostics;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-061 I14 and I19: a Cognitive rebuild derives GPOS, GOSP and TGSP from GSPO. It carries every entry of every
/// graph as it is, with both times and its retraction. It discards what the three held, so when one of them holds an
/// entry that GSPO does not hold as it is, the rebuild changes nothing and says so: such a difference needs a rule of
/// its own (ADR-061 LQ5).
///
/// The store chooses an index by the pattern of a read, so each index is read through the read it serves: the
/// subject bound or nothing bound reads GSPO, the predicate alone GPOS, the object alone GOSP, and a time range TGSP.
/// A range does not return a retracted period.
/// </summary>
public class CognitiveRebuildTests : IDisposable
{
    private const string P = "<urn:rebuild:p>";
    private const string G = "<urn:rebuild:g>";

    private static DateTimeOffset Reading(int n) => new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(n);

    private readonly string _root;

    public CognitiveRebuildTests()
    {
        var tempPath = TempPath.Test("cognitive-rebuild");
        tempPath.MarkOwnership();
        _root = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_root);

    /// <summary>
    /// What one index serves in one graph, one line per period, sorted: subject, object, the period in clock readings
    /// (minutes after 2031-01-01), and when it was retracted. <paramref name="index"/> is GSPO, GPOS, GOSP or TGSP.
    /// </summary>
    private static List<string> PeriodsThrough(QuadStore store, string index, string graph, string[] objects)
    {
        var start = Reading(0);
        string Minutes(DateTimeOffset t) => ((long)(t - start).TotalMinutes).ToString();
        var lines = new List<string>();
        store.AcquireReadLock();
        try
        {
            var reads = index == "GOSP" ? objects.Length : 1;
            for (int i = 0; i < reads; i++)
            {
                var entries = index switch
                {
                    "GSPO" => store.QueryEvolution(default, default, default, graph),
                    "GPOS" => store.QueryEvolution(default, P, default, graph),
                    "GOSP" => store.QueryEvolution(default, default, objects[i], graph),
                    _ => store.QueryChanges(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, default, default, default, graph),
                };
                while (entries.MoveNext())
                {
                    var e = entries.Current;
                    var forgotten = e.ForgottenAt.Year >= 9000 ? "" : Minutes(e.ForgottenAt);
                    var retracted = e.RetractedAt is { } r ? $" retracted {Minutes(r)}" : "";
                    lines.Add($"{e.Subject.ToString()} {e.Object.ToString()} [{Minutes(e.MemorizedAt)}, {forgotten}){retracted}");
                }
                entries.Dispose();
            }
        }
        finally { store.ReleaseReadLock(); }
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    [Fact]
    public void AnIndexFileThatIsLost_IsRebuiltFromGspo_WithEveryPeriodOfEveryGraphAsItWas()
    {
        var dir = Path.Combine(_root, "store");
        var clock = new SuppliedClock();
        var options = new StorageOptions { Clock = clock, IndexInitialSizeBytes = 1L << 20, AtomDataInitialSizeBytes = 1L << 20 };
        string[] objects = ["\"one\"", "\"two\"", "\"three\""];

        // In the default graph and in G alike: "one" is remembered; "two" was told, forgotten and told again; "three"
        // was told and then retracted, recorded in error.
        using (var store = new QuadStore(dir, null, null, options))
        {
            foreach (var graph in new[] { "", G })
            {
                clock.Reading = Reading(1);
                store.AddCurrent("<urn:rebuild:s1>", P, "\"one\"", graph);
                clock.Reading = Reading(2);
                store.AddCurrent("<urn:rebuild:s2>", P, "\"two\"", graph);
                clock.Reading = Reading(3);
                store.DeleteCurrent("<urn:rebuild:s2>", P, "\"two\"", graph);
                clock.Reading = Reading(4);
                store.AddCurrent("<urn:rebuild:s2>", P, "\"two\"", graph);
                clock.Reading = Reading(5);
                store.AddCurrent("<urn:rebuild:s3>", P, "\"three\"", graph);
                clock.Reading = Reading(6);
                Assert.True(store.Retract("<urn:rebuild:s3>", P, "\"three\"", Reading(5), DateTimeOffset.MaxValue, graph));
            }
        }

        List<string> every = ["<urn:rebuild:s1> \"one\" [1, )", "<urn:rebuild:s2> \"two\" [2, 3)", "<urn:rebuild:s2> \"two\" [4, )",
            "<urn:rebuild:s3> \"three\" [5, ) retracted 6"];
        List<string> notRetracted = ["<urn:rebuild:s1> \"one\" [1, )", "<urn:rebuild:s2> \"two\" [2, 3)", "<urn:rebuild:s2> \"two\" [4, )"];

        // The three secondary index files are lost.
        File.Delete(Path.Combine(dir, "gpos.tdb"));
        File.Delete(Path.Combine(dir, "gosp.tdb"));
        File.Delete(Path.Combine(dir, "tgsp.tdb"));

        var logPath = Path.Combine(_root, "mercury.log");
        var log = new FileLogger(logPath, LogLevel.Info);
        using (var store = new QuadStore(dir, log, null, options))
        {
            foreach (var graph in new[] { "", G })
            {
                Assert.Equal(every, PeriodsThrough(store, "GSPO", graph, objects));
                Assert.Empty(PeriodsThrough(store, "GPOS", graph, objects));
                Assert.Empty(PeriodsThrough(store, "GOSP", graph, objects));
                Assert.Empty(PeriodsThrough(store, "TGSP", graph, objects));
            }

            var rebuilt = new List<(string Name, long Count)>();
            store.RebuildSecondaryIndexes((name, count) => rebuilt.Add((name, count)));
            Assert.Equal(new List<(string, long)> { ("GPOS", 8), ("GOSP", 8), ("TGSP", 8) }, rebuilt.GetRange(0, 3));

            foreach (var graph in new[] { "", G })
            {
                Assert.Equal(every, PeriodsThrough(store, "GSPO", graph, objects));
                Assert.Equal(every, PeriodsThrough(store, "GPOS", graph, objects));
                Assert.Equal(every, PeriodsThrough(store, "GOSP", graph, objects));
                Assert.Equal(notRetracted, PeriodsThrough(store, "TGSP", graph, objects));
            }
        }
        log.Dispose();
        Assert.Contains("Rebuilt TGSP from GSPO: it held 0 entries, each of them in GSPO, and holds 8.", File.ReadAllText(logPath));

        using (var store = new QuadStore(dir, null, null, options))
        {
            foreach (var graph in new[] { "", G })
            {
                Assert.Equal(every, PeriodsThrough(store, "GPOS", graph, objects));
                Assert.Equal(every, PeriodsThrough(store, "GOSP", graph, objects));
                Assert.Equal(notRetracted, PeriodsThrough(store, "TGSP", graph, objects));
            }
        }
    }

    [Fact]
    public void AfterABulkLoad_TheRebuildGivesTheSecondaryIndexesEveryGraph()
    {
        var dir = Path.Combine(_root, "bulk");
        var clock = new SuppliedClock { Reading = Reading(1) };
        var options = new StorageOptions { Clock = clock, BulkMode = true, IndexInitialSizeBytes = 1L << 20, AtomDataInitialSizeBytes = 1L << 20 };
        string[] objects = ["\"one\"", "\"two\""];

        using var store = new QuadStore(dir, null, null, options);
        store.BeginBatch();
        store.AddCurrentBatched("<urn:rebuild:s1>", P, "\"one\"");
        store.AddCurrentBatched("<urn:rebuild:s2>", P, "\"two\"");
        store.AddCurrentBatched("<urn:rebuild:s1>", P, "\"one\"", G);
        store.AddCurrentBatched("<urn:rebuild:s2>", P, "\"two\"", G);
        store.CommitBatch();
        store.FlushToDisk();

        var rebuilt = new List<(string Name, long Count)>();
        store.RebuildSecondaryIndexes((name, count) => rebuilt.Add((name, count)));

        Assert.Equal(new List<(string, long)> { ("GPOS", 4), ("GOSP", 4), ("TGSP", 4) }, rebuilt.GetRange(0, 3));
        List<string> both = ["<urn:rebuild:s1> \"one\" [1, )", "<urn:rebuild:s2> \"two\" [1, )"];
        foreach (var graph in new[] { "", G })
        {
            Assert.Equal(both, PeriodsThrough(store, "GPOS", graph, objects));
            Assert.Equal(both, PeriodsThrough(store, "GOSP", graph, objects));
            Assert.Equal(both, PeriodsThrough(store, "TGSP", graph, objects));
        }
    }

    [Fact]
    public void AnEntryOpenInTgsp_ThatGspoHoldsAsForgotten_IsNotResolved_TheRebuildChangesNothingAndSaysSo()
    {
        // Two stores with the same writes at the same readings, so their atoms have the same identifiers. In the
        // second, "two" is then forgotten. The second gets the first one's TGSP: it holds "two" open, the kind of
        // the live memory store's 204 entries (ADR-061 M11).
        var told = Path.Combine(_root, "told");
        var forgotten = Path.Combine(_root, "forgotten");
        var clock = new SuppliedClock();
        var options = new StorageOptions { Clock = clock, IndexInitialSizeBytes = 1L << 20, AtomDataInitialSizeBytes = 1L << 20 };
        string[] objects = ["\"one\"", "\"two\""];
        foreach (var dir in new[] { told, forgotten })
        {
            using var store = new QuadStore(dir, null, null, options);
            clock.Reading = Reading(1);
            store.AddCurrent("<urn:rebuild:s1>", P, "\"one\"");
            clock.Reading = Reading(2);
            store.AddCurrent("<urn:rebuild:s2>", P, "\"two\"");
            if (dir == forgotten)
            {
                clock.Reading = Reading(3);
                store.DeleteCurrent("<urn:rebuild:s2>", P, "\"two\"");
            }
        }
        File.Copy(Path.Combine(told, "tgsp.tdb"), Path.Combine(forgotten, "tgsp.tdb"), overwrite: true);

        var logPath = Path.Combine(_root, "mercury.log");
        var log = new FileLogger(logPath, LogLevel.Info);
        using (var store = new QuadStore(forgotten, log, null, options))
        {
            List<string> inGspo = ["<urn:rebuild:s1> \"one\" [1, )", "<urn:rebuild:s2> \"two\" [2, 3)"];
            List<string> inTgsp = ["<urn:rebuild:s1> \"one\" [1, )", "<urn:rebuild:s2> \"two\" [2, )"];
            Assert.Equal(inGspo, PeriodsThrough(store, "GSPO", "", objects));
            Assert.Equal(inTgsp, PeriodsThrough(store, "TGSP", "", objects));

            var refusal = Assert.Throws<InvalidOperationException>(() => store.RebuildSecondaryIndexes());
            Assert.Contains("The rebuild changed nothing: GPOS holds 0, GOSP 0 and TGSP 1 entries that GSPO does not hold as they are.", refusal.Message);

            Assert.Equal(StoreIndexState.Ready, store.IndexState);
            Assert.Equal(inGspo, PeriodsThrough(store, "GSPO", "", objects));
            Assert.Equal(inGspo, PeriodsThrough(store, "GPOS", "", objects));
            Assert.Equal(inGspo, PeriodsThrough(store, "GOSP", "", objects));
            Assert.Equal(inTgsp, PeriodsThrough(store, "TGSP", "", objects));
        }
        log.Dispose();
        Assert.Contains("The rebuild changed nothing: GPOS holds 0, GOSP 0 and TGSP 1 entries", File.ReadAllText(logPath));
    }

    [Fact]
    public void AnEntryInGpos_ThatGspoDoesNotHold_IsNotDiscarded_TheRebuildChangesNothingAndSaysSo()
    {
        // Two stores with the same writes at the same readings. In the second, "two" is forgotten in the reading it
        // was told in, so no period of it is kept (ADR-061 I9). The second gets the first one's GPOS: it holds an
        // entry GSPO does not hold at all, the kind of the live memory store's 47 entries (ADR-061 M11).
        var told = Path.Combine(_root, "told");
        var notKept = Path.Combine(_root, "not-kept");
        var clock = new SuppliedClock();
        var options = new StorageOptions { Clock = clock, IndexInitialSizeBytes = 1L << 20, AtomDataInitialSizeBytes = 1L << 20 };
        string[] objects = ["\"one\"", "\"two\""];
        foreach (var dir in new[] { told, notKept })
        {
            using var store = new QuadStore(dir, null, null, options);
            clock.Reading = Reading(1);
            store.AddCurrent("<urn:rebuild:s1>", P, "\"one\"");
            clock.Reading = Reading(2);
            store.AddCurrent("<urn:rebuild:s2>", P, "\"two\"");
            if (dir == notKept)
                store.DeleteCurrent("<urn:rebuild:s2>", P, "\"two\"");
        }
        File.Copy(Path.Combine(told, "gpos.tdb"), Path.Combine(notKept, "gpos.tdb"), overwrite: true);

        using var store2 = new QuadStore(notKept, null, null, options);
        List<string> inGspo = ["<urn:rebuild:s1> \"one\" [1, )"];
        List<string> inGpos = ["<urn:rebuild:s1> \"one\" [1, )", "<urn:rebuild:s2> \"two\" [2, )"];
        Assert.Equal(inGspo, PeriodsThrough(store2, "GSPO", "", objects));
        Assert.Equal(inGpos, PeriodsThrough(store2, "GPOS", "", objects));

        var refusal = Assert.Throws<InvalidOperationException>(() => store2.RebuildSecondaryIndexes());
        Assert.Contains("The rebuild changed nothing: GPOS holds 1, GOSP 0 and TGSP 0 entries that GSPO does not hold as they are.", refusal.Message);

        Assert.Equal(inGpos, PeriodsThrough(store2, "GPOS", "", objects));
        Assert.Equal(inGspo, PeriodsThrough(store2, "GOSP", "", objects));
        Assert.Equal(inGspo, PeriodsThrough(store2, "TGSP", "", objects));
    }
}
