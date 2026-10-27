using System.Linq;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// Every VALUES, inside a WHERE, after a query and after a sub-SELECT, is [62] DataBlock, read from the source by one
/// reader that the parser checks it with and the executor binds it with. Before, three copies of a parser filled a
/// struct that held 4 variables and 16 values and dropped the rest without an error.
/// </summary>
[Collection("QuadStore")]
public class DataBlockTests : PooledStoreTestBase
{
    public DataBlockTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData("SELECT ?x WHERE { VALUES ?x { $values } }")]
    [InlineData("SELECT ?x WHERE { } VALUES ?x { $values }")]
    [InlineData("SELECT ?x WHERE { { SELECT ?x WHERE { } VALUES ?x { $values } } }")]
    public void Values_With100Values_ReturnsEveryRow(string shape)
    {
        var values = string.Join(" ", Enumerable.Range(1, 100));

        var result = SparqlEngine.Query(Store, shape.Replace("$values", values), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(100, result.Rows!.Count);
    }

    [Fact]
    public void Values_WithSixVariables_BindsEach()
    {
        var result = SparqlEngine.Query(Store,
            "SELECT * WHERE { VALUES (?a ?b ?c ?d ?e ?f) { (1 2 3 4 5 6) (7 8 9 10 11 12) } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.Rows!.Count);
        Assert.Contains(result.Rows, r => r.Count == 6 && r["f"].StartsWith("\"12\""));
    }

    [Fact]
    public void Values_Empty_AfterAQuery_LeavesNoSolutions()
    {
        // Joining with no data rows gives no solutions; the empty clause used to be ignored
        var result = SparqlEngine.Query(Store, "SELECT * WHERE { BIND(1 AS ?y) } VALUES ?x { }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.Rows!);
    }

    [Fact]
    public void Values_ExponentAndSingleQuotedValues_AreDataBlockValues()
    {
        // [65] DataBlockValue takes any NumericLiteral and RDFLiteral, as a triple pattern's object does
        var result = SparqlEngine.Query(Store, "SELECT ?x WHERE { VALUES ?x { 1e3 'single' \"\"\"long\"\"\" } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(3, result.Rows!.Count);
    }

    [Fact]
    public void Values_DollarVariable_JoinsWithTheQuestionMarkVariable()
    {
        // $x and ?x are one variable (SPARQL 1.1 §19.8 VAR1/VAR2)
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");

        var result = SparqlEngine.Query(Store,
            "SELECT ?o WHERE { ?s <http://ex/p> ?o VALUES $o { <http://ex/o> <http://ex/other> } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.Equal("<http://ex/o>", row["o"]);
    }

    [Theory]
    [InlineData("SELECT ?x WHERE { VALUES ?x 1 }")]                    // no '{'
    [InlineData("SELECT ?x WHERE { VALUES { 1 } }")]                   // no variable
    [InlineData("SELECT ?x WHERE { } VALUES ?x { 1 2")]                // no '}'
    [InlineData("SELECT ?x WHERE { VALUES ?x { _:b } }")]              // a blank node is not a DataBlockValue
    [InlineData("SELECT ?x WHERE { VALUES ?x { ?y } }")]               // nor is a variable
    [InlineData("SELECT * WHERE { VALUES (?x ?y) { (1) } }")]          // too few values in a row
    [InlineData("SELECT * WHERE { VALUES (?x ?y) { (1 2 3) } }")]      // too many
    [InlineData("SELECT * WHERE { VALUES (?x ?y) { 1 2 } }")]          // a full block's rows are parenthesized
    public void Values_Malformed_IsASyntaxError(string query)
    {
        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }
}
