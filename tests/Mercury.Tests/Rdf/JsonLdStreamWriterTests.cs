using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SkyOmega.Mercury.JsonLd;
using SkyOmega.Mercury.NQuads;
using Xunit;

namespace SkyOmega.Mercury.Tests.Rdf;

/// <summary>
/// Tests for JsonLdStreamWriter - RDF to JSON-LD writer.
/// </summary>
public class JsonLdStreamWriterTests
{
    #region Basic Writing

    [Fact]
    public void WriteQuad_SingleTriple_OutputsValidJson()
    {
        using var sw = new StringWriter();
        using var writer = new JsonLdStreamWriter(sw);

        writer.WriteQuad("<http://example.org/alice>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\"");
        writer.Flush();

        var json = sw.ToString();
        Assert.True(IsValidJson(json));
    }

    [Fact]
    public void WriteQuad_WithId_OutputsId()
    {
        using var sw = new StringWriter();
        using var writer = new JsonLdStreamWriter(sw);

        writer.WriteQuad("<http://example.org/alice>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\"");
        writer.Flush();

        var json = sw.ToString();
        Assert.Contains("\"@id\"", json);
        Assert.Contains("http://example.org/alice", json);
    }

    [Fact]
    public void WriteQuad_RdfType_OutputsAtType()
    {
        using var sw = new StringWriter();
        using var writer = new JsonLdStreamWriter(sw);

        writer.WriteQuad("<http://example.org/alice>",
            "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>",
            "<http://xmlns.com/foaf/0.1/Person>");
        writer.Flush();

        var json = sw.ToString();
        Assert.Contains("\"@type\"", json);
        Assert.Contains("http://xmlns.com/foaf/0.1/Person", json);
    }

