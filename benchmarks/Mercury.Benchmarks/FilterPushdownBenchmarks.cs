using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Benchmarks;

/// <summary>
/// Benchmarks for filter pushdown optimization.
/// Measures the performance improvement from pushing filter evaluation
/// earlier in query execution.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 5)]
public class FilterPushdownBenchmarks : IDisposable
{
    private QuadStore _store = null!;
    private string _testDir = null!;

    // Test queries
    private const string QueryNoFilter = "SELECT ?s ?name ?age WHERE { ?s <http://ex.org/name> ?name . ?s <http://ex.org/age> ?age }";
    private const string QuerySelectiveFilter = "SELECT ?s WHERE { ?s <http://ex.org/name> ?name . ?s <http://ex.org/age> ?age . FILTER(?age > 95) }";
    private const string QueryModerateFilter = "SELECT ?s WHERE { ?s <http://ex.org/name> ?name . ?s <http://ex.org/age> ?age . FILTER(?age > 50) }";
    private const string QueryStringFilter = "SELECT ?s WHERE { ?s <http://ex.org/name> ?name . FILTER(CONTAINS(?name, \"Person1\")) }";

    // Data sizes
    private const int SmallDataset = 100;
    private const int MediumDataset = 1_000;
    private const int LargeDataset = 10_000;

    [GlobalSetup]
    public void Setup()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"filter_bench_{Guid.NewGuid():N}");
        _store = new QuadStore(_testDir);

        // Generate test data
        _store.BeginBatch();
        for (int i = 0; i < LargeDataset; i++)
        {
            var subject = $"<http://ex.org/person{i}>";
            _store.AddCurrentBatched(subject, "<http://ex.org/name>", $"\"Person{i}\"");
            _store.AddCurrentBatched(subject, "<http://ex.org/age>", (i % 100).ToString());
            _store.AddCurrentBatched(subject, "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>", "<http://ex.org/Person>");
        }
        _store.CommitBatch();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    private int ExecuteQuery(string query)
    {
        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();
        var executor = new QueryExecutor(_store, query.AsSpan(), parsedQuery);

        _store.AcquireReadLock();
        try
        {
            var results = executor.Execute();
            int count = 0;
            while (results.MoveNext())
            {
                count++;
            }
            results.Dispose();
            return count;
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Benchmark(Description = "No filter (baseline)")]
    public int NoFilter()
    {
        return ExecuteQuery(QueryNoFilter);
    }

    [Benchmark(Description = "Highly selective filter (5%)")]
    public int SelectiveFilter()
    {
        return ExecuteQuery(QuerySelectiveFilter);
    }

    [Benchmark(Description = "Moderate filter (50%)")]
    public int ModerateFilter()
    {
        return ExecuteQuery(QueryModerateFilter);
    }

    [Benchmark(Description = "String filter (CONTAINS)")]
    public int StringFilter()
    {
        return ExecuteQuery(QueryStringFilter);
    }

    public void Dispose()
    {
        Cleanup();
    }
}
