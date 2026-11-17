using System;
using System.Collections.Generic;
using System.IO;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-064 M2 to M5: on a Graph store a fact's states form a sequence, asserted, retracted, asserted and so on, and
/// the fact's version names the state it is in: odd is asserted, even is retracted. Asserting a fact that is asserted,
/// or retracting one that is not, changes nothing (G2); every other assert or retract raises the version by one (G3),
/// inside a batch too (M2). Every index holds the same versions, retracted facts included (G5), and recovery changes
/// nothing (G7). A client sees the facts whose version is odd; the quad count counts every fact the store holds.
///
/// What an index holds is read from its file once the store is closed, through the index's own cursor: no read of the
/// store returns a version (ADR-064 D4).
/// </summary>
public class FactStatesTests : IDisposable
{
    private const string S = "<urn:states:s>";
    private const string P = "<urn:states:p>";
    private const string G = "<urn:states:g>";

    private readonly string _root;

    public FactStatesTests()
    {
        var tempPath = TempPath.Test("fact-states");
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
        var entries = held.QueryAllVersions(-1, -1, -1, -1);
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

    [Theory]
    [InlineData(false)]   // each operation is a single write
    [InlineData(true)]    // every operation is in one batch
    public void EachAssertOrRetract_ThatChangesAFact_IsAStateOfItsOwn_AndEveryIndexHoldsItsVersion(bool inOneBatch)
    {
        var dir = Path.Combine(_root, "store");
        var options = new StorageOptions { Profile = StoreProfile.Graph, IndexInitialSizeBytes = 1L << 20, AtomDataInitialSizeBytes = 1L << 20 };

        // One fact for each row of M4's table, and the same fact as s3's in a named graph, which is another fact (G1).
        using (var store = new QuadStore(dir, null, null, options))
        {
            if (inOneBatch) store.BeginBatch();
            void AssertFact(string subject, string @object, string graph = "")
            {
                if (inOneBatch) store.AddCurrentBatched(subject, P, @object, graph);
                else store.AddCurrent(subject, P, @object, graph);
            }
            void RetractFact(string subject, string @object)
            {
                if (inOneBatch) store.DeleteCurrentBatched(subject, P, @object);
                else store.DeleteCurrent(subject, P, @object);
            }

            // The last operation on each fact is the row of M4 it states.
            AssertFact("<urn:states:s1>", "\"one\"");                                       // assert, none: version 1
            AssertFact("<urn:states:s2>", "\"two\"");
            AssertFact("<urn:states:s2>", "\"two\"");                                       // assert, odd: nothing
            AssertFact("<urn:states:s3>", "\"three\"");
            RetractFact("<urn:states:s3>", "\"three\"");
            AssertFact("<urn:states:s3>", "\"three\"");                                     // assert, even 2: 3
            RetractFact("<urn:states:s4>", "\"four\"");                                     // retract, none: nothing
            AssertFact("<urn:states:s5>", "\"five\"");
            RetractFact("<urn:states:s5>", "\"five\"");                                     // retract, odd 1: 2
            AssertFact("<urn:states:s6>", "\"six\"");
            RetractFact("<urn:states:s6>", "\"six\"");
            RetractFact("<urn:states:s6>", "\"six\"");                                      // retract, even: nothing
            AssertFact("<urn:states:s3>", "\"three\"", G);                                  // assert, none: 1
            if (inOneBatch) store.CommitBatch();

            // A client sees the facts whose version is odd.
            foreach (var (subject, @object, graph, seen) in new[]
            {
                ("<urn:states:s1>", "\"one\"", "", true), ("<urn:states:s2>", "\"two\"", "", true),
                ("<urn:states:s3>", "\"three\"", "", true), ("<urn:states:s3>", "\"three\"", G, true),
                ("<urn:states:s4>", "\"four\"", "", false), ("<urn:states:s5>", "\"five\"", "", false),
                ("<urn:states:s6>", "\"six\"", "", false),
            })
            {
                var rows = 0;
                var current = store.QueryCurrent(subject, P, @object, graph);
                while (current.MoveNext()) rows++;
                current.Dispose();
                Assert.True(rows == (seen ? 1 : 0), $"{subject} {@object} {graph}: {rows} rows");
            }

            // Six facts are held: s4 was never asserted.
            Assert.Equal(6, store.GetStatistics().QuadCount);
        }

        List<string> held =
        [
            "<urn:states:s1> <urn:states:p> \"one\" v1",
            "<urn:states:s2> <urn:states:p> \"two\" v1",
            "<urn:states:s3> <urn:states:p> \"three\" <urn:states:g> v1",
            "<urn:states:s3> <urn:states:p> \"three\" v3",
            "<urn:states:s5> <urn:states:p> \"five\" v2",
            "<urn:states:s6> <urn:states:p> \"six\" v2",
        ];
        Assert.Equal(held, HeldIn(dir, "gspo"));
        Assert.Equal(held, HeldIn(dir, "gpos"));
        Assert.Equal(held, HeldIn(dir, "gosp"));
    }

    /// <summary>
    /// A separate process (tests/Mercury.CrashTarget, the two states modes) writes and is killed, so the files are what
    /// a killed process leaves. Its first run asserts four facts and exits cleanly: the test copies that store, whose
    /// index files have applied the log up to there. Its second run asserts and retracts them again, in single writes
    /// and in batches, and is killed: those index files have applied all of the log, and the log is not truncated. The
    /// test then opens stores in which some index files are the earlier ones and some the later ones.
    /// </summary>
    [Theory]
    [InlineData("")]                   // every index has applied all of the log
    [InlineData("gspo gpos gosp")]     // every index is at the earlier point
    [InlineData("gpos gosp")]          // GSPO is ahead of the other two
    [InlineData("gspo")]               // GSPO is behind the other two
    [InlineData("gosp")]               // GOSP is behind the other two
    public void ReplayingTheLog_OntoIndexesAtDifferentPoints_GivesTheVersionsOfAnUninterruptedRun(string indexesAtTheEarlierPoint)
    {
        var killed = Path.Combine(_root, "killed");
        var earlier = Path.Combine(_root, "earlier");

        CrashTarget.Run("states-first", killed, "Graph", 0).AssertExitedCleanly();
        Directory.CreateDirectory(earlier);
        foreach (var file in Directory.GetFiles(killed))
            File.Copy(file, Path.Combine(earlier, Path.GetFileName(file)));

        CrashTarget.Run("states-killed", killed, "Graph", 0).AssertKilledAfter("changed");

        foreach (var index in indexesAtTheEarlierPoint.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            File.Copy(Path.Combine(earlier, index + ".tdb"), Path.Combine(killed, index + ".tdb"), overwrite: true);

        using (var store = new QuadStore(killed))
        {
            // A client sees K, A, D and E; B and C are retracted.
            var seen = new List<string>();
            var current = store.QueryCurrent(S, P, default);
            while (current.MoveNext()) seen.Add(current.Current.Object.ToString());
            current.Dispose();
            seen.Sort(StringComparer.Ordinal);
            Assert.Equal(["\"A\"", "\"D\"", "\"E\"", "\"K\""], seen);

            // Six facts are held.
            Assert.Equal(6, store.GetStatistics().QuadCount);
        }

        List<string> held =
        [
            "<urn:states:s> <urn:states:p> \"A\" v3",    // retracted and asserted again
            "<urn:states:s> <urn:states:p> \"B\" v2",    // retracted, and retracted again, which changed nothing
            "<urn:states:s> <urn:states:p> \"C\" v4",    // retracted, asserted and retracted in one batch
            "<urn:states:s> <urn:states:p> \"D\" v3",    // asserted, retracted and asserted in one batch
            "<urn:states:s> <urn:states:p> \"E\" v1",
            "<urn:states:s> <urn:states:p> \"K\" v1",    // asserted again, which changed nothing
        ];
        Assert.Equal(held, HeldIn(killed, "gspo"));
        Assert.Equal(held, HeldIn(killed, "gpos"));
        Assert.Equal(held, HeldIn(killed, "gosp"));
    }
}
