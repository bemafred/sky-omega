using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SkyOmega.Mercury.NQuads;
using Xunit;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for NQuadsStreamParser - streaming zero-GC N-Quads parser.
/// </summary>
public class NQuadsStreamParserTests
{
    #region Basic Parsing

    [Fact]
    public async Task ParseAsync_QuadWithGraph_ParsesCorrectly()
    {
        var nquads = "<http://example.org/s> <http://example.org/p> <http://example.org/o> <http://example.org/g> ."u8.ToArray();

        await using var stream = new MemoryStream(nquads);
        var parser = new NQuadsStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Single(quads);
        Assert.Equal("<http://example.org/s>", quads[0].S);
        Assert.Equal("<http://example.org/p>", quads[0].P);
        Assert.Equal("<http://example.org/o>", quads[0].O);
        Assert.Equal("<http://example.org/g>", quads[0].G);
    }

    [Fact]
    public async Task ParseAsync_TripleWithoutGraph_ParsesAsDefaultGraph()
    {
        var nquads = "<http://example.org/s> <http://example.org/p> <http://example.org/o> ."u8.ToArray();

        await using var stream = new MemoryStream(nquads);
        var parser = new NQuadsStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Single(quads);
        Assert.Equal("<http://example.org/s>", quads[0].S);
        Assert.Equal("<http://example.org/p>", quads[0].P);
        Assert.Equal("<http://example.org/o>", quads[0].O);
        Assert.Empty(quads[0].G); // Default graph = empty
    }

    [Fact]
    public async Task ParseAsync_MultipleQuads_ParsesAll()
    {
        var nquads = """
            <http://ex.org/s1> <http://ex.org/p1> <http://ex.org/o1> <http://ex.org/g1> .
            <http://ex.org/s2> <http://ex.org/p2> <http://ex.org/o2> .
            <http://ex.org/s3> <http://ex.org/p3> <http://ex.org/o3> <http://ex.org/g2> .
            """u8.ToArray();

        await using var stream = new MemoryStream(nquads);
        var parser = new NQuadsStreamParser(stream);

        int count = 0;
        await parser.ParseAsync((s, p, o, g) => count++);

        Assert.Equal(3, count);
    }

    [Fact]
    public async Task ParseAsync_WithComments_SkipsComments()
    {
        var nquads = """
            # This is a comment
            <http://ex.org/s1> <http://ex.org/p1> <http://ex.org/o1> <http://ex.org/g1> .
            # Another comment
            <http://ex.org/s2> <http://ex.org/p2> <http://ex.org/o2> .
            """u8.ToArray();

        await using var stream = new MemoryStream(nquads);
        var parser = new NQuadsStreamParser(stream);

        int count = 0;
        await parser.ParseAsync((s, p, o, g) => count++);

        Assert.Equal(2, count);
    }

    #endregion

    #region Named Graph Variations

    [Fact]
    public async Task ParseAsync_BlankNodeAsGraph_ParsesCorrectly()
    {
        var nquads = "<http://ex.org/s> <http://ex.org/p> <http://ex.org/o> _:graphB1 ."u8.ToArray();

        await using var stream = new MemoryStream(nquads);
        var parser = new NQuadsStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Single(quads);
        Assert.Equal("_:graphB1", quads[0].G);
    }

    [Fact]
    public async Task ParseAsync_SameSubjectDifferentGraphs_ParsesAllQuads()
    {
        var nquads = """
            <http://ex.org/s> <http://ex.org/p> "value1" <http://ex.org/g1> .
            <http://ex.org/s> <http://ex.org/p> "value2" <http://ex.org/g2> .
            <http://ex.org/s> <http://ex.org/p> "value3" .
            """u8.ToArray();

        await using var stream = new MemoryStream(nquads);
        var parser = new NQuadsStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Equal(3, quads.Count);
        Assert.Equal("<http://ex.org/g1>", quads[0].G);
        Assert.Equal("<http://ex.org/g2>", quads[1].G);
        Assert.Empty(quads[2].G);
    }

