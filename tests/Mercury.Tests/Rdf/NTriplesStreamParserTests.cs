using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SkyOmega.Mercury.NTriples;
using SkyOmega.Mercury.Rdf;
using Xunit;

namespace SkyOmega.Mercury.Tests.Rdf;

/// <summary>
/// Tests for NTriplesStreamParser - streaming zero-GC N-Triples parser.
/// </summary>
public class NTriplesStreamParserTests
{
    #region Basic Parsing

    [Fact]
    public async Task ParseAsync_BasicTriple_ParsesCorrectly()
    {
        var ntriples = "<http://example.org/subject> <http://example.org/predicate> <http://example.org/object> ."u8.ToArray();

        await using var stream = new MemoryStream(ntriples);
        var parser = new NTriplesStreamParser(stream);

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
        var ntriples = """
            <http://ex.org/s1> <http://ex.org/p1> <http://ex.org/o1> .
            <http://ex.org/s2> <http://ex.org/p2> <http://ex.org/o2> .
            <http://ex.org/s3> <http://ex.org/p3> <http://ex.org/o3> .
            """u8.ToArray();

        await using var stream = new MemoryStream(ntriples);
        var parser = new NTriplesStreamParser(stream);

        int count = 0;
        await parser.ParseAsync((s, p, o) => count++);

        Assert.Equal(3, count);
    }

