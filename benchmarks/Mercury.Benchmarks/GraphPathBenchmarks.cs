using System;
using System.IO;
using BenchmarkDotNet.Attributes;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;

namespace SkyOmega.Mercury.Benchmarks;

/// <summary>
/// ADR-045 cutover: allocation/throughput benchmarks for the GRAPH path, which (since the cutover) executes through
/// the live TreeJoinExecutor wire — a pooled-BindingTable + TriplePatternScan nested-loop join feeding the shared
/// FromMaterializedSimple modifier layer. [MemoryDiagnoser] reports per-op allocation; the inner scan loop is
/// zero-GC (the per-scan-step zero-GC property is gated in AllocationTests.GraphPath_HotScanLoop_IsZeroGcPerScanStep),
/// so the reported "Allocated" should track the materialized result rows, not the scanned-triple count. Covers the
/// operators the cutover fixed inside GRAPH: BGP, join, OPTIONAL, BIND.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class GraphPathBenchmarks
{
    private string _dbPath = null!;
    private QuadStore _store = null!;

    private string _bgpSource = null!;
    private string _joinSource = null!;
    private string _optionalSource = null!;
    private string _bindSource = null!;

    [GlobalSetup]
    public void Setup()
    {
        var tempPath = TempPath.Benchmark("graph-path");
        tempPath.MarkOwnership();
        _dbPath = tempPath;
        _store = new QuadStore(_dbPath);

        _store.BeginBatch();
        try
        {
            // 5000 s -> o -> z chains, all inside the named graph <urn:g>.
            for (int i = 0; i < 5000; i++)
            {
                _store.AddCurrentBatched($"<urn:s{i}>", "<urn:p>", $"<urn:o{i}>", "<urn:g>");
                _store.AddCurrentBatched($"<urn:o{i}>", "<urn:next>", $"<urn:z{i}>", "<urn:g>");
            }
            _store.CommitBatch();
        }
        catch
        {
            _store.RollbackBatch();
            throw;
        }

        _bgpSource = "SELECT ?s ?o WHERE { GRAPH <urn:g> { ?s <urn:p> ?o } } LIMIT 1000";
        _joinSource = "SELECT ?s ?x WHERE { GRAPH <urn:g> { ?s <urn:p> ?o . ?o <urn:next> ?x } } LIMIT 1000";
        _optionalSource = "SELECT ?s ?x WHERE { GRAPH <urn:g> { ?s <urn:p> ?o OPTIONAL { ?o <urn:next> ?x } } } LIMIT 1000";
        _bindSource = "SELECT ?s ?l WHERE { GRAPH <urn:g> { ?s <urn:p> ?o BIND(STR(?o) AS ?l) } } LIMIT 1000";
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);
    }

    private long Run(string source)
    {
        long count = 0;
        var query = new SparqlParser(source.AsSpan()).ParseQuery();
        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, source.AsSpan(), in query);
            var results = executor.Execute();
            try
            {
                while (results.MoveNext())
                {
                    count++;
                    _ = results.Current.Count;
                }
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            _store.ReleaseReadLock();
        }
        return count;
    }

    [Benchmark(Description = "GRAPH BGP")]
    public long GraphBgp() => Run(_bgpSource);

    [Benchmark(Description = "GRAPH 2-pattern join")]
    public long GraphJoin() => Run(_joinSource);

    [Benchmark(Description = "GRAPH OPTIONAL")]
    public long GraphOptional() => Run(_optionalSource);

    [Benchmark(Description = "GRAPH BIND")]
    public long GraphBind() => Run(_bindSource);
}
