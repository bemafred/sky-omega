using System.Text;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Rdf;

[Collection("QuadStore")]
public class RdfEngineTests : PooledStoreTestBase
{
    public RdfEngineTests(QuadStorePoolFixture fixture) : base(fixture) { }

    #region Content Negotiation

    [Theory]
    [InlineData("text/turtle", RdfFormat.Turtle)]
    [InlineData("application/n-triples", RdfFormat.NTriples)]
    [InlineData("application/rdf+xml", RdfFormat.RdfXml)]
    [InlineData("application/n-quads", RdfFormat.NQuads)]
    [InlineData("application/trig", RdfFormat.TriG)]
    [InlineData("application/ld+json", RdfFormat.JsonLd)]
    [InlineData("text/plain", RdfFormat.NTriples)]
    [InlineData("application/xml", RdfFormat.RdfXml)]
    [InlineData("unknown/type", RdfFormat.Unknown)]
    public void DetermineFormat_ReturnsCorrectFormat(string contentType, RdfFormat expected)
    {
        var result = RdfEngine.DetermineFormat(contentType.AsSpan());
        Assert.Equal(expected, result);
    }

    [Fact]
    public void NegotiateFromAccept_ReturnsPreferredFormat()
    {
        var result = RdfEngine.NegotiateFromAccept("text/turtle, application/n-triples;q=0.5".AsSpan());
        Assert.Equal(RdfFormat.Turtle, result);
    }

    [Fact]
    public void NegotiateFromAccept_WithLowerQuality_ReturnsHigherQuality()
    {
        var result = RdfEngine.NegotiateFromAccept("text/turtle;q=0.3, application/n-triples;q=0.8".AsSpan());
        Assert.Equal(RdfFormat.NTriples, result);
    }

    [Theory]
    [InlineData(RdfFormat.Turtle, "text/turtle")]
    [InlineData(RdfFormat.NTriples, "application/n-triples")]
    [InlineData(RdfFormat.RdfXml, "application/rdf+xml")]
    [InlineData(RdfFormat.NQuads, "application/n-quads")]
    [InlineData(RdfFormat.TriG, "application/trig")]
    [InlineData(RdfFormat.JsonLd, "application/ld+json")]
    public void GetContentType_ReturnsCorrectType(RdfFormat format, string expected)
    {
        var result = RdfEngine.GetContentType(format);
        Assert.Equal(expected, result);
    }

    #endregion

    #region Loading

    [Fact]
    public async Task LoadAsync_Turtle_LoadsTriples()
    {
        var turtle = """
            @prefix ex: <http://example.org/> .
            ex:alice ex:name "Alice" .
            ex:bob ex:name "Bob" .
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(turtle));
        var count = await RdfEngine.LoadAsync(Store, stream, RdfFormat.Turtle);

        Assert.Equal(2, count);

        var query = SparqlEngine.Query(Store, "ASK { <http://example.org/alice> <http://example.org/name> \"Alice\" }");
        Assert.True(query.AskResult);
    }

    [Fact]
    public async Task LoadAsync_NTriples_LoadsTriples()
    {
        var nt = """
            <http://ex/s1> <http://ex/p> "value1" .
            <http://ex/s2> <http://ex/p> "value2" .
            <http://ex/s3> <http://ex/p> "value3" .
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(nt));
        var count = await RdfEngine.LoadAsync(Store, stream, RdfFormat.NTriples);

