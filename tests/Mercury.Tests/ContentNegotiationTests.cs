using System;
using System.IO;
using SkyOmega.Mercury.NTriples;
using SkyOmega.Mercury.Rdf;
using SkyOmega.Mercury.Rdf.Turtle;
using SkyOmega.Mercury.RdfXml;
using SkyOmega.Mercury.Sparql.Results;
using Xunit;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for RDF and SPARQL result format content negotiation.
/// </summary>
public class ContentNegotiationTests
{
    #region RDF Format - Content Type Detection

    [Theory]
    [InlineData("text/turtle", RdfFormat.Turtle)]
    [InlineData("application/x-turtle", RdfFormat.Turtle)]
    [InlineData("TEXT/TURTLE", RdfFormat.Turtle)]
    [InlineData("text/turtle; charset=utf-8", RdfFormat.Turtle)]
    [InlineData("application/n-triples", RdfFormat.NTriples)]
    [InlineData("text/plain", RdfFormat.NTriples)]
    [InlineData("application/rdf+xml", RdfFormat.RdfXml)]
    [InlineData("application/xml", RdfFormat.RdfXml)]
    [InlineData("text/xml", RdfFormat.RdfXml)]
    [InlineData("application/octet-stream", RdfFormat.Unknown)]
    [InlineData("", RdfFormat.Unknown)]
    public void RdfFormat_FromContentType_DetectsCorrectly(string contentType, RdfFormat expected)
    {
        var result = RdfFormatNegotiator.FromContentType(contentType.AsSpan());
        Assert.Equal(expected, result);
    }

    #endregion

    #region RDF Format - Extension Detection

    [Theory]
    [InlineData(".ttl", RdfFormat.Turtle)]
    [InlineData("ttl", RdfFormat.Turtle)]
    [InlineData(".TTL", RdfFormat.Turtle)]
    [InlineData(".turtle", RdfFormat.Turtle)]
    [InlineData(".nt", RdfFormat.NTriples)]
    [InlineData(".ntriples", RdfFormat.NTriples)]
    [InlineData(".rdf", RdfFormat.RdfXml)]
    [InlineData(".xml", RdfFormat.RdfXml)]
    [InlineData(".json", RdfFormat.Unknown)]
    [InlineData("", RdfFormat.Unknown)]
    public void RdfFormat_FromExtension_DetectsCorrectly(string extension, RdfFormat expected)
    {
        var result = RdfFormatNegotiator.FromExtension(extension.AsSpan());
        Assert.Equal(expected, result);
    }

    #endregion

    #region RDF Format - Path Detection

    [Theory]
    [InlineData("/data/graph.ttl", RdfFormat.Turtle)]
    [InlineData("http://example.org/data.nt", RdfFormat.NTriples)]
    [InlineData("http://example.org/data.rdf?query=1", RdfFormat.RdfXml)]
    [InlineData("file.turtle", RdfFormat.Turtle)]
    [InlineData("/no-extension", RdfFormat.Unknown)]
    public void RdfFormat_FromPath_DetectsCorrectly(string path, RdfFormat expected)
    {
        var result = RdfFormatNegotiator.FromPath(path.AsSpan());
        Assert.Equal(expected, result);
    }

    #endregion

    #region RDF Format - Negotiation

    [Fact]
    public void RdfFormat_Negotiate_PrefersContentType()
    {
        // Content type should win over path extension
        var result = RdfFormatNegotiator.Negotiate("text/turtle", "/data/graph.nt");
        Assert.Equal(RdfFormat.Turtle, result);
    }

    [Fact]
    public void RdfFormat_Negotiate_FallsBackToPath()
    {
        // When content type is unknown, use path
        var result = RdfFormatNegotiator.Negotiate("application/octet-stream", "/data/graph.ttl");
        Assert.Equal(RdfFormat.Turtle, result);
    }

    [Fact]
    public void RdfFormat_Negotiate_NullContentType()
    {
        var result = RdfFormatNegotiator.Negotiate(null, "/data/graph.rdf");
        Assert.Equal(RdfFormat.RdfXml, result);
    }

    #endregion

    #region RDF Format - Content Type and Extension Lookup

