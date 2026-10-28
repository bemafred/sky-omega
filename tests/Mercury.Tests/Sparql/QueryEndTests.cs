using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// A query ends after its [28] ValuesClause: [2] Query ::= Prologue ( SelectQuery | … ) ValuesClause. The parser
/// used to stop where the query form ended and ignore the rest, so a VALUES after ASK or CONSTRUCT, a stray '}' or
/// any other text was dropped without an error.
/// </summary>
[Collection("QuadStore")]
public class QueryEndTests : PooledStoreTestBase
{
    public QueryEndTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData("SELECT * WHERE { ?s ?p ?o } garbage")]
    [InlineData("SELECT * WHERE { ?s ?p ?o } LIMIT 1 }")]
    [InlineData("ASK { ?s ?p ?o } }")]
    [InlineData("CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o } ;")]
    [InlineData("SELECT * WHERE { } VALUES ?x { 1 } VALUES ?y { 2 }")]
    public void Query_WithTextAfterItsEnd_IsASyntaxError(string query)
    {
        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }

    [Fact]
    public void Query_EndingInACommentOrWhitespace_Parses()
    {
        var result = SparqlEngine.Query(Store, "SELECT * WHERE { BIND(1 AS ?x) } # done\n  \n",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Single(result.Rows!);
    }

    [Fact]
    public void Ask_WithASolutionModifier_Parses()
    {
        // [12] AskQuery ::= 'ASK' DatasetClause* WhereClause SolutionModifier
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");

        var result = SparqlEngine.Query(Store, "ASK { ?s ?p ?o } ORDER BY ?s LIMIT 1",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.AskResult);
    }

    [Theory]
    [InlineData("DESCRIBE ?s WHERE { ?s ?p ?o }")]
    [InlineData("DESCRIBE <http://ex/s>")]
    [InlineData("DESCRIBE ?s <http://ex/s> WHERE { ?s ?p ?o } LIMIT 1")]
    [InlineData("DESCRIBE <http://ex/s> VALUES ?x { 1 }")]
    [InlineData("DESCRIBE *  WHERE { ?s ?p ?o }")]
    public void Describe_ReadsItsTargets(string query)
    {
        // [11] DescribeQuery ::= 'DESCRIBE' ( VarOrIri+ | '*' ) DatasetClause* WhereClause? SolutionModifier
        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
    }

    [Theory]
    [InlineData("DESCRIBE WHERE { ?s ?p ?o }")]
    [InlineData("DESCRIBE \"literal\"")]
    public void Describe_WithoutAVariableOrIri_IsASyntaxError(string query)
    {
        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }
}
