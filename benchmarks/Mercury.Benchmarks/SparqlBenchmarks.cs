using BenchmarkDotNet.Attributes;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;

namespace SkyOmega.Mercury.Benchmarks;

/// <summary>
/// Benchmarks for SPARQL query parsing throughput.
/// Measures zero-GC parser performance on queries of varying complexity.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class SparqlParserBenchmarks
{
    private string _simpleQuery = null!;
    private string _threePatternQuery = null!;
    private string _complexQuery = null!;
    private string _propertyPathQuery = null!;

    [GlobalSetup]
    public void Setup()
    {
        _simpleQuery = "SELECT * WHERE { ?s ?p ?o }";

        _threePatternQuery = @"SELECT * WHERE {
            ?s <http://xmlns.com/foaf/0.1/name> ?name .
            ?s <http://xmlns.com/foaf/0.1/age> ?age .
            ?s <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> ?type
        }";

        _complexQuery = @"SELECT ?s ?name (COUNT(?friend) as ?friendCount) WHERE {
            ?s <http://xmlns.com/foaf/0.1/name> ?name .
            ?s <http://xmlns.com/foaf/0.1/age> ?age .
            ?s <http://xmlns.com/foaf/0.1/knows> ?friend .
            ?friend <http://xmlns.com/foaf/0.1/name> ?fn .
            ?friend <http://xmlns.com/foaf/0.1/age> ?fa .
            ?friend <http://xmlns.com/foaf/0.1/knows> ?fof .
            ?fof <http://xmlns.com/foaf/0.1/name> ?fofn .
            ?s <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> ?type .
            ?friend <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> ?ftype .
            ?fof <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> ?foftype
            FILTER(?age > 25 && ?fa < 50)
        } GROUP BY ?s ?name ORDER BY DESC(?friendCount) LIMIT 100";

        _propertyPathQuery = @"SELECT * WHERE {
            <http://example.org/person0> <http://xmlns.com/foaf/0.1/knows>+ ?reachable
        }";
    }

    [Benchmark(Description = "Parse simple query (1 pattern)")]
    public int ParseSimpleQuery()
    {
        var parser = new SparqlParser(_simpleQuery.AsSpan());
        var query = parser.ParseQuery();
        return query.WhereClause.Pattern.PatternCount;
    }

    [Benchmark(Description = "Parse 3-pattern query")]
    public int ParseThreePatternQuery()
    {
        var parser = new SparqlParser(_threePatternQuery.AsSpan());
        var query = parser.ParseQuery();
        return query.WhereClause.Pattern.PatternCount;
    }

    [Benchmark(Description = "Parse complex query (10 patterns + modifiers)")]
    public int ParseComplexQuery()
    {
        var parser = new SparqlParser(_complexQuery.AsSpan());
        var query = parser.ParseQuery();
        return query.WhereClause.Pattern.PatternCount;
    }

    [Benchmark(Description = "Parse property path query")]
    public int ParsePropertyPathQuery()
    {
        var parser = new SparqlParser(_propertyPathQuery.AsSpan());
        var query = parser.ParseQuery();
        return query.WhereClause.Pattern.PatternCount;
    }
}

