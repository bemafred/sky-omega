using System.Linq;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// A blank node is its own kind of RDF term in an expression (SPARQL 1.1 §17.4.2): not an IRI and not a literal.
/// Through 1.8.28 the evaluator read a bound blank node as a string literal: isBlank was false for every stored blank
/// node, isLiteral was true, and STR returned its label where it is an error.
/// </summary>
[Collection("QuadStore")]
public class BlankNodeTermTests : PooledStoreTestBase
{
    public BlankNodeTermTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData("isBlank(?o)", new[] { "_:n" })]
    [InlineData("!isBlank(?o)", new[] { "<http://ex/i>", "\"lit\"" })]
    [InlineData("isLiteral(?o)", new[] { "\"lit\"" })]
    [InlineData("isIRI(?o)", new[] { "<http://ex/i>" })]
    [InlineData("isIRI(?o) || isBlank(?o)", new[] { "<http://ex/i>", "_:n" })]
    public void TermTests_TellABlankNodeApart(string test, string[] expected)
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "_:n");
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "<http://ex/i>");
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "\"lit\"");

        var result = SparqlEngine.Query(Store, $"SELECT ?o WHERE {{ ?s <http://ex/p> ?o FILTER({test}) }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var objects = result.Rows!.Select(r => r["o"]).ToList();
        Assert.Equal(expected.Length, objects.Count);
        Assert.All(expected, e => Assert.Contains(objects, o => e == "_:n" ? o.StartsWith("_:") : o == e));
    }

    [Fact]
    public void Str_OfABlankNode_IsAnError()
    {
        // An error leaves BIND's variable unbound
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "_:n");

        var result = SparqlEngine.Query(Store, "SELECT ?s ?x WHERE { ?s <http://ex/p> ?o BIND(STR(?o) AS ?x) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(result.Rows!);
        Assert.False(row.ContainsKey("x"));
    }

    [Fact]
    public void EqualityAndSameTerm_OnBlankNodes_CompareTheNode()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "_:n");
        Store.AddCurrent("<http://ex/b>", "<http://ex/q>", "_:n");
        Store.AddCurrent("<http://ex/c>", "<http://ex/q>", "_:m");

        var equal = SparqlEngine.Query(Store,
            "SELECT ?t WHERE { ?s <http://ex/p> ?o . ?t <http://ex/q> ?u FILTER(?o = ?u) }", TestContext.Current.CancellationToken);
        var same = SparqlEngine.Query(Store,
            "SELECT ?t WHERE { ?s <http://ex/p> ?o . ?t <http://ex/q> ?u FILTER(sameTerm(?o, ?u)) }", TestContext.Current.CancellationToken);

        Assert.True(equal.Success, equal.ErrorMessage);
        Assert.Equal("<http://ex/b>", Assert.Single(equal.Rows!)["t"]);
        Assert.Equal("<http://ex/b>", Assert.Single(same.Rows!)["t"]);
    }

    [Fact]
    public void ABlankNodeThroughBindAndCoalesce_StaysABlankNode()
    {
        Store.AddCurrent("<http://ex/a>", "<http://ex/p>", "_:n");

        var result = SparqlEngine.Query(Store,
            "SELECT ?s WHERE { ?s <http://ex/p> ?o BIND(COALESCE(?o, 1) AS ?b) FILTER(isBlank(?b)) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("<http://ex/a>", Assert.Single(result.Rows!)["s"]);
    }
}