        Assert.Equal(3, count);
    }

    [Fact]
    public async Task LoadAsync_RdfXml_LoadsTriples()
    {
        var rdfXml = """
            <?xml version="1.0"?>
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                     xmlns:ex="http://example.org/">
              <rdf:Description rdf:about="http://example.org/alice">
                <ex:name>Alice</ex:name>
              </rdf:Description>
            </rdf:RDF>
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(rdfXml));
        var count = await RdfEngine.LoadAsync(Store, stream, RdfFormat.RdfXml);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task LoadAsync_NQuads_LoadsQuads()
    {
        var nq = """
            <http://ex/s> <http://ex/p> "value" <http://ex/g1> .
            <http://ex/s> <http://ex/p> "value2" .
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(nq));
        var count = await RdfEngine.LoadAsync(Store, stream, RdfFormat.NQuads);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task LoadAsync_TriG_LoadsQuads()
    {
        var trig = """
            @prefix ex: <http://example.org/> .
            ex:graph1 {
                ex:alice ex:name "Alice" .
            }
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(trig));
        var count = await RdfEngine.LoadAsync(Store, stream, RdfFormat.TriG);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task LoadAsync_JsonLd_LoadsQuads()
    {
        var jsonLd = """
            {
              "@id": "http://example.org/alice",
              "http://example.org/name": [{ "@value": "Alice" }]
            }
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(jsonLd));
        var count = await RdfEngine.LoadAsync(Store, stream, RdfFormat.JsonLd);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task LoadAsync_Rollback_OnError()
    {
        var invalidTurtle = "@prefix ex: <http://example.org/> .\nex:s ex:p INVALID_TOKEN";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(invalidTurtle));
        await Assert.ThrowsAnyAsync<Exception>(() => RdfEngine.LoadAsync(Store, stream, RdfFormat.Turtle));
    }

    #endregion

    #region Parsing

    [Fact]
    public async Task ParseAsync_Turtle_InvokesHandler()
    {
        var turtle = """
            @prefix ex: <http://example.org/> .
            ex:alice ex:name "Alice" .
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(turtle));
        var triples = new List<(string, string, string)>();

        await RdfEngine.ParseAsync(stream, RdfFormat.Turtle, (s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Single(triples);
        Assert.Equal("<http://example.org/alice>", triples[0].Item1);
    }

    [Fact]
    public async Task ParseTriplesAsync_ReturnsAllTriples()
    {
        var nt = """
            <http://ex/s1> <http://ex/p> "v1" .
            <http://ex/s2> <http://ex/p> "v2" .
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(nt));
        var triples = await RdfEngine.ParseTriplesAsync(stream, RdfFormat.NTriples);

        Assert.Equal(2, triples.Count);
        Assert.Equal("<http://ex/s1>", triples[0].Subject);
        Assert.Equal("\"v2\"", triples[1].Object);
    }

    #endregion

    #region Writing

    [Fact]
    public void WriteTriples_NTriples_WritesOutput()
    {
        var triples = new List<(string, string, string)>
        {
            ("<http://ex/s>", "<http://ex/p>", "\"value\"")
        };

        using var sw = new StringWriter();
        RdfEngine.WriteTriples(sw, RdfFormat.NTriples, triples);
        var output = sw.ToString();

        Assert.Contains("<http://ex/s>", output);
        Assert.Contains("<http://ex/p>", output);
        Assert.Contains("\"value\"", output);
    }

    [Fact]
    public void WriteTriples_Turtle_WritesOutput()
    {
        var triples = new List<(string, string, string)>
        {
            ("<http://ex/s>", "<http://ex/p>", "\"value\"")
        };

        using var sw = new StringWriter();
        RdfEngine.WriteTriples(sw, RdfFormat.Turtle, triples);
        var output = sw.ToString();

        Assert.Contains("<http://ex/s>", output);
    }

    [Fact]
    public void WriteQuads_NQuads_WritesOutput()
    {
        var quads = new List<(string, string, string, string)>
        {
            ("<http://ex/s>", "<http://ex/p>", "\"value\"", "<http://ex/g>")
        };

        using var sw = new StringWriter();
        RdfEngine.WriteQuads(sw, RdfFormat.NQuads, quads);
        var output = sw.ToString();

        Assert.Contains("<http://ex/s>", output);
        Assert.Contains("<http://ex/g>", output);
    }

    [Fact]
    public void WriteQuads_TriG_WritesOutput()
    {
        var quads = new List<(string, string, string, string)>
        {
            ("<http://ex/s>", "<http://ex/p>", "\"value\"", "<http://ex/g>")
        };

        using var sw = new StringWriter();
        RdfEngine.WriteQuads(sw, RdfFormat.TriG, quads);
        var output = sw.ToString();

        Assert.Contains("<http://ex/s>", output);
    }

    [Fact]
    public void WriteTriples_UnsupportedFormat_Throws()
    {
        var triples = new List<(string, string, string)>
        {
            ("<http://ex/s>", "<http://ex/p>", "\"value\"")
        };

        using var sw = new StringWriter();
        Assert.Throws<NotSupportedException>(() => RdfEngine.WriteTriples(sw, RdfFormat.NQuads, triples));
    }

    [Fact]
    public void WriteQuads_UnsupportedFormat_Throws()
    {
        var quads = new List<(string, string, string, string)>
        {
            ("<http://ex/s>", "<http://ex/p>", "\"value\"", "<http://ex/g>")
        };

        using var sw = new StringWriter();
        Assert.Throws<NotSupportedException>(() => RdfEngine.WriteQuads(sw, RdfFormat.NTriples, quads));
    }

    #endregion
}
