#:project ../../../src/Mercury/Mercury.csproj
#:property TargetFramework=net10.0
#:property AssemblyName=Mercury.Benchmarks
// Runs on c3b6025 (the commit before ADR-062 Step 6): it reads the Graph index entries through
// VersionedQuadIndex.QueryAllVersions, whose enumerator Step 6 removes.
//
// Question (Martin, 2026-11-16): why do the Graph profile's version fields exist, and what does it tell us that only
// tests read them? This probe measures what the store keeps in them.
// Hypotheses, read in VersionedQuadIndex and QuadStore on 2026-11-16:
// - each of the four Graph indexes stamps CreatedAt and ModifiedAt with its own DateTimeOffset.UtcNow, not the
//   store's clock, so the four copies of one quad need not agree, and a supplied clock is ignored;
// - Version counts the Add, Delete and re-Add calls the index applied: 3 after add, delete, add;
// - a deleted quad stays as an entry marked deleted, and the entry count (and the store's quad count) still counts it;
// - the Graph rebuild reads live entries only and inserts them anew, so after it GPOS, GOSP and TGSP hold no deleted
//   entry, Version 1, and the rebuild's time as CreatedAt, while GSPO keeps its own;
// - no read of the store returns any of the four fields.
// Construction: a Graph store with a supplied clock fixed at 2031-01-01. Add x, delete x, add x again, add y, delete
// y, each 5 ms apart. Print each index's entries with their version fields, the counts, and what SPARQL returns;
// rebuild; print again.
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Storage;

var dir = Path.Combine(Path.GetTempPath(), "mercury-probe-graph-version-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
const string X = "<urn:probe:x>", Y = "<urn:probe:y>", P = "<urn:probe:p>", O = "<urn:probe:o>";
var clock = new FixedClock(new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero));
long t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

VersionedQuadIndex Index(QuadStore store, string field) =>
    (VersionedQuadIndex)typeof(QuadStore).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;

void Show(QuadStore store, string when)
{
    Console.WriteLine($"{when}  (times: ms after the probe started; the supplied clock reads 2031-01-01)");
    var atoms = (IAtomStore)typeof(QuadStore).GetField("_atoms", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
    var x = atoms.GetAtomId(X);
    foreach (var (field, name) in new[] { ("_gspoGraph", "GSPO"), ("_gposGraph", "GPOS"), ("_gospGraph", "GOSP"), ("_tgspGraph", "TGSP") })
    {
        var index = Index(store, field);
        var line = $"  {name}: count {index.QuadCount};";
        var entries = index.QueryAllVersions(-1, -1, -1, -1);
        while (entries.MoveNext())
        {
            var e = entries.Current;
            var subject = name switch { "GPOS" => e.Tertiary, "GOSP" => e.Secondary, _ => e.Primary };
            line += $" {(subject == x ? "x" : "y")} v{e.Version} created {e.CreatedAt - t0} modified {e.ModifiedAt - t0}{(e.IsDeleted ? " deleted" : "")};";
        }
        Console.WriteLine(line);
    }
    var (quadCount, _, _) = store.GetStatistics();
    var result = SparqlEngine.Query(store, "SELECT ?s WHERE { ?s ?p ?o }");
    Console.WriteLine($"  store quad count {quadCount}; SELECT ?s WHERE {{ ?s ?p ?o }}: {result.Rows!.Count} rows");
}

try
{
    var options = new StorageOptions { Profile = StoreProfile.Graph, Clock = clock, IndexInitialSizeBytes = 1L << 20, AtomDataInitialSizeBytes = 1L << 20 };
    using var store = new QuadStore(dir, null, null, options);
    store.AddCurrent(X, P, O); Thread.Sleep(5);
    store.DeleteCurrent(X, P, O); Thread.Sleep(5);
    store.AddCurrent(X, P, O); Thread.Sleep(5);
    store.AddCurrent(Y, P, O); Thread.Sleep(5);
    store.DeleteCurrent(Y, P, O); Thread.Sleep(5);
    Show(store, "after add x, delete x, add x, add y, delete y");

    Thread.Sleep(20);
    store.RebuildSecondaryIndexes();
    Show(store, "after the rebuild");

    // The reads that return every version of a quad, or a past state, on a Graph store.
    try { store.QueryEvolution(X, default, default); Console.WriteLine("  QueryEvolution: answered"); }
    catch (Exception e) { Console.WriteLine($"  QueryEvolution on a Graph store: {e.GetType().Name}"); }
    try { store.QueryAsOf(X, default, default, DateTimeOffset.UtcNow); Console.WriteLine("  QueryAsOf: answered"); }
    catch (Exception e) { Console.WriteLine($"  QueryAsOf on a Graph store: {e.GetType().Name}"); }
}
finally
{
    Directory.Delete(dir, recursive: true);
}

sealed class FixedClock(DateTimeOffset reading) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => reading;
}
