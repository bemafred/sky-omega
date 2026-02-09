using BenchmarkDotNet.Attributes;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Execution.Federated;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;

namespace SkyOmega.Mercury.Benchmarks;

/// <summary>
/// Benchmarks for SERVICE clause execution.
/// Measures overhead of federated query execution via ISparqlServiceExecutor.
/// Used to establish baseline before pool-based materialization refactoring.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class ServiceBenchmarks
{
    private string _dbPath = null!;
    private QuadStore _store = null!;
    private BenchmarkServiceExecutor _serviceExecutor = null!;

    // Query strings
    private string _serviceOnlySmall = null!;
    private string _serviceOnlyMedium = null!;
    private string _serviceOnlyLarge = null!;
    private string _serviceWithLocalJoin = null!;
    private string _multipleServices = null!;

    [GlobalSetup]
    public void Setup()
    {
        var tempPath = TempPath.Benchmark("service-bench");
        tempPath.MarkOwnership();
        _dbPath = tempPath;

        _store = new QuadStore(_dbPath);

        // Populate local store with data for join benchmarks
        _store.BeginBatch();
        try
        {
            // 1000 entities with properties
            for (int i = 0; i < 1000; i++)
            {
                _store.AddCurrentBatched(
                    $"<http://ex.org/person{i}>",
                    "<http://xmlns.com/foaf/0.1/name>",
                    $"\"Person{i}\""
                );
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

        // Create mock service executor
        _serviceExecutor = new BenchmarkServiceExecutor();

        // Configure small result set (10 rows)
        _serviceExecutor.ConfigureEndpoint("http://small.example.org/sparql", 10);

        // Configure medium result set (100 rows)
        _serviceExecutor.ConfigureEndpoint("http://medium.example.org/sparql", 100);

        // Configure large result set (1000 rows)
        _serviceExecutor.ConfigureEndpoint("http://large.example.org/sparql", 1000);

        // Configure endpoint for local joins (returns persons that exist in local store)
        _serviceExecutor.ConfigureEndpoint("http://join.example.org/sparql", 100, useLocalPersonIds: true);

        // Query strings
        _serviceOnlySmall = @"SELECT * WHERE {
            SERVICE <http://small.example.org/sparql> {
                ?s <http://xmlns.com/foaf/0.1/name> ?name
            }
        }";

        _serviceOnlyMedium = @"SELECT * WHERE {
            SERVICE <http://medium.example.org/sparql> {
                ?s <http://xmlns.com/foaf/0.1/name> ?name
            }
        }";

        _serviceOnlyLarge = @"SELECT * WHERE {
            SERVICE <http://large.example.org/sparql> {
                ?s <http://xmlns.com/foaf/0.1/name> ?name
            }
        }";

        _serviceWithLocalJoin = @"SELECT * WHERE {
            ?s <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://xmlns.com/foaf/0.1/Person> .
            SERVICE <http://join.example.org/sparql> {
                ?s <http://xmlns.com/foaf/0.1/email> ?email
            }
        }";

        _multipleServices = @"SELECT * WHERE {
            SERVICE <http://small.example.org/sparql> {
                ?s <http://xmlns.com/foaf/0.1/name> ?name
            }
            SERVICE <http://medium.example.org/sparql> {
                ?s <http://xmlns.com/foaf/0.1/age> ?age
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

    [Benchmark(Description = "SERVICE only - 10 results")]
    public int ServiceOnlySmall()
    {
        var parser = new SparqlParser(_serviceOnlySmall.AsSpan());
        var query = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, _serviceOnlySmall.AsSpan(), in query, _serviceExecutor);
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

    [Benchmark(Description = "SERVICE only - 100 results")]
    public int ServiceOnlyMedium()
    {
        var parser = new SparqlParser(_serviceOnlyMedium.AsSpan());
        var query = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, _serviceOnlyMedium.AsSpan(), in query, _serviceExecutor);
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

    [Benchmark(Description = "SERVICE only - 1000 results")]
    public int ServiceOnlyLarge()
    {
        var parser = new SparqlParser(_serviceOnlyLarge.AsSpan());
        var query = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, _serviceOnlyLarge.AsSpan(), in query, _serviceExecutor);
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

    [Benchmark(Description = "SERVICE + local join (100 results)")]
    public int ServiceWithLocalJoin()
    {
        var parser = new SparqlParser(_serviceWithLocalJoin.AsSpan());
        var query = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, _serviceWithLocalJoin.AsSpan(), in query, _serviceExecutor);
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

    [Benchmark(Description = "Multiple SERVICE clauses")]
    public int MultipleServices()
    {
        var parser = new SparqlParser(_multipleServices.AsSpan());
        var query = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, _multipleServices.AsSpan(), in query, _serviceExecutor);
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
}

/// <summary>
/// Fast mock service executor for benchmarking.
/// Pre-generates result sets to minimize overhead during benchmarks.
/// </summary>
internal class BenchmarkServiceExecutor : ISparqlServiceExecutor
{
    private readonly Dictionary<string, List<ServiceResultRow>> _results = new();

    public void ConfigureEndpoint(string endpoint, int resultCount, bool useLocalPersonIds = false)
    {
        var rows = new List<ServiceResultRow>(resultCount);
        for (int i = 0; i < resultCount; i++)
        {
            var row = new ServiceResultRow();
            var personId = useLocalPersonIds ? i % 1000 : i;
            row.AddBinding("s", new ServiceBinding(ServiceBindingType.Uri, $"<http://ex.org/person{personId}>"));
            row.AddBinding("name", new ServiceBinding(ServiceBindingType.Literal, $"\"Person{personId}\""));

            // Add additional bindings based on endpoint type (for variety)
            if (endpoint.Contains("medium") || endpoint.Contains("large"))
            {
                row.AddBinding("age", new ServiceBinding(ServiceBindingType.Literal, $"\"{20 + (i % 60)}\"^^<http://www.w3.org/2001/XMLSchema#integer>"));
            }
            if (endpoint.Contains("join"))
            {
                row.AddBinding("email", new ServiceBinding(ServiceBindingType.Literal, $"\"person{personId}@example.org\""));
            }
            rows.Add(row);
        }
        _results[endpoint] = rows;
    }

    public ValueTask<List<ServiceResultRow>> ExecuteSelectAsync(string endpointUri, string query, System.Threading.CancellationToken ct = default)
    {
        if (_results.TryGetValue(endpointUri, out var rows))
        {
            // Return a copy to avoid mutation issues
            return ValueTask.FromResult(new List<ServiceResultRow>(rows));
        }
        return ValueTask.FromResult(new List<ServiceResultRow>());
    }

    public ValueTask<bool> ExecuteAskAsync(string endpointUri, string query, System.Threading.CancellationToken ct = default)
    {
        return ValueTask.FromResult(_results.ContainsKey(endpointUri) && _results[endpointUri].Count > 0);
    }
}
