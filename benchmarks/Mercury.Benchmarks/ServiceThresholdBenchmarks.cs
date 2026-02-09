using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;

namespace SkyOmega.Mercury.Benchmarks;

/// <summary>
/// Benchmarks to find break-even point between in-memory and indexed SERVICE paths.
/// Measures GC impact, memory allocation, and iteration performance at scale.
/// Used to determine optimal IndexedThreshold for ServiceMaterializerOptions.
/// </summary>
[MemoryDiagnoser]
[GcServer(true)]
[SimpleJob(warmupCount: 2, iterationCount: 5)]
[Config(typeof(GcCollectionConfig))]
public class ServiceThresholdBenchmarks
{
    private string _dbPath = null!;
    private QuadStore _store = null!;
    private ScalableServiceExecutor _serviceExecutor = null!;

    // Query templates
    private string _serviceOnlyQuery = null!;
    private string _serviceJoinQuery = null!;

    private class GcCollectionConfig : ManualConfig
    {
        public GcCollectionConfig()
        {
            // Add GC collection count columns
            AddColumn(new GcCollectionColumn(0));
            AddColumn(new GcCollectionColumn(1));
            AddColumn(new GcCollectionColumn(2));
        }
    }

    private class GcCollectionColumn : IColumn
    {
        private readonly int _generation;
        public GcCollectionColumn(int generation) => _generation = generation;
        public string Id => $"Gen{_generation}";
        public string ColumnName => $"Gen{_generation}";
        public bool AlwaysShow => true;
        public ColumnCategory Category => ColumnCategory.Custom;
        public int PriorityInCategory => 0;
        public bool IsNumeric => true;
        public UnitType UnitType => UnitType.Dimensionless;
        public string Legend => $"GC Gen{_generation} collections per operation";
        public string GetValue(BenchmarkDotNet.Reports.Summary summary, BenchmarkDotNet.Running.BenchmarkCase benchmarkCase)
            => GetValue(summary, benchmarkCase, BenchmarkDotNet.Reports.SummaryStyle.Default);
        public string GetValue(BenchmarkDotNet.Reports.Summary summary, BenchmarkDotNet.Running.BenchmarkCase benchmarkCase, BenchmarkDotNet.Reports.SummaryStyle style)
        {
            var report = summary[benchmarkCase];
            if (report?.GcStats == null) return "-";
            var count = _generation switch
            {
                0 => report.GcStats.Gen0Collections,
                1 => report.GcStats.Gen1Collections,
                2 => report.GcStats.Gen2Collections,
                _ => 0
            };
            return count.ToString();
        }
        public bool IsDefault(BenchmarkDotNet.Reports.Summary summary, BenchmarkDotNet.Running.BenchmarkCase benchmarkCase) => false;
        public bool IsAvailable(BenchmarkDotNet.Reports.Summary summary) => true;
    }

