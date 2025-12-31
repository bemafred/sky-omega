using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;

namespace Mercury.Tests;

/// <summary>
/// Tests for Temporal SPARQL extensions: AS OF, DURING, ALL VERSIONS
/// </summary>
public class TemporalSparqlTests : IDisposable
{
    private readonly string _tempDir;
    private readonly QuadStore _store;

    public TemporalSparqlTests()
    {
        _tempDir = TempPath.Test("temporal-sparql");
        Directory.CreateDirectory(_tempDir);
        _store = new QuadStore(_tempDir);

        // Add test data with different valid times
        // Alice worked at Acme from 2020-01-01 to 2023-06-30
        _store.Add(
            "<http://ex.org/alice>",
            "<http://ex.org/worksFor>",
            "<http://ex.org/Acme>",
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2023, 6, 30, 0, 0, 0, TimeSpan.Zero));

        // Alice works at Anthropic from 2023-07-01 to infinity
        _store.Add(
            "<http://ex.org/alice>",
            "<http://ex.org/worksFor>",
            "<http://ex.org/Anthropic>",
            new DateTimeOffset(2023, 7, 1, 0, 0, 0, TimeSpan.Zero),
            DateTimeOffset.MaxValue);

        // Bob worked at Acme from 2019-01-01 to 2022-12-31
        _store.Add(
            "<http://ex.org/bob>",
            "<http://ex.org/worksFor>",
            "<http://ex.org/Acme>",
            new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2022, 12, 31, 0, 0, 0, TimeSpan.Zero));
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void Parse_AsOf_ExtractsTimestamp()
    {
        var query = "SELECT * WHERE { ?s ?p ?o } AS OF \"2021-06-15\"^^xsd:date";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        Assert.Equal(TemporalQueryMode.AsOf, parsed.SolutionModifier.Temporal.Mode);
        Assert.True(parsed.SolutionModifier.Temporal.TimeStartLength > 0);

        // Verify the literal was captured
        var literal = query.AsSpan().Slice(
            parsed.SolutionModifier.Temporal.TimeStartStart,
            parsed.SolutionModifier.Temporal.TimeStartLength);
        Assert.Contains("2021-06-15", literal.ToString());
    }

    [Fact]
    public void Parse_During_ExtractsRange()
    {
        var query = "SELECT * WHERE { ?s ?p ?o } DURING [\"2023-01-01\"^^xsd:date, \"2023-12-31\"^^xsd:date]";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        Assert.Equal(TemporalQueryMode.During, parsed.SolutionModifier.Temporal.Mode);
        Assert.True(parsed.SolutionModifier.Temporal.TimeStartLength > 0);
        Assert.True(parsed.SolutionModifier.Temporal.TimeEndLength > 0);

        var startLiteral = query.AsSpan().Slice(
            parsed.SolutionModifier.Temporal.TimeStartStart,
            parsed.SolutionModifier.Temporal.TimeStartLength);
        Assert.Contains("2023-01-01", startLiteral.ToString());

        var endLiteral = query.AsSpan().Slice(
            parsed.SolutionModifier.Temporal.TimeEndStart,
            parsed.SolutionModifier.Temporal.TimeEndLength);
        Assert.Contains("2023-12-31", endLiteral.ToString());
    }

    [Fact]
    public void Parse_AllVersions_SetsMode()
    {
        var query = "SELECT * WHERE { ?s ?p ?o } ALL VERSIONS";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        Assert.Equal(TemporalQueryMode.AllVersions, parsed.SolutionModifier.Temporal.Mode);
    }

    [Fact]
    public void Parse_NoTemporalClause_DefaultsToCurrent()
    {
        var query = "SELECT * WHERE { ?s ?p ?o }";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        Assert.Equal(TemporalQueryMode.Current, parsed.SolutionModifier.Temporal.Mode);
    }

    [Fact]
    public void AsOf_ReturnsDataValidAtTime_MidPeriod()
    {
        // Query: Who worked where on June 15, 2021?
        // Expected: Alice at Acme, Bob at Acme
        var query = "SELECT ?person ?company WHERE { ?person <http://ex.org/worksFor> ?company } AS OF \"2021-06-15\"^^xsd:date";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsed);
            var results = executor.Execute();

            var bindings = new List<(string person, string company)>();
            while (results.MoveNext())
            {
                var b = results.Current;
                var personIdx = b.FindBinding("?person".AsSpan());
                var companyIdx = b.FindBinding("?company".AsSpan());
                if (personIdx >= 0 && companyIdx >= 0)
                {
                    bindings.Add((b.GetString(personIdx).ToString(), b.GetString(companyIdx).ToString()));
                }
            }
            results.Dispose();

            Assert.Equal(2, bindings.Count);
            Assert.Contains(bindings, x => x.person.Contains("alice") && x.company.Contains("Acme"));
            Assert.Contains(bindings, x => x.person.Contains("bob") && x.company.Contains("Acme"));
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void AsOf_ReturnsDataValidAtTime_AfterChange()
    {
        // Query: Who worked where on August 1, 2023?
        // Expected: Alice at Anthropic (Bob no longer works anywhere)
        var query = "SELECT ?person ?company WHERE { ?person <http://ex.org/worksFor> ?company } AS OF \"2023-08-01\"^^xsd:date";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsed);
            var results = executor.Execute();

            var bindings = new List<(string person, string company)>();
            while (results.MoveNext())
            {
                var b = results.Current;
                var personIdx = b.FindBinding("?person".AsSpan());
                var companyIdx = b.FindBinding("?company".AsSpan());
                if (personIdx >= 0 && companyIdx >= 0)
                {
                    bindings.Add((b.GetString(personIdx).ToString(), b.GetString(companyIdx).ToString()));
                }
            }
            results.Dispose();

            Assert.Single(bindings);
            Assert.Contains(bindings, x => x.person.Contains("alice") && x.company.Contains("Anthropic"));
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void AllVersions_ReturnsCompleteHistory()
    {
        // Query: All employment history for Alice
        // Expected: 2 records (Acme and Anthropic)
        var query = "SELECT ?company WHERE { <http://ex.org/alice> <http://ex.org/worksFor> ?company } ALL VERSIONS";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsed);
            var results = executor.Execute();

            var companies = new List<string>();
            while (results.MoveNext())
            {
                var b = results.Current;
                var companyIdx = b.FindBinding("?company".AsSpan());
                if (companyIdx >= 0)
                {
                    companies.Add(b.GetString(companyIdx).ToString());
                }
            }
            results.Dispose();

            Assert.Equal(2, companies.Count);
            Assert.Contains(companies, c => c.Contains("Acme"));
            Assert.Contains(companies, c => c.Contains("Anthropic"));
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void During_ReturnsChangesInPeriod()
    {
        // Query: What employment changes happened in 2023?
        // Expected: Alice left Acme (ended 2023-06-30), Alice joined Anthropic (started 2023-07-01)
        var query = "SELECT ?person ?company WHERE { ?person <http://ex.org/worksFor> ?company } DURING [\"2023-01-01\"^^xsd:date, \"2023-12-31\"^^xsd:date]";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        _store.AcquireReadLock();
        try
        {
            using var executor = new QueryExecutor(_store, query.AsSpan(), parsed);
            var results = executor.Execute();

            var bindings = new List<(string person, string company)>();
            while (results.MoveNext())
            {
                var b = results.Current;
                var personIdx = b.FindBinding("?person".AsSpan());
                var companyIdx = b.FindBinding("?company".AsSpan());
                if (personIdx >= 0 && companyIdx >= 0)
                {
                    bindings.Add((b.GetString(personIdx).ToString(), b.GetString(companyIdx).ToString()));
                }
            }
            results.Dispose();

            // Should include Alice's employment at both Acme and Anthropic (overlapping with 2023)
            Assert.Contains(bindings, x => x.person.Contains("alice") && x.company.Contains("Acme"));
            Assert.Contains(bindings, x => x.person.Contains("alice") && x.company.Contains("Anthropic"));
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    [Fact]
    public void TemporalWithLimitOffset_ParsesCorrectly()
    {
        // Verify that temporal clauses can be combined with LIMIT/OFFSET
        var query = "SELECT ?company WHERE { <http://ex.org/alice> <http://ex.org/worksFor> ?company } LIMIT 10 OFFSET 5 ALL VERSIONS";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        Assert.Equal(TemporalQueryMode.AllVersions, parsed.SolutionModifier.Temporal.Mode);
        Assert.Equal(10, parsed.SolutionModifier.Limit);
        Assert.Equal(5, parsed.SolutionModifier.Offset);
    }

    [Fact]
    public void AsOfWithDateTime_ParsesCorrectly()
    {
        var query = "SELECT * WHERE { ?s ?p ?o } AS OF \"2023-06-15T14:30:00Z\"^^xsd:dateTime";
        var parser = new SparqlParser(query.AsSpan());
        var parsed = parser.ParseQuery();

        Assert.Equal(TemporalQueryMode.AsOf, parsed.SolutionModifier.Temporal.Mode);

        var literal = query.AsSpan().Slice(
            parsed.SolutionModifier.Temporal.TimeStartStart,
            parsed.SolutionModifier.Temporal.TimeStartLength);
        Assert.Contains("2023-06-15T14:30:00Z", literal.ToString());
    }
}
