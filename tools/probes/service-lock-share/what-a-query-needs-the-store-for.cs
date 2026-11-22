#!/usr/bin/env -S dotnet
#:project ../../../src/Mercury/Mercury.csproj
#:property TargetFramework=net10.0
#:property PublishAot=false
// How much of a query's time needs the store's read lock (ADR-066, Martin's question 6 of 2026-11-22). A query holds
// the lock from the end of parsing to its last row (SparqlEngine.QueryCore), but reads the store only in its scans: the
// triple patterns of each group, the text:match lookup, GRAPH ?g's graph list, DESCRIBE's reads. Joins, FILTER, BIND,
// VALUES, ORDER BY, GROUP BY, DISTINCT, LIMIT and the rows' drain work on values already copied out of the store.
//
// Measured by subtraction, not by instrumenting the engine: each query is timed whole, and then its triple patterns
// alone (the same patterns as SELECT *, which scans the same rows and copies them out). The patterns' share is an upper
// bound on what needs the lock: their time also includes the drain of their rows, which does not.
//
//   dotnet run tools/probes/service-lock-share/what-a-query-needs-the-store-for.cs
using System.Diagnostics;
using System.Globalization;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Storage;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var root = Directory.CreateTempSubdirectory("lockshare");
const int Subjects = 20_000;
using var store = new QuadStore(root.FullName, null, null, StorageOptions.ForTesting);
store.BeginBatch();
for (int i = 0; i < Subjects; i++)
{
    store.AddCurrentBatched($"<urn:s{i}>", "<urn:type>", $"<urn:class{i % 20}>");
    store.AddCurrentBatched($"<urn:s{i}>", "<urn:label>", $"\"label number {i} of the probe\"");
    store.AddCurrentBatched($"<urn:s{i}>", "<urn:value>", $"\"{i % 997}\"^^<http://www.w3.org/2001/XMLSchema#integer>");
    store.AddCurrentBatched($"<urn:s{i}>", "<urn:next>", $"<urn:s{(i + 1) % Subjects}>");
}
store.CommitBatch();

// (name, the whole query, its triple patterns alone)
var queries = new (string Name, string Whole, string Patterns)[]
{
    ("one subject's facts",
        "SELECT ?p ?o WHERE { <urn:s42> ?p ?o }",
        "SELECT * WHERE { <urn:s42> ?p ?o }"),
    ("a class, ordered by value",
        "SELECT ?s ?v WHERE { ?s <urn:type> <urn:class3> . ?s <urn:value> ?v } ORDER BY DESC(?v) LIMIT 10",
        "SELECT * WHERE { ?s <urn:type> <urn:class3> . ?s <urn:value> ?v }"),
    ("count per class",
        "SELECT ?c (COUNT(?s) AS ?n) WHERE { ?s <urn:type> ?c . ?s <urn:value> ?v } GROUP BY ?c ORDER BY DESC(?n)",
        "SELECT * WHERE { ?s <urn:type> ?c . ?s <urn:value> ?v }"),
    ("a filter over values",
        "SELECT ?s ?v WHERE { ?s <urn:value> ?v FILTER(?v > 500 && ?v < 510) }",
        "SELECT * WHERE { ?s <urn:value> ?v }"),
    ("a two-hop join, distinct",
        "SELECT DISTINCT ?c2 WHERE { ?s <urn:type> <urn:class7> . ?s <urn:next> ?t . ?t <urn:type> ?c2 }",
        "SELECT * WHERE { ?s <urn:type> <urn:class7> . ?s <urn:next> ?t . ?t <urn:type> ?c2 }"),
    ("labels by substring (text:match)",
        "SELECT ?s ?l WHERE { ?s <urn:label> ?l FILTER(text:match(?l, \"number 123\")) } ORDER BY ?l",
        "SELECT * WHERE { ?s <urn:label> ?l FILTER(text:match(?l, \"number 123\")) }"),
};

double Median(string sparql)
{
    for (int i = 0; i < 3; i++) Check(SparqlEngine.Query(store, sparql));
    var ms = new List<double>();
    for (int i = 0; i < 15; i++)
    {
        var result = SparqlEngine.Query(store, sparql);
        Check(result);
        ms.Add(result.ExecutionTime.TotalMilliseconds);
    }
    ms.Sort();
    return ms[ms.Count / 2];
}
static void Check(QueryResult result)
{
    if (!result.Success) throw new InvalidOperationException(result.ErrorMessage);
}

Console.WriteLine($"{Subjects * 4:N0} quads, {Environment.ProcessorCount} cores, .NET {Environment.Version}. " +
                  "Execution time (under the lock today), medians of 15.");
Console.WriteLine();
Console.WriteLine("| Query | Whole, ms | Its patterns alone, ms | The patterns' share |");
Console.WriteLine("|---|--:|--:|--:|");
foreach (var (name, whole, patterns) in queries)
{
    var w = Median(whole);
    var p = Median(patterns);
    Console.WriteLine($"| {name} | {w:F2} | {p:F2} | {Math.Min(1, p / w):P0} |");
}
store.Dispose();
root.Delete(true);
