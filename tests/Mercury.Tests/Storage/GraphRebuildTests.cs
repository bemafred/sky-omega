using System;
using System.Collections.Generic;
using System.IO;
using SkyOmega.Mercury.Diagnostics;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-064 G6, G8 and D6: a Graph rebuild derives GPOS and GOSP from GSPO. It carries every fact of every graph with
/// its version, retracted facts included. It discards what the two held, so when one of them holds an entry that
/// GSPO does not hold as it is, with its version, the rebuild changes nothing and says so.
///
/// What an index holds is read from its file once the store is closed, through the index's own cursor: no read of the
/// store returns a version (ADR-064 D4).
/// </summary>
public class GraphRebuildTests : IDisposable
{
    private const string P = "<urn:rebuild:p>";
    private const string G = "<urn:rebuild:g>";

    private readonly string _root;

    public GraphRebuildTests()
    {
        var tempPath = TempPath.Test("graph-rebuild");
        tempPath.MarkOwnership();
        _root = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_root);

    /// <summary>
    /// What one index file of a closed Graph store holds, one line per fact, sorted: subject, predicate, object, the
    /// graph when it is a named one, and the version. <paramref name="index"/> is gspo, gpos or gosp.
    /// </summary>
    private static List<string> HeldIn(string dir, string index)
    {
        var lines = new List<string>();
        using var atoms = new HashAtomStore(Path.Combine(dir, "atoms"));
        using var held = new VersionedQuadIndex(Path.Combine(dir, index + ".tdb"), atoms, 1L << 20);
        var entries = held.QueryHeld(-1, -1, -1, -1);
        while (entries.MoveNext())
        {
            var (s, p, o) = index switch
            {
                "gpos" => (entries.Tertiary, entries.Primary, entries.Secondary),
                "gosp" => (entries.Secondary, entries.Tertiary, entries.Primary),
                _ => (entries.Primary, entries.Secondary, entries.Tertiary),
            };
            var graph = entries.Graph == 0 ? "" : " " + atoms.GetAtomString(entries.Graph);
            lines.Add($"{atoms.GetAtomString(s)} {atoms.GetAtomString(p)} {atoms.GetAtomString(o)}{graph} v{entries.Version}");
        }
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    [Fact]
    public void AnIndexFileThatIsLost_IsRebuiltFromGspo_WithEveryFactsVersion_RetractedFactsIncluded()
    {
        var dir = Path.Combine(_root, "store");
        var options = new StorageOptions { Profile = StoreProfile.Graph, IndexInitialSizeBytes = 1L << 20, AtomDataInitialSizeBytes = 1L << 20 };

        // In the default graph and in G alike: "one" is asserted; "two" was asserted, retracted and asserted again;
        // "three" was asserted and retracted.
        using (var store = new QuadStore(dir, null, null, options))
        {
            foreach (var graph in new[] { "", G })
            {
                store.AddCurrent("<urn:rebuild:s1>", P, "\"one\"", graph);
                store.AddCurrent("<urn:rebuild:s2>", P, "\"two\"", graph);
                store.DeleteCurrent("<urn:rebuild:s2>", P, "\"two\"", graph);
                store.AddCurrent("<urn:rebuild:s2>", P, "\"two\"", graph);
                store.AddCurrent("<urn:rebuild:s3>", P, "\"three\"", graph);
                store.DeleteCurrent("<urn:rebuild:s3>", P, "\"three\"", graph);
            }
        }

        List<string> every =
        [
            "<urn:rebuild:s1> <urn:rebuild:p> \"one\" <urn:rebuild:g> v1",
            "<urn:rebuild:s1> <urn:rebuild:p> \"one\" v1",
            "<urn:rebuild:s2> <urn:rebuild:p> \"two\" <urn:rebuild:g> v3",
            "<urn:rebuild:s2> <urn:rebuild:p> \"two\" v3",
            "<urn:rebuild:s3> <urn:rebuild:p> \"three\" <urn:rebuild:g> v2",
            "<urn:rebuild:s3> <urn:rebuild:p> \"three\" v2",
        ];
        Assert.Equal(every, HeldIn(dir, "gspo"));

        // The two secondary index files are lost.
        File.Delete(Path.Combine(dir, "gpos.tdb"));
        File.Delete(Path.Combine(dir, "gosp.tdb"));

        var logPath = Path.Combine(_root, "mercury.log");
        var log = new FileLogger(logPath, LogLevel.Info);
        using (var store = new QuadStore(dir, log, null, options))
        {
            var rebuilt = new List<(string Name, long Count)>();
            store.RebuildSecondaryIndexes((name, count) => rebuilt.Add((name, count)));
            Assert.Equal(new List<(string, long)> { ("GPOS", 6), ("GOSP", 6) }, rebuilt.GetRange(0, 2));

            // A read that binds the predicate is served by GPOS, one that binds the object by GOSP. Each sees the
            // asserted facts of its graph.
            foreach (var graph in new[] { "", G })
            {
                var byPredicate = 0;
                var gpos = store.QueryCurrent(default, P, default, graph);
                while (gpos.MoveNext()) byPredicate++;
                gpos.Dispose();
                Assert.Equal(2, byPredicate);

                var byObject = 0;
                var gosp = store.QueryCurrent(default, default, "\"two\"", graph);
                while (gosp.MoveNext()) byObject++;
                gosp.Dispose();
                Assert.Equal(1, byObject);
            }
        }
        log.Dispose();
        Assert.Contains("Rebuilt GOSP from GSPO: it held 0 entries, each of them in GSPO, and holds 6.", File.ReadAllText(logPath));

        Assert.Equal(every, HeldIn(dir, "gspo"));
        Assert.Equal(every, HeldIn(dir, "gpos"));
        Assert.Equal(every, HeldIn(dir, "gosp"));
    }

    [Fact]
    public void AnEntryInGpos_ThatGspoHoldsAtAnotherVersion_IsNotResolved_TheRebuildChangesNothingAndSaysSo()
    {
        // Two stores with the same writes, so their atoms have the same identifiers. In the second, "two" is then
        // retracted. The second gets the first one's GPOS: it holds "two" asserted, at version 1, where GSPO holds it
        // retracted, at version 2.
        var asserted = Path.Combine(_root, "asserted");
        var retracted = Path.Combine(_root, "retracted");
        var options = new StorageOptions { Profile = StoreProfile.Graph, IndexInitialSizeBytes = 1L << 20, AtomDataInitialSizeBytes = 1L << 20 };
        foreach (var dir in new[] { asserted, retracted })
        {
            using var store = new QuadStore(dir, null, null, options);
            store.AddCurrent("<urn:rebuild:s1>", P, "\"one\"");
            store.AddCurrent("<urn:rebuild:s2>", P, "\"two\"");
            if (dir == retracted)
                store.DeleteCurrent("<urn:rebuild:s2>", P, "\"two\"");
        }
        File.Copy(Path.Combine(asserted, "gpos.tdb"), Path.Combine(retracted, "gpos.tdb"), overwrite: true);

        List<string> inGspo = ["<urn:rebuild:s1> <urn:rebuild:p> \"one\" v1", "<urn:rebuild:s2> <urn:rebuild:p> \"two\" v2"];
        List<string> inGpos = ["<urn:rebuild:s1> <urn:rebuild:p> \"one\" v1", "<urn:rebuild:s2> <urn:rebuild:p> \"two\" v1"];

        var logPath = Path.Combine(_root, "mercury.log");
        var log = new FileLogger(logPath, LogLevel.Info);
        using (var store = new QuadStore(retracted, log, null, options))
        {
            var refusal = Assert.Throws<InvalidOperationException>(() => store.RebuildSecondaryIndexes());
            Assert.Contains("The rebuild changed nothing: GPOS holds 1 and GOSP 0 entries that GSPO does not hold as they are.", refusal.Message);
            Assert.Equal(StoreIndexState.Ready, store.IndexState);
        }
        log.Dispose();
        Assert.Contains("The rebuild changed nothing: GPOS holds 1 and GOSP 0 entries", File.ReadAllText(logPath));

        Assert.Equal(inGspo, HeldIn(retracted, "gspo"));
        Assert.Equal(inGpos, HeldIn(retracted, "gpos"));
        Assert.Equal(inGspo, HeldIn(retracted, "gosp"));
    }
}
