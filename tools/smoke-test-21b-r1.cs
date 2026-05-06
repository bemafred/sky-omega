#!/usr/bin/env -S dotnet
#:project ../src/Mercury/Mercury.csproj

// Smoke test for the cycle 8 21.3B substrate.
// Opens wiki-21b-ref-r1, runs queries that exercise atom store + GSPO + GPOS,
// prints results. Fails loudly if anything throws.

using System.Diagnostics;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Storage;

// The outer directory (wiki-21b-ref-r1) is a QuadStorePool; the actual sealed
// store lives under stores/<guid>/. Resolve the guid dynamically — there's
// exactly one for this substrate.
var poolDir = "/Users/bemafred/Library/SkyOmega/stores/wiki-21b-ref-r1";
var storesDir = Path.Combine(poolDir, "stores");
var guidDirs = Directory.GetDirectories(storesDir);
if (guidDirs.Length != 1)
    throw new InvalidOperationException($"Expected exactly 1 store under {storesDir}, found {guidDirs.Length}");
var storePath = guidDirs[0];

Console.WriteLine($"=== Smoke test against {storePath} ===");
Console.WriteLine($"Started: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
Console.WriteLine();

var openSw = Stopwatch.StartNew();
using var store = new QuadStore(storePath);
openSw.Stop();
Console.WriteLine($"[1/5] Open store ............ {openSw.Elapsed.TotalSeconds:F2}s");
Console.WriteLine($"      Profile:              {store.Schema.Profile}");
Console.WriteLine($"      AtomStore:            {store.Schema.AtomStore}");
Console.WriteLine($"      Index state:          {store.IndexState}");
Console.WriteLine();

// ----- 2. Statistics — verifies metadata load
var statsSw = Stopwatch.StartNew();
var stats = SparqlEngine.GetStatistics(store);
statsSw.Stop();
Console.WriteLine($"[2/5] Statistics ............ {statsSw.Elapsed.TotalMilliseconds:F0}ms");
Console.WriteLine($"      Quad count:           {stats.QuadCount:N0}");
Console.WriteLine();

// ----- 3. SPARQL: COUNT all triples — exercises full GSPO scan
//        Note: at 21.3B this is expensive; LIMIT-bound count or single-pattern
//        is more honest as a smoke test. We use a cheap bounded query.
Console.WriteLine($"[3/5] Bounded SELECT — exercises atom store + GSPO");
var bSw = Stopwatch.StartNew();
var bResult = SparqlEngine.Query(store, "SELECT ?s ?p ?o WHERE { ?s ?p ?o } LIMIT 5");
bSw.Stop();
if (bResult.Success)
{
    Console.WriteLine($"      First 5 quads:        (took {bSw.Elapsed.TotalSeconds:F3}s)");
    foreach (var row in bResult.Rows ?? new())
    {
        Console.WriteLine($"        {Trunc(row.GetValueOrDefault("s","?"), 60)}  {Trunc(row.GetValueOrDefault("p","?"), 50)}  {Trunc(row.GetValueOrDefault("o","?"), 60)}");
    }
}
else
{
    Console.WriteLine($"      ✗ FAILED: {bResult.ErrorMessage}");
}
Console.WriteLine();

// ----- 4. Predicate-bound query — exercises GPOS
//        wdt:P31 = "instance of" (Wikidata's most common predicate)
Console.WriteLine($"[4/5] Predicate-bound: wdt:P31 LIMIT 5 (uses GPOS)");
var pSw = Stopwatch.StartNew();
var pResult = SparqlEngine.Query(store, """
    SELECT ?s ?o WHERE {
      ?s <http://www.wikidata.org/prop/direct/P31> ?o
    }
    LIMIT 5
""");
pSw.Stop();
if (pResult.Success)
{
    Console.WriteLine($"      First 5 instances:    (took {pSw.Elapsed.TotalSeconds:F3}s)");
    foreach (var row in pResult.Rows ?? new())
    {
        Console.WriteLine($"        {Trunc(row.GetValueOrDefault("s","?"), 60)}  →  {Trunc(row.GetValueOrDefault("o","?"), 60)}");
    }
    if (pResult.Rows is null || pResult.Rows.Count == 0)
        Console.WriteLine($"      (no results — predicate might not exist in this dataset)");
}
else
{
    Console.WriteLine($"      ✗ FAILED: {pResult.ErrorMessage}");
}
Console.WriteLine();

// ----- 5. Trigram (full-text) search — exercises trigram.hash + trigram.posts
//        Mercury supports text:match(?var, "term") as a FILTER extension for
//        case-insensitive substring search over literal objects, backed by the
//        trigram index built during rebuild phase 7.
Console.WriteLine($"[5/5] Trigram FILTER text:match(\"Stockholm\") LIMIT 5 (uses trigram index)");
var tSw = Stopwatch.StartNew();
var tResult = SparqlEngine.Query(store, """
    SELECT ?s ?o WHERE {
      ?s ?p ?o .
      FILTER(text:match(?o, "Stockholm"))
    }
    LIMIT 5
""");
tSw.Stop();
if (tResult.Success)
{
    Console.WriteLine($"      First 5 matches:      (took {tSw.Elapsed.TotalSeconds:F3}s)");
    foreach (var row in tResult.Rows ?? new())
    {
        Console.WriteLine($"        {Trunc(row.GetValueOrDefault("s","?"), 60)}  →  {Trunc(row.GetValueOrDefault("o","?"), 70)}");
    }
    if (tResult.Rows is null || tResult.Rows.Count == 0)
        Console.WriteLine($"      (no results — would be surprising for a 21.3B Wikidata corpus)");
}
else
{
    Console.WriteLine($"      ✗ FAILED: {tResult.ErrorMessage}");
}
Console.WriteLine();

Console.WriteLine($"=== Smoke test complete: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} ===");

static string Trunc(string s, int max) =>
    s.Length <= max ? s : s.Substring(0, max - 3) + "...";
