#:project ../../../src/Mercury/Mercury.csproj
#:property TargetFramework=net10.0
#:property AssemblyName=Mercury.Benchmarks
// Runs on c3b6025 (the commit before ADR-062 Step 6): it reads the Graph index entries through
// VersionedQuadIndex.QueryAllVersions, whose enumerator Step 6 removes.
//
// Question (2026-11-16): the Graph profile numbers a fact's states with Version. Assert, retract, assert gives v1, v2,
// v3, and because a repeated assert or retract changes nothing the states alternate: an odd version is asserted, an
// even one retracted. So the counter alone holds the fact's states, at the profile's resolution. Does it survive a
// kill?
// Hypothesis, read in QuadStore on 2026-11-16, not run: the Graph log holds the operations (Add, Retract), not the
// change each one made, and recovery applies every record since the last checkpoint again (ApplyToIndexesById ->
// AddRaw, ApplyRetractToIndexesById -> DeleteRaw). After a kill the indexes already hold those writes. So replaying
// add, retract, add onto x at v3 gives no-op, v4, v5: the client still sees x, and every index holds x at v5, two
// states that never happened.
// Construction: mode "write" opens a new Graph store, asserts x, retracts it, asserts it again (three commits),
// prints each index's entries, and kills its own process (SIGKILL, as tests/Mercury.CrashTarget does). Mode "read"
// opens the store, which recovers, and prints each index's entries and what SPARQL returns.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Storage;

if (args.Length != 2 || (args[0] != "write" && args[0] != "read"))
{
    Console.Error.WriteLine("usage: a-killed-graph-store-replays-its-log.cs <write|read> <new store directory>");
    return 2;
}
var dir = args[1];
const string X = "<urn:probe:x>", P = "<urn:probe:p>", O = "<urn:probe:o>";

void Show(QuadStore store, string when)
{
    Console.WriteLine(when);
    foreach (var (field, name) in new[] { ("_gspoGraph", "GSPO"), ("_gposGraph", "GPOS"), ("_gospGraph", "GOSP"), ("_tgspGraph", "TGSP") })
    {
        var index = (VersionedQuadIndex)typeof(QuadStore).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
        var line = $"  {name}: count {index.QuadCount};";
        var entries = index.QueryAllVersions(-1, -1, -1, -1);
        while (entries.MoveNext())
        {
            var e = entries.Current;
            line += $" x v{e.Version}{(e.IsDeleted ? " retracted" : " asserted")};";
        }
        Console.WriteLine(line);
    }
    var result = SparqlEngine.Query(store, "SELECT ?s WHERE { ?s ?p ?o }");
    Console.WriteLine($"  SELECT ?s WHERE {{ ?s ?p ?o }}: {result.Rows!.Count} rows");
}

var options = new StorageOptions { Profile = StoreProfile.Graph, IndexInitialSizeBytes = 1L << 20, AtomDataInitialSizeBytes = 1L << 20 };
if (args[0] == "write")
{
    if (Directory.Exists(dir))
    {
        Console.Error.WriteLine($"{dir} exists: give the probe a new directory.");
        return 2;
    }
    Directory.CreateDirectory(dir);
    var store = new QuadStore(dir, null, null, options);
    store.AddCurrent(X, P, O);
    store.DeleteCurrent(X, P, O);
    store.AddCurrent(X, P, O);
    Show(store, "after assert x, retract x, assert x; the process is killed now");
    Console.Out.Flush();
    Process.GetCurrentProcess().Kill();
    return 1;
}

using (var store = new QuadStore(dir, null, null, options))
    Show(store, "after the store was opened again, and recovered");
using (var store = new QuadStore(dir, null, null, options))
    Show(store, "after a second open");
var log = Path.Combine(dir, "mercury.log");
Console.WriteLine(File.Exists(log) ? $"store log: {log}" : "the store wrote no log of its own");
return 0;
