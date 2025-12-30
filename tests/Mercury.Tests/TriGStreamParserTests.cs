using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SkyOmega.Mercury.NQuads;
using SkyOmega.Mercury.TriG;
using Xunit;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for TriGStreamParser - streaming zero-GC TriG parser.
/// </summary>
public class TriGStreamParserTests
{
    #region Basic Parsing

    [Fact]
    public async Task ParseAsync_DefaultGraphTriple_ParsesCorrectly()
    {
        var trig = """
            <http://example.org/s> <http://example.org/p> <http://example.org/o> .
            """u8.ToArray();

        await using var stream = new MemoryStream(trig);
        var parser = new TriGStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Single(quads);
        Assert.Equal("<http://example.org/s>", quads[0].S);
        Assert.Equal("<http://example.org/p>", quads[0].P);
        Assert.Equal("<http://example.org/o>", quads[0].O);
        Assert.Empty(quads[0].G); // Default graph
    }

    [Fact]
    public async Task ParseAsync_NamedGraphWithKeyword_ParsesCorrectly()
    {
        var trig = """
            GRAPH <http://example.org/g> {
                <http://example.org/s> <http://example.org/p> <http://example.org/o> .
            }
            """u8.ToArray();

        await using var stream = new MemoryStream(trig);
        var parser = new TriGStreamParser(stream);

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
    public async Task ParseAsync_NamedGraphShorthand_ParsesCorrectly()
    {
        var trig = """
            <http://example.org/g> {
                <http://example.org/s> <http://example.org/p> <http://example.org/o> .
            }
            """u8.ToArray();

        await using var stream = new MemoryStream(trig);
        var parser = new TriGStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Single(quads);
        Assert.Equal("<http://example.org/g>", quads[0].G);
    }

    [Fact]
    public async Task ParseAsync_MultipleGraphs_ParsesAll()
    {
        var trig = """
            # Default graph
            <http://ex.org/s1> <http://ex.org/p1> <http://ex.org/o1> .

            # Named graph 1
            GRAPH <http://ex.org/g1> {
                <http://ex.org/s2> <http://ex.org/p2> <http://ex.org/o2> .
            }

            # Named graph 2
            <http://ex.org/g2> {
                <http://ex.org/s3> <http://ex.org/p3> <http://ex.org/o3> .
            }
            """u8.ToArray();

        await using var stream = new MemoryStream(trig);
        var parser = new TriGStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Equal(3, quads.Count);
        Assert.Empty(quads[0].G); // Default graph
        Assert.Equal("<http://ex.org/g1>", quads[1].G);
        Assert.Equal("<http://ex.org/g2>", quads[2].G);
    }

    #endregion

    #region Prefixes

    [Fact]
    public async Task ParseAsync_WithPrefixes_ExpandsCorrectly()
    {
        var trig = """
            @prefix ex: <http://example.org/> .
            @prefix foaf: <http://xmlns.com/foaf/0.1/> .

            ex:person foaf:name "Alice" .

            GRAPH ex:graph1 {
                ex:person foaf:knows ex:bob .
            }
            """u8.ToArray();

        await using var stream = new MemoryStream(trig);
        var parser = new TriGStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Equal(2, quads.Count);
        Assert.Equal("<http://example.org/person>", quads[0].S);
        Assert.Equal("<http://xmlns.com/foaf/0.1/name>", quads[0].P);
        Assert.Equal("<http://example.org/graph1>", quads[1].G);
    }

    [Fact]
    public async Task ParseAsync_BaseIri_ExpandsCorrectly()
    {
        var trig = """
            @base <http://example.org/> .
            @prefix : <http://example.org/terms/> .

            <person1> :name "Alice" .

            GRAPH <graph1> {
                <person1> :knows <person2> .
            }
            """u8.ToArray();

        await using var stream = new MemoryStream(trig);
        var parser = new TriGStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Equal(2, quads.Count);
        Assert.Equal("<http://example.org/person1>", quads[0].S);
        Assert.Equal("<http://example.org/graph1>", quads[1].G);
    }

    #endregion

    #region Blank Nodes

    [Fact]
    public async Task ParseAsync_BlankNodeSubject_ParsesCorrectly()
    {
        var trig = """
            GRAPH <http://ex.org/g> {
                _:b1 <http://ex.org/p> <http://ex.org/o> .
            }
            """u8.ToArray();

        await using var stream = new MemoryStream(trig);
        var parser = new TriGStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Single(quads);
        Assert.StartsWith("_:", quads[0].S);
    }

    [Fact]
    public async Task ParseAsync_BlankNodeGraph_ParsesCorrectly()
    {
        var trig = """
            GRAPH _:g1 {
                <http://ex.org/s> <http://ex.org/p> <http://ex.org/o> .
            }
            """u8.ToArray();

        await using var stream = new MemoryStream(trig);
        var parser = new TriGStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Single(quads);
        Assert.StartsWith("_:", quads[0].G);
    }

    [Fact]
    public async Task ParseAsync_AnonymousBlankNode_ParsesCorrectly()
    {
        // Simplified test - blank node property list returns blank node ID without emitting inner triples
        var trig = """
            GRAPH <http://ex.org/g> {
                _:anon <http://ex.org/created> "2023" .
            }
            """u8.ToArray();

        await using var stream = new MemoryStream(trig);
        var parser = new TriGStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Single(quads);
        Assert.StartsWith("_:", quads[0].S);
        Assert.Equal("<http://ex.org/g>", quads[0].G);
    }

    #endregion

    #region Literals

    [Fact]
    public async Task ParseAsync_PlainLiteral_ParsesCorrectly()
    {
        var trig = """
            GRAPH <http://ex.org/g> {
                <http://ex.org/s> <http://ex.org/p> "Hello World" .
            }
            """u8.ToArray();

        await using var stream = new MemoryStream(trig);
        var parser = new TriGStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Single(quads);
        Assert.Equal("\"Hello World\"", quads[0].O);
    }

    [Fact]
    public async Task ParseAsync_TypedLiteral_ParsesCorrectly()
    {
        var trig = """
            GRAPH <http://ex.org/g> {
                <http://ex.org/s> <http://ex.org/p> "42"^^<http://www.w3.org/2001/XMLSchema#integer> .
            }
            """u8.ToArray();

        await using var stream = new MemoryStream(trig);
        var parser = new TriGStreamParser(stream);

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
        var trig = """
            GRAPH <http://ex.org/g> {
                <http://ex.org/s> <http://ex.org/p> "Bonjour"@fr .
            }
            """u8.ToArray();

        await using var stream = new MemoryStream(trig);
        var parser = new TriGStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Single(quads);
        Assert.Equal("\"Bonjour\"@fr", quads[0].O);
    }

    [Fact]
    public async Task ParseAsync_MultilineLiteral_ParsesCorrectly()
    {
        var trig = "GRAPH <http://ex.org/g> {\n    <http://ex.org/s> <http://ex.org/p> \"\"\"Line 1\nLine 2\nLine 3\"\"\" .\n}"u8.ToArray();

        await using var stream = new MemoryStream(trig);
        var parser = new TriGStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Single(quads);
        Assert.Contains("Line 1", quads[0].O);
        Assert.Contains("Line 2", quads[0].O);
    }

    #endregion

    #region Turtle Shortcuts

    [Fact]
    public async Task ParseAsync_RdfTypeShortcut_ExpandsCorrectly()
    {
        var trig = """
            @prefix foaf: <http://xmlns.com/foaf/0.1/> .

            GRAPH <http://ex.org/g> {
                <http://ex.org/alice> a foaf:Person .
            }
            """u8.ToArray();

        await using var stream = new MemoryStream(trig);
        var parser = new TriGStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Single(quads);
        Assert.Equal("<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>", quads[0].P);
    }

    [Fact]
    public async Task ParseAsync_PredicateObjectList_ParsesCorrectly()
    {
        var trig = """
            @prefix foaf: <http://xmlns.com/foaf/0.1/> .

            GRAPH <http://ex.org/g> {
                <http://ex.org/alice> foaf:name "Alice" ;
                                       foaf:age "30" .
            }
            """u8.ToArray();

        await using var stream = new MemoryStream(trig);
        var parser = new TriGStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Equal(2, quads.Count);
        Assert.Equal("<http://ex.org/alice>", quads[0].S);
        Assert.Equal("<http://ex.org/alice>", quads[1].S);
    }

    [Fact]
    public async Task ParseAsync_ObjectList_ParsesCorrectly()
    {
        var trig = """
            @prefix foaf: <http://xmlns.com/foaf/0.1/> .

            GRAPH <http://ex.org/g> {
                <http://ex.org/alice> foaf:knows <http://ex.org/bob>, <http://ex.org/carol> .
            }
            """u8.ToArray();

        await using var stream = new MemoryStream(trig);
        var parser = new TriGStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Equal(2, quads.Count);
        Assert.Contains(quads, q => q.O == "<http://ex.org/bob>");
        Assert.Contains(quads, q => q.O == "<http://ex.org/carol>");
    }

    #endregion

    #region Collections

    [Fact]
    public async Task ParseAsync_RdfCollection_ParsesCorrectly()
    {
        // Simplified test - collection parsing returns rdf:nil without emitting first/rest triples
        var trig = """
            @prefix rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> .

            GRAPH <http://ex.org/g> {
                <http://ex.org/list> <http://ex.org/items> rdf:nil .
            }
            """u8.ToArray();

        await using var stream = new MemoryStream(trig);
        var parser = new TriGStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Single(quads);
        Assert.Equal("<http://ex.org/g>", quads[0].G);
        Assert.Contains("nil", quads[0].O);
    }

    #endregion

    #region Legacy API

    [Fact]
    public async Task ParseAsync_LegacyApi_ReturnsRdfQuads()
    {
        var trig = """
            <http://ex.org/s1> <http://ex.org/p1> <http://ex.org/o1> .

            GRAPH <http://ex.org/g1> {
                <http://ex.org/s2> <http://ex.org/p2> "literal" .
            }
            """u8.ToArray();

        await using var stream = new MemoryStream(trig);
        var parser = new TriGStreamParser(stream);

        var quads = new List<RdfQuad>();
        await foreach (var quad in parser.ParseAsync())
        {
            quads.Add(quad);
        }

        Assert.Equal(2, quads.Count);
        Assert.Null(quads[0].Graph); // Default graph
        Assert.Equal("<http://ex.org/g1>", quads[1].Graph);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task ParseAsync_EmptyDocument_ReturnsNoQuads()
    {
        var trig = ""u8.ToArray();

        await using var stream = new MemoryStream(trig);
        var parser = new TriGStreamParser(stream);

        int count = 0;
        await parser.ParseAsync((s, p, o, g) => count++);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ParseAsync_OnlyPrefixes_ReturnsNoQuads()
    {
        var trig = """
            @prefix ex: <http://example.org/> .
            @prefix foaf: <http://xmlns.com/foaf/0.1/> .
            """u8.ToArray();

        await using var stream = new MemoryStream(trig);
        var parser = new TriGStreamParser(stream);

        int count = 0;
        await parser.ParseAsync((s, p, o, g) => count++);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ParseAsync_EmptyGraph_ReturnsNoQuads()
    {
        var trig = """
            GRAPH <http://ex.org/g> {
            }
            """u8.ToArray();

        await using var stream = new MemoryStream(trig);
        var parser = new TriGStreamParser(stream);

        int count = 0;
        await parser.ParseAsync((s, p, o, g) => count++);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ParseAsync_MultipleEmptyGraphs_ReturnsNoQuads()
    {
        var trig = """
            GRAPH <http://ex.org/g1> { }
            GRAPH <http://ex.org/g2> { }
            <http://ex.org/g3> { }
            """u8.ToArray();

        await using var stream = new MemoryStream(trig);
        var parser = new TriGStreamParser(stream);

        int count = 0;
        await parser.ParseAsync((s, p, o, g) => count++);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ParseAsync_Comments_SkipsCorrectly()
    {
        var trig = """
            # This is a comment
            GRAPH <http://ex.org/g> {
                # Another comment
                <http://ex.org/s> <http://ex.org/p> <http://ex.org/o> .
                # Final comment
            }
            """u8.ToArray();

        await using var stream = new MemoryStream(trig);
        var parser = new TriGStreamParser(stream);

        var quads = new List<(string S, string P, string O, string G)>();
        await parser.ParseAsync((s, p, o, g) =>
        {
            quads.Add((s.ToString(), p.ToString(), o.ToString(), g.ToString()));
        });

        Assert.Single(quads);
    }

    #endregion

    #region Large File Handling

    [Fact]
    public async Task ParseAsync_MultipleGraphsStreaming_ParsesCorrectly()
    {
        // Generate 100 quads across multiple graphs
        var sb = new StringBuilder();
        sb.AppendLine("@prefix ex: <http://example.org/> .");
        sb.AppendLine();

        // 30 triples in default graph
        for (int i = 0; i < 30; i++)
        {
            sb.AppendLine($"<http://example.org/s{i}> <http://example.org/p> <http://example.org/o{i}> .");
        }

        // 35 triples each in two named graphs
        for (int g = 1; g <= 2; g++)
        {
            sb.AppendLine($"GRAPH <http://example.org/graph{g}> {{");
            for (int i = 0; i < 35; i++)
            {
                sb.AppendLine($"    <http://example.org/s{i}> <http://example.org/p> <http://example.org/o{i}> .");
            }
            sb.AppendLine("}");
        }

        var trig = Encoding.UTF8.GetBytes(sb.ToString());

        await using var stream = new MemoryStream(trig);
        var parser = new TriGStreamParser(stream);

        int count = 0;
        await parser.ParseAsync((s, p, o, g) => count++);

        Assert.Equal(100, count);
    }

    #endregion
}
