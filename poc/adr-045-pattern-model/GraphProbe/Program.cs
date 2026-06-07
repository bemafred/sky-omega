// ADR-045 cost-shape confirmation (black-box, DrHook-independent).
// Hypothesis: the GRAPH path materializes ALL matching rows before applying LIMIT.
// Test: at fixed N, does LIMIT 2 cost the same as no-LIMIT? If yes, LIMIT saves no
// work => materialize-then-filter. (DrHook separately observed the live dispatch into
// FromMaterializedWithGraphContext, which receives a pre-built list + a separate limit.)

using System.Diagnostics;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Storage;

int n = args.Length > 0 ? int.Parse(args[0]) : 50000;

var dir = Path.Combine(Path.GetTempPath(), "graphprobe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
using var store = new QuadStore(dir);

// Insert N triples into a named graph, in batches (each batch = one INSERT DATA operation).
const int batch = 5000;
for (int start = 1; start <= n; start += batch)
{
    int end = Math.Min(start + batch - 1, n);
    var triples = string.Join(" ", Enumerable.Range(start, end - start + 1).Select(i => $"<urn:s{i}> <urn:p> <urn:o{i}> ."));
    var res = SparqlEngine.Update(store, $"INSERT DATA {{ GRAPH <urn:g> {{ {triples} }} }}");
    if (!res.Success) { Console.WriteLine($"insert failed at {start}: {res.ErrorMessage}"); return; }
}

const string qLimited = "SELECT ?s ?o WHERE { GRAPH <urn:g> { ?s <urn:p> ?o } } LIMIT 2";
const string qAll     = "SELECT ?s ?o WHERE { GRAPH <urn:g> { ?s <urn:p> ?o } }";

// warm up (JIT + caches)
_ = SparqlEngine.Query(store, qLimited);
_ = SparqlEngine.Query(store, qAll);

double MinMs(string sparql, out int rowCount)
{
    double best = double.MaxValue; int rc = -1;
    for (int i = 0; i < 5; i++)
    {
        var sw = Stopwatch.StartNew();
        var r = SparqlEngine.Query(store, sparql);
        sw.Stop();
        rc = r.Rows?.Count ?? -1;
        if (sw.Elapsed.TotalMilliseconds < best) best = sw.Elapsed.TotalMilliseconds;
    }
    rowCount = rc;
    return best;
}

double tLimited = MinMs(qLimited, out int rcLimited);
double tAll = MinMs(qAll, out int rcAll);

Console.WriteLine($"N={n}");
Console.WriteLine($"  LIMIT 2 : {tLimited,8:F2} ms  (returned {rcLimited} rows)");
Console.WriteLine($"  no-LIMIT: {tAll,8:F2} ms  (returned {rcAll} rows)");
Console.WriteLine($"  ratio LIMIT2/noLIMIT = {tLimited / tAll:F2}  (~1.0 => LIMIT saves no work => materialize-then-filter)");
