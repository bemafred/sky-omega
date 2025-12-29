using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SkyOmega.Mercury.RdfXml;
using Xunit;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for RdfXmlStreamParser - streaming zero-GC RDF/XML parser.
/// </summary>
public class RdfXmlStreamParserTests
{
    #region Basic Parsing

    [Fact]
    public async Task ParseAsync_BasicDescription_ParsesCorrectly()
    {
        var rdfxml = """
            <?xml version="1.0"?>
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                     xmlns:ex="http://example.org/">
                <rdf:Description rdf:about="http://example.org/subject">
                    <ex:predicate rdf:resource="http://example.org/object"/>
                </rdf:Description>
            </rdf:RDF>
            """u8.ToArray();

        await using var stream = new MemoryStream(rdfxml);
        var parser = new RdfXmlStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Single(triples);
        Assert.Equal("<http://example.org/subject>", triples[0].S);
        Assert.Equal("<http://example.org/predicate>", triples[0].P);
        Assert.Equal("<http://example.org/object>", triples[0].O);
    }

    [Fact]
    public async Task ParseAsync_MultipleTriples_ParsesAll()
    {
        var rdfxml = """
            <?xml version="1.0"?>
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                     xmlns:ex="http://example.org/">
                <rdf:Description rdf:about="http://example.org/s1">
                    <ex:p1 rdf:resource="http://example.org/o1"/>
                    <ex:p2 rdf:resource="http://example.org/o2"/>
                </rdf:Description>
                <rdf:Description rdf:about="http://example.org/s2">
                    <ex:p3 rdf:resource="http://example.org/o3"/>
                </rdf:Description>
            </rdf:RDF>
            """u8.ToArray();

        await using var stream = new MemoryStream(rdfxml);
        var parser = new RdfXmlStreamParser(stream);

        int count = 0;
        await parser.ParseAsync((s, p, o) => count++);

        Assert.Equal(3, count);
    }

    #endregion

    #region Typed Nodes

    [Fact]
    public async Task ParseAsync_TypedNode_EmitsTypeTriple()
    {
        var rdfxml = """
            <?xml version="1.0"?>
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                     xmlns:foaf="http://xmlns.com/foaf/0.1/">
                <foaf:Person rdf:about="http://example.org/alice">
                    <foaf:name>Alice</foaf:name>
                </foaf:Person>
            </rdf:RDF>
            """u8.ToArray();

        await using var stream = new MemoryStream(rdfxml);
        var parser = new RdfXmlStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Equal(2, triples.Count);

        // First triple should be rdf:type
        Assert.Contains(triples, t =>
            t.S == "<http://example.org/alice>" &&
            t.P.Contains("type") &&
            t.O.Contains("Person"));

        // Second triple should be foaf:name
        Assert.Contains(triples, t =>
            t.S == "<http://example.org/alice>" &&
            t.P.Contains("name") &&
            t.O.Contains("Alice"));
    }

    #endregion

    #region Literals

    [Fact]
    public async Task ParseAsync_PlainLiteral_ParsesCorrectly()
    {
        var rdfxml = """
            <?xml version="1.0"?>
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                     xmlns:ex="http://example.org/">
                <rdf:Description rdf:about="http://example.org/s">
                    <ex:name>Hello World</ex:name>
                </rdf:Description>
            </rdf:RDF>
            """u8.ToArray();

        await using var stream = new MemoryStream(rdfxml);
        var parser = new RdfXmlStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Single(triples);
        Assert.Equal("\"Hello World\"", triples[0].O);
    }

    [Fact]
    public async Task ParseAsync_TypedLiteral_ParsesCorrectly()
    {
        var rdfxml = """
            <?xml version="1.0"?>
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                     xmlns:ex="http://example.org/"
                     xmlns:xsd="http://www.w3.org/2001/XMLSchema#">
                <rdf:Description rdf:about="http://example.org/s">
                    <ex:age rdf:datatype="http://www.w3.org/2001/XMLSchema#integer">42</ex:age>
                </rdf:Description>
            </rdf:RDF>
            """u8.ToArray();

        await using var stream = new MemoryStream(rdfxml);
        var parser = new RdfXmlStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Single(triples);
        Assert.Contains("42", triples[0].O);
        Assert.Contains("XMLSchema#integer", triples[0].O);
    }

