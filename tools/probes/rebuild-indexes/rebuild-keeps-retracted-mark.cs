#:project ../../../src/Mercury/Mercury.csproj
#:property TargetFramework=net10.0
// Hypothesis (read in QuadStore.RebuildIndex, 2026-11-16, not run): rebuilding the secondary indexes of a Cognitive
// store copies every GSPO entry with TemporalQuadIndex.AddRaw, which takes the key and no deleted mark. So a version
// retracted before the rebuild (ADR-054 D2, LegacyIsDeleted) is retracted in GSPO and NOT retracted in GPOS, GOSP and
// TGSP afterwards, and a read that goes through GPOS sees it as current again.
// Construction: a Cognitive store; memorize <s> <p> "a" and <s> <p> "b"; retract the version of "a" by its times;
// read the current state through GSPO (subject bound) and through GPOS (only the predicate bound), and the history
// through both; rebuild the secondary indexes; read the same again; open the store again and read once more.
// Control: "b", which is never retracted, must be current through both indexes every time.
using System;
using System.Collections.Generic;
using System.IO;
using SkyOmega.Mercury.Storage;

var dir = Path.Combine(Path.GetTempPath(), "mercury-probe-rebuild-retracted-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
const string S = "<urn:probe:s>", P = "<urn:probe:p>";

string Current(QuadStore store, bool throughSubject)
{
    var objects = new List<string>();
    store.AcquireReadLock();
    try
    {
        // Subject bound: GSPO. Only the predicate bound: GPOS.
        var rows = throughSubject ? store.QueryCurrent(S, P, default) : store.QueryCurrent(default, P, default);
        while (rows.MoveNext()) objects.Add(rows.Current.Object.ToString());
        rows.Dispose();
    }
    finally { store.ReleaseReadLock(); }
    objects.Sort(StringComparer.Ordinal);
    return string.Join(" ", objects);
}

string History(QuadStore store, bool throughSubject)
{
    var rows = new List<string>();
    store.AcquireReadLock();
    try
    {
        var all = throughSubject ? store.QueryEvolution(S, P, default) : store.QueryEvolution(default, P, default);
        while (all.MoveNext())
            rows.Add($"{all.Current.Object.ToString()}{(all.Current.LegacyIsDeleted ? " retracted" : "")}");
        all.Dispose();
    }
    finally { store.ReleaseReadLock(); }
    rows.Sort(StringComparer.Ordinal);
    return string.Join(", ", rows);
}

void Show(QuadStore store, string when)
{
    Console.WriteLine($"{when}:");
    Console.WriteLine($"  current through GSPO : {Current(store, true)}");
    Console.WriteLine($"  current through GPOS : {Current(store, false)}");
    Console.WriteLine($"  history through GSPO : {History(store, true)}");
    Console.WriteLine($"  history through GPOS : {History(store, false)}");
}

try
{
    var options = new StorageOptions { IndexInitialSizeBytes = 1L << 20, AtomDataInitialSizeBytes = 1L << 20 };
    using (var store = new QuadStore(dir, null, null, options))
    {
        store.AddCurrent(S, P, "\"a\"");
        store.AddCurrent(S, P, "\"b\"");

        DateTimeOffset memorizedAt = default, forgottenAt = default;
        store.AcquireReadLock();
        try
        {
            var all = store.QueryEvolution(S, P, "\"a\"");
            while (all.MoveNext()) { memorizedAt = all.Current.MemorizedAt; forgottenAt = all.Current.ForgottenAt; }
            all.Dispose();
        }
        finally { store.ReleaseReadLock(); }
        Console.WriteLine($"retract \"a\" [{memorizedAt:O}, {forgottenAt:O}): {store.Retract(S, P, "\"a\"", memorizedAt, forgottenAt)}");

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
