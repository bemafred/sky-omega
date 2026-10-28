using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// The group grammar's separators and term positions ([54] GroupGraphPatternSub, [55] TriplesBlock, [58]
/// GraphGraphPattern, [78] Verb). Through 1.8.30 the body loop skipped every '.', so a missing, leading or doubled '.'
/// was accepted, and a blank node or literal was accepted as a predicate or a GRAPH name (W3C syntax-sparql3
/// syn-bad-02 to syn-bad-14, syn-bad-27, syn-bad-28, syn-bad-31).
/// </summary>
[Collection("QuadStore")]
public class PatternSyntaxTests : PooledStoreTestBase
{
    public PatternSyntaxTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData("SELECT * WHERE { ?s ?p ?o ?s1 ?p1 ?o1 }")]
    [InlineData("SELECT * WHERE { . }")]
    [InlineData("SELECT * WHERE { . ?s ?p ?o }")]
    [InlineData("SELECT * WHERE { ?s ?p ?o . . }")]
    [InlineData("SELECT * WHERE { ?s ?p ?o .. ?s1 ?p1 ?o1 }")]
    [InlineData("SELECT * WHERE { . FILTER(?x) }")]
    [InlineData("SELECT * WHERE { OPTIONAL { ?s ?p ?o } . . }")]
    [InlineData("SELECT * WHERE { { . } }")]
    [InlineData("SELECT * WHERE { GRAPH ?g { ?s ?p ?o ?a ?b ?c } }")]
    public void Separators_TheGrammarDoesNotAllow_AreSyntaxErrors(string query)
    {
        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("'.'", result.ErrorMessage);
    }

    [Theory]
    [InlineData("SELECT * WHERE { }")]
    [InlineData("SELECT * WHERE { ?s ?p ?o . }")]
    [InlineData("SELECT * WHERE { ?s ?p ?o . ?a ?b ?c . }")]
    [InlineData("SELECT * WHERE { ?s ?p ?o FILTER(true) ?a ?b ?c }")]
    [InlineData("SELECT * WHERE { ?s ?p ?o OPTIONAL { ?s ?q ?r } ?a ?b ?c }")]
    [InlineData("SELECT * WHERE { OPTIONAL { ?s ?p ?o } . ?s ?q ?r . }")]
    [InlineData("SELECT * WHERE { { ?s ?p ?o } . { ?a ?b ?c } . }")]
    [InlineData("SELECT * WHERE { ?s ?p ?o ; . }")]
    [InlineData("SELECT * WHERE { FILTER(true) . BIND(1 AS ?x) . }")]
    public void Separators_TheGrammarAllows_Parse(string query)
    {
        // One optional '.' after a triples statement or a clause; a clause needs no '.' before the next triples
        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
    }

    [Theory]
    [InlineData("SELECT * WHERE { <http://ex/x> [] <http://ex/q> }")]
    [InlineData("SELECT * WHERE { <http://ex/x> _:a <http://ex/q> }")]
    [InlineData("SELECT * WHERE { <http://ex/x> \"p\" <http://ex/q> }")]
    [InlineData("SELECT * WHERE { <http://ex/x> <http://ex/p> ?o ; _:a <http://ex/q> }")]
    [InlineData("SELECT * WHERE { <http://ex/x> <http://ex/p> [ _:a <http://ex/q> ] }")]
    public void Predicate_ThatIsABlankNodeOrALiteral_IsASyntaxError(string query)
    {
        // [78] Verb ::= VarOrIri | 'a'
        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("predicate", result.ErrorMessage);
    }

    [Theory]
    [InlineData("SELECT * WHERE { GRAPH [] { } }")]
    [InlineData("SELECT * WHERE { GRAPH _:g { ?s ?p ?o } }")]
    [InlineData("SELECT * WHERE { GRAPH \"g\" { ?s ?p ?o } }")]
    public void GraphName_ThatIsABlankNodeOrALiteral_IsASyntaxError(string query)
    {
        // [58] GraphGraphPattern ::= 'GRAPH' VarOrIri GroupGraphPattern
        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("GRAPH", result.ErrorMessage);
    }
}
