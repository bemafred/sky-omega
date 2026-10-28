using System.Linq;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// SPARQL 1.1 §18.2.1: the in-scope variables of a WHERE, read from its pattern tree (ADR-055 D7). They are the
/// variables of a <c>SELECT *</c>, including variables no row binds; the variables a sub-SELECT * passes out;
/// the variables <c>DESCRIBE *</c> describes; and the variables a BIND may not rebind. On 1.8.26 the
/// <c>SELECT *</c> header listed only variables some row bound, and the BIND check missed VALUES and SERVICE.
/// </summary>
[Collection("QuadStore")]
public class InScopeVariablesTests : PooledStoreTestBase
{
    public InScopeVariablesTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Fact]
    public void SelectAll_ListsAnOptionalVariableNoRowBinds()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/o>");

        var result = SparqlEngine.Query(Store,
            "SELECT * WHERE { ?s <http://ex/p> ?o OPTIONAL { ?s <http://ex/label> ?label } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(["s", "o", "label"], result.Variables!);
        var row = Assert.Single(result.Rows!);
        Assert.False(row.ContainsKey("label"));
    }

    [Fact]
    public void SelectAll_OverNoSolutions_StillListsTheVariables()
    {
        var result = SparqlEngine.Query(Store, "SELECT * WHERE { ?s <http://ex/p> ?o }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(["s", "o"], result.Variables!);
        Assert.Empty(result.Rows!);
    }

    [Fact]
    public void SelectAll_OverTheEmptyGroup_ListsNoVariables()
    {
        var result = SparqlEngine.Query(Store, "SELECT * {}", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.Variables!);
        Assert.Empty(Assert.Single(result.Rows!));
    }

    [Fact]
    public void SelectAll_LeavesOutMinusFilterAndExistsVariables()
    {
        // MINUS and EXISTS bodies, and FILTER expressions, bind nothing outside themselves
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/o>");

        var result = SparqlEngine.Query(Store,
            "SELECT * WHERE { ?s <http://ex/p> ?o MINUS { ?s <http://ex/q> ?m } " +
            "FILTER(!BOUND(?f)) FILTER NOT EXISTS { ?s <http://ex/r> ?e } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(["s", "o"], result.Variables!);
        Assert.Single(result.Rows!);
    }

    [Fact]
    public void SelectAll_ListsGraphBindAndValuesVariables_InTheOrderTheWhereHasThem()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/o>", "<http://ex/g>");

        var result = SparqlEngine.Query(Store,
            "SELECT * WHERE { VALUES ?v { 1 } GRAPH ?g { ?s <http://ex/p> ?o } BIND(2 AS ?b) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(["v", "g", "s", "o", "b"], result.Variables!);
        Assert.Single(result.Rows!);
    }

    [Fact]
    public void SelectAll_ListsTheTrailingValuesVariables()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/o>");

        var result = SparqlEngine.Query(Store, "SELECT * WHERE { ?s <http://ex/p> ?o } VALUES ?v { UNDEF }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(["s", "o", "v"], result.Variables!);
        Assert.Single(result.Rows!);
    }

    [Fact]
    public void SelectAll_ListsWhatASubSelectProjects_NotItsOtherVariables()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/o>");

        var result = SparqlEngine.Query(Store,
            "SELECT * WHERE { { SELECT ?s (1 AS ?one) WHERE { ?s <http://ex/p> ?o } } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(["s", "one"], result.Variables!);
        var row = Assert.Single(result.Rows!);
        Assert.Equal("<http://ex/a>", row["s"]);
    }

    [Fact]
    public void SubSelectAll_PassesOutItsWhereVariables_AndItsTrailingValues()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/o>");

        var result = SparqlEngine.Query(Store,
            "SELECT ?o ?v WHERE { { SELECT * WHERE { ?s <http://ex/p> ?o } VALUES ?v { \"x\" } } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.Equal("<http://ex/o>", row["o"]);
        Assert.Equal("\"x\"", row["v"]);
    }

    [Fact]
    public void SelectDistinctAll_RowsDifferingOnlyInAPatternBlankNode_AreOneRow()
    {
        // A blank node in a pattern is not a variable: it is not projected, so it does not tell rows apart
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "_:b1");
        Store.AddCurrent("_:b1", "<http://ex/q>", "<http://ex/c>");
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "_:b2");
        Store.AddCurrent("_:b2", "<http://ex/q>", "<http://ex/c>");

        var result = SparqlEngine.Query(Store,
            "SELECT DISTINCT * WHERE { ?s <http://ex/p> [ <http://ex/q> ?o ] }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(["s", "o"], result.Variables!);
        var row = Assert.Single(result.Rows!);
        Assert.Equal("<http://ex/c>", row["o"]);
    }

    [Fact]
    public void SubSelectDistinctAll_RowsDifferingOnlyInAPatternBlankNode_AreOneRow()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "_:b1");
        Store.AddCurrent("_:b1", "<http://ex/q>", "<http://ex/c>");
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "_:b2");
        Store.AddCurrent("_:b2", "<http://ex/q>", "<http://ex/c>");

        var result = SparqlEngine.Query(Store,
            "SELECT ?s ?o WHERE { { SELECT DISTINCT * WHERE { ?s <http://ex/p> [ <http://ex/q> ?o ] } } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.Equal("<http://ex/a>", row["s"]);
    }

    [Fact]
    public void DescribeAll_DescribesTheVariables_NotAPatternBlankNode()
    {
        // _:b matches the pattern's [ ] but is not a variable, so its own triple "_:b <r> x" is not returned
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "_:b");
        Store.AddCurrent("_:b", "<http://ex/q>", "<http://ex/c>");
        Store.AddCurrent("_:b", "<http://ex/r>", "\"x\"");

        var result = SparqlEngine.Query(Store, "DESCRIBE * WHERE { ?s <http://ex/p> [ <http://ex/q> ?o ] }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.Triples!.Count);
        Assert.Contains(result.Triples!, t => t.Subject == "<http://ex/a>" && t.Predicate == "<http://ex/p>");
        Assert.Contains(result.Triples!, t => t.Predicate == "<http://ex/q>" && t.Object == "<http://ex/c>");
        Assert.DoesNotContain(result.Triples!, t => t.Predicate == "<http://ex/r>");
    }

    [Fact]
    public void Bind_RebindingAValuesVariable_IsASyntaxError()
    {
        var result = SparqlEngine.Query(Store, "SELECT * WHERE { VALUES ?x { 1 } BIND(2 AS ?x) }",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("already in scope", result.ErrorMessage);
    }

    [Fact]
    public void Bind_RebindingAServiceVariable_IsASyntaxError()
    {
        // Rejected by the parser, so the endpoint is never called
        var result = SparqlEngine.Query(Store,
            "SELECT * WHERE { SERVICE <http://example.invalid/sparql> { ?x <http://ex/p> ?o } BIND(2 AS ?x) }",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("already in scope", result.ErrorMessage);
    }

    [Fact]
    public void Bind_AVariableOnlyInAnEarlierMinusOrFilter_IsAllowed()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/o>");

        var result = SparqlEngine.Query(Store,
            "SELECT ?m WHERE { ?s <http://ex/p> ?o MINUS { ?s <http://ex/q> ?m } FILTER(!BOUND(?f)) BIND(3 AS ?m) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("3", Assert.Single(result.Rows!)["m"]);
    }
}
