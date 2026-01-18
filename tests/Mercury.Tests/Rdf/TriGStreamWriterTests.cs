using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SkyOmega.Mercury.NQuads;
using SkyOmega.Mercury.TriG;
using Xunit;

namespace SkyOmega.Mercury.Tests.Rdf;

/// <summary>
/// Tests for TriGStreamWriter - streaming zero-GC TriG writer.
/// </summary>
public class TriGStreamWriterTests
{
    #region Basic Writing

    [Fact]
    public void WriteQuad_DefaultGraph_WritesWithoutGraphBlock()
    {
        using var sw = new StringWriter();
        using var writer = new TriGStreamWriter(sw);

        writer.WriteQuad(
            "<http://example.org/s>".AsSpan(),
            "<http://example.org/p>".AsSpan(),
            "<http://example.org/o>".AsSpan());
        writer.Flush();

        var result = sw.ToString();
        Assert.Contains("<http://example.org/s>", result);
        Assert.DoesNotContain("GRAPH", result);
        Assert.DoesNotContain("{", result);
    }

    [Fact]
    public void WriteQuad_NamedGraph_WritesGraphBlock()
    {
        using var sw = new StringWriter();
        using var writer = new TriGStreamWriter(sw);

        writer.WriteQuad(
            "<http://example.org/s>".AsSpan(),
            "<http://example.org/p>".AsSpan(),
            "<http://example.org/o>".AsSpan(),
            "<http://example.org/g>".AsSpan());
        writer.Flush();

        var result = sw.ToString();
        Assert.Contains("GRAPH <http://example.org/g>", result);
        Assert.Contains("{", result);
        Assert.Contains("}", result);
    }

    [Fact]
    public void WriteQuad_NamedGraphShorthand_WritesWithoutKeyword()
    {
        using var sw = new StringWriter();
        using var writer = new TriGStreamWriter(sw, useGraphKeyword: false);

        writer.WriteQuad(
            "<http://example.org/s>".AsSpan(),
            "<http://example.org/p>".AsSpan(),
            "<http://example.org/o>".AsSpan(),
            "<http://example.org/g>".AsSpan());
        writer.Flush();

        var result = sw.ToString();
        Assert.DoesNotContain("GRAPH", result);
        Assert.Contains("<http://example.org/g> {", result);
    }

    [Fact]
    public void WriteQuad_MultipleGraphs_SeparatesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new TriGStreamWriter(sw);

        writer.WriteQuad("<http://ex.org/s1>", "<http://ex.org/p1>", "<http://ex.org/o1>", null);
        writer.WriteQuad("<http://ex.org/s2>", "<http://ex.org/p2>", "<http://ex.org/o2>", "<http://ex.org/g1>");
        writer.WriteQuad("<http://ex.org/s3>", "<http://ex.org/p3>", "<http://ex.org/o3>", "<http://ex.org/g2>");
        writer.Flush();

