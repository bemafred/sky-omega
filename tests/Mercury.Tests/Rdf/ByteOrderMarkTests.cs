using System.Text;
using SkyOmega.Mercury.JsonLd;
using SkyOmega.Mercury.NQuads;
using SkyOmega.Mercury.NTriples;
using SkyOmega.Mercury.Rdf.Turtle;
using SkyOmega.Mercury.RdfXml;
using SkyOmega.Mercury.Sparql.Results;
using SkyOmega.Mercury.TriG;
using Xunit;

namespace SkyOmega.Mercury.Tests.Rdf;

/// <summary>
/// A document may begin with a UTF-8 byte order mark (EF BB BF), which each reader skips (Utf8ByteOrderMark). The
/// streaming parsers read through SlowReadStream at one byte per read, so the byte order mark arrives over three
/// reads.
/// </summary>
public class ByteOrderMarkTests
{
    [Fact]
    public async Task NTriples_DocumentStartingWithAByteOrderMark_IsTheDocumentAfterIt()
    {
        var document = "\uFEFF<http://e/s> <http://e/p> \"x\" .\n";
        await using var stream = new SlowReadStream(new MemoryStream(Encoding.UTF8.GetBytes(document)), 1);
        var parser = new NTriplesStreamParser(stream);

        var statements = new List<string>();
        await parser.ParseAsync((s, p, o) => statements.Add($"{s} {p} {o}"), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "<http://e/s> <http://e/p> \"x\"" }, statements);
    }

    [Fact]
    public async Task NTriples_AByteOrderMarkAfterTheStart_IsAnError()
    {
        // Only the start of a document may hold one; anywhere else U+FEFF is a character the grammar does not admit.
        var document = "<http://e/s> <http://e/p> \"x\" .\n\uFEFF<http://e/s> <http://e/p> \"y\" .\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document));
        var parser = new NTriplesStreamParser(stream);

        var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await parser.ParseAsync((s, p, o) => { }, TestContext.Current.CancellationToken));

        Assert.Contains("line 2", error.Message);
        Assert.Contains("U+FEFF", error.Message);
    }

    [Fact]
    public async Task NQuads_DocumentStartingWithAByteOrderMark_IsTheDocumentAfterIt()
    {
        var document = "\uFEFF<http://e/s> <http://e/p> \"x\" <http://e/g> .\n";
        await using var stream = new SlowReadStream(new MemoryStream(Encoding.UTF8.GetBytes(document)), 1);
        var parser = new NQuadsStreamParser(stream);

        var statements = new List<string>();
        await parser.ParseAsync((s, p, o, g) => statements.Add($"{s} {p} {o} {g}"), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "<http://e/s> <http://e/p> \"x\" <http://e/g>" }, statements);
    }

    [Fact]
    public async Task Turtle_DocumentStartingWithAByteOrderMark_IsTheDocumentAfterIt()
    {
        var document = "\uFEFF@prefix e: <http://e/> .\ne:s e:p \"x\" .\n";
        await using var stream = new SlowReadStream(new MemoryStream(Encoding.UTF8.GetBytes(document)), 1);
        var parser = new TurtleStreamParser(stream);

        var statements = new List<string>();
        await parser.ParseAsync((s, p, o) => statements.Add($"{s} {p} {o}"), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "<http://e/s> <http://e/p> \"x\"" }, statements);
    }

    [Fact]
    public async Task TriG_DocumentStartingWithAByteOrderMark_IsTheDocumentAfterIt()
    {
        var document = "\uFEFF@prefix e: <http://e/> .\ne:g { e:s e:p \"x\" . }\n";
        await using var stream = new SlowReadStream(new MemoryStream(Encoding.UTF8.GetBytes(document)), 1);
        var parser = new TriGStreamParser(stream);

        var statements = new List<string>();
        await parser.ParseAsync((s, p, o, g) => statements.Add($"{s} {p} {o} {g}"), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "<http://e/s> <http://e/p> \"x\" <http://e/g>" }, statements);
    }

    [Fact]
    public async Task RdfXml_DocumentStartingWithAByteOrderMark_IsTheDocumentAfterIt()
    {
        // XML 1.0 §4.3.3: a UTF-8 document may begin with a byte order mark.
        var document = "\uFEFF<?xml version=\"1.0\"?>\n" +
                       "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\" xmlns:e=\"http://e/\">" +
                       "<rdf:Description rdf:about=\"http://e/s\"><e:p>x</e:p></rdf:Description></rdf:RDF>";
        await using var stream = new SlowReadStream(new MemoryStream(Encoding.UTF8.GetBytes(document)), 1);
        var parser = new RdfXmlStreamParser(stream);

        var statements = new List<string>();
        await parser.ParseAsync((s, p, o) => statements.Add($"{s} {p} {o}"), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "<http://e/s> <http://e/p> \"x\"" }, statements);
    }

    [Fact]
    public async Task JsonLd_DocumentStartingWithAByteOrderMark_IsTheDocumentAfterIt()
    {
        // RFC 8259 §8.1: a JSON reader may ignore a byte order mark.
        var document = "\uFEFF{\"@id\": \"http://e/s\", \"http://e/p\": \"x\"}";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document));
        var parser = new JsonLdStreamParser(stream);

        var statements = new List<string>();
        await parser.ParseAsync((s, p, o, g) => statements.Add($"{s} {p} {o}"), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "<http://e/s> <http://e/p> \"x\"" }, statements);
    }

    [Fact]
    public async Task SparqlJsonResults_DocumentStartingWithAByteOrderMark_IsTheDocumentAfterIt()
    {
        // A SERVICE endpoint's response, read by the results parser.
        var document = "\uFEFF{\"head\": {\"vars\": [\"x\"]}, \"results\": {\"bindings\": [{\"x\": {\"type\": \"literal\", \"value\": \"y\"}}]}}";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document));
        await using var parser = new SparqlJsonResultParser(stream);

        await parser.ParseAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "x" }, parser.Variables);
        Assert.True(parser.Rows[0].TryGetValue("x", out var x));
        Assert.Equal("y", x.Value);
    }
}
