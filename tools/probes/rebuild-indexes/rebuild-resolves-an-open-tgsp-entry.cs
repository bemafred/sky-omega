#:project ../../../src/Mercury/Mercury.csproj
#:property TargetFramework=net10.0
#:property AssemblyName=Mercury.Benchmarks
// Hypothesis (read in RebuildIndex and InsertIntoLeaf, 2026-11-16): the live memory store has entries that TGSP holds
// as open and GSPO as closed (ADR-061 M11: 204). A Cognitive rebuild does not clear TGSP and inserts GSPO's closed
// entry with AddRaw. Its key sorts before the open one (ForgottenAt comes before LegacyTransactionTime in both
// orders), so the AlreadyPresent check does not see the open entry, and the closed one is inserted beside it. After
// the rebuild TGSP holds both: the open entry is still there, and a read through TGSP still returns the quad as
// remembered. So the rebuild does not resolve a difference of this kind, and adds an entry.
// Construction: a Cognitive store with <s> <p> "a" and <s> <p> "b". "a" is forgotten in GSPO, GPOS and GOSP alone,
// by each index's own internal EndPeriod, and stays open in TGSP. Read the current state through GSPO, the changes over
// all time through TGSP (QueryChanges, a range read), and the entry count per index; rebuild; read again; open the
// store again and read again. Control: "b" is remembered in every index every time.
// The assembly is named Mercury.Benchmarks to see Mercury's internals; the private index fields are read by reflection.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using SkyOmega.Mercury.Storage;

var dir = Path.Combine(Path.GetTempPath(), "mercury-probe-rebuild-open-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
const string S = "<urn:probe:s>", P = "<urn:probe:p>";

TemporalQuadIndex Index(QuadStore store, string field) =>
    (TemporalQuadIndex)typeof(QuadStore).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;

void Show(QuadStore store, string when)
{
    Console.WriteLine($"{when}:");
    var current = new List<string>();
    var changes = new List<string>();
    store.AcquireReadLock();
    try
    {
        var rows = store.QueryCurrent(S, P, default);
        while (rows.MoveNext()) current.Add(rows.Current.Object.ToString());
        rows.Dispose();
        var all = store.QueryChanges(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddYears(1), S, P, default);
        while (all.MoveNext())
            changes.Add($"{all.Current.Object.ToString()} {(all.Current.ForgottenAt.Year > 9000 ? "open" : "closed")}");
        all.Dispose();
    }
    finally { store.ReleaseReadLock(); }
    current.Sort(StringComparer.Ordinal);
    changes.Sort(StringComparer.Ordinal);
    Console.WriteLine($"  current through GSPO  : {string.Join(" ", current)}");
    Console.WriteLine($"  changes through TGSP  : {string.Join(", ", changes)}");
    Console.WriteLine($"  entries: GSPO {Index(store, "_gspoIndex").QuadCount}, GPOS {Index(store, "_gposIndex").QuadCount}, " +
                      $"GOSP {Index(store, "_gospIndex").QuadCount}, TGSP {Index(store, "_tgspIndex").QuadCount}");
}

try
{
    var options = new StorageOptions { IndexInitialSizeBytes = 1L << 20, AtomDataInitialSizeBytes = 1L << 20 };
    using (var store = new QuadStore(dir, null, null, options))
    {
        store.AddCurrent(S, P, "\"a\"");
        store.AddCurrent(S, P, "\"b\"");

        long g = 0, s = store.Atoms.GetAtomId(S), p = store.Atoms.GetAtomId(P), a = store.Atoms.GetAtomId("\"a\"");
        long memorizedAt = 0;
        store.AcquireReadLock();
        try
        {
            var all = store.QueryEvolution(S, P, "\"a\"");
            while (all.MoveNext()) memorizedAt = all.Current.MemorizedAt.ToUnixTimeMilliseconds();
            all.Dispose();
        }
        finally { store.ReleaseReadLock(); }
        // Forgotten in the past, so that a current read no longer finds it where the end was written.
        System.Threading.Thread.Sleep(5);
        long forgottenAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Console.WriteLine($"end \"a\" in GSPO alone: {Index(store, "_gspoIndex").EndPeriod(g, s, p, a, memorizedAt, forgottenAt)}");
        Console.WriteLine($"end \"a\" in GPOS alone: {Index(store, "_gposIndex").EndPeriod(g, p, a, s, memorizedAt, forgottenAt)}");
        Console.WriteLine($"end \"a\" in GOSP alone: {Index(store, "_gospIndex").EndPeriod(g, a, s, p, memorizedAt, forgottenAt)}");

        Show(store, "before the rebuild");
        store.RebuildSecondaryIndexes();
        Show(store, "after the rebuild");
    }
    using (var reopened = new QuadStore(dir, null, null, options))
        Show(reopened, "opened again");
}
finally
{
    try { Directory.Delete(dir, true); } catch { }
}