/// <summary>
/// Benchmarks for SPARQL query execution on pre-populated store.
/// Note: Queries are parsed fresh each iteration to avoid state issues.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class SparqlExecutionBenchmarks
{
    private string _dbPath = null!;
    private QuadStore _store = null!;

    // Query strings (parsed fresh each iteration)
    private string _singlePatternSource = null!;
    private string _threePatternSource = null!;
    private string _filterSource = null!;
    private string _askSource = null!;

    [GlobalSetup]
    public void Setup()
    {
        var tempPath = TempPath.Benchmark("sparql-exec");
        tempPath.MarkOwnership();
        _dbPath = tempPath;

        _store = new QuadStore(_dbPath);

        // Populate with 50K triples using batch writes
        _store.BeginBatch();
        try
        {
            // 10K entities with properties (30K triples)
            for (int i = 0; i < 10000; i++)
            {
                _store.AddCurrentBatched(
                    $"<http://ex.org/person{i}>",
                    "<http://xmlns.com/foaf/0.1/name>",
                    $"\"Person{i}\""
                );
                _store.AddCurrentBatched(
                    $"<http://ex.org/person{i}>",
                    "<http://xmlns.com/foaf/0.1/age>",
                    $"\"{20 + (i % 60)}\"^^<http://www.w3.org/2001/XMLSchema#integer>"
                );
                _store.AddCurrentBatched(
                    $"<http://ex.org/person{i}>",
                    "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>",
                    "<http://xmlns.com/foaf/0.1/Person>"
                );
            }

            // Relationships (~25K triples)
            for (int i = 0; i < 10000; i++)
            {
                for (int j = 1; j <= (i % 4) + 2; j++)
                {
                    var target = (i + j * 100) % 10000;
                    _store.AddCurrentBatched(
                        $"<http://ex.org/person{i}>",
                        "<http://xmlns.com/foaf/0.1/knows>",
                        $"<http://ex.org/person{target}>"
                    );
                }
            }

            _store.CommitBatch();
        }
        catch
        {
            _store.RollbackBatch();
            throw;
        }

        // Store query strings
        _singlePatternSource = "SELECT * WHERE { ?s <http://xmlns.com/foaf/0.1/name> ?name }";

        _threePatternSource = @"SELECT * WHERE {
            ?s <http://xmlns.com/foaf/0.1/name> ?name .
            ?s <http://xmlns.com/foaf/0.1/age> ?age .
            ?s <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> ?type
        }";

        _filterSource = @"SELECT * WHERE {
            ?s <http://xmlns.com/foaf/0.1/name> ?name .
            ?s <http://xmlns.com/foaf/0.1/age> ?age
            FILTER(?age > 50)
        }";

        _askSource = "ASK WHERE { <http://ex.org/person100> <http://xmlns.com/foaf/0.1/knows> ?someone }";
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);
    }

    [Benchmark(Description = "Execute single pattern")]
    public long ExecuteSinglePattern()
    {
        long count = 0;
        var parser = new SparqlParser(_singlePatternSource.AsSpan());
        var query = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, _singlePatternSource.AsSpan(), in query);
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

    [Benchmark(Description = "Execute 3-pattern query")]
    public long ExecuteThreePatterns()
    {
        long count = 0;
        var parser = new SparqlParser(_threePatternSource.AsSpan());
        var query = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, _threePatternSource.AsSpan(), in query);
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

    [Benchmark(Description = "Execute query with FILTER")]
    public long ExecuteWithFilter()
    {
        long count = 0;
        var parser = new SparqlParser(_filterSource.AsSpan());
        var query = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, _filterSource.AsSpan(), in query);
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

    [Benchmark(Description = "Execute ASK query (early termination)")]
    public bool ExecuteAskQuery()
    {
        var parser = new SparqlParser(_askSource.AsSpan());
        var query = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, _askSource.AsSpan(), in query);
            return executor.ExecuteAsk();
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }
}

/// <summary>
/// Benchmarks for JOIN operator scaling with pattern count.
/// Note: Queries are parsed fresh each iteration to avoid state issues.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class JoinBenchmarks
{
    private string _dbPath = null!;
    private QuadStore _store = null!;

    private string _twoPatternSource = null!;
    private string _fivePatternSource = null!;
    private string _eightPatternSource = null!;

    [GlobalSetup]
    public void Setup()
    {
        var tempPath = TempPath.Benchmark("join");
        tempPath.MarkOwnership();
        _dbPath = tempPath;

        _store = new QuadStore(_dbPath);

        // Populate with graph structure
        _store.BeginBatch();
        try
        {
            for (int i = 0; i < 5000; i++)
            {
                _store.AddCurrentBatched(
                    $"<http://ex.org/person{i}>",
                    "<http://xmlns.com/foaf/0.1/name>",
                    $"\"Person{i}\""
                );
                _store.AddCurrentBatched(
                    $"<http://ex.org/person{i}>",
                    "<http://xmlns.com/foaf/0.1/age>",
                    $"\"{20 + (i % 60)}\"^^<http://www.w3.org/2001/XMLSchema#integer>"
                );
                _store.AddCurrentBatched(
                    $"<http://ex.org/person{i}>",
                    "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>",
                    "<http://xmlns.com/foaf/0.1/Person>"
                );
                _store.AddCurrentBatched(
                    $"<http://ex.org/person{i}>",
                    "<http://xmlns.com/foaf/0.1/email>",
                    $"\"person{i}@example.org\""
                );

                // Each person knows 2-3 others
                for (int j = 1; j <= (i % 2) + 2; j++)
                {
                    var target = (i + j * 100) % 5000;
                    _store.AddCurrentBatched(
                        $"<http://ex.org/person{i}>",
                        "<http://xmlns.com/foaf/0.1/knows>",
                        $"<http://ex.org/person{target}>"
                    );
                }
            }
            _store.CommitBatch();
        }
        catch
        {
            _store.RollbackBatch();
            throw;
        }

        // Store query strings
        _twoPatternSource = @"SELECT * WHERE {
            ?s <http://xmlns.com/foaf/0.1/name> ?name .
            ?s <http://xmlns.com/foaf/0.1/age> ?age
        } LIMIT 1000";

        _fivePatternSource = @"SELECT * WHERE {
            ?s <http://xmlns.com/foaf/0.1/name> ?name .
            ?s <http://xmlns.com/foaf/0.1/age> ?age .
            ?s <http://xmlns.com/foaf/0.1/knows> ?friend .
            ?friend <http://xmlns.com/foaf/0.1/name> ?fn .
            ?friend <http://xmlns.com/foaf/0.1/age> ?fa
        } LIMIT 1000";

        _eightPatternSource = @"SELECT * WHERE {
            ?s <http://xmlns.com/foaf/0.1/name> ?name .
            ?s <http://xmlns.com/foaf/0.1/age> ?age .
            ?s <http://xmlns.com/foaf/0.1/email> ?email .
            ?s <http://xmlns.com/foaf/0.1/knows> ?friend .
            ?friend <http://xmlns.com/foaf/0.1/name> ?fn .
            ?friend <http://xmlns.com/foaf/0.1/age> ?fa .
            ?friend <http://xmlns.com/foaf/0.1/knows> ?fof .
            ?fof <http://xmlns.com/foaf/0.1/name> ?fofn
        } LIMIT 1000";
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);
    }

    [Benchmark(Description = "2-pattern JOIN")]
    public long TwoPatternJoin()
    {
        long count = 0;
        var parser = new SparqlParser(_twoPatternSource.AsSpan());
        var query = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, _twoPatternSource.AsSpan(), in query);
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

    [Benchmark(Description = "5-pattern JOIN")]
    public long FivePatternJoin()
    {
        long count = 0;
        var parser = new SparqlParser(_fivePatternSource.AsSpan());
        var query = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, _fivePatternSource.AsSpan(), in query);
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

    [Benchmark(Description = "8-pattern JOIN")]
    public long EightPatternJoin()
    {
        long count = 0;
        var parser = new SparqlParser(_eightPatternSource.AsSpan());
        var query = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, _eightPatternSource.AsSpan(), in query);
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
}

