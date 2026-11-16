#:project ../../../src/Mercury/Mercury.csproj
#:property TargetFramework=net10.0
#:property AssemblyName=Mercury.Benchmarks
// Runs against the code BEFORE ADR-054: copy this file into a clone checked out at 1fa836e (the last commit before
// 3966e42, ADR-054) at the same path, and run it there with the store directory as its argument. See README.md.
//
// Hypothesis (read in DeleteFromTree and InsertIntoLeaf at 1fa836e, 2026-11-16): the live memory store's TGSP differs
// from GSPO (ADR-061 M11) because, before ADR-054, each index decided which entry was the quad's by where the quad's
// key sorts, and only an index whose key leads with the quad keeps a quad's entries together:
// - DeleteCurrent searched from ValidFrom = MinValue. In TGSP, where the time leads, that lands on the first leaf, and
//   the scan stops at the first entry that sorts after the search key, which is the first entry. So TGSP marked the
//   quad deleted only when it was TGSP's first entry; GSPO, GPOS and GOSP marked it.
// - AddCurrent of a quad marked deleted (still open) was taken for a duplicate in GSPO, GPOS and GOSP, where the
//   marked entry is the neighbour (ADR-054, defect 2). In TGSP the neighbour is another quad, so the telling was
//   inserted.
// - AddCurrent of a quad that is remembered, with another entry after it in TGSP, likewise: GSPO, GPOS and GOSP took
//   it for a duplicate, TGSP inserted it.
// Construction: a Cognitive store, default graph. Add a, b, c, d, e, 3 ms apart. Delete a (the control: TGSP's first
// entry), delete c, add c again, add d again. Print every entry of every index, with times relative to a's.
// Expected: a marked deleted in all four; c marked in GSPO, GPOS, GOSP and not in TGSP; TGSP alone holds the second
// telling of c and of d.
// The assembly is named Mercury.Benchmarks to see Mercury's internals; the private fields are read by reflection.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using SkyOmega.Mercury.Storage;

if (args.Length != 1 || Directory.Exists(args[0]))
{
    Console.Error.WriteLine("usage: 1-write-before-adr-054.cs <store directory that does not exist yet>");
    return 2;
}
var dir = args[0];
const string P = "<urn:probe:p>", O = "<urn:probe:o>";
string[] subjects = ["<urn:probe:a>", "<urn:probe:b>", "<urn:probe:c>", "<urn:probe:d>", "<urn:probe:e>"];

var options = new StorageOptions { IndexInitialSizeBytes = 1L << 20, AtomDataInitialSizeBytes = 1L << 20 };
using (var store = new QuadStore(dir, null, null, options))
{
    foreach (var s in subjects)
    {
        store.AddCurrent(s, P, O);
        Thread.Sleep(3);
    }
    Console.WriteLine($"delete a: {store.DeleteCurrent(subjects[0], P, O)}");
    Thread.Sleep(3);
    Console.WriteLine($"delete c: {store.DeleteCurrent(subjects[2], P, O)}");
    Thread.Sleep(3);
    store.AddCurrent(subjects[2], P, O);
    Console.WriteLine("add c again");
    Thread.Sleep(3);
    store.AddCurrent(subjects[3], P, O);
    Console.WriteLine("add d again");

    var atoms = (IAtomStore)typeof(QuadStore).GetField("_atoms", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
    var names = subjects.ToDictionary(s => atoms.GetAtomId(s), s => s.Substring(11, 1));
    long t0 = 0;
    foreach (var (field, name, subjectAt) in new[] { ("_gspoIndex", "GSPO", 0), ("_gposIndex", "GPOS", 2), ("_gospIndex", "GOSP", 1), ("_tgspIndex", "TGSP", 0) })
    {
        var index = (TemporalQuadIndex)typeof(QuadStore).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
        var rows = new List<string>();
        var entries = index.QueryHistory(ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
        while (entries.MoveNext())
        {
            var q = entries.Current;
            var subject = subjectAt == 0 ? q.Primary : subjectAt == 1 ? q.Secondary : q.Tertiary;
            if (t0 == 0) t0 = q.ValidFrom;
            var to = q.ValidTo > 253370764800000L ? "open" : $"{q.ValidTo - t0}";
            rows.Add($"{names[subject]} [{q.ValidFrom - t0}, {to}){(q.IsDeleted ? " marked deleted" : "")}");
        }
        rows.Sort(StringComparer.Ordinal);
        Console.WriteLine($"{name}, {rows.Count} entries: {string.Join("; ", rows)}");
    }
}
return 0;
