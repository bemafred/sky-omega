using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SkyOmega.Mercury.RdfXml;
using Xunit;

namespace SkyOmega.Mercury.Tests.Rdf;

/// <summary>
/// Tests for RdfXmlStreamWriter - streaming zero-GC RDF/XML writer.
/// </summary>
public class RdfXmlStreamWriterTests
{
    #region Document Structure

    [Fact]
    public void WriteStartDocument_WritesXmlDeclarationAndRdfRoot()
    {
        using var sw = new StringWriter();
        using var writer = new RdfXmlStreamWriter(sw);

        writer.WriteStartDocument();
        writer.WriteEndDocument();

        var result = sw.ToString();
        Assert.Contains("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", result);
        Assert.Contains("<rdf:RDF", result);
        Assert.Contains("xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"", result);
        Assert.Contains("</rdf:RDF>", result);
    }

    [Fact]
    public void WriteStartDocument_IncludesStandardNamespaces()
    {
        using var sw = new StringWriter();
        using var writer = new RdfXmlStreamWriter(sw);

        writer.WriteStartDocument();
        writer.WriteEndDocument();

        var result = sw.ToString();
        Assert.Contains("xmlns:rdf=", result);
        Assert.Contains("xmlns:rdfs=", result);
        Assert.Contains("xmlns:xsd=", result);
    }

    [Fact]
    public void RegisterNamespace_CustomNamespace_IncludedInDocument()
    {
        using var sw = new StringWriter();
        using var writer = new RdfXmlStreamWriter(sw);

        writer.RegisterNamespace("ex", "http://example.org/");
        writer.WriteStartDocument();
        writer.WriteEndDocument();

        var result = sw.ToString();
        Assert.Contains("xmlns:ex=\"http://example.org/\"", result);
    }

    #endregion

    #region Basic Triples

    [Fact]
    public void WriteTriple_IriSubjectAndObject_WritesDescriptionWithResource()
    {
        using var sw = new StringWriter();
        using var writer = new RdfXmlStreamWriter(sw);

        writer.RegisterNamespace("ex", "http://example.org/");
        writer.WriteStartDocument();
        writer.WriteTripleUngrouped(
            "<http://example.org/Alice>".AsSpan(),
            "<http://example.org/knows>".AsSpan(),
            "<http://example.org/Bob>".AsSpan());
        writer.WriteEndDocument();

        var result = sw.ToString();
        Assert.Contains("rdf:about=\"http://example.org/Alice\"", result);
        Assert.Contains("<ex:knows rdf:resource=\"http://example.org/Bob\"/>", result);
    }

    [Fact]
    public void WriteTriple_LiteralObject_WritesAsElementContent()
    {
        using var sw = new StringWriter();
        using var writer = new RdfXmlStreamWriter(sw);

        writer.RegisterNamespace("ex", "http://example.org/");
        writer.WriteStartDocument();
        writer.WriteTripleUngrouped(
            "<http://example.org/Alice>".AsSpan(),
            "<http://example.org/name>".AsSpan(),
            "\"Alice\"".AsSpan());
        writer.WriteEndDocument();

        var result = sw.ToString();
        Assert.Contains("<ex:name>Alice</ex:name>", result);
    }

    [Fact]
    public void WriteTriple_LiteralWithLanguageTag_WritesXmlLang()
    {
        using var sw = new StringWriter();
        using var writer = new RdfXmlStreamWriter(sw);

        writer.RegisterNamespace("ex", "http://example.org/");
        writer.WriteStartDocument();
        writer.WriteTripleUngrouped(
            "<http://example.org/s>".AsSpan(),
            "<http://example.org/label>".AsSpan(),
            "\"Bonjour\"@fr".AsSpan());
        writer.WriteEndDocument();

        var result = sw.ToString();
        Assert.Contains("xml:lang=\"fr\"", result);
        Assert.Contains(">Bonjour</ex:label>", result);
    }

    [Fact]
    public void WriteTriple_TypedLiteral_WritesRdfDatatype()
    {
        using var sw = new StringWriter();
        using var writer = new RdfXmlStreamWriter(sw);

        writer.RegisterNamespace("ex", "http://example.org/");
        writer.WriteStartDocument();
        writer.WriteTripleUngrouped(
            "<http://example.org/s>".AsSpan(),
            "<http://example.org/count>".AsSpan(),
            "\"42\"^^<http://www.w3.org/2001/XMLSchema#integer>".AsSpan());
        writer.WriteEndDocument();

        var result = sw.ToString();
        Assert.Contains("rdf:datatype=\"http://www.w3.org/2001/XMLSchema#integer\"", result);
        Assert.Contains(">42</ex:count>", result);
    }

    #endregion

    #region Blank Nodes

    [Fact]
    public void WriteTriple_BlankNodeSubject_WritesNodeID()
    {
        using var sw = new StringWriter();
        using var writer = new RdfXmlStreamWriter(sw);

        writer.RegisterNamespace("ex", "http://example.org/");
        writer.WriteStartDocument();
        writer.WriteTripleUngrouped(
            "_:b0".AsSpan(),
            "<http://example.org/name>".AsSpan(),
            "\"Anonymous\"".AsSpan());
        writer.WriteEndDocument();

        var result = sw.ToString();
        Assert.Contains("rdf:nodeID=\"b0\"", result);
    }

    [Fact]
    public void WriteTriple_BlankNodeObject_WritesNodeID()
    {
        using var sw = new StringWriter();
        using var writer = new RdfXmlStreamWriter(sw);

        writer.RegisterNamespace("ex", "http://example.org/");
        writer.WriteStartDocument();
        writer.WriteTripleUngrouped(
            "<http://example.org/Alice>".AsSpan(),
            "<http://example.org/address>".AsSpan(),
            "_:addr1".AsSpan());
        writer.WriteEndDocument();

        var result = sw.ToString();
        Assert.Contains("<ex:address rdf:nodeID=\"addr1\"/>", result);
    }

    #endregion

    #region Subject Grouping

    [Fact]
    public void WriteTriple_SameSubject_GroupsUnderOneDescription()
    {
        using var sw = new StringWriter();
        using var writer = new RdfXmlStreamWriter(sw);

        writer.RegisterNamespace("ex", "http://example.org/");
        writer.WriteStartDocument();
        writer.WriteTriple("<http://example.org/Alice>".AsSpan(), "<http://example.org/name>".AsSpan(), "\"Alice\"".AsSpan());
        writer.WriteTriple("<http://example.org/Alice>".AsSpan(), "<http://example.org/age>".AsSpan(), "\"30\"".AsSpan());
        writer.Flush();
        writer.WriteEndDocument();

        var result = sw.ToString();
        // Should only have one rdf:Description for Alice
        var descCount = CountOccurrences(result, "rdf:about=\"http://example.org/Alice\"");
        Assert.Equal(1, descCount);
        Assert.Contains("<ex:name>Alice</ex:name>", result);
        Assert.Contains("<ex:age>30</ex:age>", result);
    }

    [Fact]
    public void WriteTriple_DifferentSubjects_CreatesSeparateDescriptions()
    {
        using var sw = new StringWriter();
        using var writer = new RdfXmlStreamWriter(sw);

        writer.RegisterNamespace("ex", "http://example.org/");
        writer.WriteStartDocument();
        writer.WriteTriple("<http://example.org/Alice>".AsSpan(), "<http://example.org/name>".AsSpan(), "\"Alice\"".AsSpan());
        writer.WriteTriple("<http://example.org/Bob>".AsSpan(), "<http://example.org/name>".AsSpan(), "\"Bob\"".AsSpan());
        writer.Flush();
        writer.WriteEndDocument();

        var result = sw.ToString();
        Assert.Contains("rdf:about=\"http://example.org/Alice\"", result);
        Assert.Contains("rdf:about=\"http://example.org/Bob\"", result);
        // Should have two separate Description elements
        var descCount = CountOccurrences(result, "<rdf:Description");
        Assert.Equal(2, descCount);
    }

    #endregion

    #region XML Escaping

    [Fact]
    public void WriteTriple_LiteralWithSpecialChars_EscapesProperly()
    {
        using var sw = new StringWriter();
        using var writer = new RdfXmlStreamWriter(sw);

        writer.RegisterNamespace("ex", "http://example.org/");
        writer.WriteStartDocument();
        writer.WriteTripleUngrouped(
            "<http://example.org/s>".AsSpan(),
            "<http://example.org/formula>".AsSpan(),
            "\"x < y & y > z\"".AsSpan());
        writer.WriteEndDocument();

        var result = sw.ToString();
        Assert.Contains("x &lt; y &amp; y &gt; z", result);
    }

    [Fact]
    public void WriteTriple_IriWithSpecialChars_EscapesProperly()
    {
        using var sw = new StringWriter();
        using var writer = new RdfXmlStreamWriter(sw);

        writer.RegisterNamespace("ex", "http://example.org/");
        writer.WriteStartDocument();
        writer.WriteTripleUngrouped(
            "<http://example.org/item?id=1&type=test>".AsSpan(),
            "<http://example.org/label>".AsSpan(),
            "\"Test\"".AsSpan());
        writer.WriteEndDocument();

        var result = sw.ToString();
        Assert.Contains("rdf:about=\"http://example.org/item?id=1&amp;type=test\"", result);
    }

    #endregion

    #region Async Operations

    [Fact]
    public async Task WriteTripleAsync_BasicTriple_WritesCorrectly()
    {
        using var sw = new StringWriter();
        await using var writer = new RdfXmlStreamWriter(sw);

        writer.RegisterNamespace("ex", "http://example.org/");
        await writer.WriteStartDocumentAsync();
        await writer.WriteTripleAsync(
            "<http://example.org/s>",
            "<http://example.org/p>",
            "<http://example.org/o>");
        await writer.FlushAsync();
        await writer.WriteEndDocumentAsync();

        var result = sw.ToString();
        Assert.Contains("rdf:about=\"http://example.org/s\"", result);
        Assert.Contains("rdf:resource=\"http://example.org/o\"", result);
    }

    #endregion

    #region Auto-dispose Behavior

    [Fact]
    public void Dispose_WithOpenDocument_ClosesDocument()
    {
        var sw = new StringWriter();

        using (var writer = new RdfXmlStreamWriter(sw))
        {
            writer.WriteStartDocument();
            writer.WriteTripleUngrouped(
                "<http://example.org/s>".AsSpan(),
                "<http://example.org/p>".AsSpan(),
                "\"value\"".AsSpan());
            // Don't call WriteEndDocument - let Dispose handle it
        }

        var result = sw.ToString();
        Assert.Contains("</rdf:RDF>", result);
    }

    #endregion

    #region Roundtrip Tests

    [Fact]
    public async Task Roundtrip_WriteAndParse_ProducesSameTripleCount()
    {
        // Write some triples
        using var sw = new StringWriter();
        using (var writer = new RdfXmlStreamWriter(sw))
        {
            writer.RegisterNamespace("ex", "http://example.org/");
            writer.WriteStartDocument();
            writer.WriteTripleUngrouped("<http://example.org/Alice>".AsSpan(), "<http://example.org/type>".AsSpan(), "<http://example.org/Person>".AsSpan());
            writer.WriteTripleUngrouped("<http://example.org/Alice>".AsSpan(), "<http://example.org/name>".AsSpan(), "\"Alice\"".AsSpan());
            writer.WriteTripleUngrouped("<http://example.org/Alice>".AsSpan(), "<http://example.org/knows>".AsSpan(), "<http://example.org/Bob>".AsSpan());
            writer.WriteEndDocument();
        }

        var rdfXml = sw.ToString();

        // Parse it back
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(rdfXml));
        var parser = new RdfXmlStreamParser(stream);

        int count = 0;
        await parser.ParseAsync((s, p, o) => count++);

        // Verify triple count matches
        Assert.Equal(3, count);
    }

    #endregion

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}