    [Fact]
    public void WriteQuad_MultipleTriplesSameSubject_GroupsProperties()
    {
        using var sw = new StringWriter();
        using var writer = new JsonLdStreamWriter(sw);

        writer.WriteQuad("<http://example.org/alice>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\"");
        writer.WriteQuad("<http://example.org/alice>", "<http://xmlns.com/foaf/0.1/age>", "\"30\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        writer.Flush();

        var json = sw.ToString();
        // Should have single object with both properties
        var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void WriteQuad_MultipleSubjects_OutputsArray()
    {
        using var sw = new StringWriter();
        using var writer = new JsonLdStreamWriter(sw);

        writer.WriteQuad("<http://example.org/alice>", "<http://ex.org/name>", "\"Alice\"");
        writer.WriteQuad("<http://example.org/bob>", "<http://ex.org/name>", "\"Bob\"");
        writer.Flush();

        var json = sw.ToString();
        var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(2, doc.RootElement.GetArrayLength());
    }

    #endregion

    #region Expanded Form

    [Fact]
    public void WriteQuad_ExpandedForm_UsesFullIris()
    {
        using var sw = new StringWriter();
        using var writer = new JsonLdStreamWriter(sw, JsonLdForm.Expanded);

        writer.WriteQuad("<http://example.org/alice>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\"");
        writer.Flush();

        var json = sw.ToString();
        Assert.Contains("http://xmlns.com/foaf/0.1/name", json);
    }

    [Fact]
    public void WriteQuad_ExpandedForm_ValuesInArrays()
    {
        using var sw = new StringWriter();
        using var writer = new JsonLdStreamWriter(sw, JsonLdForm.Expanded);

        writer.WriteQuad("<http://example.org/alice>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\"");
        writer.Flush();

        var json = sw.ToString();
        var doc = JsonDocument.Parse(json);

        // In expanded form, values are in arrays
        var nameProperty = doc.RootElement.GetProperty("http://xmlns.com/foaf/0.1/name");
        Assert.Equal(JsonValueKind.Array, nameProperty.ValueKind);
    }

    [Fact]
    public void WriteQuad_ExpandedForm_LiteralHasValue()
    {
        using var sw = new StringWriter();
        using var writer = new JsonLdStreamWriter(sw, JsonLdForm.Expanded);

        writer.WriteQuad("<http://example.org/s>", "<http://ex.org/name>", "\"Hello\"");
        writer.Flush();

        var json = sw.ToString();
        Assert.Contains("\"@value\"", json);
        Assert.Contains("Hello", json);
    }

    #endregion

    #region Compacted Form

    [Fact]
    public void WriteQuad_CompactedForm_UsesContext()
    {
        using var sw = new StringWriter();
        using var writer = new JsonLdStreamWriter(sw, JsonLdForm.Compacted);

        writer.RegisterPrefix("foaf", "http://xmlns.com/foaf/0.1/");
        writer.WriteQuad("<http://example.org/alice>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\"");
        writer.Flush();

        var json = sw.ToString();
        Assert.Contains("\"@context\"", json);
        Assert.Contains("\"foaf\"", json);
    }

    [Fact]
    public void WriteQuad_CompactedForm_AbbreviatesIris()
    {
        using var sw = new StringWriter();
        using var writer = new JsonLdStreamWriter(sw, JsonLdForm.Compacted);

        writer.RegisterPrefix("foaf", "http://xmlns.com/foaf/0.1/");
        writer.WriteQuad("<http://example.org/alice>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\"");
        writer.Flush();

        var json = sw.ToString();
        Assert.Contains("foaf:name", json);
    }

    [Fact]
    public void WriteQuad_CompactedForm_NativeInteger()
    {
        using var sw = new StringWriter();
        using var writer = new JsonLdStreamWriter(sw, JsonLdForm.Compacted);

        writer.WriteQuad("<http://example.org/s>", "<http://ex.org/age>", "\"42\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        writer.Flush();

        var json = sw.ToString();
        var doc = JsonDocument.Parse(json);

        // Should contain native number 42
        Assert.Contains("42", json);
    }

    [Fact]
    public void WriteQuad_CompactedForm_NativeBoolean()
    {
        using var sw = new StringWriter();
        using var writer = new JsonLdStreamWriter(sw, JsonLdForm.Compacted);

        writer.WriteQuad("<http://example.org/s>", "<http://ex.org/active>", "\"true\"^^<http://www.w3.org/2001/XMLSchema#boolean>");
        writer.Flush();

        var json = sw.ToString();
        Assert.Contains("true", json);
    }

    #endregion

    #region Named Graphs

    [Fact]
    public void WriteQuad_NamedGraph_OutputsGraphKeyword()
    {
        using var sw = new StringWriter();
        using var writer = new JsonLdStreamWriter(sw);

        writer.WriteQuad("<http://example.org/alice>", "<http://ex.org/name>", "\"Alice\"", "<http://example.org/graph1>");
        writer.Flush();

        var json = sw.ToString();
        Assert.Contains("\"@graph\"", json);
        Assert.Contains("http://example.org/graph1", json);
    }

    [Fact]
    public void WriteQuad_MultipleNamedGraphs_OutputsAll()
    {
        using var sw = new StringWriter();
        using var writer = new JsonLdStreamWriter(sw);

        writer.WriteQuad("<http://ex.org/s1>", "<http://ex.org/p>", "\"v1\"", "<http://ex.org/g1>");
        writer.WriteQuad("<http://ex.org/s2>", "<http://ex.org/p>", "\"v2\"", "<http://ex.org/g2>");
        writer.Flush();

        var json = sw.ToString();
        Assert.Contains("http://ex.org/g1", json);
        Assert.Contains("http://ex.org/g2", json);
    }

    [Fact]
    public void WriteQuad_MixedGraphs_OutputsBoth()
    {
        using var sw = new StringWriter();
        using var writer = new JsonLdStreamWriter(sw);

        writer.WriteQuad("<http://ex.org/s1>", "<http://ex.org/p>", "\"default\"");
        writer.WriteQuad("<http://ex.org/s2>", "<http://ex.org/p>", "\"named\"", "<http://ex.org/g1>");
        writer.Flush();

        var json = sw.ToString();
        Assert.Contains("\"default\"", json);
        Assert.Contains("\"named\"", json);
        Assert.Contains("http://ex.org/g1", json);
    }

    #endregion

    #region Blank Nodes

    [Fact]
    public void WriteQuad_BlankNodeSubject_OutputsCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new JsonLdStreamWriter(sw);

        writer.WriteQuad("_:b0", "<http://ex.org/name>", "\"Anonymous\"");
        writer.Flush();

        var json = sw.ToString();
        Assert.Contains("_:b0", json);
    }

    [Fact]
    public void WriteQuad_BlankNodeObject_OutputsReference()
    {
        using var sw = new StringWriter();
        using var writer = new JsonLdStreamWriter(sw);

        writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/knows>", "_:b1");
        writer.Flush();

        var json = sw.ToString();
        Assert.Contains("_:b1", json);
    }

    #endregion

    #region Literals

    [Fact]
    public void WriteQuad_PlainLiteral_OutputsValue()
    {
        using var sw = new StringWriter();
        using var writer = new JsonLdStreamWriter(sw);

        writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/p>", "\"Hello World\"");
        writer.Flush();

        var json = sw.ToString();
        Assert.Contains("Hello World", json);
    }

    [Fact]
    public void WriteQuad_LanguageTaggedLiteral_OutputsLanguage()
    {
        using var sw = new StringWriter();
        using var writer = new JsonLdStreamWriter(sw);

        writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/name>", "\"Bonjour\"@fr");
        writer.Flush();

        var json = sw.ToString();
        Assert.Contains("\"@language\"", json);
        Assert.Contains("\"fr\"", json);
    }