/// <summary>
/// Benchmarks for FILTER expression evaluation overhead.
/// Note: Queries are parsed fresh each iteration to avoid state issues.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class FilterBenchmarks
{
    private string _dbPath = null!;
    private QuadStore _store = null!;

    private string _numericSource = null!;
    private string _stringSource = null!;
    private string _regexSource = null!;

    [GlobalSetup]
    public void Setup()
    {
        var tempPath = TempPath.Benchmark("filter");
        tempPath.MarkOwnership();
        _dbPath = tempPath;

        _store = new QuadStore(_dbPath);

        // Populate with typed data
        _store.BeginBatch();
        try
        {
            for (int i = 0; i < 10000; i++)
            {
                _store.AddCurrentBatched(
                    $"<http://ex.org/person{i}>",
                    "<http://xmlns.com/foaf/0.1/name>",
                    $"\"Person{i}\""
                );
                _store.AddCurrentBatched(
                    $"<http://ex.org/person{i}>",
                    "<http://xmlns.com/foaf/0.1/age>",
                    $"\"{20 + (i % 60)}\"^^<http://www.w3.org/2001/XMLSchema#integer>"
                );
            }
            _store.CommitBatch();
        }
        catch
        {
            _store.RollbackBatch();
            throw;
        }

        _numericSource = @"SELECT * WHERE {
            ?s <http://xmlns.com/foaf/0.1/age> ?age
            FILTER(?age > 50)
        }";

        _stringSource = @"SELECT * WHERE {
            ?s <http://xmlns.com/foaf/0.1/name> ?name
            FILTER(CONTAINS(?name, ""Person1""))
        }";

        _regexSource = @"SELECT * WHERE {
            ?s <http://xmlns.com/foaf/0.1/name> ?name
            FILTER(REGEX(?name, ""^Person1.*""))
        }";
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);
    }

    [Benchmark(Description = "Numeric comparison FILTER")]
    public long NumericComparison()
    {
        long count = 0;
        var parser = new SparqlParser(_numericSource.AsSpan());
        var query = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, _numericSource.AsSpan(), in query);
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

    [Benchmark(Description = "String CONTAINS FILTER")]
    public long StringContains()
    {
        long count = 0;
        var parser = new SparqlParser(_stringSource.AsSpan());
        var query = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, _stringSource.AsSpan(), in query);
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

    [Benchmark(Description = "REGEX FILTER")]
    public long RegexFilter()
    {
        long count = 0;
        var parser = new SparqlParser(_regexSource.AsSpan());
        var query = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, _regexSource.AsSpan(), in query);
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
}
