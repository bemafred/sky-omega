using System;
using System.IO;
using System.Threading.Tasks;
using SkyOmega.Mercury.NQuads;
using Xunit;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for NQuadsStreamWriter - streaming zero-GC N-Quads writer.
/// </summary>
public class NQuadsStreamWriterTests
{
    #region Basic Writing

    [Fact]
    public void WriteQuad_WithNamedGraph_WritesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new NQuadsStreamWriter(sw);

        writer.WriteQuad(
            "<http://example.org/s>".AsSpan(),
            "<http://example.org/p>".AsSpan(),
            "<http://example.org/o>".AsSpan(),
            "<http://example.org/g>".AsSpan());

        var result = sw.ToString();
        Assert.Equal("<http://example.org/s> <http://example.org/p> <http://example.org/o> <http://example.org/g> .\n", result);
    }

    [Fact]
    public void WriteQuad_DefaultGraph_OmitsGraphLabel()
    {
        using var sw = new StringWriter();
        using var writer = new NQuadsStreamWriter(sw);

        writer.WriteQuad(
            "<http://example.org/s>".AsSpan(),
            "<http://example.org/p>".AsSpan(),
            "<http://example.org/o>".AsSpan());

        var result = sw.ToString();
        Assert.Equal("<http://example.org/s> <http://example.org/p> <http://example.org/o> .\n", result);
    }

    [Fact]
    public void WriteTriple_WritesToDefaultGraph()
    {
        using var sw = new StringWriter();
        using var writer = new NQuadsStreamWriter(sw);

        writer.WriteTriple(
            "<http://example.org/s>".AsSpan(),
            "<http://example.org/p>".AsSpan(),
            "<http://example.org/o>".AsSpan());

        var result = sw.ToString();
        Assert.Equal("<http://example.org/s> <http://example.org/p> <http://example.org/o> .\n", result);
    }

    [Fact]
    public void WriteQuad_MultipleQuads_WritesAllCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new NQuadsStreamWriter(sw);

        writer.WriteQuad("<http://ex.org/s1>", "<http://ex.org/p1>", "<http://ex.org/o1>", "<http://ex.org/g1>");
        writer.WriteQuad("<http://ex.org/s2>", "<http://ex.org/p2>", "<http://ex.org/o2>", "<http://ex.org/g2>");
        writer.WriteQuad("<http://ex.org/s3>", "<http://ex.org/p3>", "<http://ex.org/o3>");

        var result = sw.ToString();
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.Contains("<http://ex.org/g1>", lines[0]);
        Assert.Contains("<http://ex.org/g2>", lines[1]);
        Assert.DoesNotContain("<http://ex.org/g", lines[2].Replace("<http://ex.org/o3>", ""));
    }

    #endregion

    #region Graph Types

    [Fact]
    public void WriteQuad_BlankNodeGraph_WritesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new NQuadsStreamWriter(sw);

        writer.WriteQuad(
            "<http://ex.org/s>".AsSpan(),
            "<http://ex.org/p>".AsSpan(),
            "<http://ex.org/o>".AsSpan(),
            "_:g1".AsSpan());

        var result = sw.ToString();
        Assert.Contains("_:g1 .", result);
    }

    #endregion

    #region Blank Nodes

    [Fact]
    public void WriteQuad_BlankNodeSubject_WritesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new NQuadsStreamWriter(sw);

        writer.WriteQuad("_:b1", "<http://ex.org/p>", "<http://ex.org/o>", "<http://ex.org/g>");

        var result = sw.ToString();
        Assert.StartsWith("_:b1", result);
    }

    [Fact]
    public void WriteQuad_BlankNodeObject_WritesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new NQuadsStreamWriter(sw);

        writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/p>", "_:blank123", "<http://ex.org/g>");

        var result = sw.ToString();
        Assert.Contains("_:blank123", result);
    }

    #endregion

    #region Literals

    [Fact]
    public void WriteQuad_PlainLiteral_WritesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new NQuadsStreamWriter(sw);

        writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/p>", "\"Hello World\"", "<http://ex.org/g>");

        var result = sw.ToString();
        Assert.Contains("\"Hello World\"", result);
    }

    [Fact]
    public void WriteQuad_TypedLiteral_WritesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new NQuadsStreamWriter(sw);

        writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/p>",
            "\"42\"^^<http://www.w3.org/2001/XMLSchema#integer>", "<http://ex.org/g>");

        var result = sw.ToString();
        Assert.Contains("\"42\"^^<http://www.w3.org/2001/XMLSchema#integer>", result);
    }

    [Fact]
    public void WriteQuad_LanguageTaggedLiteral_WritesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new NQuadsStreamWriter(sw);

        writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/p>", "\"Bonjour\"@fr", "<http://ex.org/g>");

        var result = sw.ToString();
        Assert.Contains("\"Bonjour\"@fr", result);
    }

    [Fact]
    public void WriteQuad_LiteralWithNewline_EscapesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new NQuadsStreamWriter(sw);

        writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/p>", "\"Line1\nLine2\"", "<http://ex.org/g>");

        var result = sw.ToString();
        Assert.Contains("\\n", result);
    }

    [Fact]
    public void WriteQuad_LiteralWithTab_EscapesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new NQuadsStreamWriter(sw);

        writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/p>", "\"Col1\tCol2\"", "<http://ex.org/g>");

        var result = sw.ToString();
        Assert.Contains("\\t", result);
    }

    [Fact]
    public void WriteQuad_LiteralWithQuotes_EscapesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new NQuadsStreamWriter(sw);

        // Input literal with escaped quotes inside: "He said \"Hello\""
        writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/p>", "\"He said \\\"Hello\\\"\"", "<http://ex.org/g>");

        var result = sw.ToString();
        // The writer should preserve the escaped quotes
        Assert.Contains("\\\"", result);
    }

    [Fact]
    public void WriteQuad_LiteralWithBackslash_EscapesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new NQuadsStreamWriter(sw);

        writer.WriteQuad("<http://ex.org/s>", "<http://ex.org/p>", "\"C:\\Users\\Test\"", "<http://ex.org/g>");

        var result = sw.ToString();
        Assert.Contains("\\\\", result);
    }

    #endregion

    #region Async API

    [Fact]
    public async Task WriteQuadAsync_WithNamedGraph_WritesCorrectly()
    {
        await using var sw = new StringWriter();
        await using var writer = new NQuadsStreamWriter(sw);

        await writer.WriteQuadAsync(
            "http://example.org/s".AsMemory(),
            "http://example.org/p".AsMemory(),
            "http://example.org/o".AsMemory(),
            "http://example.org/g".AsMemory());

        var result = sw.ToString();
        Assert.Contains("http://example.org/g", result);
    }

    [Fact]
    public async Task WriteQuadAsync_StringOverload_WritesCorrectly()
    {
        await using var sw = new StringWriter();
        await using var writer = new NQuadsStreamWriter(sw);

        await writer.WriteQuadAsync(
            "<http://example.org/s>",
            "<http://example.org/p>",
            "<http://example.org/o>",
            "<http://example.org/g>");

        var result = sw.ToString();
        Assert.Equal("<http://example.org/s> <http://example.org/p> <http://example.org/o> <http://example.org/g> .\n", result);
    }

    [Fact]
    public async Task WriteQuadAsync_DefaultGraph_OmitsGraphLabel()
    {
        await using var sw = new StringWriter();
        await using var writer = new NQuadsStreamWriter(sw);

        await writer.WriteQuadAsync(
            "<http://example.org/s>",
            "<http://example.org/p>",
            "<http://example.org/o>");

        var result = sw.ToString();
        Assert.DoesNotContain("<http://example.org/g>", result);
        Assert.EndsWith(".\n", result);
    }

    #endregion

    #region Raw Writing

    [Fact]
    public void WriteRawQuad_WithGraph_WritesAsIs()
    {
        using var sw = new StringWriter();
        using var writer = new NQuadsStreamWriter(sw);

        writer.WriteRawQuad(
            "<http://ex.org/s>".AsSpan(),
            "<http://ex.org/p>".AsSpan(),
            "<http://ex.org/o>".AsSpan(),
            "<http://ex.org/g>".AsSpan());

        var result = sw.ToString();
        Assert.Equal("<http://ex.org/s> <http://ex.org/p> <http://ex.org/o> <http://ex.org/g> .\n", result);
    }

    [Fact]
    public void WriteRawQuad_DefaultGraph_WritesAsTriple()
    {
        using var sw = new StringWriter();
        using var writer = new NQuadsStreamWriter(sw);

        writer.WriteRawQuad(
            "<http://ex.org/s>".AsSpan(),
            "<http://ex.org/p>".AsSpan(),
            "<http://ex.org/o>".AsSpan());

        var result = sw.ToString();
        Assert.Equal("<http://ex.org/s> <http://ex.org/p> <http://ex.org/o> .\n", result);
    }

    #endregion

    #region Roundtrip

    [Fact]
    public async Task Roundtrip_WriteAndParse_PreservesData()
    {
        // Write quads
        using var sw = new StringWriter();
        using (var writer = new NQuadsStreamWriter(sw))
        {
            writer.WriteQuad("<http://ex.org/s1>", "<http://ex.org/p1>", "<http://ex.org/o1>", "<http://ex.org/g1>");
            writer.WriteQuad("<http://ex.org/s2>", "<http://ex.org/p2>", "\"literal value\"", "<http://ex.org/g2>");
            writer.WriteQuad("<http://ex.org/s3>", "<http://ex.org/p3>", "_:blank");
        }

        var nquads = sw.ToString();

        // Parse quads back
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(nquads));
        var parser = new NQuadsStreamParser(stream);

        var parsed = new System.Collections.Generic.List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            parsed.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Equal(3, parsed.Count);
        Assert.Equal("<http://ex.org/s1>", parsed[0].S);
        Assert.Equal("<http://ex.org/g1>", parsed[0].G);
        Assert.Contains("literal value", parsed[1].O);
        Assert.Equal("<http://ex.org/g2>", parsed[1].G);
        Assert.Equal("_:blank", parsed[2].O);
        Assert.Empty(parsed[2].G);
    }

    #endregion
}
