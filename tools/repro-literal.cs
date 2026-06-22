#!/usr/bin/env -S dotnet
#:project ../src/Mercury/Mercury.csproj

// Isolation sweep v2 for the silent long-literal bug. v1 showed ASCII <=900 stores fine
// (the "+2" was the read-back quotes). Two axes now: LENGTH (find the threshold in (900,1500))
// and CONTENT (pure ASCII 'x' vs multi-byte em-dash U+2014). Read back the LEXICAL length
// (quotes stripped) and compare to requested.

using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Storage;

var dir = Path.Combine(Path.GetTempPath(), "mercury-literal-repro-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
using var store = new QuadStore(dir);

Console.WriteLine($"{"reqLen",7} | {"ASCII (x)",14} | {"em-dash (U+2014)",16}");
Console.WriteLine(new string('-', 46));

int[] lengths = { 300, 800, 1000, 1020, 1023, 1024, 1025, 1100, 1500, 2000, 4000 };
foreach (var L in lengths)
{
    int a = StoreAndRead(store, $"a{L}", new string('x', L));
    int e = StoreAndRead(store, $"e{L}", new string('—', L));
    Console.WriteLine($"{L,7} | {Mark(L, a),14} | {Mark(L, e),16}");
}
Console.WriteLine();
Console.WriteLine("ok N = stored full length; ** N = stored short (the bug); NO ROW / ROW no-?o = lost entirely");

try { Directory.Delete(dir, true); } catch { }

static string Mark(int want, int got) =>
    got == want ? $"ok {got}" : got == -1 ? "NO ROW" : got == -3 ? "ROW no-?o" : $"** {got}";

static int StoreAndRead(QuadStore store, string id, string lit)
{
    SparqlEngine.Update(store, $"INSERT DATA {{ GRAPH <urn:t> {{ <urn:{id}> <urn:p> \"{lit}\" }} }}");
    var r = SparqlEngine.Query(store, $"SELECT ?o WHERE {{ GRAPH <urn:t> {{ <urn:{id}> <urn:p> ?o }} }}");
    if (r.Rows is null || r.Rows.Count == 0) return -1;
    try
    {
        var val = r.Rows[0]["o"];
        if (val is null) return -2;
        var s = val.Length >= 2 && val[0] == '"' && val[^1] == '"' ? val[1..^1] : val;
        return s.Length;
    }
    catch (System.Collections.Generic.KeyNotFoundException) { return -3; }
}
