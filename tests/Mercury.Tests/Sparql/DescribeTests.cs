using System.Linq;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// [11] DescribeQuery ::= 'DESCRIBE' ( VarOrIri+ | '*' ) DatasetClause* WhereClause? SolutionModifier (SPARQL 1.1 §16.4).
/// A DESCRIBE describes its IRI targets and the resources its variable targets take in the WHERE's solutions: here, the
/// current triples with the resource as subject or as object. Through the facade it used to run the SELECT path and
/// return each row's first three values as a triple; it never described anything (ADR-055 D3).
/// </summary>
[Collection("QuadStore")]
public class DescribeTests : PooledStoreTestBase
{
    public DescribeTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Fact]
    public void Describe_AnIriWithoutWhere_ReturnsItsTriplesAsSubjectAndObject()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "\"x\"");
        Store.AddCurrent("<http://ex/c>", "<http://ex/r>", "<http://ex/a>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "\"y\"");

        var result = SparqlEngine.Query(Store, "PREFIX ex: <http://ex/> DESCRIBE ex:a",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.Triples!.Count);
        Assert.Contains(("<http://ex/a>", "<http://ex/p>", "\"x\""), result.Triples);
        Assert.Contains(("<http://ex/c>", "<http://ex/r>", "<http://ex/a>"), result.Triples);
    }

    [Theory]
    [InlineData("DESCRIBE ?s WHERE { ?s <http://ex/p> \"y\" }")]
    [InlineData("DESCRIBE ?s { ?s <http://ex/p> \"y\" }")]
    [InlineData("DESCRIBE $s WHERE { ?s <http://ex/p> \"y\" }")]
    [InlineData("DESCRIBE ?s WHERE { VALUES ?s { <http://ex/b> } }")]
    public void Describe_AVariable_DescribesItsValuesInTheSolutions(string query)
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "\"x\"");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "\"y\"");

        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(("<http://ex/b>", "<http://ex/p>", "\"y\""), Assert.Single(result.Triples!));
    }

    [Fact]
    public void Describe_OnlyItsTargetVariables_NotEveryVariable()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/q>", "<http://ex/b>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "\"y\"");

        var targets = SparqlEngine.Query(Store, "DESCRIBE ?o WHERE { <http://ex/a> <http://ex/q> ?o }",
            TestContext.Current.CancellationToken);
        var all = SparqlEngine.Query(Store, "DESCRIBE * WHERE { ?s <http://ex/q> ?o }",
            TestContext.Current.CancellationToken);

        Assert.True(targets.Success, targets.ErrorMessage);
        Assert.Equal(2, targets.Triples!.Count); // <b>'s triples: as subject, and <a> <q> <b>
        Assert.True(all.Success, all.ErrorMessage);
        Assert.Equal(2, all.Triples!.Count);     // <a> and <b>: the triple between them is returned once
    }

    [Fact]
    public void Describe_AppliesTheSolutionModifiers()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "\"x\"");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "\"y\"");

        var result = SparqlEngine.Query(Store, "DESCRIBE ?s WHERE { ?s <http://ex/p> ?o } ORDER BY DESC(?s) LIMIT 1",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("<http://ex/b>", Assert.Single(result.Triples!).Subject);
    }

    [Fact]
    public void Describe_IrisAndVariablesTogether_DescribesEachOnce()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "\"x\"");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "\"y\"");

        var result = SparqlEngine.Query(Store,
            "DESCRIBE <http://ex/a> ?s WHERE { ?s <http://ex/p> ?o }", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(["<http://ex/a>", "<http://ex/b>"], result.Triples!.Select(t => t.Subject));
    }

    [Theory]
    [InlineData("DESCRIBE ?x")]
    [InlineData("DESCRIBE <http://ex/nothing>")]
    [InlineData("DESCRIBE ?o WHERE { ?s <http://ex/p> ?o }")] // a literal is not described
    public void Describe_NothingToDescribe_ReturnsNoTriples(string query)
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "\"x\"");

        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.Triples!);
    }

    [Fact]
    public void Describe_ALongLiteral_IsReturnedWhole()
    {
        var literal = "\"" + new string('z', 5000) + "\"";
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", literal);

        var result = SparqlEngine.Query(Store, "DESCRIBE <http://ex/a>", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(literal, Assert.Single(result.Triples!).Object);
    }

    [Fact]
    public void Describe_ReadsTheDefaultAndEveryNamedGraph()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "\"default\"");
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "\"in g1\"", "<http://ex/g1>");
        Store.AddCurrent("<http://ex/c>", "<http://ex/r>", "<http://ex/a>", "<http://ex/g2>");

        var result = SparqlEngine.Query(Store, "DESCRIBE <http://ex/a>", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(3, result.Triples!.Count);
        Assert.Contains(("<http://ex/a>", "<http://ex/p>", "\"default\""), result.Triples);
        Assert.Contains(("<http://ex/a>", "<http://ex/p>", "\"in g1\""), result.Triples);
        Assert.Contains(("<http://ex/c>", "<http://ex/r>", "<http://ex/a>"), result.Triples);
    }

    [Fact]
    public void Describe_ATripleInTwoGraphs_IsReturnedOnce()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "\"x\"");
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "\"x\"", "<http://ex/g1>");

        var result = SparqlEngine.Query(Store, "DESCRIBE <http://ex/a>", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Single(result.Triples!);
    }

    [Fact]
    public void Describe_FromNamesTheGraphsItReads()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "\"default\"");
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "\"in g1\"", "<http://ex/g1>");
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "\"in g2\"", "<http://ex/g2>");

        var result = SparqlEngine.Query(Store, "DESCRIBE <http://ex/a> FROM <http://ex/g1>",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(("<http://ex/a>", "<http://ex/p>", "\"in g1\""), Assert.Single(result.Triples!));
    }

    [Fact]
    public void Describe_AsOf_ReadsTheTriplesValidThen()
    {
        Store.Add("<http://ex/a>", "<http://ex/worksFor>", "<http://ex/Acme>",
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2023, 6, 30, 0, 0, 0, TimeSpan.Zero),
            "<http://ex/g>");
        Store.Add("<http://ex/a>", "<http://ex/worksFor>", "<http://ex/Other>",
            new DateTimeOffset(2023, 7, 1, 0, 0, 0, TimeSpan.Zero), DateTimeOffset.MaxValue, "<http://ex/g>");

        var result = SparqlEngine.Query(Store,
            "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> DESCRIBE <http://ex/a> AS OF \"2021-06-15\"^^xsd:date",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(("<http://ex/a>", "<http://ex/worksFor>", "<http://ex/Acme>"), Assert.Single(result.Triples!));
    }

    [Fact]
    public void Describe_APrefixedNameThatStartsLikeAKeyword_IsATarget()
    {
        // The target list used to end at any token starting with ORDER, VALUES, FROM, …
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "\"x\"");

        var result = SparqlEngine.Query(Store, "PREFIX orders: <http://ex/> DESCRIBE orders:a",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(("<http://ex/a>", "<http://ex/p>", "\"x\""), Assert.Single(result.Triples!));
    }
}