    [Fact]
    public void WriteQuad_TypedLiteral_OutputsType()
    {
        using var sw = new StringWriter();
        using var writer = new JsonLdStreamWriter(sw);

        writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/date>", "\"2024-01-01\"^^<http://www.w3.org/2001/XMLSchema#date>");
        writer.Flush();

        var json = sw.ToString();
        Assert.Contains("\"@type\"", json);
        Assert.Contains("http://www.w3.org/2001/XMLSchema#date", json);
    }

    [Fact]
    public void WriteQuad_EscapedQuotes_HandlesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new JsonLdStreamWriter(sw);

        writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/text>", "\"He said \\\"Hi\\\"\"");
        writer.Flush();

        var json = sw.ToString();
        Assert.True(IsValidJson(json));
        Assert.Contains("He said", json);
    }

    #endregion

    #region Async API

    [Fact]
    public async Task FlushAsync_WritesOutput()
    {
        await using var sw = new StringWriter();
        await using var writer = new JsonLdStreamWriter(sw);

        writer.WriteQuad("<http://example.org/s>", "<http://ex.org/p>", "\"value\"");
        await writer.FlushAsync();

        var json = sw.ToString();
        Assert.True(json.Length > 0);
        Assert.True(IsValidJson(json));
    }

    #endregion

    #region Pretty Print

    [Fact]
    public void WriteQuad_PrettyPrint_HasIndentation()
    {
        using var sw = new StringWriter();
        using var writer = new JsonLdStreamWriter(sw, prettyPrint: true);

        writer.WriteQuad("<http://example.org/s>", "<http://ex.org/p>", "\"value\"");
        writer.Flush();

        var json = sw.ToString();
        Assert.Contains("\n", json);
    }

    [Fact]
    public void WriteQuad_NoPrettyPrint_Compact()
    {
        using var sw = new StringWriter();
        using var writer = new JsonLdStreamWriter(sw, prettyPrint: false);

        writer.WriteQuad("<http://example.org/s>", "<http://ex.org/p>", "\"value\"");
        writer.Flush();

        var json = sw.ToString();
        // Compact output has no unnecessary newlines
        Assert.DoesNotContain("\n  ", json);
    }

    #endregion

    #region Roundtrip

    [Fact]
    public async Task Roundtrip_WriteAndParse_PreservesData()
    {
        // Write JSON-LD
        using var sw = new StringWriter();
        using (var writer = new JsonLdStreamWriter(sw))
        {
            writer.WriteQuad("<http://example.org/alice>", "<http://xmlns.com/foaf/0.1/name>", "\"Alice\"");
            writer.WriteQuad("<http://example.org/alice>",
                "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>",
                "<http://xmlns.com/foaf/0.1/Person>");
            writer.WriteQuad("<http://example.org/alice>", "<http://xmlns.com/foaf/0.1/age>", "\"30\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        }

        var jsonld = sw.ToString();

        // Parse JSON-LD back
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(jsonld));
        await using var parser = new JsonLdStreamParser(stream);

        var parsed = new List<RdfQuad>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            parsed.Add(new RdfQuad(s.ToString(), p.ToString(), o.ToString(),
                g.IsEmpty ? null : g.ToString()));
        });

        Assert.Equal(3, parsed.Count);
        Assert.Contains(parsed, q => q.Subject == "<http://example.org/alice>" && q.Predicate == "<http://xmlns.com/foaf/0.1/name>");
        Assert.Contains(parsed, q => q.Predicate == "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>");
        Assert.Contains(parsed, q => q.Predicate == "<http://xmlns.com/foaf/0.1/age>");
    }

    [Fact]
    public async Task Roundtrip_NamedGraph_PreservesGraph()
    {
        // Write JSON-LD with named graph
        using var sw = new StringWriter();
        using (var writer = new JsonLdStreamWriter(sw))
        {
            writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/p>", "\"value\"", "<http://ex.org/graph1>");
        }

        var jsonld = sw.ToString();

        // Parse back
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(jsonld));
        await using var parser = new JsonLdStreamParser(stream);

        var parsed = new List<RdfQuad>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            parsed.Add(new RdfQuad(s.ToString(), p.ToString(), o.ToString(),
                g.IsEmpty ? null : g.ToString()));
        });

        Assert.Single(parsed);
        Assert.Equal("<http://ex.org/graph1>", parsed[0].Graph);
    }

    #endregion

    #region Helper Methods

    private static bool IsValidJson(string json)
    {
        try
        {
            JsonDocument.Parse(json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion
}