    [Fact]
    public async Task ParseAsync_WithComments_SkipsComments()
    {
        var ntriples = """
            # This is a comment
            <http://ex.org/s1> <http://ex.org/p1> <http://ex.org/o1> .
            # Another comment
            <http://ex.org/s2> <http://ex.org/p2> <http://ex.org/o2> .
            """u8.ToArray();

        await using var stream = new MemoryStream(ntriples);
        var parser = new NTriplesStreamParser(stream);

        int count = 0;
        await parser.ParseAsync((s, p, o) => count++);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task ParseAsync_WithBlankLines_SkipsBlankLines()
    {
        var ntriples = """

            <http://ex.org/s1> <http://ex.org/p1> <http://ex.org/o1> .

            <http://ex.org/s2> <http://ex.org/p2> <http://ex.org/o2> .

            """u8.ToArray();

        await using var stream = new MemoryStream(ntriples);
        var parser = new NTriplesStreamParser(stream);

        int count = 0;
        await parser.ParseAsync((s, p, o) => count++);

        Assert.Equal(2, count);
    }

    #endregion

    #region Blank Nodes

    [Fact]
    public async Task ParseAsync_BlankNodeSubject_ParsesCorrectly()
    {
        var ntriples = "_:b1 <http://ex.org/p> <http://ex.org/o> ."u8.ToArray();

        await using var stream = new MemoryStream(ntriples);
        var parser = new NTriplesStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Single(triples);
        Assert.Equal("_:b1", triples[0].S);
    }

    [Fact]
    public async Task ParseAsync_BlankNodeObject_ParsesCorrectly()
    {
        var ntriples = "<http://ex.org/s> <http://ex.org/p> _:blank123 ."u8.ToArray();

        await using var stream = new MemoryStream(ntriples);
        var parser = new NTriplesStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Single(triples);
        Assert.Equal("_:blank123", triples[0].O);
    }

    [Fact]
    public async Task ParseAsync_BlankNodeWithUnderscore_ParsesCorrectly()
    {
        var ntriples = "_:blank_node_with_underscores <http://ex.org/p> <http://ex.org/o> ."u8.ToArray();

        await using var stream = new MemoryStream(ntriples);
        var parser = new NTriplesStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Single(triples);
        Assert.Equal("_:blank_node_with_underscores", triples[0].S);
    }

    #endregion

    #region Literals

    [Fact]
    public async Task ParseAsync_PlainLiteral_ParsesCorrectly()
    {
        var ntriples = """<http://ex.org/s> <http://ex.org/p> "Hello World" ."""u8.ToArray();

        await using var stream = new MemoryStream(ntriples);
        var parser = new NTriplesStreamParser(stream);

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
        var ntriples = """<http://ex.org/s> <http://ex.org/p> "42"^^<http://www.w3.org/2001/XMLSchema#integer> ."""u8.ToArray();

        await using var stream = new MemoryStream(ntriples);
        var parser = new NTriplesStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Single(triples);
        Assert.Equal("\"42\"^^<http://www.w3.org/2001/XMLSchema#integer>", triples[0].O);
    }

    [Fact]
    public async Task ParseAsync_LanguageTaggedLiteral_ParsesCorrectly()
    {
        var ntriples = """<http://ex.org/s> <http://ex.org/p> "Bonjour"@fr ."""u8.ToArray();

        await using var stream = new MemoryStream(ntriples);
        var parser = new NTriplesStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Single(triples);
        Assert.Equal("\"Bonjour\"@fr", triples[0].O);
    }

    [Fact]
    public async Task ParseAsync_LiteralWithEscapes_ParsesCorrectly()
    {
        var ntriples = """<http://ex.org/s> <http://ex.org/p> "Line1\nLine2\tTabbed" ."""u8.ToArray();

        await using var stream = new MemoryStream(ntriples);
        var parser = new NTriplesStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Single(triples);
        // Escapes are decoded in output
        Assert.Contains("\n", triples[0].O);
        Assert.Contains("\t", triples[0].O);
    }

    [Fact]
    public async Task ParseAsync_LiteralWithQuotes_ParsesCorrectly()
    {
        var ntriples = """<http://ex.org/s> <http://ex.org/p> "He said \"Hello\"" ."""u8.ToArray();

        await using var stream = new MemoryStream(ntriples);
        var parser = new NTriplesStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Single(triples);
        Assert.Contains("\"", triples[0].O.Substring(1, triples[0].O.Length - 2)); // Check quotes inside
    }

    [Fact]
    public async Task ParseAsync_LiteralWithBackslash_ParsesCorrectly()
    {
        var ntriples = """<http://ex.org/s> <http://ex.org/p> "C:\\Users\\Test" ."""u8.ToArray();

        await using var stream = new MemoryStream(ntriples);
        var parser = new NTriplesStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Single(triples);
        Assert.Contains("\\", triples[0].O);
    }

    #endregion

    #region Unicode

    [Fact]
    public async Task ParseAsync_UnicodeEscape_4Digit_ParsesCorrectly()
    {
        // \u00E9 = é
        var ntriples = """<http://ex.org/s> <http://ex.org/p> "caf\u00E9" ."""u8.ToArray();

        await using var stream = new MemoryStream(ntriples);
        var parser = new NTriplesStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Single(triples);
        Assert.Contains("café", triples[0].O);
    }

    [Fact]
    public async Task ParseAsync_UnicodeEscape_8Digit_ParsesCorrectly()
    {
        // \U0001F600 = grinning face emoji
        var ntriples = """<http://ex.org/s> <http://ex.org/p> "smile\U0001F600" ."""u8.ToArray();

        await using var stream = new MemoryStream(ntriples);
        var parser = new NTriplesStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Single(triples);
        // Note: Unicode code point may be represented differently
        Assert.StartsWith("\"smile", triples[0].O);
    }

    [Fact]
    public async Task ParseAsync_UnicodeInIri_ParsesCorrectly()
    {
        // IRI with unicode escape
        var ntriples = "<http://ex.org/caf\\u00E9> <http://ex.org/p> <http://ex.org/o> ."u8.ToArray();

        await using var stream = new MemoryStream(ntriples);
        var parser = new NTriplesStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Single(triples);
        Assert.Contains("café", triples[0].S);
    }

    #endregion

    #region Legacy API

    [Fact]
    public async Task ParseAsync_LegacyApi_ReturnsRdfTriples()
    {
        var ntriples = """
            <http://ex.org/s1> <http://ex.org/p1> <http://ex.org/o1> .
            <http://ex.org/s2> <http://ex.org/p2> "literal" .
            """u8.ToArray();

        await using var stream = new MemoryStream(ntriples);
        var parser = new NTriplesStreamParser(stream);

        var triples = new List<RdfTriple>();
        await foreach (var triple in parser.ParseAsync())
        {
            triples.Add(triple);
        }

        Assert.Equal(2, triples.Count);
        Assert.Equal("<http://ex.org/s1>", triples[0].Subject);
        Assert.Equal("\"literal\"", triples[1].Object);
    }

    #endregion

    #region Zero-GC Verification

    [Fact]
    public async Task ParseAsync_ZeroGC_MinimalAllocations()
    {
        var ntriples = """
            <http://ex.org/s1> <http://ex.org/p1> <http://ex.org/o1> .
            <http://ex.org/s2> <http://ex.org/p2> "literal value" .
            <http://ex.org/s3> <http://ex.org/p3> _:blank .
            """u8.ToArray();

        // Warmup
        await using (var warmupStream = new MemoryStream(ntriples))
        {
            var warmupParser = new NTriplesStreamParser(warmupStream);
            await warmupParser.ParseAsync((s, p, o) => { });
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        int tripleCount = 0;

        await using (var stream = new MemoryStream(ntriples))
        {
            var parser = new NTriplesStreamParser(stream);
            await parser.ParseAsync((s, p, o) =>
            {
                tripleCount++;
                _ = s.Length + p.Length + o.Length;
            });
        }

        var after = GC.GetAllocatedBytesForCurrentThread();
        var allocated = after - before;

        // Zero-GC target: minimal allocations (async state machine overhead)
        Assert.True(allocated < 50_000,
            $"N-Triples parser allocated {allocated} bytes for {tripleCount} triples. Expected < 50KB.");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task ParseAsync_EmptyDocument_ReturnsNoTriples()
    {
        var ntriples = ""u8.ToArray();

        await using var stream = new MemoryStream(ntriples);
        var parser = new NTriplesStreamParser(stream);

        int count = 0;
        await parser.ParseAsync((s, p, o) => count++);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ParseAsync_OnlyComments_ReturnsNoTriples()
    {
        var ntriples = """
            # Comment 1
            # Comment 2
            # Comment 3
            """u8.ToArray();

        await using var stream = new MemoryStream(ntriples);
        var parser = new NTriplesStreamParser(stream);

        int count = 0;
        await parser.ParseAsync((s, p, o) => count++);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ParseAsync_LongIri_ParsesCorrectly()
    {
        var longPath = new string('x', 1000);
        var ntriples = Encoding.UTF8.GetBytes(
            $"<http://example.org/{longPath}> <http://ex.org/p> <http://ex.org/o> ."
        );

        await using var stream = new MemoryStream(ntriples);
        var parser = new NTriplesStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Single(triples);
        Assert.Contains(longPath, triples[0].S);
    }

    [Fact]
    public async Task ParseAsync_LongLiteral_ParsesCorrectly()
    {
        var longText = new string('A', 5000);
        var ntriples = Encoding.UTF8.GetBytes(
            $"<http://ex.org/s> <http://ex.org/p> \"{longText}\" ."
        );

        await using var stream = new MemoryStream(ntriples);
        var parser = new NTriplesStreamParser(stream);

        var triples = new List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        Assert.Single(triples);
        Assert.Contains(longText, triples[0].O);
    }

    [Fact]
    public async Task ParseAsync_StreamingLargeFile_DoesNotOOM()
    {
        // Generate 10,000 triples
        var sb = new StringBuilder();
        for (int i = 0; i < 10_000; i++)
        {
            sb.AppendLine($"<http://ex.org/s{i}> <http://ex.org/p> <http://ex.org/o{i}> .");
        }

        var ntriples = Encoding.UTF8.GetBytes(sb.ToString());

        await using var stream = new MemoryStream(ntriples);
        var parser = new NTriplesStreamParser(stream, bufferSize: 4096); // Small buffer to test streaming

        int count = 0;
        await parser.ParseAsync((s, p, o) => count++);

        Assert.Equal(10_000, count);
    }

    #endregion
}