    [Theory]
    [InlineData(RdfFormat.Turtle, "text/turtle")]
    [InlineData(RdfFormat.NTriples, "application/n-triples")]
    [InlineData(RdfFormat.RdfXml, "application/rdf+xml")]
    public void RdfFormat_GetContentType_ReturnsCorrect(RdfFormat format, string expected)
    {
        var result = RdfFormatNegotiator.GetContentType(format);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(RdfFormat.Turtle, ".ttl")]
    [InlineData(RdfFormat.NTriples, ".nt")]
    [InlineData(RdfFormat.RdfXml, ".rdf")]
    public void RdfFormat_GetExtension_ReturnsCorrect(RdfFormat format, string expected)
    {
        var result = RdfFormatNegotiator.GetExtension(format);
        Assert.Equal(expected, result);
    }

    #endregion

    #region RDF Format - Factory Methods

    [Fact]
    public void RdfFormat_CreateParser_Turtle()
    {
        using var stream = new MemoryStream();
        using var parser = RdfFormatNegotiator.CreateParser(stream, RdfFormat.Turtle);
        Assert.IsType<TurtleStreamParser>(parser);
    }

    [Fact]
    public void RdfFormat_CreateParser_NTriples()
    {
        using var stream = new MemoryStream();
        using var parser = RdfFormatNegotiator.CreateParser(stream, RdfFormat.NTriples);
        Assert.IsType<NTriplesStreamParser>(parser);
    }

    [Fact]
    public void RdfFormat_CreateParser_RdfXml()
    {
        using var stream = new MemoryStream();
        using var parser = RdfFormatNegotiator.CreateParser(stream, RdfFormat.RdfXml);
        Assert.IsType<RdfXmlStreamParser>(parser);
    }

    [Fact]
    public void RdfFormat_CreateParser_FromContentType()
    {
        using var stream = new MemoryStream();
        using var parser = RdfFormatNegotiator.CreateParser(stream, contentType: "text/turtle");
        Assert.IsType<TurtleStreamParser>(parser);
    }

    [Fact]
    public void RdfFormat_CreateParser_FromPath()
    {
        using var stream = new MemoryStream();
        using var parser = RdfFormatNegotiator.CreateParser(stream, path: "/data/graph.nt");
        Assert.IsType<NTriplesStreamParser>(parser);
    }

    [Fact]
    public void RdfFormat_CreateWriter_Turtle()
    {
        using var sw = new StringWriter();
        using var writer = RdfFormatNegotiator.CreateWriter(sw, RdfFormat.Turtle);
        Assert.IsType<TurtleStreamWriter>(writer);
    }

    [Fact]
    public void RdfFormat_CreateWriter_NTriples()
    {
        using var sw = new StringWriter();
        using var writer = RdfFormatNegotiator.CreateWriter(sw, RdfFormat.NTriples);
        Assert.IsType<NTriplesStreamWriter>(writer);
    }

    [Fact]
    public void RdfFormat_CreateWriter_RdfXml()
    {
        using var sw = new StringWriter();
        using var writer = RdfFormatNegotiator.CreateWriter(sw, RdfFormat.RdfXml);
        Assert.IsType<RdfXmlStreamWriter>(writer);
    }

    [Fact]
    public void RdfFormat_CreateWriter_DefaultsToTurtle()
    {
        using var sw = new StringWriter();
        using var writer = RdfFormatNegotiator.CreateWriter(sw); // No format specified
        Assert.IsType<TurtleStreamWriter>(writer);
    }

    #endregion

    #region SPARQL Result Format - Content Type Detection

    [Theory]
    [InlineData("application/sparql-results+json", SparqlResultFormat.Json)]
    [InlineData("application/json", SparqlResultFormat.Json)]
    [InlineData("application/sparql-results+xml", SparqlResultFormat.Xml)]
    [InlineData("application/xml", SparqlResultFormat.Xml)]
    [InlineData("text/csv", SparqlResultFormat.Csv)]
    [InlineData("text/tab-separated-values", SparqlResultFormat.Tsv)]
    [InlineData("text/tsv", SparqlResultFormat.Tsv)]
    [InlineData("text/html", SparqlResultFormat.Unknown)]
    public void SparqlResultFormat_FromContentType_DetectsCorrectly(string contentType, SparqlResultFormat expected)
    {
        var result = SparqlResultFormatNegotiator.FromContentType(contentType.AsSpan());
        Assert.Equal(expected, result);
    }

