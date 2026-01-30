// Licensed under the MIT License.

using System.Globalization;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Infrastructure;

/// <summary>
/// Tests that verify culture-invariant behavior for numeric formatting.
/// These tests force Swedish culture (sv-SE) which uses comma as decimal separator,
/// ensuring RDF/SPARQL output uses period regardless of system locale.
/// </summary>
public class CultureInvarianceTests : IClassFixture<QuadStorePoolFixture>
{
    private readonly QuadStorePoolFixture _fixture;

    public CultureInvarianceTests(QuadStorePoolFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void CastToDouble_WithSwedishCulture_UsesPeriodDecimalSeparator()
    {
        RunWithSwedishCulture(() =>
        {
            using var lease = _fixture.Pool.RentScoped();
            var store = lease.Store;

            // Add test data
            store.AddCurrent(
                "<http://example.org/x>".AsSpan(),
                "<http://example.org/value>".AsSpan(),
                "\"3.14\"^^<http://www.w3.org/2001/XMLSchema#string>".AsSpan());

            // Query with CAST to double
            var query = "SELECT (xsd:double(?v) AS ?d) WHERE { <http://example.org/x> <http://example.org/value> ?v }";
            var parser = new SparqlParser(query.AsSpan());
            var parsedQuery = parser.ParseQuery();

            store.AcquireReadLock();
            try
            {
                var executor = new QueryExecutor(store, query.AsSpan(), parsedQuery);
                var results = executor.Execute();

                try
                {
                    Assert.True(results.MoveNext(), "Should have one result");
                    var value = results.Current.GetString(0).ToString();

                    // The value should contain period, not comma
                    Assert.DoesNotContain(",", value);
                    Assert.Contains(".", value);
                }
                finally
                {
                    results.Dispose();
                }
            }
            finally
            {
                store.ReleaseReadLock();
            }
        });
    }

    [Fact]
    public void CastToDecimal_WithSwedishCulture_UsesPeriodDecimalSeparator()
    {
        RunWithSwedishCulture(() =>
        {
            using var lease = _fixture.Pool.RentScoped();
            var store = lease.Store;

            store.AddCurrent(
                "<http://example.org/x>".AsSpan(),
                "<http://example.org/value>".AsSpan(),
                "\"2.5\"^^<http://www.w3.org/2001/XMLSchema#string>".AsSpan());

            var query = "SELECT (xsd:decimal(?v) AS ?d) WHERE { <http://example.org/x> <http://example.org/value> ?v }";
            var parser = new SparqlParser(query.AsSpan());
            var parsedQuery = parser.ParseQuery();

            store.AcquireReadLock();
            try
            {
                var executor = new QueryExecutor(store, query.AsSpan(), parsedQuery);
                var results = executor.Execute();

                try
                {
                    Assert.True(results.MoveNext(), "Should have one result");
                    var value = results.Current.GetString(0).ToString();

                    Assert.DoesNotContain(",", value);
                    Assert.Contains(".", value);
                }
                finally
                {
                    results.Dispose();
                }
            }
            finally
            {
                store.ReleaseReadLock();
            }
        });
    }

    [Fact]
    public void AvgAggregate_WithSwedishCulture_UsesPeriodDecimalSeparator()
    {
        RunWithSwedishCulture(() =>
        {
            using var lease = _fixture.Pool.RentScoped();
            var store = lease.Store;

            // Add test data with numeric values
            store.AddCurrent(
                "<http://example.org/x1>".AsSpan(),
                "<http://example.org/value>".AsSpan(),
                "\"10\"^^<http://www.w3.org/2001/XMLSchema#integer>".AsSpan());
            store.AddCurrent(
                "<http://example.org/x2>".AsSpan(),
                "<http://example.org/value>".AsSpan(),
                "\"20\"^^<http://www.w3.org/2001/XMLSchema#integer>".AsSpan());
            store.AddCurrent(
                "<http://example.org/x3>".AsSpan(),
                "<http://example.org/value>".AsSpan(),
                "\"30\"^^<http://www.w3.org/2001/XMLSchema#integer>".AsSpan());

            // AVG of 10, 20, 30 = 20.0
            var query = "SELECT (AVG(?v) AS ?avg) WHERE { ?x <http://example.org/value> ?v }";
            var parser = new SparqlParser(query.AsSpan());
            var parsedQuery = parser.ParseQuery();

            store.AcquireReadLock();
            try
            {
                var executor = new QueryExecutor(store, query.AsSpan(), parsedQuery);
                var results = executor.Execute();

                try
                {
                    Assert.True(results.MoveNext(), "Should have one result");
                    var value = results.Current.GetString(0).ToString();

                    // AVG result should not contain comma
                    Assert.DoesNotContain(",", value);
                }
                finally
                {
                    results.Dispose();
                }
            }
            finally
            {
                store.ReleaseReadLock();
            }
        });
    }

    [Fact]
    public void BindWithDivision_WithSwedishCulture_UsesPeriodDecimalSeparator()
    {
        RunWithSwedishCulture(() =>
        {
            using var lease = _fixture.Pool.RentScoped();
            var store = lease.Store;

            store.AddCurrent(
                "<http://example.org/x>".AsSpan(),
                "<http://example.org/a>".AsSpan(),
                "\"5\"^^<http://www.w3.org/2001/XMLSchema#integer>".AsSpan());
            store.AddCurrent(
                "<http://example.org/x>".AsSpan(),
                "<http://example.org/b>".AsSpan(),
                "\"2\"^^<http://www.w3.org/2001/XMLSchema#integer>".AsSpan());

            // 5 / 2 = 2.5
            var query = "SELECT ?result WHERE { ?x <http://example.org/a> ?a . ?x <http://example.org/b> ?b . BIND(?a / ?b AS ?result) }";
            var parser = new SparqlParser(query.AsSpan());
            var parsedQuery = parser.ParseQuery();

            store.AcquireReadLock();
            try
            {
                var executor = new QueryExecutor(store, query.AsSpan(), parsedQuery);
                var results = executor.Execute();

                try
                {
                    Assert.True(results.MoveNext(), "Should have one result");
                    var value = results.Current.GetString(0).ToString();

                    // Division result should use period
                    Assert.DoesNotContain(",", value);
                    Assert.Contains(".", value);
                }
                finally
                {
                    results.Dispose();
                }
            }
            finally
            {
                store.ReleaseReadLock();
            }
        });
    }

    [Fact]
    public void TypedLiteralFormatting_WithSwedishCulture_ProducesValidRdf()
    {
        RunWithSwedishCulture(() =>
        {
            using var lease = _fixture.Pool.RentScoped();
            var store = lease.Store;

            store.AddCurrent(
                "<http://example.org/x>".AsSpan(),
                "<http://example.org/value>".AsSpan(),
                "\"3.14159\"^^<http://www.w3.org/2001/XMLSchema#double>".AsSpan());

            // Query that uses the double value in a pattern match
            var query = "SELECT ?x WHERE { ?x <http://example.org/value> ?v . FILTER(?v > 3.0) }";
            var parser = new SparqlParser(query.AsSpan());
            var parsedQuery = parser.ParseQuery();

            store.AcquireReadLock();
            try
            {
                var executor = new QueryExecutor(store, query.AsSpan(), parsedQuery);
                var results = executor.Execute();

                try
                {
                    Assert.True(results.MoveNext(), "Should match the triple");
                    var subject = results.Current.GetString(0).ToString();
                    Assert.Equal("<http://example.org/x>", subject);
                }
                finally
                {
                    results.Dispose();
                }
            }
            finally
            {
                store.ReleaseReadLock();
            }
        });
    }

    /// <summary>
    /// Executes an action with Swedish culture temporarily set.
    /// Swedish uses comma as decimal separator, making it ideal for testing culture invariance.
    /// </summary>
    private static void RunWithSwedishCulture(Action action)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUICulture = CultureInfo.CurrentUICulture;

        try
        {
            var swedishCulture = new CultureInfo("sv-SE");
            CultureInfo.CurrentCulture = swedishCulture;
            CultureInfo.CurrentUICulture = swedishCulture;

            // Verify we're actually in Swedish culture
            Assert.Equal(",", swedishCulture.NumberFormat.NumberDecimalSeparator);

            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUICulture;
        }
    }
}
