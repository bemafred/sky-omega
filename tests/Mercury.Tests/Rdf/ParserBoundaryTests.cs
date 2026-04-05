using System.Text;
using SkyOmega.Mercury.Rdf.Turtle;
using Xunit;
using Xunit.Abstractions;

namespace SkyOmega.Mercury.Tests.Rdf;

/// <summary>
/// Tests that the Turtle parser handles buffer boundaries correctly at any buffer size.
/// A sliding buffer parser must produce identical results regardless of buffer size.
/// </summary>
public class ParserBoundaryTests
{
    private readonly ITestOutputHelper _output;

    public ParserBoundaryTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private const string TurtleWithBlankNode = """
        @prefix ex: <http://example.org/> .
        ex:A rdfs:subClassOf [ a ex:Restriction ;
                               ex:prop ex:value
                             ] .
        ex:B ex:name "Bob" .
        """;

    [Theory]
    [InlineData(8192)]
    [InlineData(1024)]
    [InlineData(256)]
    [InlineData(128)]
    [InlineData(64)]
    [InlineData(32)]
    public async Task BlankNode_ParsesCorrectly_AtAnyBufferSize(int bufferSize)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(TurtleWithBlankNode));
        var triples = new List<(string S, string P, string O)>();

        using var parser = new TurtleStreamParser(stream, bufferSize: bufferSize);
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        foreach (var (s, p, o) in triples)
            _output.WriteLine($"  {s} {p} {o}");

        // 4 triples: subClassOf link, blank node type, blank node prop, Bob's name
        Assert.Equal(4, triples.Count);
    }

    private const string TurtleWithMultipleBlankNodes = """
        @prefix ex: <http://example.org/> .
        ex:Class rdfs:subClassOf [ a ex:R1 ;
                                   ex:p1 ex:v1
                                 ] ;
                 rdfs:subClassOf [ a ex:R2 ;
                                   ex:p2 ex:v2
                                 ] ;
                 rdfs:subClassOf [ a ex:R3 ;
                                   ex:p3 ex:v3
                                 ] .
        """;

    [Theory]
    [InlineData(8192)]
    [InlineData(256)]
    [InlineData(64)]
    [InlineData(32)]
    public async Task MultipleBlankNodes_ParseCorrectly_AtAnyBufferSize(int bufferSize)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(TurtleWithMultipleBlankNodes));
        var triples = new List<(string S, string P, string O)>();

        using var parser = new TurtleStreamParser(stream, bufferSize: bufferSize);
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        foreach (var (s, p, o) in triples)
            _output.WriteLine($"  {s} {p} {o}");

        // 9 triples: 3 blank nodes × (type + prop) + 3 subClassOf links
        Assert.Equal(9, triples.Count);
    }
}