    [GlobalSetup]
    public void Setup()
    {
        var tempPath = TempPath.Benchmark("service-threshold-bench");
        tempPath.MarkOwnership();
        _dbPath = tempPath;

        _store = new QuadStore(_dbPath);

        // Populate local store with data for join benchmarks (10K entities)
        _store.BeginBatch();
        try
        {
            for (int i = 0; i < 10000; i++)
            {
                _store.AddCurrentBatched(
                    $"<http://ex.org/person{i}>",
                    "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>",
                    "<http://xmlns.com/foaf/0.1/Person>"
                );
            }
            _store.CommitBatch();
        }
        catch
        {
            _store.RollbackBatch();
            throw;
        }

        _serviceExecutor = new ScalableServiceExecutor();

        _serviceOnlyQuery = @"SELECT * WHERE {
            SERVICE <http://scale.example.org/sparql> {
                ?s <http://xmlns.com/foaf/0.1/name> ?name .
                ?s <http://xmlns.com/foaf/0.1/age> ?age
            }
        }";

        _serviceJoinQuery = @"SELECT * WHERE {
            ?s <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://xmlns.com/foaf/0.1/Person> .
            SERVICE <http://scale.example.org/sparql> {
                ?s <http://xmlns.com/foaf/0.1/email> ?email
            }
        }";
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store?.Dispose();
        if (_dbPath != null)
            TempPath.SafeCleanup(_dbPath);
    }

    // ==================== SERVICE-ONLY BENCHMARKS ====================
    // These measure pure iteration cost without join overhead

    [Benchmark(Description = "SERVICE only - 1K rows")]
    public int ServiceOnly_1K()
    {
        _serviceExecutor.SetResultCount(1_000);
        return ExecuteServiceOnly();
    }

    [Benchmark(Description = "SERVICE only - 5K rows")]
    public int ServiceOnly_5K()
    {
        _serviceExecutor.SetResultCount(5_000);
        return ExecuteServiceOnly();
    }

    [Benchmark(Description = "SERVICE only - 10K rows")]
    public int ServiceOnly_10K()
    {
        _serviceExecutor.SetResultCount(10_000);
        return ExecuteServiceOnly();
    }

    [Benchmark(Description = "SERVICE only - 50K rows")]
    public int ServiceOnly_50K()
    {
        _serviceExecutor.SetResultCount(50_000);
        return ExecuteServiceOnly();
    }

    [Benchmark(Description = "SERVICE only - 100K rows")]
    public int ServiceOnly_100K()
    {
        _serviceExecutor.SetResultCount(100_000);
        return ExecuteServiceOnly();
    }

    // ==================== SERVICE+JOIN BENCHMARKS ====================
    // These measure join performance where indexed path should help

    [Benchmark(Description = "SERVICE+join - 500 SERVICE rows")]
    public int ServiceJoin_500()
    {
        _serviceExecutor.SetResultCount(500, useLocalPersonIds: true);
        return ExecuteServiceJoin();
    }

    [Benchmark(Description = "SERVICE+join - 1K SERVICE rows")]
    public int ServiceJoin_1K()
    {
        _serviceExecutor.SetResultCount(1_000, useLocalPersonIds: true);
        return ExecuteServiceJoin();
    }

    [Benchmark(Description = "SERVICE+join - 5K SERVICE rows")]
    public int ServiceJoin_5K()
    {
        _serviceExecutor.SetResultCount(5_000, useLocalPersonIds: true);
        return ExecuteServiceJoin();
    }

    [Benchmark(Description = "SERVICE+join - 10K SERVICE rows")]
    public int ServiceJoin_10K()
    {
        _serviceExecutor.SetResultCount(10_000, useLocalPersonIds: true);
        return ExecuteServiceJoin();
    }

    // ==================== DIRECT PATH COMPARISON ====================
    // Force specific paths to compare directly

    [Benchmark(Description = "InMemory path - 1K rows (forced)")]
    public int InMemoryPath_1K()
    {
        _serviceExecutor.SetResultCount(1_000);
        return ExecuteWithPath(forceInMemory: true);
    }

    [Benchmark(Description = "Indexed path - 1K rows (forced)")]
    public int IndexedPath_1K()
    {
        _serviceExecutor.SetResultCount(1_000);
        return ExecuteWithPath(forceInMemory: false);
    }

    [Benchmark(Description = "InMemory path - 5K rows (forced)")]
    public int InMemoryPath_5K()
    {
        _serviceExecutor.SetResultCount(5_000);
        return ExecuteWithPath(forceInMemory: true);
    }

    [Benchmark(Description = "Indexed path - 5K rows (forced)")]
    public int IndexedPath_5K()
    {
        _serviceExecutor.SetResultCount(5_000);
        return ExecuteWithPath(forceInMemory: false);
    }

    private int ExecuteServiceOnly()
    {
        var parser = new SparqlParser(_serviceOnlyQuery.AsSpan());
        var query = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, _serviceOnlyQuery.AsSpan(), in query, _serviceExecutor);
            var results = executor.Execute();
            int count = 0;
            try
            {
                while (results.MoveNext()) count++;
            }
            finally
            {
                results.Dispose();
            }
            return count;
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    private int ExecuteServiceJoin()
    {
        var parser = new SparqlParser(_serviceJoinQuery.AsSpan());
        var query = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, _serviceJoinQuery.AsSpan(), in query, _serviceExecutor);
            var results = executor.Execute();
            int count = 0;
            try
            {
                while (results.MoveNext()) count++;
            }
            finally
            {
                results.Dispose();
            }
            return count;
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    private int ExecuteWithPath(bool forceInMemory)
    {
        // Use ServiceMaterializer directly to force specific path
        var results = _serviceExecutor.GetPrecomputedResults();

        if (forceInMemory)
        {
            // Simulate in-memory iteration
            var bindings = new Binding[16];
            var stringBuffer = new char[1024];
            var bindingTable = new BindingTable(bindings, stringBuffer);

            var scan = new ServicePatternScan(results, bindingTable);
            int count = 0;
            try
            {
                while (scan.MoveNext(ref bindingTable))
                {
                    count++;
                    bindingTable.TruncateTo(0);
                }
            }
            finally
            {
                scan.Dispose();
            }
            return count;
        }
        else
        {
            // Simulate indexed path - materialize to QuadStore
            var pool = ServiceStorePool.Instance;
            var store = pool.Rent();
            try
            {
                // Load results to store
                store.BeginBatch();
                try
                {
                    for (int i = 0; i < results.Count; i++)
                    {
                        var row = results[i];
                        var rowSubject = $"<_:row{i}>";
                        foreach (var varName in row.Variables)
                        {
                            var binding = row.GetBinding(varName);
                            store.AddCurrentBatched(rowSubject, $"<_:var:{varName}>", binding.ToRdfTerm());
                        }
                    }
                    store.CommitBatch();
                }
                catch
                {
                    store.RollbackBatch();
                    throw;
                }

                // Iterate via indexed scan
                var bindings = new Binding[16];
                var stringBuffer = new char[1024];
                var bindingTable = new BindingTable(bindings, stringBuffer);
                var varNames = new List<string>(results[0].Variables);

                var scan = new IndexedServicePatternScan(store, varNames, results.Count, bindingTable);
                int count = 0;
                try
                {
                    while (scan.MoveNext(ref bindingTable))
                    {
                        count++;
                        bindingTable.TruncateTo(0);
                    }
                }
                finally
                {
                    scan.Dispose();
                }
                return count;
            }
            finally
            {
                pool.Return(store);
            }
        }
    }
}