    [Fact]
    public async Task ParseAsync_LanguageTaggedLiteral_ParsesCorrectly()
    {
        var rdfxml = """
            <?xml version="1.0"?>
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                     xmlns:ex="http://example.org/">
                <rdf:Description rdf:about="http://example.org/s">
                    <ex:label xml:lang="en">Hello</ex:label>
                </rdf:Description>
            </rdf:RDF>
            """u8.ToArray();

        await using var stream = new MemoryStream(rdfxml);
        var parser = new RdfXmlStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Single(triples);
        Assert.Contains("Hello", triples[0].O);
        Assert.Contains("@en", triples[0].O);
    }

    #endregion

    #region Blank Nodes

    [Fact]
    public async Task ParseAsync_BlankNodeId_ParsesCorrectly()
    {
        var rdfxml = """
            <?xml version="1.0"?>
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                     xmlns:ex="http://example.org/">
                <rdf:Description rdf:nodeID="b1">
                    <ex:name>Anonymous</ex:name>
                </rdf:Description>
            </rdf:RDF>
            """u8.ToArray();

        await using var stream = new MemoryStream(rdfxml);
        var parser = new RdfXmlStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Single(triples);
        Assert.StartsWith("_:b1", triples[0].S);
    }

    [Fact]
    public async Task ParseAsync_AnonymousBlankNode_GeneratesId()
    {
        var rdfxml = """
            <?xml version="1.0"?>
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                     xmlns:ex="http://example.org/">
                <rdf:Description>
                    <ex:name>Anonymous</ex:name>
                </rdf:Description>
            </rdf:RDF>
            """u8.ToArray();

        await using var stream = new MemoryStream(rdfxml);
        var parser = new RdfXmlStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Single(triples);
        Assert.StartsWith("_:b", triples[0].S);
    }

    #endregion

    #region ParseType

    [Fact]
    public async Task ParseAsync_ParseTypeResource_CreatesBlankNode()
    {
        var rdfxml = """
            <?xml version="1.0"?>
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                     xmlns:ex="http://example.org/">
                <rdf:Description rdf:about="http://example.org/s">
                    <ex:address rdf:parseType="Resource">
                        <ex:street>123 Main St</ex:street>
                        <ex:city>Anytown</ex:city>
                    </ex:address>
                </rdf:Description>
            </rdf:RDF>
            """u8.ToArray();

        await using var stream = new MemoryStream(rdfxml);
        var parser = new RdfXmlStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        // Should have 3 triples:
        // 1. subject -> address -> blank node
        // 2. blank node -> street -> "123 Main St"
        // 3. blank node -> city -> "Anytown"
        Assert.Equal(3, triples.Count);

        // Find the address triple
        var addressTriple = triples.Find(t => t.P.Contains("address"));
        Assert.NotNull(addressTriple);
        Assert.StartsWith("_:b", addressTriple.O);

        // Find street and city triples with blank node as subject
        Assert.Contains(triples, t => t.S.StartsWith("_:b") && t.O.Contains("Main"));
        Assert.Contains(triples, t => t.S.StartsWith("_:b") && t.O.Contains("Anytown"));
    }

    [Fact]
    public async Task ParseAsync_ParseTypeLiteral_CreatesXmlLiteral()
    {
        var rdfxml = """
            <?xml version="1.0"?>
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                     xmlns:ex="http://example.org/">
                <rdf:Description rdf:about="http://example.org/s">
                    <ex:content rdf:parseType="Literal"><b>Bold</b> text</ex:content>
                </rdf:Description>
            </rdf:RDF>
            """u8.ToArray();

        await using var stream = new MemoryStream(rdfxml);
        var parser = new RdfXmlStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Single(triples);
        Assert.Contains("XMLLiteral", triples[0].O);
    }

    #endregion

    #region Nested Descriptions

    [Fact]
    public async Task ParseAsync_NestedDescription_ParsesCorrectly()
    {
        var rdfxml = """
            <?xml version="1.0"?>
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                     xmlns:ex="http://example.org/">
                <rdf:Description rdf:about="http://example.org/alice">
                    <ex:knows>
                        <rdf:Description rdf:about="http://example.org/bob">
                            <ex:name>Bob</ex:name>
                        </rdf:Description>
                    </ex:knows>
                </rdf:Description>
            </rdf:RDF>
            """u8.ToArray();

        await using var stream = new MemoryStream(rdfxml);
        var parser = new RdfXmlStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        // Should have 2 triples:
        // 1. alice -> knows -> bob
        // 2. bob -> name -> "Bob"
        Assert.Equal(2, triples.Count);

        Assert.Contains(triples, t =>
            t.S.Contains("alice") &&
            t.P.Contains("knows") &&
            t.O.Contains("bob"));

        Assert.Contains(triples, t =>
            t.S.Contains("bob") &&
            t.P.Contains("name") &&
            t.O.Contains("Bob"));
    }

    #endregion

    #region XML Entities