        var result = sw.ToString();
        Assert.Contains("GRAPH <http://ex.org/g1>", result);
        Assert.Contains("GRAPH <http://ex.org/g2>", result);
        // Count closing braces - should be 2 (one for each named graph)
        Assert.Equal(2, CountOccurrences(result, "}"));
    }

    #endregion

    #region Subject Grouping

    [Fact]
    public void WriteQuad_SameSubject_GroupsWithSemicolon()
    {
        using var sw = new StringWriter();
        using var writer = new TriGStreamWriter(sw);

        writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/p1>", "<http://ex.org/o1>", "<http://ex.org/g>");
        writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/p2>", "<http://ex.org/o2>", "<http://ex.org/g>");
        writer.Flush();

        var result = sw.ToString();
        Assert.Contains(";", result);
        // Subject should only appear once
        Assert.Equal(1, CountOccurrences(result, "<http://ex.org/s>"));
    }

    [Fact]
    public void WriteQuad_DifferentSubjects_SeparatesWithPeriod()
    {
        using var sw = new StringWriter();
        using var writer = new TriGStreamWriter(sw);

        writer.WriteQuad("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o>", "<http://ex.org/g>");
        writer.WriteQuad("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o>", "<http://ex.org/g>");
        writer.Flush();

        var result = sw.ToString();
        Assert.Contains("<http://ex.org/s1>", result);
        Assert.Contains("<http://ex.org/s2>", result);
    }

    [Fact]
    public void WriteQuad_GraphChange_ClosesSubjectGroup()
    {
        using var sw = new StringWriter();
        using var writer = new TriGStreamWriter(sw);

        writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/p1>", "<http://ex.org/o1>", "<http://ex.org/g1>");
        writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/p2>", "<http://ex.org/o2>", "<http://ex.org/g2>");
        writer.Flush();

        var result = sw.ToString();
        Assert.Contains("GRAPH <http://ex.org/g1>", result);
        Assert.Contains("GRAPH <http://ex.org/g2>", result);
        // Subject appears twice (once in each graph)
        Assert.Equal(2, CountOccurrences(result, "<http://ex.org/s>"));
    }

    #endregion

    #region Prefixes

    [Fact]
    public void WritePrefixes_OutputsDeclarations()
    {
        using var sw = new StringWriter();
        using var writer = new TriGStreamWriter(sw);

        writer.RegisterPrefix("ex", "http://example.org/");
        writer.RegisterPrefix("foaf", "http://xmlns.com/foaf/0.1/");
        writer.WritePrefixes();

        var result = sw.ToString();
        Assert.Contains("@prefix ex: <http://example.org/>", result);
        Assert.Contains("@prefix foaf: <http://xmlns.com/foaf/0.1/>", result);
    }

    [Fact]
    public void WriteQuad_WithPrefixes_AbbreviatesIris()
    {
        using var sw = new StringWriter();
        using var writer = new TriGStreamWriter(sw);

        writer.RegisterPrefix("ex", "http://example.org/");
        writer.WritePrefixes();

        writer.WriteQuad(
            "<http://example.org/subject>",
            "<http://example.org/predicate>",
            "<http://example.org/object>",
            "<http://example.org/graph>");
        writer.Flush();

        var result = sw.ToString();
        Assert.Contains("ex:subject", result);
        Assert.Contains("ex:predicate", result);
        Assert.Contains("ex:object", result);
        Assert.Contains("ex:graph", result);
    }

    [Fact]
    public void WriteQuad_RdfType_UsesAShortcut()
    {
        using var sw = new StringWriter();
        using var writer = new TriGStreamWriter(sw);

        writer.WriteQuad(
            "<http://example.org/s>",
            "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>",
            "<http://example.org/Type>",
            "<http://example.org/g>");
        writer.Flush();

        var result = sw.ToString();
        Assert.Contains(" a ", result);
    }

    #endregion

    #region Blank Nodes

    [Fact]
    public void WriteQuad_BlankNodeSubject_WritesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new TriGStreamWriter(sw);

        writer.WriteQuad("_:b1", "<http://ex.org/p>", "<http://ex.org/o>", "<http://ex.org/g>");
        writer.Flush();

        var result = sw.ToString();
        Assert.Contains("_:b1", result);
    }

    [Fact]
    public void WriteQuad_BlankNodeObject_WritesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new TriGStreamWriter(sw);

        writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/p>", "_:blank123", "<http://ex.org/g>");
        writer.Flush();

        var result = sw.ToString();
        Assert.Contains("_:blank123", result);
    }

    [Fact]
    public void WriteQuad_BlankNodeGraph_WritesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new TriGStreamWriter(sw);

        writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>", "_:g1");
        writer.Flush();

        var result = sw.ToString();
        Assert.Contains("GRAPH _:g1", result);
    }

    #endregion

    #region Literals

    [Fact]
    public void WriteQuad_PlainLiteral_WritesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new TriGStreamWriter(sw);

        writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/p>", "\"Hello World\"", "<http://ex.org/g>");
        writer.Flush();

        var result = sw.ToString();
        Assert.Contains("\"Hello World\"", result);
    }

    [Fact]
    public void WriteQuad_TypedLiteral_WritesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new TriGStreamWriter(sw);

        writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/p>",
            "\"42\"^^<http://www.w3.org/2001/XMLSchema#integer>", "<http://ex.org/g>");
        writer.Flush();

        var result = sw.ToString();
        Assert.Contains("\"42\"^^<http://www.w3.org/2001/XMLSchema#integer>", result);
    }

    [Fact]
    public void WriteQuad_LanguageTaggedLiteral_WritesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new TriGStreamWriter(sw);

        writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/p>", "\"Bonjour\"@fr", "<http://ex.org/g>");
        writer.Flush();

        var result = sw.ToString();
        Assert.Contains("\"Bonjour\"@fr", result);
    }

    #endregion

    #region Async API

    [Fact]
    public async Task WriteQuadAsync_WritesCorrectly()
    {
        await using var sw = new StringWriter();
        await using var writer = new TriGStreamWriter(sw);

        await writer.WriteQuadAsync(
            "<http://example.org/s>",
            "<http://example.org/p>",
            "<http://example.org/o>",
            "<http://example.org/g>");
        await writer.FlushAsync();

        var result = sw.ToString();
        Assert.Contains("GRAPH <http://example.org/g>", result);
    }

    [Fact]
    public async Task WritePrefixesAsync_WritesCorrectly()
    {
        await using var sw = new StringWriter();
        await using var writer = new TriGStreamWriter(sw);

        writer.RegisterPrefix("ex", "http://example.org/");
        await writer.WritePrefixesAsync();

        var result = sw.ToString();
        Assert.Contains("@prefix ex: <http://example.org/>", result);
    }

    #endregion

    #region Raw Writing

    [Fact]
    public void WriteRawQuad_WritesWithoutAbbreviation()
    {
        using var sw = new StringWriter();
        using var writer = new TriGStreamWriter(sw);

        writer.RegisterPrefix("ex", "http://example.org/");
        writer.WritePrefixes();

        writer.WriteRawQuad(
            "<http://example.org/subject>".AsSpan(),
            "<http://example.org/predicate>".AsSpan(),
            "<http://example.org/object>".AsSpan(),
            "<http://example.org/graph>".AsSpan());
        writer.Flush();

        var result = sw.ToString();
        // Raw should not abbreviate
        Assert.Contains("<http://example.org/subject>", result);
        Assert.DoesNotContain("ex:subject", result);
    }

    #endregion

    #region Roundtrip

    [Fact]
    public async Task Roundtrip_WriteAndParse_PreservesData()
    {
        // Write quads
        using var sw = new StringWriter();
        using (var writer = new TriGStreamWriter(sw))
        {
            writer.RegisterPrefix("ex", "http://example.org/");
            writer.WritePrefixes();

            writer.WriteQuad("<http://ex.org/s1>", "<http://ex.org/p1>", "<http://ex.org/o1>", null);
            writer.WriteQuad("<http://ex.org/s2>", "<http://ex.org/p2>", "\"literal value\"", "<http://ex.org/g1>");
            writer.WriteQuad("<http://ex.org/s3>", "<http://ex.org/p3>", "_:blank", "<http://ex.org/g2>");
        }

        var trig = sw.ToString();

        // Parse quads back
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(trig));
        var parser = new TriGStreamParser(stream);

        var parsed = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            parsed.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Equal(3, parsed.Count);
        Assert.Contains(parsed, q => q.S == "<http://ex.org/s1>" && string.IsNullOrEmpty(q.G));
        Assert.Contains(parsed, q => q.S == "<http://ex.org/s2>" && q.G == "<http://ex.org/g1>");
        Assert.Contains(parsed, q => q.S == "<http://ex.org/s3>" && q.G == "<http://ex.org/g2>");
    }

    #endregion

    #region Helper Methods

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

    #endregion
}