/// <summary>
/// Service executor that supports configurable result counts for threshold testing.
/// </summary>
internal class ScalableServiceExecutor : ISparqlServiceExecutor
{
    private List<ServiceResultRow> _precomputedResults = new();
    private int _resultCount = 1000;
    private bool _useLocalPersonIds = false;

    public void SetResultCount(int count, bool useLocalPersonIds = false)
    {
        if (_resultCount != count || _useLocalPersonIds != useLocalPersonIds)
        {
            _resultCount = count;
            _useLocalPersonIds = useLocalPersonIds;
            RegenerateResults();
        }
    }

    public List<ServiceResultRow> GetPrecomputedResults() => _precomputedResults;

    private void RegenerateResults()
    {
        _precomputedResults = new List<ServiceResultRow>(_resultCount);
        for (int i = 0; i < _resultCount; i++)
        {
            var row = new ServiceResultRow();
            var personId = _useLocalPersonIds ? i % 10000 : i;
            row.AddBinding("s", new ServiceBinding(ServiceBindingType.Uri, $"<http://ex.org/person{personId}>"));
            row.AddBinding("name", new ServiceBinding(ServiceBindingType.Literal, $"\"Person{personId}\""));
            row.AddBinding("age", new ServiceBinding(ServiceBindingType.Literal, $"\"{20 + (i % 60)}\"^^<http://www.w3.org/2001/XMLSchema#integer>"));
            row.AddBinding("email", new ServiceBinding(ServiceBindingType.Literal, $"\"person{personId}@example.org\""));
            _precomputedResults.Add(row);
        }
    }

    public ValueTask<List<ServiceResultRow>> ExecuteSelectAsync(string endpointUri, string query, System.Threading.CancellationToken ct = default)
    {
        if (_precomputedResults.Count == 0)
            RegenerateResults();
        return ValueTask.FromResult(new List<ServiceResultRow>(_precomputedResults));
    }

    public ValueTask<bool> ExecuteAskAsync(string endpointUri, string query, System.Threading.CancellationToken ct = default)
    {
        return ValueTask.FromResult(_precomputedResults.Count > 0);
    }
}