    [Fact]
    public async Task ParseAsync_XmlEntities_DecodesCorrectly()
    {
        var rdfxml = """
            <?xml version="1.0"?>
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                     xmlns:ex="http://example.org/">
                <rdf:Description rdf:about="http://example.org/s">
                    <ex:text>1 &lt; 2 &amp; 3 &gt; 2</ex:text>
                </rdf:Description>
            </rdf:RDF>
            """u8.ToArray();

        await using var stream = new MemoryStream(rdfxml);
        var parser = new RdfXmlStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Single(triples);
        // The literal should contain decoded entities
        Assert.Contains("<", triples[0].O);
        Assert.Contains("&", triples[0].O);
        Assert.Contains(">", triples[0].O);
    }

    [Fact]
    public async Task ParseAsync_NumericEntity_DecodesCorrectly()
    {
        var rdfxml = """
            <?xml version="1.0"?>
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                     xmlns:ex="http://example.org/">
                <rdf:Description rdf:about="http://example.org/s">
                    <ex:text>&#233;</ex:text>
                </rdf:Description>
            </rdf:RDF>
            """u8.ToArray();

        await using var stream = new MemoryStream(rdfxml);
        var parser = new RdfXmlStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Single(triples);
        // &#233; is 'é'
        Assert.Contains("é", triples[0].O);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task ParseAsync_EmptyDocument_ReturnsNoTriples()
    {
        var rdfxml = """
            <?xml version="1.0"?>
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            </rdf:RDF>
            """u8.ToArray();

        await using var stream = new MemoryStream(rdfxml);
        var parser = new RdfXmlStreamParser(stream);

        int count = 0;
        await parser.ParseAsync((s, p, o) => count++);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ParseAsync_SelfClosingProperty_ParsesCorrectly()
    {
        var rdfxml = """
            <?xml version="1.0"?>
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                     xmlns:ex="http://example.org/">
                <rdf:Description rdf:about="http://example.org/s">
                    <ex:related rdf:resource="http://example.org/o"/>
                </rdf:Description>
            </rdf:RDF>
            """u8.ToArray();

        await using var stream = new MemoryStream(rdfxml);
        var parser = new RdfXmlStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Single(triples);
        Assert.Equal("<http://example.org/o>", triples[0].O);
    }

    [Fact]
    public async Task ParseAsync_WithComments_SkipsComments()
    {
        var rdfxml = """
            <?xml version="1.0"?>
            <!-- This is a comment -->
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                     xmlns:ex="http://example.org/">
                <!-- Another comment -->
                <rdf:Description rdf:about="http://example.org/s">
                    <ex:p rdf:resource="http://example.org/o"/>
                </rdf:Description>
            </rdf:RDF>
            """u8.ToArray();

        await using var stream = new MemoryStream(rdfxml);
        var parser = new RdfXmlStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Single(triples);
    }

    [Fact]
    public async Task ParseAsync_StreamingLargeFile_DoesNotOOM()
    {
        // Generate 1000 descriptions
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\"?>");
        sb.AppendLine("<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"");
        sb.AppendLine("         xmlns:ex=\"http://example.org/\">");

        for (int i = 0; i < 1000; i++)
        {
            sb.AppendLine($"  <rdf:Description rdf:about=\"http://example.org/s{i}\">");
            sb.AppendLine($"    <ex:index>{i}</ex:index>");
            sb.AppendLine("  </rdf:Description>");
        }

        sb.AppendLine("</rdf:RDF>");

        var rdfxml = Encoding.UTF8.GetBytes(sb.ToString());

        await using var stream = new MemoryStream(rdfxml);
        var parser = new RdfXmlStreamParser(stream, bufferSize: 4096); // Small buffer

        int count = 0;
        await parser.ParseAsync((s, p, o) => count++);

        Assert.Equal(1000, count);
    }

    #endregion

    #region Property Attributes

    [Fact]
    public async Task ParseAsync_PropertyAttributes_EmitsTriples()
    {
        var rdfxml = """
            <?xml version="1.0"?>
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                     xmlns:ex="http://example.org/">
                <rdf:Description rdf:about="http://example.org/s"
                                 ex:name="Alice"
                                 ex:age="30">
                </rdf:Description>
            </rdf:RDF>
            """u8.ToArray();

        await using var stream = new MemoryStream(rdfxml);
        var parser = new RdfXmlStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Equal(2, triples.Count);
        Assert.Contains(triples, t => t.P.Contains("name") && t.O.Contains("Alice"));
        Assert.Contains(triples, t => t.P.Contains("age") && t.O.Contains("30"));
    }

    #endregion
}
