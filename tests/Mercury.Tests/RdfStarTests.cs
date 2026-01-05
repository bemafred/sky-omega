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
using SkyOmega.Mercury.Tests.Fixtures;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for RDF-star reification support.
/// RDF-star triples are converted to standard RDF reification for storage and query.
/// </summary>
[Collection("QuadStore")]
public class RdfStarTests : PooledStoreTestBase
{
    // RDF namespace constants (with angle brackets to match Turtle parser output)
    private const string RdfType = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>";
    private const string RdfStatement = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#Statement>";
    private const string RdfSubject = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#subject>";
    private const string RdfPredicate = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#predicate>";
    private const string RdfObject = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#object>";

    public RdfStarTests(QuadStorePoolFixture fixture) : base(fixture)
    {
    }

    private async Task<List<RdfTriple>> ParseTurtle(string turtle)
    {
        var triples = new List<RdfTriple>();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(turtle));
        using var parser = new TurtleStreamParser(stream);
        await foreach (var triple in parser.ParseAsync())
        {
            triples.Add(triple);
        }
        return triples;
    }

    private async Task ParseTurtleIntoStore(string turtle)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(turtle));
        using var parser = new TurtleStreamParser(stream);
        await parser.ParseAsync((subject, predicate, obj) =>
        {
            Store.AddCurrent(subject, predicate, obj);
        });
    }

    [Fact]
    public async Task ParseReifiedTriple_EmitsReificationTriples()
    {
        var turtle = @"
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
                <http://ex.org/confidence> ""0.9"" .
        ";

        var triples = await ParseTurtle(turtle);

        // Expect 6 triples: 4 reification + 1 asserted + 1 annotation
        Assert.Equal(6, triples.Count);

        // Check for rdf:type rdf:Statement
        Assert.Contains(triples, t =>
            t.Predicate == RdfType && t.Object == RdfStatement);

        // Check for rdf:subject
        Assert.Contains(triples, t =>
            t.Predicate == RdfSubject && t.Object == "<http://ex.org/Alice>");

        // Check for rdf:predicate
        Assert.Contains(triples, t =>
            t.Predicate == RdfPredicate && t.Object == "<http://ex.org/knows>");

        // Check for rdf:object
        Assert.Contains(triples, t =>
            t.Predicate == RdfObject && t.Object == "<http://ex.org/Bob>");

        // Check for asserted triple
        Assert.Contains(triples, t =>
            t.Subject == "<http://ex.org/Alice>" &&
            t.Predicate == "<http://ex.org/knows>" &&
            t.Object == "<http://ex.org/Bob>");

        // Check for annotation triple
        Assert.Contains(triples, t =>
            t.Predicate == "<http://ex.org/confidence>" &&
            t.Object.Contains("0.9"));
    }

    [Fact]
    public async Task ParseReifiedTriple_WithExplicitReifier_UsesProvidedIri()
    {
        var turtle = @"
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> ~ <http://ex.org/stmt1> >>
                <http://ex.org/source> <http://ex.org/Survey> .
        ";

        var triples = await ParseTurtle(turtle);

        // Check that the explicit reifier is used
        Assert.Contains(triples, t =>
            t.Subject == "<http://ex.org/stmt1>" &&
            t.Predicate == RdfType &&
            t.Object == RdfStatement);

        Assert.Contains(triples, t =>
            t.Subject == "<http://ex.org/stmt1>" &&
            t.Predicate == "<http://ex.org/source>" &&
            t.Object == "<http://ex.org/Survey>");
    }

    [Fact]
    public async Task ParseReifiedTriple_ZeroGcPath_EmitsReificationTriples()
    {
        var turtle = @"
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
                <http://ex.org/confidence> ""0.9"" .
        ";

        var triples = new List<(string s, string p, string o)>();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(turtle));
        using var parser = new TurtleStreamParser(stream);
        await parser.ParseAsync((subject, predicate, obj) =>
        {
            triples.Add((subject.ToString(), predicate.ToString(), obj.ToString()));
        });

        // Should have same 6 triples as allocating path
        Assert.Equal(6, triples.Count);

        // Check for reification triples
        Assert.Contains(triples, t => t.p == RdfType && t.o == RdfStatement);
        Assert.Contains(triples, t => t.p == RdfSubject && t.o == "<http://ex.org/Alice>");
        Assert.Contains(triples, t => t.p == RdfPredicate && t.o == "<http://ex.org/knows>");
        Assert.Contains(triples, t => t.p == RdfObject && t.o == "<http://ex.org/Bob>");
    }

    [Fact]
    public async Task QueryReifiedTriple_SinglePattern_FindsStatement()
    {
        var turtle = @"
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
                <http://ex.org/confidence> ""0.9"" .
        ";

        await ParseTurtleIntoStore(turtle);

        // Query just for rdf:Statement type (simplest pattern)
        var query = @"
            SELECT ?stmt WHERE {
                ?stmt <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://www.w3.org/1999/02/22-rdf-syntax-ns#Statement> .
            }
        ";

        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var statements = new List<string>();
            while (results.MoveNext())
            {
                var b = results.Current;
                var idx = b.FindBinding("?stmt".AsSpan());
                if (idx >= 0)
                {
                    statements.Add(b.GetString(idx).ToString());
                }
            }
            results.Dispose();

            Assert.Single(statements);
            Assert.StartsWith("_:b", statements[0]); // Should be a blank node
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public async Task QueryReifiedTriple_TwoPatterns_FindsStatement()
    {
        var turtle = @"
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
                <http://ex.org/confidence> ""0.9"" .
        ";

        await ParseTurtleIntoStore(turtle);

        // Query with two patterns: rdf:type AND rdf:subject
        var query = @"
            SELECT ?stmt ?subj WHERE {
                ?stmt <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://www.w3.org/1999/02/22-rdf-syntax-ns#Statement> .
                ?stmt <http://www.w3.org/1999/02/22-rdf-syntax-ns#subject> ?subj .
            }
        ";

        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var bindings = new List<(string stmt, string subj)>();
            while (results.MoveNext())
            {
                var b = results.Current;
                var stmtIdx = b.FindBinding("?stmt".AsSpan());
                var subjIdx = b.FindBinding("?subj".AsSpan());
                if (stmtIdx >= 0 && subjIdx >= 0)
                {
                    bindings.Add((b.GetString(stmtIdx).ToString(), b.GetString(subjIdx).ToString()));
                }
            }
            results.Dispose();

            Assert.Single(bindings);
            Assert.StartsWith("_:b", bindings[0].stmt);
            Assert.Equal("<http://ex.org/Alice>", bindings[0].subj);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public async Task QueryReifiedTriple_ThreePatterns_WithBoundSubject()
    {
        var turtle = @"
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
                <http://ex.org/confidence> ""0.9"" .
        ";

        await ParseTurtleIntoStore(turtle);

        // Query with three patterns: rdf:type, rdf:subject (bound value), rdf:predicate (variable)
        var query = @"
            SELECT ?stmt ?pred WHERE {
                ?stmt <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://www.w3.org/1999/02/22-rdf-syntax-ns#Statement> .
                ?stmt <http://www.w3.org/1999/02/22-rdf-syntax-ns#subject> <http://ex.org/Alice> .
                ?stmt <http://www.w3.org/1999/02/22-rdf-syntax-ns#predicate> ?pred .
            }
        ";

        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var bindings = new List<(string stmt, string pred)>();
            while (results.MoveNext())
            {
                var b = results.Current;
                var stmtIdx = b.FindBinding("?stmt".AsSpan());
                var predIdx = b.FindBinding("?pred".AsSpan());
                if (stmtIdx >= 0 && predIdx >= 0)
                {
                    bindings.Add((b.GetString(stmtIdx).ToString(), b.GetString(predIdx).ToString()));
                }
            }
            results.Dispose();

            Assert.Single(bindings);
            Assert.Equal("<http://ex.org/knows>", bindings[0].pred);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public async Task QueryReifiedTriple_FourPatterns_AllBound()
    {
        var turtle = @"
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
                <http://ex.org/confidence> ""0.9"" .
        ";

        await ParseTurtleIntoStore(turtle);

        // Query with four patterns - all reification patterns with bound values
        var query = @"
            SELECT ?stmt WHERE {
                ?stmt <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://www.w3.org/1999/02/22-rdf-syntax-ns#Statement> .
                ?stmt <http://www.w3.org/1999/02/22-rdf-syntax-ns#subject> <http://ex.org/Alice> .
                ?stmt <http://www.w3.org/1999/02/22-rdf-syntax-ns#predicate> <http://ex.org/knows> .
                ?stmt <http://www.w3.org/1999/02/22-rdf-syntax-ns#object> <http://ex.org/Bob> .
            }
        ";

        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var statements = new List<string>();
            while (results.MoveNext())
            {
                var b = results.Current;
                var idx = b.FindBinding("?stmt".AsSpan());
                if (idx >= 0)
                {
                    statements.Add(b.GetString(idx).ToString());
                }
            }
            results.Dispose();

            Assert.Single(statements);
            Assert.StartsWith("_:b", statements[0]);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public async Task QueryReifiedTriple_DebugAllTriples()
    {
        var turtle = @"
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
                <http://ex.org/confidence> ""0.9"" .
        ";

        // First, let's see what triples are actually emitted
        var emittedTriples = new List<(string s, string p, string o)>();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(turtle));
        using var parser1 = new TurtleStreamParser(stream);
        await parser1.ParseAsync((subject, predicate, obj) =>
        {
            emittedTriples.Add((subject.ToString(), predicate.ToString(), obj.ToString()));
        });

        // Log all triples for debugging
        var triplesLog = string.Join("\n", emittedTriples.Select(t => $"  S={t.s} P={t.p} O={t.o}"));

        // Should have 7 triples: 4 reification + 1 asserted + 1 annotation + ???
        // Let's check if the annotation triple (_:b0 confidence 0.9) is present
        var annotationTriple = emittedTriples.Find(t =>
            t.p == "<http://ex.org/confidence>" && t.o.Contains("0.9"));

        Assert.True(annotationTriple != default,
            $"Annotation triple not found! All triples:\n{triplesLog}");
        Assert.True(annotationTriple.s.StartsWith("_:b"),
            $"Annotation triple subject should be blank node. Got: {annotationTriple.s}\nAll triples:\n{triplesLog}");
    }

    [Fact(Skip = "ADR-009 Phase 2: Multiple queries in single async method exceeds stack. Requires QueryBuffer migration.")]
    public async Task QueryReifiedTriple_WithSparql()
    {
        var turtle = @"
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
                <http://ex.org/confidence> ""0.9"" .
        ";

        await ParseTurtleIntoStore(turtle);

        // First, let's find the blank node via 4-pattern query (which we know works)
        var query4 = @"
            SELECT ?stmt WHERE {
                ?stmt <http://www.w3.org/1999/02/22-rdf-syntax-ns#type> <http://www.w3.org/1999/02/22-rdf-syntax-ns#Statement> .
                ?stmt <http://www.w3.org/1999/02/22-rdf-syntax-ns#subject> <http://ex.org/Alice> .
                ?stmt <http://www.w3.org/1999/02/22-rdf-syntax-ns#predicate> <http://ex.org/knows> .
                ?stmt <http://www.w3.org/1999/02/22-rdf-syntax-ns#object> <http://ex.org/Bob> .
            }
        ";

        string? stmtBlankNode = null;

        Store.AcquireReadLock();
        try
        {
            var parser4 = new SparqlParser(query4.AsSpan());
            var parsedQuery4 = parser4.ParseQuery();
            using var executor4 = new QueryExecutor(Store, query4.AsSpan(), parsedQuery4);
            var results4 = executor4.Execute();

            if (results4.MoveNext())
            {
                var b = results4.Current;
                var idx = b.FindBinding("?stmt".AsSpan());
                if (idx >= 0)
                {
                    stmtBlankNode = b.GetString(idx).ToString();
                }
            }
            results4.Dispose();
        }
        finally
        {
            Store.ReleaseReadLock();
        }

        Assert.NotNull(stmtBlankNode);
        Assert.StartsWith("_:b", stmtBlankNode);

        // Now query for confidence using the blank node directly
        // This will help us understand if the issue is with blank node join or with the confidence pattern
        var query5 = $@"
            SELECT ?confidence WHERE {{
                {stmtBlankNode} <http://ex.org/confidence> ?confidence .
            }}
        ";

        Store.AcquireReadLock();
        try
        {
            var parser5 = new SparqlParser(query5.AsSpan());
            var parsedQuery5 = parser5.ParseQuery();
            using var executor5 = new QueryExecutor(Store, query5.AsSpan(), parsedQuery5);
            var results5 = executor5.Execute();

            var confidences = new List<string>();
            while (results5.MoveNext())
            {
                var b = results5.Current;
                var idx = b.FindBinding("?confidence".AsSpan());
                if (idx >= 0)
                {
                    confidences.Add(b.GetString(idx).ToString());
                }
            }
            results5.Dispose();

            Assert.Single(confidences);
            Assert.Contains("0.9", confidences[0]);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public async Task ReifiedTriple_AssertedTripleIsQueryable()
    {
        var turtle = @"
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
                <http://ex.org/confidence> ""0.9"" .
        ";

        await ParseTurtleIntoStore(turtle);

        // The asserted triple should be directly queryable
        var query = @"
            SELECT ?person WHERE {
                <http://ex.org/Alice> <http://ex.org/knows> ?person .
            }
        ";

        var parser = new SparqlParser(query.AsSpan());
        var parsedQuery = parser.ParseQuery();

        Store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(Store, query.AsSpan(), parsedQuery);
            var results = executor.Execute();

            var people = new List<string>();
            while (results.MoveNext())
            {
                var b = results.Current;
                var idx = b.FindBinding("?person".AsSpan());
                if (idx >= 0)
                {
                    people.Add(b.GetString(idx).ToString());
                }
            }
            results.Dispose();

            Assert.Single(people);
            Assert.Equal("<http://ex.org/Bob>", people[0]);
        }
        finally
        {
            Store.ReleaseReadLock();
        }
    }

    [Fact]
    public async Task NestedReifiedTriples_Work()
    {
        // A reified triple about a reified triple
        var turtle = @"
            << << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
                  <http://ex.org/source> <http://ex.org/Survey> >>
                <http://ex.org/confidence> ""0.95"" .
        ";

        var triples = await ParseTurtle(turtle);

        // Should have:
        // Inner reification: 4 + 1 = 5 triples
        // Outer reification: 4 + 1 = 5 triples (subject is inner reifier, predicate is source, object is Survey)
        // Outer annotation: 1 triple
        // Total: 11 triples
        Assert.True(triples.Count >= 10, $"Expected at least 10 triples, got {triples.Count}");

        // Check for nested structure - outer reification should reference inner reifier
        var innerReificationTriple = triples.Find(t =>
            t.Predicate == RdfSubject && t.Object == "<http://ex.org/Alice>");
        var innerReifier = innerReificationTriple.Subject;

        Assert.False(string.IsNullOrEmpty(innerReifier));

        // The outer reification should have the inner reifier as its subject
        Assert.Contains(triples, t =>
            t.Predicate == RdfSubject && t.Object == innerReifier);
    }

    [Fact]
    public async Task MultipleReifiedTriples_HaveDistinctReifiers()
    {
        var turtle = @"
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Bob> >>
                <http://ex.org/confidence> ""0.9"" .
            << <http://ex.org/Alice> <http://ex.org/knows> <http://ex.org/Charlie> >>
                <http://ex.org/confidence> ""0.8"" .
        ";

        var triples = await ParseTurtle(turtle);

        // Find all rdf:Statement type assertions
        var statements = triples.FindAll(t =>
            t.Predicate == RdfType && t.Object == RdfStatement);

        // Should have 2 distinct statements
        Assert.Equal(2, statements.Count);
        Assert.NotEqual(statements[0].Subject, statements[1].Subject);
    }
}
