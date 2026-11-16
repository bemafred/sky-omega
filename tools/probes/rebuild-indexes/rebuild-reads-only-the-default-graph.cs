#:project ../../../src/Mercury/Mercury.csproj
#:property TargetFramework=net10.0
// Hypothesis (read in RebuildIndex and TemporalQuadIndex.QueryHistory, 2026-11-16): the Cognitive rebuild reads GSPO
// with QueryHistory(empty, empty, empty), and an empty graph there means the default graph (ResolveGraphAtom gives 0).
// So after a bulk load, GPOS, GOSP and TGSP get the default graph's entries and none of a named graph's, and a read
// that the store serves from a secondary index misses every named-graph quad.
// Construction: a Cognitive store in bulk mode; one batch with two quads in the default graph and two in the named
// graph <urn:probe:g>; flush; rebuild with progress. Then read the same predicate through GPOS (predicate bound, so
// the store chooses GPOS) and the same subjects through GSPO, in both graphs, through SPARQL.
// Expected: the rebuild reports 2 entries for each of GPOS, GOSP and TGSP, not 4; the GPOS read finds the default
// graph's two quads and not the named graph's; the GSPO reads find all four.
using System;
using System.IO;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Storage;

var dir = Path.Combine(Path.GetTempPath(), "mercury-probe-rebuild-graphs-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try
{
    var options = new StorageOptions { BulkMode = true, IndexInitialSizeBytes = 1L << 20, AtomDataInitialSizeBytes = 1L << 20 };
    using var store = new QuadStore(dir, null, null, options);
    store.BeginBatch();
    store.AddCurrentBatched("<urn:probe:s1>", "<urn:probe:p>", "\"default one\"");
    store.AddCurrentBatched("<urn:probe:s2>", "<urn:probe:p>", "\"default two\"");
    store.AddCurrentBatched("<urn:probe:s3>", "<urn:probe:p>", "\"named one\"", "<urn:probe:g>");
    store.AddCurrentBatched("<urn:probe:s4>", "<urn:probe:p>", "\"named two\"", "<urn:probe:g>");
    store.CommitBatch();
    store.FlushToDisk();

    store.RebuildSecondaryIndexes((name, count) => Console.WriteLine($"rebuild {name}: {count} entries"));

    foreach (var (what, query) in new[]
    {
        ("default graph, predicate bound (GPOS)", "SELECT ?s WHERE { ?s <urn:probe:p> ?o }"),
        ("named graph, predicate bound (GPOS)", "SELECT ?s WHERE { GRAPH <urn:probe:g> { ?s <urn:probe:p> ?o } }"),
        ("default graph, subjects bound (GSPO)", "SELECT ?o WHERE { VALUES ?s { <urn:probe:s1> <urn:probe:s2> } ?s ?p ?o }"),
        ("named graph, subjects bound (GSPO)", "SELECT ?o WHERE { GRAPH <urn:probe:g> { VALUES ?s { <urn:probe:s3> <urn:probe:s4> } ?s ?p ?o } }"),
    })
    {
        var result = SparqlEngine.Query(store, query);
        Console.WriteLine($"{what}: {(result.Success ? result.Rows!.Count.ToString() : result.ErrorMessage)} rows");
    }
}
finally
{
    Directory.Delete(dir, recursive: true);
}