    #endregion

    #region Blank Nodes

    [Fact]
    public async Task ParseAsync_BlankNodeSubject_ParsesCorrectly()
    {
        var nquads = "_:b1 <http://ex.org/p> <http://ex.org/o> <http://ex.org/g> ."u8.ToArray();

        await using var stream = new MemoryStream(nquads);
        var parser = new NQuadsStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Single(quads);
        Assert.Equal("_:b1", quads[0].S);
    }

    [Fact]
    public async Task ParseAsync_BlankNodeObject_ParsesCorrectly()
    {
        var nquads = "<http://ex.org/s> <http://ex.org/p> _:blank123 <http://ex.org/g> ."u8.ToArray();

        await using var stream = new MemoryStream(nquads);
        var parser = new NQuadsStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Single(quads);
        Assert.Equal("_:blank123", quads[0].O);
    }

    #endregion

    #region Literals

    [Fact]
    public async Task ParseAsync_PlainLiteralInNamedGraph_ParsesCorrectly()
    {
        var nquads = """<http://ex.org/s> <http://ex.org/p> "Hello World" <http://ex.org/g> ."""u8.ToArray();

        await using var stream = new MemoryStream(nquads);
        var parser = new NQuadsStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Single(quads);
        Assert.Equal("\"Hello World\"", quads[0].O);
        Assert.Equal("<http://ex.org/g>", quads[0].G);
    }

    [Fact]
    public async Task ParseAsync_TypedLiteral_ParsesCorrectly()
    {
        var nquads = """<http://ex.org/s> <http://ex.org/p> "42"^^<http://www.w3.org/2001/XMLSchema#integer> <http://ex.org/g> ."""u8.ToArray();

        await using var stream = new MemoryStream(nquads);
        var parser = new NQuadsStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Single(quads);
        Assert.Equal("\"42\"^^<http://www.w3.org/2001/XMLSchema#integer>", quads[0].O);
    }

    [Fact]
    public async Task ParseAsync_LanguageTaggedLiteral_ParsesCorrectly()
    {
        var nquads = """<http://ex.org/s> <http://ex.org/p> "Bonjour"@fr <http://ex.org/g> ."""u8.ToArray();

        await using var stream = new MemoryStream(nquads);
        var parser = new NQuadsStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Single(quads);
        Assert.Equal("\"Bonjour\"@fr", quads[0].O);
    }

    [Fact]
    public async Task ParseAsync_LiteralWithEscapes_ParsesCorrectly()
    {
        var nquads = """<http://ex.org/s> <http://ex.org/p> "Line1\nLine2\tTabbed" <http://ex.org/g> ."""u8.ToArray();

        await using var stream = new MemoryStream(nquads);
        var parser = new NQuadsStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Single(quads);
        Assert.Contains("\n", quads[0].O);
        Assert.Contains("\t", quads[0].O);
    }

    #endregion

    #region Unicode

    [Fact]
    public async Task ParseAsync_UnicodeEscape_4Digit_ParsesCorrectly()
    {
        var nquads = """<http://ex.org/s> <http://ex.org/p> "caf\u00E9" <http://ex.org/g> ."""u8.ToArray();

        await using var stream = new MemoryStream(nquads);
        var parser = new NQuadsStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Single(quads);
        Assert.Contains("café", quads[0].O);
    }

    #endregion

    #region Legacy API

    [Fact]
    public async Task ParseAsync_LegacyApi_ReturnsRdfQuads()
    {
        var nquads = """
            <http://ex.org/s1> <http://ex.org/p1> <http://ex.org/o1> <http://ex.org/g1> .
            <http://ex.org/s2> <http://ex.org/p2> "literal" .
            """u8.ToArray();

        await using var stream = new MemoryStream(nquads);
        var parser = new NQuadsStreamParser(stream);

        var quads = new List<RdfQuad>();
        await foreach (var quad in parser.ParseAsync())
        {
            quads.Add(quad);
        }

        Assert.Equal(2, quads.Count);
        Assert.Equal("<http://ex.org/s1>", quads[0].Subject);
        Assert.Equal("<http://ex.org/g1>", quads[0].Graph);
        Assert.Equal("\"literal\"", quads[1].Object);
        Assert.Null(quads[1].Graph); // Default graph
    }

