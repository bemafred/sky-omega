using System.Text;
using SkyOmega.Mercury.Rdf.Turtle;
using Xunit;
using Xunit.Abstractions;

namespace SkyOmega.Mercury.Tests.Rdf;

/// <summary>
/// Tests that the Turtle parser handles large statements that require
/// multiple buffer refills within a single statement parse.
/// Reproduces the Wikidata failure: entities with hundreds of labels
/// spanning far beyond the 8 KB buffer.
/// </summary>
public class ParserLargeStatementTests
{
    private readonly ITestOutputHelper _output;

    public ParserLargeStatementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Generate Turtle with a single entity that has N label triples,
    /// using ; continuation. Total size scales with N × ~50 bytes per label.
    /// At N=500, the statement is ~25 KB — requires 3+ refills on an 8 KB buffer.
    /// </summary>
    private static string GenerateLargeEntity(int labelCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@prefix wd: <http://www.wikidata.org/entity/> .");
        sb.AppendLine("@prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .");
        sb.AppendLine("@prefix schema: <http://schema.org/> .");
        sb.AppendLine();
        sb.AppendLine("wd:Q298");

        for (int i = 0; i < labelCount; i++)
        {
            var lang = $"x-l{i}";
            if (i < labelCount - 1)
                sb.AppendLine($"    rdfs:label \"Label-{i}\"@{lang} ;");
            else
                sb.AppendLine($"    rdfs:label \"Label-{i}\"@{lang} .");
        }

        return sb.ToString();
    }

    [Theory]
    [InlineData(10)]    // ~500 bytes — fits in one buffer
    [InlineData(100)]   // ~5 KB — fits in one buffer
    [InlineData(200)]   // ~10 KB — crosses 8 KB boundary
    [InlineData(500)]   // ~25 KB — multiple refills
    [InlineData(1000)]  // ~50 KB — many refills
    public async Task LargeEntity_WithManyLabels_ParsesCorrectly(int labelCount)
    {
        var turtle = GenerateLargeEntity(labelCount);
        _output.WriteLine($"Input size: {turtle.Length} bytes, {labelCount} labels");

        var stream = new MemoryStream(Encoding.UTF8.GetBytes(turtle));
        var triples = new List<string>();

        using var parser = new TurtleStreamParser(stream, bufferSize: 8192);
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add($"{s} {p} {o}");
        });

        _output.WriteLine($"Parsed {triples.Count} triples");
        Assert.Equal(labelCount, triples.Count);
    }

    [Fact]
    public async Task LargeEntity_FollowedBySmallEntity_BothParse()
    {
        var sb = new StringBuilder();
        sb.AppendLine("@prefix ex: <http://example.org/> .");
        sb.Append("ex:Big ");
        for (int i = 0; i < 500; i++)
        {
            var sep = i < 499 ? " ;" : " .";
            sb.AppendLine($"ex:p{i} \"value-{i}\"{sep}");
            sb.Append("    ");
        }
        sb.AppendLine();
        sb.AppendLine("ex:Small ex:name \"after-big\" .");

        var turtle = sb.ToString();
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(turtle));
        var subjects = new List<string>();

        using var parser = new TurtleStreamParser(stream, bufferSize: 8192);
        await parser.ParseAsync((s, p, o) =>
        {
            subjects.Add(s.ToString());
        });

        Assert.Equal(501, subjects.Count); // 500 big + 1 small
        Assert.Equal("<http://example.org/Small>", subjects.Last());
    }
}
