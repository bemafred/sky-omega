using System.Linq;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// A constant in a pattern, VALUES, a CONSTRUCT template or Update data denotes an RDF term, and is stored and matched
/// as the RDF parsers store that term (LiteralForm.StoredTerm, read by the evaluator's constant reader). Through 1.8.32
/// only a short double-quoted literal was decoded: a single-quoted or long string, and a prefixed datatype, were matched
/// as written and found nothing (W3C quotes-1 to -4, term-6, -7, open-eq-02); VALUES and CONSTRUCT copied them as
/// written; and :p +5 was read as the path :p+ with the object 5 (term-8), where §19.8's longest match makes +5 one
/// INTEGER_POSITIVE token. Each test stores the same term in Turtle's form and finds it from every written form.
/// </summary>
[Collection("QuadStore")]
public class ConstantTermTests : PooledStoreTestBase
{
    public ConstantTermTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData("\"x\"", "'x'")]
    [InlineData("\"x\"", "'''x'''")]
    [InlineData("\"x\"", "\"\"\"x\"\"\"")]
    [InlineData("\"x\ny\"", "'''x\ny'''")]                                                        // a long string over two lines
    [InlineData("\"a\"b\"", "'a\"b'")]                                                            // a quote inside: stored raw (ADR-044)
    [InlineData("\"x\"@en", "'x'@en")]
    [InlineData("\"456.\"^^<http://www.w3.org/2001/XMLSchema#decimal>", "\"456.\"^^xsd:decimal")] // a prefixed datatype
    [InlineData("\"x\ny\"^^<http://example.org/t>", "\"\"\"x\ny\"\"\"^^:t")]
    [InlineData("\"+5\"^^<http://www.w3.org/2001/XMLSchema#integer>", "+5")]                      // +5 is a number, not :p+
    [InlineData("\"-7\"^^<http://www.w3.org/2001/XMLSchema#integer>", "-7")]
    [InlineData("\"+.5\"^^<http://www.w3.org/2001/XMLSchema#decimal>", "+.5")]
    [InlineData("\"true\"^^<http://www.w3.org/2001/XMLSchema#boolean>", "true")]
    public void Constant_InEveryForm_MatchesItsStoredTerm(string stored, string written)
    {
        Store.AddCurrent("<http://example.org/s>", "<http://example.org/p>", stored);
        const string Prologue = "PREFIX : <http://example.org/> PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> ";

        var pattern = SparqlEngine.Query(Store, Prologue + $"SELECT ?s WHERE {{ ?s :p {written} }}",
            TestContext.Current.CancellationToken);
        var values = SparqlEngine.Query(Store, Prologue + $"SELECT ?s WHERE {{ VALUES ?o {{ {written} }} ?s :p ?o }}",
            TestContext.Current.CancellationToken);
        var construct = SparqlEngine.Query(Store, Prologue + $"CONSTRUCT {{ :t :q {written} }} WHERE {{ }}",
            TestContext.Current.CancellationToken);

        Assert.True(pattern.Success, pattern.ErrorMessage);
        Assert.Equal("<http://example.org/s>", Assert.Single(pattern.Rows!)["s"]);
        Assert.True(values.Success, values.ErrorMessage);
        Assert.Equal("<http://example.org/s>", Assert.Single(values.Rows!)["s"]);
        Assert.True(construct.Success, construct.ErrorMessage);
        Assert.Equal(stored, Assert.Single(construct.Triples!).Object);
    }

    [Fact]
    public void Update_StoresEachConstantAsItsTerm()
    {
        var update = SparqlEngine.Update(Store,
            "PREFIX : <http://example.org/> PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> " +
            "INSERT DATA { :a :p '''x''' . :b :p \"456.\"^^xsd:decimal . :c :p +5 }",
            TestContext.Current.CancellationToken);
        Assert.True(update.Success, update.ErrorMessage);

        var result = SparqlEngine.Query(Store, "SELECT ?o WHERE { ?s <http://example.org/p> ?o } ORDER BY ?s",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(
            new[]
            {
                "\"x\"",
                "\"456.\"^^<http://www.w3.org/2001/XMLSchema#decimal>",
                "\"+5\"^^<http://www.w3.org/2001/XMLSchema#integer>",
            },
            result.Rows!.Select(row => row["o"]));
    }

    [Theory]
    [InlineData("SELECT ?o WHERE { <http://example.org/a> <http://example.org/p>+ ?o }", 2)]   // + then a space: a path
    [InlineData("SELECT ?o WHERE { <http://example.org/a> <http://example.org/p>? ?o }", 2)]   // ? then a space: a path
    [InlineData("SELECT ?1 WHERE { <http://example.org/a> <http://example.org/p> ?1 }", 1)]    // ?1 is a variable
    public void PathModifier_IsReadWhereNoTokenIsLonger(string query, int expected)
    {
        Store.AddCurrent("<http://example.org/a>", "<http://example.org/p>", "<http://example.org/b>");
        Store.AddCurrent("<http://example.org/b>", "<http://example.org/p>", "<http://example.org/c>");

        var result = SparqlEngine.Query(Store, query, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(expected, result.Rows!.Count);
    }
}