    [Fact]
    public void RdfQuad_ToNQuads_FormatsCorrectly()
    {
        var quad = new RdfQuad(
            "<http://ex.org/s>",
            "<http://ex.org/p>",
            "<http://ex.org/o>",
            "<http://ex.org/g>");

        var result = quad.ToNQuads();

        Assert.Equal("<http://ex.org/s> <http://ex.org/p> <http://ex.org/o> <http://ex.org/g> .", result);
    }

    [Fact]
    public void RdfQuad_ToNQuads_DefaultGraph_OmitsGraph()
    {
        var quad = new RdfQuad(
            "<http://ex.org/s>",
            "<http://ex.org/p>",
            "<http://ex.org/o>",
            null);

        var result = quad.ToNQuads();

        Assert.Equal("<http://ex.org/s> <http://ex.org/p> <http://ex.org/o> .", result);
    }

    #endregion

    #region Zero-GC Verification

    [Fact]
    public async Task ParseAsync_ZeroGC_MinimalAllocations()
    {
        var nquads = """
            <http://ex.org/s1> <http://ex.org/p1> <http://ex.org/o1> <http://ex.org/g1> .
            <http://ex.org/s2> <http://ex.org/p2> "literal value" <http://ex.org/g2> .
            <http://ex.org/s3> <http://ex.org/p3> _:blank .
            """u8.ToArray();

        // Warmup
        await using (var warmupStream = new MemoryStream(nquads))
        {
            var warmupParser = new NQuadsStreamParser(warmupStream);
            await warmupParser.ParseAsync((s, p, o, g) => { });
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        int quadCount = 0;

        await using (var stream = new MemoryStream(nquads))
        {
            var parser = new NQuadsStreamParser(stream);
            await parser.ParseAsync((s, p, o, g) =>
            {
                quadCount++;
                _ = s.Length + p.Length + o.Length + g.Length;
            });
        }

        var after = GC.GetAllocatedBytesForCurrentThread();
        var allocated = after - before;

        // Zero-GC target: minimal allocations (async state machine overhead)
        Assert.True(allocated < 50_000,
            $"N-Quads parser allocated {allocated} bytes for {quadCount} quads. Expected < 50KB.");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task ParseAsync_EmptyDocument_ReturnsNoQuads()
    {
        var nquads = ""u8.ToArray();

        await using var stream = new MemoryStream(nquads);
        var parser = new NQuadsStreamParser(stream);

        int count = 0;
        await parser.ParseAsync((s, p, o, g) => count++);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ParseAsync_OnlyComments_ReturnsNoQuads()
    {
        var nquads = """
            # Comment 1
            # Comment 2
            # Comment 3
            """u8.ToArray();

        await using var stream = new MemoryStream(nquads);
        var parser = new NQuadsStreamParser(stream);

        int count = 0;
        await parser.ParseAsync((s, p, o, g) => count++);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ParseAsync_StreamingLargeFile_DoesNotOOM()
    {
        // Generate 10,000 quads
        var sb = new StringBuilder();
        for (int i = 0; i < 10_000; i++)
        {
            var graph = i % 2 == 0 ? " <http://ex.org/graph>" : "";
            sb.AppendLine($"<http://ex.org/s{i}> <http://ex.org/p> <http://ex.org/o{i}>{graph} .");
        }

        var nquads = Encoding.UTF8.GetBytes(sb.ToString());

        await using var stream = new MemoryStream(nquads);
        var parser = new NQuadsStreamParser(stream, bufferSize: 4096);

        int count = 0;
        await parser.ParseAsync((s, p, o, g) => count++);

        Assert.Equal(10_000, count);
    }

    #endregion
}
