#:project ../../../src/Mercury/Mercury.csproj
#:property TargetFramework=net10.0
// Hypothesis (2026-11-16, after the fix of the Cognitive rebuild): on a copy of the live memory store, which differs
// from GSPO in TGSP alone (ADR-061 M11: 204 entries open in TGSP and forgotten in GSPO, 47 that only TGSP holds), the
// rebuild changes nothing and reports GPOS 0, GOSP 0 and TGSP 251.
// Construction: open the store directory given, which must be a copy and not under the machine's stores directory;
// count each index's entries; rebuild; print the refusal; count again.
// Run it on a copy of a copy: the rebuild changes the store it is given when it does not refuse.
using System;
using System.IO;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Storage;

if (args.Length != 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("usage: rebuild-on-a-copy-of-the-live-store.cs <copy of a store directory>");
    return 2;
}
var dir = Path.GetFullPath(args[0]);
var stores = Path.GetFullPath(Path.GetDirectoryName(MercuryPaths.Store("mcp"))!);
if (dir.StartsWith(stores, StringComparison.Ordinal))
{
    Console.Error.WriteLine($"{dir} is under {stores}: give the probe a copy.");
    return 2;
}

using var store = new QuadStore(dir);
Console.WriteLine($"profile {store.Schema.Profile}, index state {store.IndexState}");
try
{
    store.RebuildSecondaryIndexes((name, count) => Console.WriteLine($"rebuilt {name}: {count:N0} entries"));
    Console.WriteLine("the rebuild ran");
}
catch (InvalidOperationException refusal)
{
    Console.WriteLine(refusal.Message);
}
Console.WriteLine($"index state {store.IndexState}");
return 0;
