using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using SkyOmega.Mercury.Rdf;
using SkyOmega.Mercury.Rdf.Turtle;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;

namespace Mercury.Tests;

/// <summary>
/// Tests for SPARQL-star syntax support.
/// SPARQL-star quoted triples (<< s p o >>) are expanded at parse time to reification patterns.
/// </summary>
public class SparqlStarTests : IDisposable
{
    private readonly string _tempDir;
    private readonly QuadStore _store;

    public SparqlStarTests()
    {
        var tempPath = TempPath.Test("sparqlstar");
        tempPath.MarkOwnership();
        _tempDir = tempPath;
        _store = new QuadStore(_tempDir);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private async Task ParseTurtleIntoStore(string turtle)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(turtle));
        using var parser = new TurtleStreamParser(stream);
        await parser.ParseAsync((subject, predicate, obj) =>
        {
            _store.AddCurrent(subject, predicate, obj);
        });
    }

    private (List<string> values, int count) ExecuteQueryForVariable(string sparql, string varName)
    {
        var parser = new SparqlParser(sparql.AsSpan());
        var parsed = parser.ParseQuery();

        var values = new List<string>();
        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, sparql.AsSpan(), parsed);
            var resultEnum = executor.Execute();

            while (resultEnum.MoveNext())
            {
                var bindings = resultEnum.Current;
                var idx = bindings.FindBinding(varName.AsSpan());
                if (idx >= 0)
                    values.Add(bindings.GetString(idx).ToString());
            }
            resultEnum.Dispose();
        }
        finally
        {
            _store.ReleaseReadLock();
        }
        return (values, values.Count);
    }

    private List<(string, string)> ExecuteQueryForTwoVariables(string sparql, string var1, string var2)
    {
        var parser = new SparqlParser(sparql.AsSpan());
        var parsed = parser.ParseQuery();

        var results = new List<(string, string)>();
        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, sparql.AsSpan(), parsed);
            var resultEnum = executor.Execute();

            while (resultEnum.MoveNext())
            {
                var bindings = resultEnum.Current;
                var idx1 = bindings.FindBinding(var1.AsSpan());
                var idx2 = bindings.FindBinding(var2.AsSpan());
                var v1 = idx1 >= 0 ? bindings.GetString(idx1).ToString() : "";
                var v2 = idx2 >= 0 ? bindings.GetString(idx2).ToString() : "";
                results.Add((v1, v2));
            }
            resultEnum.Dispose();
        }
        finally
        {
            _store.ReleaseReadLock();
        }
        return results;
    }

    [Fact]
    public void ParseQuotedTriple_GeneratesReificationPatterns()
    {
        // Parse a query with quoted triple syntax
        var sparql = @"SELECT ?confidence WHERE {
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
                <http://ex.org/confidence> ?confidence .
        }";

        var parser = new SparqlParser(sparql.AsSpan());
        var query = parser.ParseQuery();

        // Should expand to 5 patterns: 4 reification + 1 main
        // ?_qt0 rdf:type rdf:Statement
        // ?_qt0 rdf:subject <http://ex.org/Alice>
        // ?_qt0 rdf:predicate <http://ex.org/knows>
        // ?_qt0 rdf:object <http://ex.org/Bob>
        // ?_qt0 <http://ex.org/confidence> ?confidence
        Assert.Equal(5, query.WhereClause.Pattern.RequiredPatternCount);
    }

    [Fact]
    public void ParseTwoQuotedTriples_GeneratesAllReificationPatterns()
    {
        // Parse a query with two quoted triple patterns
        var sparql = @"SELECT ?confidence ?source WHERE {
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
                <http://ex.org/confidence> ?confidence .
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
                <http://ex.org/source> ?source .
        }";

        var parser = new SparqlParser(sparql.AsSpan());
        var query = parser.ParseQuery();

        // Should expand to 10 patterns: 5 for each quoted triple
        Assert.Equal(10, query.WhereClause.Pattern.RequiredPatternCount);
    }

    [Fact]
    public async Task QueryReifiedTriple_WithConcreteQuotedTriple()
    {
        // Load RDF-star data via Turtle parser (which expands to reification)
        var turtle = @"
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
                <http://ex.org/confidence> ""0.9"" .
        ";

        await ParseTurtleIntoStore(turtle);

        // Query using SPARQL-star syntax
        var sparql = @"SELECT ?confidence WHERE {
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
                <http://ex.org/confidence> ?confidence .
        }";

        var (values, count) = ExecuteQueryForVariable(sparql, "?confidence");

        Assert.Equal(1, count);
        Assert.Equal("\"0.9\"", values[0]);
    }

    [Fact]
    public async Task QueryReifiedTriple_WithVariableInQuotedTriple()
    {
        // Load multiple RDF-star annotations
        var turtle = @"
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
                <http://ex.org/confidence> ""0.9"" .
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Carol> >>
                <http://ex.org/confidence> ""0.8"" .
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Dan> >>
                <http://ex.org/confidence> ""0.7"" .
        ";

        await ParseTurtleIntoStore(turtle);

        // Query with variable in quoted triple object position
        var sparql = @"SELECT ?person ?confidence WHERE {
            << <http://ex.org/Alice> <http://ex.org/knows> ?person >>
                <http://ex.org/confidence> ?confidence .
        }";

        var results = ExecuteQueryForTwoVariables(sparql, "?person", "?confidence");

        Assert.Equal(3, results.Count);

        // Verify all results are present
        var people = new HashSet<string>(results.ConvertAll(r => r.Item1));
        Assert.Contains("<http://ex.org/Bob>", people);
        Assert.Contains("<http://ex.org/Carol>", people);
        Assert.Contains("<http://ex.org/Dan>", people);
    }

    [Fact]
    public async Task QueryReifiedTriple_WithVariableInSubjectPosition()
    {
        var turtle = @"
            << <http://ex.org/Alice> <http://ex.org/worksAt> <http://ex.org/Acme> >>
                <http://ex.org/since> ""2020"" .
            << <http://ex.org/Bob> <http://ex.org/worksAt> <http://ex.org/Acme> >>
                <http://ex.org/since> ""2021"" .
        ";

        await ParseTurtleIntoStore(turtle);

        var sparql = @"SELECT ?employee ?since WHERE {
            << ?employee <http://ex.org/worksAt> <http://ex.org/Acme> >>
                <http://ex.org/since> ?since .
        }";

        var results = ExecuteQueryForTwoVariables(sparql, "?employee", "?since");

        Assert.Equal(2, results.Count);

        var employees = new HashSet<string>(results.ConvertAll(r => r.Item1));
        Assert.Contains("<http://ex.org/Alice>", employees);
        Assert.Contains("<http://ex.org/Bob>", employees);
    }

    [Fact]
    public async Task QueryReifiedTriple_WithVariablePredicate()
    {
        var turtle = @"
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
                <http://ex.org/confidence> ""0.9"" .
            << <http://ex.org/Alice> <http://ex.org/likes> <http://ex.org/Bob> >>
                <http://ex.org/confidence> ""0.8"" .
        ";

        await ParseTurtleIntoStore(turtle);

        var sparql = @"SELECT ?rel ?confidence WHERE {
            << <http://ex.org/Alice> ?rel <http://ex.org/Bob> >>
                <http://ex.org/confidence> ?confidence .
        }";

        var results = ExecuteQueryForTwoVariables(sparql, "?rel", "?confidence");

        Assert.Equal(2, results.Count);

        var relations = new HashSet<string>(results.ConvertAll(r => r.Item1));
        Assert.Contains("<http://ex.org/knows>", relations);
        Assert.Contains("<http://ex.org/likes>", relations);
    }

    [Fact]
    public async Task QueryReifiedTriple_MultipleAnnotations()
    {
        var turtle = @"
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
                <http://ex.org/confidence> ""0.9"" ;
                <http://ex.org/source> <http://ex.org/Survey> .
        ";

        await ParseTurtleIntoStore(turtle);

        var sparql = @"SELECT ?confidence ?source WHERE {
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
                <http://ex.org/confidence> ?confidence .
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
                <http://ex.org/source> ?source .
        }";

        var results = ExecuteQueryForTwoVariables(sparql, "?confidence", "?source");

        Assert.Single(results);
        Assert.Equal("\"0.9\"", results[0].Item1);
        Assert.Equal("<http://ex.org/Survey>", results[0].Item2);
    }

    [Fact]
    public async Task QueryReifiedTriple_NoMatch()
    {
        var turtle = @"
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
                <http://ex.org/confidence> ""0.9"" .
        ";

        await ParseTurtleIntoStore(turtle);

        // Query for a different triple
        var sparql = @"SELECT ?confidence WHERE {
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Carol> >>
                <http://ex.org/confidence> ?confidence .
        }";

        var (_, count) = ExecuteQueryForVariable(sparql, "?confidence");

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task QueryReifiedTriple_WithFilter()
    {
        // Use plain literals (no datatype) for simpler comparison
        var turtle = @"
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
                <http://ex.org/score> ""high"" .
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Carol> >>
                <http://ex.org/score> ""low"" .
        ";

        await ParseTurtleIntoStore(turtle);

        // First check without filter - should find 2 people
        var sparqlNoFilter = @"SELECT ?person ?score WHERE {
            << <http://ex.org/Alice> <http://ex.org/knows> ?person >>
                <http://ex.org/score> ?score .
        }";
        var allResults = ExecuteQueryForTwoVariables(sparqlNoFilter, "?person", "?score");
        Assert.Equal(2, allResults.Count);

        // Filter to just high scores using CONTAINS
        var sparql = @"SELECT ?person ?score WHERE {
            << <http://ex.org/Alice> <http://ex.org/knows> ?person >>
                <http://ex.org/score> ?score .
            FILTER(CONTAINS(?score, ""high""))
        }";

        var results = ExecuteQueryForTwoVariables(sparql, "?person", "?score");

        Assert.Single(results);
        Assert.Equal("<http://ex.org/Bob>", results[0].Item1);
        Assert.Contains("high", results[0].Item2);
    }

    [Fact]
    public async Task QueryReifiedTriple_CombinedWithRegularPattern()
    {
        var turtle = @"
            <http://ex.org/Alice> <http://ex.org/name> ""Alice"" .
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
                <http://ex.org/confidence> ""0.9"" .
        ";

        await ParseTurtleIntoStore(turtle);

        var sparql = @"SELECT ?name ?confidence WHERE {
            ?person <http://ex.org/name> ?name .
            << ?person <http://ex.org/knows> <http://ex.org/Bob> >>
                <http://ex.org/confidence> ?confidence .
        }";

        var results = ExecuteQueryForTwoVariables(sparql, "?name", "?confidence");

        Assert.Single(results);
        Assert.Equal("\"Alice\"", results[0].Item1);
        Assert.Equal("\"0.9\"", results[0].Item2);
    }

    [Fact]
    public void ParseNestedQuotedTriple_GeneratesMultipleReificationPatterns()
    {
        // Nested quoted triple: << << s p o >> p2 o2 >>
        var sparql = @"SELECT ?meta WHERE {
            << << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
                <http://ex.org/confidence> ""0.9"" >>
                <http://ex.org/assessedBy> ?meta .
        }";

        var parser = new SparqlParser(sparql.AsSpan());
        var query = parser.ParseQuery();

        // Inner quoted triple: 4 patterns
        // Outer quoted triple: 4 patterns (with inner as subject)
        // Main pattern: 1
        // Total: 9 patterns
        Assert.Equal(9, query.WhereClause.Pattern.RequiredPatternCount);
    }
}
