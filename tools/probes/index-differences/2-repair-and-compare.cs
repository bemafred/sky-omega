#:project ../../../src/Mercury/Mercury.csproj
#:property TargetFramework=net10.0
#:property AssemblyName=Mercury.Benchmarks
// Runs against today's code, on the store 1-write-before-adr-054.cs wrote with the code before ADR-054. See README.md.
//
// Hypothesis: the ADR-054 repair (RepairDeletes, as run on the live memory store on 2026-10-27) ends each entry an
// index marked deleted, on each index's own entries. TGSP marked only a, so afterwards:
// - c is forgotten in GSPO and open in TGSP, with the same memorized time: the kind of the live store's 204;
// - TGSP alone holds c's second telling, a later telling of a fact GSPO holds as forgotten: the kind of the 38;
// - TGSP alone holds d's second telling, while GSPO holds d as remembered: the kind of the 9;
// - a, b and e are equal in GSPO and TGSP (a is the control: TGSP did mark it).
// Construction: open the store, run RepairDeletes without a restore, print every entry of every index with times
// relative to a's, then compare TGSP with GSPO entry by entry, as the third count of ADR-061 M11 did.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SkyOmega.Mercury.Storage;

if (args.Length != 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("usage: 2-repair-and-compare.cs <store directory written by 1-write-before-adr-054.cs>");
    return 2;
}
string[] subjects = ["<urn:probe:a>", "<urn:probe:b>", "<urn:probe:c>", "<urn:probe:d>", "<urn:probe:e>"];
const long Open = 253370764800000L;

using var store = new QuadStore(args[0]);
var (ended, restored, left) = store.RepairDeletes([], dryRun: false, TextWriter.Null);
Console.WriteLine($"repair: ended {ended}, restored {restored}, left {left}");

var atoms = (IAtomStore)typeof(QuadStore).GetField("_atoms", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
var names = subjects.ToDictionary(s => atoms.GetAtomId(s), s => s.Substring(11, 1));
long t0 = 0;
var periods = new Dictionary<string, List<(string Quad, long MemorizedAt, long ForgottenAt)>>();
foreach (var (field, name, subjectAt) in new[] { ("_gspoIndex", "GSPO", 0), ("_gposIndex", "GPOS", 2), ("_gospIndex", "GOSP", 1), ("_tgspIndex", "TGSP", 0) })
{
    var index = (TemporalQuadIndex)typeof(QuadStore).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
    var list = new List<(string Quad, long MemorizedAt, long ForgottenAt)>();
    var entries = index.QueryHistory(ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
    while (entries.MoveNext())
    {
        var q = entries.Current;
        var subject = subjectAt == 0 ? q.Primary : subjectAt == 1 ? q.Secondary : q.Tertiary;
        if (t0 == 0) t0 = q.MemorizedAt;
        if (q.LegacyIsDeleted) Console.WriteLine($"  {name}: {names[subject]} is still marked deleted");
        list.Add((names[subject], q.MemorizedAt, q.ForgottenAt));
    }
    list.Sort();
    periods[name] = list;
    Console.WriteLine($"{name}, {list.Count} entries: " + string.Join("; ",
        list.Select(e => $"{e.Quad} [{e.MemorizedAt - t0}, {(e.ForgottenAt >= Open ? "open" : (e.ForgottenAt - t0).ToString())})")));
}

foreach (var other in new[] { "GPOS", "GOSP" })
    Console.WriteLine($"{other} equals GSPO: {periods[other].SequenceEqual(periods["GSPO"])}");

Console.WriteLine("TGSP against GSPO:");
var gspo = periods["GSPO"];
foreach (var t in periods["TGSP"])
{
    if (gspo.Contains(t)) continue;
    var sameStart = gspo.Where(g => g.Quad == t.Quad && g.MemorizedAt == t.MemorizedAt).ToList();
    var quadInGspo = gspo.Where(g => g.Quad == t.Quad).ToList();
    var kind =
        sameStart.Count == 1 && sameStart[0].ForgottenAt < Open && t.ForgottenAt >= Open ? "forgotten in GSPO, open in TGSP, same memorized time (the 204)"
        : sameStart.Count == 0 && quadInGspo.Any(g => g.ForgottenAt >= Open) ? "no GSPO entry at that memorized time; GSPO remembers the fact (the 9)"
        : sameStart.Count == 0 && quadInGspo.Count > 0 && quadInGspo.All(g => g.ForgottenAt < Open) ? "no GSPO entry at that memorized time; GSPO holds the fact as forgotten (the 38)"
        : "another kind";
    Console.WriteLine($"  {t.Quad} [{t.MemorizedAt - t0}, {(t.ForgottenAt >= Open ? "open" : (t.ForgottenAt - t0).ToString())}): {kind}");
}
foreach (var g in gspo.Where(g => !periods["TGSP"].Contains(g)))
    Console.WriteLine($"  GSPO holds {g.Quad} [{g.MemorizedAt - t0}, {(g.ForgottenAt >= Open ? "open" : (g.ForgottenAt - t0).ToString())}) and TGSP does not hold it so");
return 0;
