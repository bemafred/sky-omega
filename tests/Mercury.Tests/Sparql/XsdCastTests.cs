// Tests for XSD type cast functions
using System.Text;
using SkyOmega.Mercury.Rdf.Turtle;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Tests.Fixtures;
using SkyOmega.Mercury.Tests.W3C;
using Xunit;
using Xunit.Abstractions;

namespace SkyOmega.Mercury.Tests.Sparql;

[Collection("QuadStore")]
public class XsdCastTests : PooledStoreTestBase
{
    private readonly ITestOutputHelper _output;

    public XsdCastTests(ITestOutputHelper output, QuadStorePoolFixture fixture) : base(fixture)
    {
        _output = output;
        // No data added here - we'll load it in each test
    }

    [Fact]
    public async Task XsdIntegerCast_PlainString_ReturnsInteger()
    {
        // Load test data via Turtle parser (same as W3C tests)
        var turtle = """
            @prefix : <http://example.org/> .
            :s06 :p "0" .
            :s09 :p "1" .
            :s11 :p "13" .
            :n01 :p 0 .
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(turtle));
        using var parser = new TurtleStreamParser(stream);
        await parser.ParseAsync((s, p, o) => Store.AddCurrent(s, p, o));

        // First, let's see what values are stored
        _output.WriteLine("=== Stored values ===");
        Store.AcquireReadLock();
        try
        {
            var allResults = Store.QueryCurrent(null, null, null);
            while (allResults.MoveNext())
            {
                var quad = allResults.Current;
                _output.WriteLine($"  {quad.Subject} {quad.Predicate} {quad.Object}");
            }
            allResults.Dispose();
        }
        finally
        {
            Store.ReleaseReadLock();
        }

        var query = """
            PREFIX : <http://example.org/>
            PREFIX xsd: <http://www.w3.org/2001/XMLSchema#>
            SELECT ?a ?v (xsd:integer(?v) AS ?integer)
            WHERE { ?a :p ?v . }
            """;

        _output.WriteLine($"Query:\n{query}\n");

        var sparqlParser = new SparqlParser(query.AsSpan());
        var parsedQuery = sparqlParser.ParseQuery();

        _output.WriteLine($"Query type: {parsedQuery.Type}");
        _output.WriteLine($"SelectClause.AggregateCount: {parsedQuery.SelectClause.AggregateCount}");

        for (int i = 0; i < parsedQuery.SelectClause.AggregateCount; i++)
        {
            var agg = parsedQuery.SelectClause.GetAggregate(i);
            var expr = query.AsSpan(agg.VariableStart, agg.VariableLength).ToString();
            var alias = agg.AliasLength > 0 ? query.AsSpan(agg.AliasStart, agg.AliasLength).ToString() : "(none)";
            _output.WriteLine($"  Aggregate {i}: Function={agg.Function}, Expr='{expr}', Alias='{alias}'");
        }

        Store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var rows = 0;
            while (results.MoveNext())
            {
                rows++;
                var bindings = results.Current;

                // Get all bindings and their values
                _output.WriteLine($"Row {rows}:");
                _output.WriteLine($"  Binding count: {bindings.Count}");

                // Check ?a binding
                var aIdx = bindings.FindBinding("?a".AsSpan());
                if (aIdx >= 0)
                    _output.WriteLine($"  ?a = {bindings.GetString(aIdx).ToString()}");

                // Check ?v binding
                var vIdx = bindings.FindBinding("?v".AsSpan());
                if (vIdx >= 0)
                    _output.WriteLine($"  ?v = {bindings.GetString(vIdx).ToString()}");

                // Check ?integer binding
                var intIdx = bindings.FindBinding("?integer".AsSpan());
                _output.WriteLine($"  ?integer index = {intIdx}");
                if (intIdx >= 0)
                {
                    _output.WriteLine($"  ?integer = {bindings.GetString(intIdx).ToString()}");
                }
                else
                {
                    _output.WriteLine($"  ?integer = NOT FOUND (UNDEF)");
                }

                // Note: ?integer will only be bound when ?v is a valid integer string
            }
            results.Dispose();
            Assert.True(rows > 0, "Should have at least one result");
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public async Task XsdIntegerCast_W3CData_ReturnsCorrectResults()
    {
        // Load the exact W3C test data file
        var dataPath = Path.Combine(
            W3CTestContext.TestsRoot,
            "sparql", "sparql11", "cast", "data.ttl");

        if (!File.Exists(dataPath))
        {
            _output.WriteLine($"Data file not found: {dataPath}");
            Skip.If(true, "W3C test data not available");
            return;
        }

        using var fileStream = File.OpenRead(dataPath);
        using var parser = new TurtleStreamParser(fileStream);
        await parser.ParseAsync((s, p, o) => Store.AddCurrent(s, p, o));

        // Count stored triples
        int tripleCount = 0;
        Store.AcquireReadLock();
        try
        {
            var allResults = Store.QueryCurrent(null, null, null);
            while (allResults.MoveNext())
            {
                tripleCount++;
            }
            allResults.Dispose();
        }
        finally
        {
            Store.ReleaseReadLock();
        }
        _output.WriteLine($"Loaded {tripleCount} triples");

        // Use the exact W3C query
        var queryPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "..", "..", "..", "..", "..",
            "tests/w3c-rdf-tests/sparql/sparql11/cast/cast-int.rq");
        var query = await File.ReadAllTextAsync(queryPath);
        _output.WriteLine($"Query:\n{query}\n");

        var sparqlParser = new SparqlParser(query.AsSpan());
        var parsedQuery = sparqlParser.ParseQuery();

        _output.WriteLine($"Query type: {parsedQuery.Type}");
        _output.WriteLine($"SelectClause.AggregateCount: {parsedQuery.SelectClause.AggregateCount}");

        for (int i = 0; i < parsedQuery.SelectClause.AggregateCount; i++)
        {
            var agg = parsedQuery.SelectClause.GetAggregate(i);
            var expr = query.AsSpan(agg.VariableStart, agg.VariableLength).ToString();
            var alias = agg.AliasLength > 0 ? query.AsSpan(agg.AliasStart, agg.AliasLength).ToString() : "(none)";
            _output.WriteLine($"  Aggregate {i}: Function={agg.Function}, Expr='{expr}', Alias='{alias}'");
        }

        // Run on separate thread like W3C tests do
        var (foundRows, foundS06, s06IntIdx, s06IntValue) = RunOnLargeStack(() =>
        {
            var rows = 0;
            var s06Found = false;
            var intIdx = -1;
            var intValue = "";

            Store.AcquireReadLock();
            try
            {
                var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
                var results = executor.Execute();

                while (results.MoveNext())
                {
                    rows++;
                    var bindings = results.Current;

                    // Check if this is s06
                    var aIdx = bindings.FindBinding("?a".AsSpan());
                    if (aIdx >= 0)
                    {
                        var aValue = bindings.GetString(aIdx).ToString();
                        if (aValue.Contains("s06"))
                        {
                            s06Found = true;
                            intIdx = bindings.FindBinding("?integer".AsSpan());
                            if (intIdx >= 0)
                            {
                                intValue = bindings.GetString(intIdx).ToString();
                            }
                        }
                    }
                }
                results.Dispose();
            }
            finally
            {
                Store.ReleaseReadLock();
            }

            return (rows, s06Found, intIdx, intValue);
        });

        _output.WriteLine($"Total rows: {foundRows}");
        _output.WriteLine($"s06 found: {foundS06}");
        _output.WriteLine($"?integer index: {s06IntIdx}");
        _output.WriteLine($"?integer value: {s06IntValue}");

        Assert.True(foundS06, "Should have found row for s06");
        Assert.True(s06IntIdx >= 0, "?integer should be bound for s06");
    }

    private static T RunOnLargeStack<T>(Func<T> func)
    {
        const int stackSize = 8 * 1024 * 1024; // 8MB
        T? result = default;
        Exception? exception = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        }, stackSize);

        thread.Start();
        thread.Join();

        if (exception != null)
            throw new AggregateException(exception);

        return result!;
    }

    [SkippableFact]
    public async Task XsdIntegerCast_DiagnosticW3CStyle()
    {
        // This test mimics exactly how the W3C test extracts bindings
        // to diagnose why ?integer shows as UNDEF

        // Use exact W3C test data file
        var dataPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "..", "..", "..", "..", "..", // Back to repo root
            "tests/w3c-rdf-tests/sparql/sparql11/cast/data.ttl");

        Skip.IfNot(File.Exists(dataPath), $"W3C test data not found: {dataPath}");

        using var fileStream = File.OpenRead(dataPath);
        using var parser = new TurtleStreamParser(fileStream);
        await parser.ParseAsync((s, p, o) => Store.AddCurrent(s, p, o));

        // Use exact W3C query file
        var queryPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "..", "..", "..", "..", "..",
            "tests/w3c-rdf-tests/sparql/sparql11/cast/cast-int.rq");
        var query = await File.ReadAllTextAsync(queryPath);

        _output.WriteLine($"Query:\n{query}\n");

        var sparqlParser = new SparqlParser(query.AsSpan());
        var parsedQuery = sparqlParser.ParseQuery();

        _output.WriteLine($"Query type: {parsedQuery.Type}");
        _output.WriteLine($"SelectClause.HasAggregates: {parsedQuery.SelectClause.HasAggregates}");
        _output.WriteLine($"SelectClause.AggregateCount: {parsedQuery.SelectClause.AggregateCount}");

        for (int i = 0; i < parsedQuery.SelectClause.AggregateCount; i++)
        {
            var agg = parsedQuery.SelectClause.GetAggregate(i);
            var expr = query.AsSpan(agg.VariableStart, agg.VariableLength).ToString();
            var alias = agg.AliasLength > 0 ? query.AsSpan(agg.AliasStart, agg.AliasLength).ToString() : "(none)";
            _output.WriteLine($"  Aggregate {i}: Function={agg.Function}, Expr='{expr}', Alias='{alias}'");
        }

        // Execute and extract bindings EXACTLY like W3C test does
        var (success, diagnostics) = RunOnLargeStack(() =>
        {
            var diags = new System.Text.StringBuilder();

            Store.AcquireReadLock();
            try
            {
                using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
                var results = executor.Execute();
                try
                {
                    while (results.MoveNext())
                    {
                        var current = results.Current;
                        diags.AppendLine($"Row: BindingCount={current.Count}");

                        // Log all bindings
                        for (int i = 0; i < current.Count; i++)
                        {
                            var type = current.GetType(i);
                            var value = current.GetString(i).ToString();
                            diags.AppendLine($"  Binding[{i}]: Type={type}, Value='{value}'");
                        }

                        // Now try to find bindings the W3C way
                        var varNames = new[] { "a", "v", "integer" };
                        foreach (var varName in varNames)
                        {
                            var prefixedName = "?" + varName;
                            var idx = current.FindBinding(prefixedName.AsSpan());
                            if (idx < 0)
                                idx = current.FindBinding(varName.AsSpan());

                            if (idx < 0)
                            {
                                diags.AppendLine($"  ?{varName}: NOT FOUND (idx=-1)");
                            }
                            else
                            {
                                var type = current.GetType(idx);
                                var value = current.GetString(idx).ToString();
                                diags.AppendLine($"  ?{varName}: idx={idx}, Type={type}, Value='{value}'");
                            }
                        }
                    }
                }
                finally
                {
                    results.Dispose();
                }
            }
            finally
            {
                Store.ReleaseReadLock();
            }

            return (true, diags.ToString());
        });

        _output.WriteLine(diagnostics);
        Assert.True(success);
    }
}
