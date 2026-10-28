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

    [Theory]
    [InlineData("SELECT * WHERE { _:a ?p ?v . { _:a ?q 1 } }")]
    [InlineData("SELECT * WHERE { _:a ?p ?v OPTIONAL { ?s ?p ?v } _:a ?q 1 }")]
    [InlineData("SELECT * WHERE { _:a ?p ?v GRAPH ?g { ?s ?p ?v } _:a ?q 1 }")]
    [InlineData("SELECT * WHERE { _:a ?p ?v BIND(1 AS ?x) _:a ?q ?y }")]
    [InlineData("SELECT * WHERE { { _:a ?p ?v } UNION { _:a ?q 1 } }")]
    [InlineData("SELECT * WHERE { ?s ?p [ ?q _:a ] OPTIONAL { _:a ?r ?t } }")]
    [InlineData("SELECT * WHERE { _:a ?p ?v FILTER EXISTS { _:a ?q ?w } }")]
    [InlineData("SELECT * WHERE { _:a ?p ?v { SELECT ?w WHERE { _:a ?q ?w } } }")]
    public void BlankNodeLabel_InTwoBasicGraphPatterns_IsASyntaxError(string query)
    {
        // §19.6: a label belongs to one basic graph pattern; a group, OPTIONAL, GRAPH, BIND, UNION, EXISTS or
        // sub-SELECT starts another one
        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("_:a", result.ErrorMessage);
    }

    [Theory]
    [InlineData("SELECT * WHERE { _:a ?p ?v . _:a ?q ?w }")]
    [InlineData("SELECT * WHERE { _:a ?p ?v FILTER(true) _:a ?q ?w }")]
    [InlineData("SELECT * WHERE { _:a ?p [ ?q _:a ] . }")]
    [InlineData("SELECT * WHERE { { _:a ?p ?v } UNION { _:b ?q 1 } }")]
    public void BlankNodeLabel_InOneBasicGraphPattern_Parses(string query)
    {
        // A FILTER doesn't end a basic graph pattern (§18.2.2.1), and a property list is in its subject's pattern
        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
    }

    [Fact]
    public void BlankNodeLabel_InTwoOperationsWheres_Parses()
    {
        // The rule is per WHERE: each operation of an Update request is a query of its own
        var result = SparqlEngine.Update(Store,
            "DELETE { ?s ?p ?o } WHERE { _:a ?p ?o . ?s ?p ?o } ; DELETE { ?s ?p ?o } WHERE { _:a ?q ?o . ?s ?p ?o }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
    }

    [Theory]
    [InlineData("PREFIX : <http://ex/> SELECT * WHERE { :a.b :p :o . }", "<http://ex/a.b>")]
    [InlineData("PREFIX x.y: <http://ex/xy#> SELECT * WHERE { x.y:s :p :o . }", "<http://ex/xy#s>")]
    [InlineData("PREFIX : <http://ex/> SELECT * WHERE { : :p :o . }", "<http://ex/>")]
    [InlineData("PREFIX 食: <http://ex/food#> SELECT * WHERE { 食:食べる :p :o . }", "<http://ex/food#食べる>")]
    [InlineData("PREFIX : <http://ex/> SELECT * WHERE { :resumé :p :o . }", "<http://ex/resumé>")]
    [InlineData("PREFIX : <http://ex/> SELECT * WHERE { :a\\-b :p :o . }", "<http://ex/a-b>")]
    [InlineData("PREFIX : <http://ex/> SELECT * WHERE { :a%20b :p :o . }", "<http://ex/a%20b>")]
    [InlineData("PREFIX : <http://ex/> SELECT * WHERE { :s :p :o.}", "<http://ex/s>")]
    public void PrefixedName_ReadsTheGrammarsPnLocal(string query, string subject)
    {
        // [168] PN_PREFIX and [169] PN_LOCAL: dots inside a name, an empty local part, the Unicode name characters,
        // escapes and percent-encoding; a '.' that ends a name is the triple's. Through 1.8.30 names were ASCII only,
        // a prefix could hold no '.', and :a.b was read as :a
        Store.AddCurrent(subject, "<http://ex/p>", "<http://ex/o>");
        var withP = query.Replace(":p :o", "<http://ex/p> <http://ex/o>").Replace(" :o.}", " <http://ex/o>.}");

        var result = SparqlEngine.Query(Store, withP.Replace("SELECT *", "ASK"), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.AskResult);
    }

    [Theory]
    [InlineData("PREFIX : <http://ex/> SELECT * WHERE { :a%2 ?p ?o }")]
    [InlineData("PREFIX : <http://ex/> SELECT * WHERE { :a\\q ?p ?o }")]
    [InlineData("PREFIX _x: <http://ex/> SELECT * WHERE { ?s ?p ?o }")]
    [InlineData("PREFIX x.: <http://ex/> SELECT * WHERE { ?s ?p ?o }")]
    public void PrefixedName_TheGrammarDoesNotRead_IsASyntaxError(string query)
    {
        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
    }

    [Fact]
    public void Filter_APrefixedFunctionWithTheEmptyPrefix_Parses()
    {
        // [121] BuiltInCall | FunctionCall: FILTER :myFunc(?s, ?o) (W3C syntax-expr-05); an unknown function is an error,
        // which keeps no row
        var result = SparqlEngine.Query(Store,
            "PREFIX : <http://ex/> SELECT * WHERE { ?s ?p ?o . FILTER :myFunc(?s, ?o) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
    }

    [Theory]
    [InlineData("\"\"\"Long\\\"\"\"Literal\"\"\"")]
    [InlineData("'''Long\\'''Literal'''")]
    public void LongString_WithAnEscapedQuote_IsOneLiteral(string literal)
    {
        // [158]–[159]: an escaped quote inside a long string is part of it (W3C syntax-lit-13, -14): Long, three quotes,
        // Literal. Through 1.8.30 the pattern reader ended a long string at the first three quotes, escaped or not.
        var result = SparqlEngine.Query(Store,
            $"SELECT (STRLEN({literal}) AS ?n) WHERE {{ OPTIONAL {{ ?s ?p {literal} }} }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("\"14\"^^<http://www.w3.org/2001/XMLSchema#integer>", Assert.Single(result.Rows!)["n"]);
    }

    [Fact]
    public void LanguageTag_WithADigitSubtag_IsOneTag()
    {
        // [145] LANGTAG ::= '@' [a-zA-Z]+ ('-' [a-zA-Z0-9]+)*
        Store.AddCurrent("<http://ex/s>", "<http://ex/p>", "\"x\"@zh-hant-2020");

        var result = SparqlEngine.Query(Store,
            "ASK WHERE { <http://ex/s> <http://ex/p> \"x\"@zh-hant-2020 }", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.AskResult);
    }
}
