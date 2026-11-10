using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SkyOmega.Mercury.NTriples;
using SkyOmega.Mercury.NQuads;
using SkyOmega.Mercury.Rdf;
using SkyOmega.Mercury.Rdf.Turtle;
using SkyOmega.Mercury.TriG;
using Xunit;

namespace SkyOmega.Mercury.Tests.Rdf;

/// <summary>
/// A language-tagged literal is well-formed the same way in every format (docs/divergence S1f): its tag matches
/// BCP 47's Language-Tag ABNF (RDF 1.2 Concepts §3.4, RFC 5646 §2.2.9), and <c>rdf:langString</c> and
/// <c>rdf:dirLangString</c> are never written as an explicit datatype. The W3C negative syntax tests
/// <c>*-langdir-bad-3/4/5</c> check one example of each; these check the rule.
/// </summary>
public class RdfLanguageTagTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("EN-us")]                // case does not matter
    [InlineData("es-419")]               // region of three digits
    [InlineData("zh-Hant-TW")]           // script and region
    [InlineData("zh-min-nan")]           // extended language subtags (also a regular grandfathered tag)
    [InlineData("ar-afb-arz-apc")]       // three extended language subtags, the most there can be
    [InlineData("sr-Latn-RS")]
    [InlineData("de-1901")]              // variant of a digit and three alphanumerics
    [InlineData("sl-rozaj-biske")]       // two variants
    [InlineData("en-a-bbb-x-a-ccc")]     // an extension, then private use
    [InlineData("x-whatever")]           // private use alone
    [InlineData("be-x-old")]
    [InlineData("simple")]               // a language of five to eight letters
    [InlineData("i-klingon")]            // irregular grandfathered
    [InlineData("en-GB-oed")]            // irregular grandfathered
    [InlineData("en--ltr")]              // the base direction after "--" is not part of the tag
    public void WellFormedTags_AreAccepted(string tag)
    {
        Assert.True(RdfLanguageTag.IsWellFormed(tag));
    }

    [Theory]
    [InlineData("cantbethislong")]       // the W3C tests' tag: a subtag of 14 letters (at most 8)
    [InlineData("en-abcdefghi")]         // a later subtag of 9
    [InlineData("")]
    [InlineData("e")]                    // a language of one letter
    [InlineData("1en")]
    [InlineData("en-")]                  // an empty subtag
    [InlineData("-en")]
    [InlineData("en-US-abcd")]           // four letters after a region: neither script nor variant
    [InlineData("ar-afb-arz-apc-ary")]   // four extended language subtags
    [InlineData("en-a")]                 // an extension singleton with no subtag
    [InlineData("en-x")]                 // private use with no subtag
    [InlineData("x")]
    [InlineData("i-foo")]                // "i-" only in the irregular grandfathered tags
    [InlineData("en_US")]
    [InlineData("été")]                  // BCP 47 letters are ASCII
    public void IllFormedTags_AreRejected(string tag)
    {
        Assert.False(RdfLanguageTag.IsWellFormed(tag));
    }

    [Fact]
    public void LangStringAndDirLangString_AreTheTagOnlyDatatypes()
    {
        Assert.True(RdfLanguageTag.IsTagOnlyDatatype("<http://www.w3.org/1999/02/22-rdf-syntax-ns#langString>"));
        Assert.True(RdfLanguageTag.IsTagOnlyDatatype("<http://www.w3.org/1999/02/22-rdf-syntax-ns#dirLangString>"));
        Assert.False(RdfLanguageTag.IsTagOnlyDatatype("<http://www.w3.org/2001/XMLSchema#string>"));
        Assert.False(RdfLanguageTag.IsTagOnlyDatatype("<http://www.w3.org/1999/02/22-rdf-syntax-ns#HTML>"));
    }

    [Theory]
    [InlineData("\"Hello\"@cantbethislong")]
    [InlineData("\"Hello\"@en-")]
    [InlineData("\"Hello\"^^<http://www.w3.org/1999/02/22-rdf-syntax-ns#langString>")]
    [InlineData("\"Hello\"^^<http://www.w3.org/1999/02/22-rdf-syntax-ns#dirLangString>")]
    public async Task IllFormedLiteral_IsRejectedByEveryFormat(string literal)
    {
        string triple = $"<http://ex.org/s> <http://ex.org/p> {literal} .";
        var cancellationToken = TestContext.Current.CancellationToken;

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(triple));
            await new NTriplesStreamParser(stream).ParseAsync((_, _, _) => { }, cancellationToken);
        });

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(triple));
            await new NQuadsStreamParser(stream).ParseAsync((_, _, _, _) => { }, cancellationToken);
        });

        // Turtle reads literals in two places: the handler API through spans, the enumerable API through strings
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(triple));
            await new TurtleStreamParser(stream).ParseAsync((_, _, _) => { }, cancellationToken);
        });

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(triple));
            await foreach (var _ in new TurtleStreamParser(stream).ParseAsync(cancellationToken)) { }
        });

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(triple));
            await new TriGStreamParser(stream).ParseAsync((_, _, _, _) => { }, cancellationToken);
        });
    }

    [Fact]
    public async Task LangStringAsAPrefixedDatatype_IsRejectedByTurtleAndTriG()
    {
        const string document = """
            PREFIX rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#>
            <http://ex.org/s> <http://ex.org/p> "Hello"^^rdf:langString .
            """;
        var cancellationToken = TestContext.Current.CancellationToken;

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document));
            await new TurtleStreamParser(stream).ParseAsync((_, _, _) => { }, cancellationToken);
        });

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document));
            await foreach (var _ in new TurtleStreamParser(stream).ParseAsync(cancellationToken)) { }
        });

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document));
            await new TriGStreamParser(stream).ParseAsync((_, _, _, _) => { }, cancellationToken);
        });
    }

    [Fact]
    public async Task WellFormedTag_IsKeptAsWrittenByEveryFormat()
    {
        const string triple = "<http://ex.org/s> <http://ex.org/p> \"Hello\"@zh-Hant-TW .";
        const string expected = "\"Hello\"@zh-Hant-TW";
        var cancellationToken = TestContext.Current.CancellationToken;

        string nt = null!;
        await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(triple)))
            await new NTriplesStreamParser(stream).ParseAsync((_, _, o) => nt = o.ToString(), cancellationToken);

        string nq = null!;
        await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(triple)))
            await new NQuadsStreamParser(stream).ParseAsync((_, _, o, _) => nq = o.ToString(), cancellationToken);

        string ttlSpans = null!;
        await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(triple)))
            await new TurtleStreamParser(stream).ParseAsync((_, _, o) => ttlSpans = o.ToString(), cancellationToken);

        string ttlStrings = null!;
        await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(triple)))
            await foreach (var t in new TurtleStreamParser(stream).ParseAsync(cancellationToken))
                ttlStrings = t.Object;

        string trig = null!;
        await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(triple)))
            await new TriGStreamParser(stream).ParseAsync((_, _, o, _) => trig = o.ToString(), cancellationToken);

        Assert.Equal(expected, nt);
        Assert.Equal(expected, nq);
        Assert.Equal(expected, ttlSpans);
        Assert.Equal(expected, ttlStrings);
        Assert.Equal(expected, trig);
    }
}
