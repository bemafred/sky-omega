using System.Linq;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// SPARQL 1.1 §17.4.3: a string function takes a string literal (a simple literal, an xsd:string literal or a
/// language-tagged literal); an IRI, a number or a literal of another datatype is a type error, and STR gives an IRI's
/// text. Through 1.8.33 every string function read an IRI's text as well (from their first implementation, with no
/// recorded decision), so REGEX matched an IRI (W3C dawg-regex-003). REGEX's q flag, which makes the pattern literal,
/// was ignored, and an unknown flag accepted (W3C regex-no-metacharacters, -case-insensitive). text:match, Mercury's
/// own function, still reads IRIs (ADR-051). Each test seeds one subject with an IRI, a string and a number.
/// </summary>
[Collection("QuadStore")]
public class StringFunctionArgumentTests : PooledStoreTestBase
{
    public StringFunctionArgumentTests(QuadStorePoolFixture fixture) : base(fixture) { }

    [Theory]
    [InlineData("REGEX(?o, \"example\")", new[] { "\"http://example.com/literal\"" })]                      // not the IRI
    [InlineData("REGEX(STR(?o), \"example\")", new[] { "<http://example.com/uri>", "\"http://example.com/literal\"" })]
    [InlineData("CONTAINS(?o, \"example\")", new[] { "\"http://example.com/literal\"" })]
    [InlineData("STRSTARTS(?o, \"http\")", new[] { "\"http://example.com/literal\"" })]
    [InlineData("STRLEN(?o) > 3", new[] { "\"http://example.com/literal\"" })]                              // not the number
    [InlineData("UCASE(?o) = \"HTTP://EXAMPLE.COM/LITERAL\"", new[] { "\"http://example.com/literal\"" })]
    public void StringFunction_TakesAStringLiteral(string filter, string[] expected)
    {
        Store.AddCurrent("<http://example.com/s>", "<http://example.com/p>", "<http://example.com/uri>");
        Store.AddCurrent("<http://example.com/s>", "<http://example.com/p>", "\"http://example.com/literal\"");
        Store.AddCurrent("<http://example.com/s>", "<http://example.com/p>", "\"12345\"^^<http://www.w3.org/2001/XMLSchema#integer>");

        var result = SparqlEngine.Query(Store, $"SELECT ?o WHERE {{ ?s <http://example.com/p> ?o FILTER({filter}) }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(expected.OrderBy(o => o, System.StringComparer.Ordinal),
            result.Rows!.Select(row => row["o"]).OrderBy(o => o, System.StringComparer.Ordinal));
    }

    [Fact]
    public void TextMatch_MercurysOwn_StillReadsAnIrisText()
    {
        // text:match is not a SPARQL string function: it reads an IRI's text (ADR-051, Lucy's recall). The values come
        // from VALUES, so the evaluator reads them; a scan's IRI object is not in the trigram index until ADR-051.
        var result = SparqlEngine.Query(Store,
            "SELECT ?o WHERE { VALUES ?o { <http://example.com/uri> \"http://example.com/literal\" } FILTER(text:match(?o, \"example\")) }",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.Rows!.Count);
    }

    [Theory]
    [InlineData("REGEX(\"a?+*.{}()[]c\", \"a?+*.{}()[]c\", \"q\")", true)]   // q: every character literal
    [InlineData("REGEX(\"abc\", \"a.c\", \"q\")", false)]                     // so '.' is a dot, not any character
    [InlineData("REGEX(\"A.C\", \"a.c\", \"iq\")", true)]                     // i still applies
    [InlineData("REGEX(\"abc\", \"a.c\")", true)]
    [InlineData("REPLACE(\"a.b.c\", \".\", \"$0\", \"q\") = \"a$0b$0c\"", true)] // q: the replacement is literal too
    [InlineData("REGEX(\"example.com\", \"example\\\\.com\")", true)]           // an escaped dot (W3C dawg-regex-003)
    [InlineData("REGEX(\"exampleXcom\", \"example\\\\.com\")", false)]
    [InlineData("BOUND(?unbound) || REGEX(\"abc\", \"b\", \"z\")", false)]    // an unknown flag is an error (FORX0001)
    [InlineData("CONTAINS(\"abc\"@en, \"b\"@fr)", false)]                      // not argument compatible: an error
    [InlineData("STRSTARTS(\"abc\"@en, \"a\"@en)", true)]
    public void Regex_AndCompatibility_FollowXPath(string expression, bool expected)
    {
        var result = SparqlEngine.Query(Store, $"ASK {{ FILTER({expression}) }}", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(expected, result.AskResult);
    }
}
