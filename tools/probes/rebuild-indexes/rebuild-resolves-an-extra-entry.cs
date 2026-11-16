#:project ../../../src/Mercury/Mercury.csproj
#:property TargetFramework=net10.0
#:property AssemblyName=Mercury.Benchmarks
// Hypothesis (read in QuadStore.RebuildIndex, 2026-11-16): the Cognitive rebuild of the secondary indexes does not clear
// them first (the Graph rebuild does, QuadStore.cs:2162). It inserts every GSPO entry with AddRaw, which writes nothing
// for an entry that is already there. So an entry that GPOS holds and GSPO does not is still in GPOS after the rebuild:
// the rebuild does not resolve a difference of that kind.
// Construction: a Cognitive store with <s> <p> "a" and <s> <q> "x". One period is put into GPOS alone, by the index's
// own internal BeginPeriod: <s> <p> "x". Read <s> <p> ?o through GSPO (subject bound) and ?s <p> ?o through GPOS
// (only the predicate bound), and the quad count per index; rebuild; read again; open the store again and read again.
// Control: <s> <p> "a" is in both indexes every time.
// The assembly is named Mercury.Benchmarks to see Mercury's internals; the private index field is read by reflection.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using SkyOmega.Mercury.Storage;

var dir = Path.Combine(Path.GetTempPath(), "mercury-probe-rebuild-extra-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
const string S = "<urn:probe:s>", P = "<urn:probe:p>", Q = "<urn:probe:q>";

TemporalQuadIndex Index(QuadStore store, string field) =>
    (TemporalQuadIndex)typeof(QuadStore).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;

string Objects(QuadStore store, bool throughSubject)
{
    var objects = new List<string>();
    store.AcquireReadLock();
    try
    {
        var rows = throughSubject ? store.QueryCurrent(S, P, default) : store.QueryCurrent(default, P, default);
        while (rows.MoveNext()) objects.Add(rows.Current.Object.ToString());
        rows.Dispose();
    }
    finally { store.ReleaseReadLock(); }
    objects.Sort(StringComparer.Ordinal);
    return string.Join(" ", objects);
}

void Show(QuadStore store, string when)
{
    Console.WriteLine($"{when}:");
    Console.WriteLine($"  <s> <p> ?o through GSPO : {Objects(store, true)}");
    Console.WriteLine($"  ?s <p> ?o through GPOS  : {Objects(store, false)}");
    Console.WriteLine($"  entries: GSPO {Index(store, "_gspoIndex").QuadCount}, GPOS {Index(store, "_gposIndex").QuadCount}, " +
                      $"GOSP {Index(store, "_gospIndex").QuadCount}, TGSP {Index(store, "_tgspIndex").QuadCount}");
}

try
{
    var options = new StorageOptions { IndexInitialSizeBytes = 1L << 20, AtomDataInitialSizeBytes = 1L << 20 };
    using (var store = new QuadStore(dir, null, null, options))
    {
        store.AddCurrent(S, P, "\"a\"");
        store.AddCurrent(S, Q, "\"x\"");

        long s = store.Atoms.GetAtomId(S), p = store.Atoms.GetAtomId(P), x = store.Atoms.GetAtomId("\"x\"");
        // GPOS order: predicate, object, subject.
        var put = Index(store, "_gposIndex").BeginPeriod(0, p, x, s, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Console.WriteLine($"put <s> <p> \"x\" into GPOS alone: {put}");

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
