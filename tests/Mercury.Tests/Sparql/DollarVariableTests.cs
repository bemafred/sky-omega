using System.Linq;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// [108] Var ::= VAR1 | VAR2: <c>?x</c> and <c>$x</c> are one variable (SPARQL 1.1 §19.8). Bindings were keyed by the
/// name as written, sigil included, so <c>$s</c> and <c>?s</c> were two variables that the results then named alike,
/// and most clause parsers did not read <c>$</c> at all. Each case here was wrong or a syntax error on 1.8.26.
/// </summary>
[Collection("QuadStore")]
public class DollarVariableTests : PooledStoreTestBase
{
    public DollarVariableTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Fact]
    public void DollarAndQuestionMark_InTwoTriplePatterns_Join()
    {
        // Only <a> has an <http://ex/q>; as two variables, the patterns were a cross product with 2 rows
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/o1>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "<http://ex/o2>");
        Store.AddCurrent("<http://ex/a>", "<http://ex/q>", "<http://ex/b>");

        var result = SparqlEngine.Query(Store, "SELECT * WHERE { $s <http://ex/p> ?o . ?s <http://ex/q> $t }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.Equal("<http://ex/a>", row["s"]);
        Assert.Equal("<http://ex/b>", row["t"]);
    }

    [Fact]
    public void SelectDollar_ProjectsTheVariable()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/o>");

        var result = SparqlEngine.Query(Store, "SELECT DISTINCT $s WHERE { ?s <http://ex/p> ?o }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(["s"], result.Variables!);
        Assert.Equal("<http://ex/a>", Assert.Single(result.Rows!)["s"]);
    }

    [Fact]
    public void Filter_ReadsADollarVariable()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/o1>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "<http://ex/o2>");

        var result = SparqlEngine.Query(Store,
            "SELECT ?s WHERE { ?s <http://ex/p> ?o FILTER(BOUND($o) && $o = <http://ex/o2>) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("<http://ex/b>", Assert.Single(result.Rows!)["s"]);
    }

    [Fact]
    public void Bind_ToADollarVariable_IsTheQuestionMarkVariable()
    {
        var result = SparqlEngine.Query(Store, "SELECT ?y WHERE { BIND(1 AS ?x) BIND($x + 1 AS $y) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("2", Assert.Single(result.Rows!)["y"]);
    }

    [Fact]
    public void Bind_RebindingAQuestionMarkVariableAsDollar_IsASyntaxError()
    {
        // [60] Bind: the variable must not already be in scope, whatever its sigil
        var result = SparqlEngine.Query(Store, "SELECT * WHERE { BIND(1 AS ?x) BIND(2 AS $x) }",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }

    [Fact]
    public void MinusAndExists_CorrelateOnADollarVariable()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/o1>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "<http://ex/o2>");
        Store.AddCurrent("<http://ex/a>", "<http://ex/q>", "<http://ex/z>");

        var minus = SparqlEngine.Query(Store, "SELECT ?s WHERE { ?s <http://ex/p> ?o MINUS { $s <http://ex/q> ?z } }",
            TestContext.Current.CancellationToken);
        var exists = SparqlEngine.Query(Store,
            "SELECT ?s WHERE { ?s <http://ex/p> ?o FILTER EXISTS { $s <http://ex/q> ?z } }",
            TestContext.Current.CancellationToken);

        Assert.True(minus.Success, minus.ErrorMessage);
        Assert.Equal("<http://ex/b>", Assert.Single(minus.Rows!)["s"]);
        Assert.True(exists.Success, exists.ErrorMessage);
        Assert.Equal("<http://ex/a>", Assert.Single(exists.Rows!)["s"]);
    }

    [Fact]
    public void Construct_TemplateWithDollarVariables_BuildsTriples()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/o>");

        var result = SparqlEngine.Query(Store, "CONSTRUCT { $s <http://ex/r> $o } WHERE { ?s <http://ex/p> ?o }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(("<http://ex/a>", "<http://ex/r>", "<http://ex/o>"), Assert.Single(result.Triples!));
    }

    [Fact]
    public void SolutionModifiers_ReadDollarVariables()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/o1>");
        Store.AddCurrent("<http://ex/b>", "<http://ex/p>", "<http://ex/o2>");
        Store.AddCurrent("<http://ex/c>", "<http://ex/p>", "<http://ex/o2>");

        var ordered = SparqlEngine.Query(Store, "SELECT ?s WHERE { ?s <http://ex/p> ?o } ORDER BY DESC($s)",
            TestContext.Current.CancellationToken);
        var grouped = SparqlEngine.Query(Store,
            "SELECT $o (COUNT($s) AS $n) WHERE { ?s <http://ex/p> ?o } GROUP BY $o HAVING (COUNT(?s) > 1)",
            TestContext.Current.CancellationToken);

        Assert.True(ordered.Success, ordered.ErrorMessage);
        Assert.Equal(["<http://ex/c>", "<http://ex/b>", "<http://ex/a>"], ordered.Rows!.Select(r => r["s"]));
        Assert.True(grouped.Success, grouped.ErrorMessage);
        var row = Assert.Single(grouped.Rows!);
        Assert.Equal("<http://ex/o2>", row["o"]);
        Assert.StartsWith("\"2\"", row["n"]);
    }

    [Fact]
    public void SubSelect_ProjectingADollarAlias_IsSeenByTheOuterQuery()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/o>");

        var result = SparqlEngine.Query(Store,
            "SELECT ?x WHERE { { SELECT ($o AS $x) WHERE { ?s <http://ex/p> ?o } } }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("<http://ex/o>", Assert.Single(result.Rows!)["x"]);
    }

    [Fact]
    public void UpdateTemplate_WithDollarVariables_WritesTheWhereBindings()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/o>");

        var update = SparqlEngine.Update(Store, "INSERT { $s <http://ex/copy> $o } WHERE { ?s <http://ex/p> ?o }",
            TestContext.Current.CancellationToken);
        var result = SparqlEngine.Query(Store, "SELECT ?o WHERE { <http://ex/a> <http://ex/copy> ?o }",
            TestContext.Current.CancellationToken);

        Assert.True(update.Success, update.ErrorMessage);
        Assert.Equal("<http://ex/o>", Assert.Single(result.Rows!)["o"]);
    }
}