    #endregion

    #region SPARQL Result Format - Extension Detection

    [Theory]
    [InlineData(".json", SparqlResultFormat.Json)]
    [InlineData(".srj", SparqlResultFormat.Json)]
    [InlineData(".xml", SparqlResultFormat.Xml)]
    [InlineData(".srx", SparqlResultFormat.Xml)]
    [InlineData(".csv", SparqlResultFormat.Csv)]
    [InlineData(".tsv", SparqlResultFormat.Tsv)]
    [InlineData(".html", SparqlResultFormat.Unknown)]
    public void SparqlResultFormat_FromExtension_DetectsCorrectly(string extension, SparqlResultFormat expected)
    {
        var result = SparqlResultFormatNegotiator.FromExtension(extension.AsSpan());
        Assert.Equal(expected, result);
    }

    #endregion

    #region SPARQL Result Format - Accept Header Negotiation

    [Theory]
    [InlineData("application/sparql-results+json", SparqlResultFormat.Json)]
    [InlineData("application/sparql-results+xml", SparqlResultFormat.Xml)]
    [InlineData("*/*", SparqlResultFormat.Json)] // Default
    [InlineData("", SparqlResultFormat.Json)] // Default
    public void SparqlResultFormat_FromAcceptHeader_Simple(string accept, SparqlResultFormat expected)
    {
        var result = SparqlResultFormatNegotiator.FromAcceptHeader(accept.AsSpan());
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SparqlResultFormat_FromAcceptHeader_WithQuality()
    {
        // Prefer XML (q=1.0) over JSON (q=0.8)
        var accept = "application/sparql-results+json;q=0.8, application/sparql-results+xml;q=1.0";
        var result = SparqlResultFormatNegotiator.FromAcceptHeader(accept.AsSpan());
        Assert.Equal(SparqlResultFormat.Xml, result);
    }

    [Fact]
    public void SparqlResultFormat_FromAcceptHeader_MultipleFormats()
    {
        // First matching format without quality
        var accept = "text/html, application/sparql-results+json, text/csv";
        var result = SparqlResultFormatNegotiator.FromAcceptHeader(accept.AsSpan());
        // JSON should match first since they all have default quality
        Assert.Equal(SparqlResultFormat.Json, result);
    }

    #endregion

    #region SPARQL Result Format - Factory Methods

    [Fact]
    public void SparqlResultFormat_CreateWriter_Json()
    {
        using var sw = new StringWriter();
        using var writer = SparqlResultFormatNegotiator.CreateWriter(sw, SparqlResultFormat.Json);
        Assert.IsType<SparqlJsonResultWriter>(writer);
    }

    [Fact]
    public void SparqlResultFormat_CreateWriter_Xml()
    {
        using var sw = new StringWriter();
        using var writer = SparqlResultFormatNegotiator.CreateWriter(sw, SparqlResultFormat.Xml);
        Assert.IsType<SparqlXmlResultWriter>(writer);
    }

    [Fact]
    public void SparqlResultFormat_CreateWriter_Csv()
    {
        using var sw = new StringWriter();
        using var writer = SparqlResultFormatNegotiator.CreateWriter(sw, SparqlResultFormat.Csv);
        Assert.IsType<SparqlCsvResultWriter>(writer);
    }

    [Fact]
    public void SparqlResultFormat_CreateWriter_Tsv()
    {
        using var sw = new StringWriter();
        using var writer = SparqlResultFormatNegotiator.CreateWriter(sw, SparqlResultFormat.Tsv);
        Assert.IsType<SparqlCsvResultWriter>(writer);
    }

    [Fact]
    public void SparqlResultFormat_CreateWriter_FromAcceptHeader()
    {
        using var sw = new StringWriter();
        using var writer = SparqlResultFormatNegotiator.CreateWriter(sw, "application/sparql-results+xml");
        Assert.IsType<SparqlXmlResultWriter>(writer);
    }

    [Fact]
    public void SparqlResultFormat_CreateWriterFromPath()
    {
        using var sw = new StringWriter();
        using var writer = SparqlResultFormatNegotiator.CreateWriterFromPath(sw, "/results/output.csv");
        Assert.IsType<SparqlCsvResultWriter>(writer);
    }

    #endregion
}
