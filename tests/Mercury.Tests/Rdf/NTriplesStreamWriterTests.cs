using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SkyOmega.Mercury.NTriples;
using Xunit;

namespace SkyOmega.Mercury.Tests.Rdf;

/// <summary>
/// Tests for NTriplesStreamWriter - streaming zero-GC N-Triples writer.
/// </summary>
public class NTriplesStreamWriterTests
{
    #region Basic Writing

    [Fact]
    public void WriteTriple_BasicIRIs_FormatsCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new NTriplesStreamWriter(sw);

        writer.WriteTriple(
            "<http://example.org/subject>".AsSpan(),
            "<http://example.org/predicate>".AsSpan(),
            "<http://example.org/object>".AsSpan());

        var result = sw.ToString();
        Assert.Equal("<http://example.org/subject> <http://example.org/predicate> <http://example.org/object> .\n", result);
    }

    [Fact]
    public void WriteTriple_BlankNode_FormatsCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new NTriplesStreamWriter(sw);

        writer.WriteTriple(
            "_:b0".AsSpan(),
            "<http://example.org/predicate>".AsSpan(),
            "_:b1".AsSpan());

        var result = sw.ToString();
        Assert.Equal("_:b0 <http://example.org/predicate> _:b1 .\n", result);
    }

    [Fact]
    public void WriteTriple_SimpleLiteral_FormatsCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new NTriplesStreamWriter(sw);

        writer.WriteTriple(
            "<http://example.org/subject>".AsSpan(),
            "<http://example.org/name>".AsSpan(),
            "\"Alice\"".AsSpan());

        var result = sw.ToString();
        Assert.Equal("<http://example.org/subject> <http://example.org/name> \"Alice\" .\n", result);
    }

    [Fact]
    public void WriteTriple_LiteralWithLanguageTag_FormatsCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new NTriplesStreamWriter(sw);

        writer.WriteTriple(
            "<http://example.org/subject>".AsSpan(),
            "<http://example.org/label>".AsSpan(),
            "\"Hello\"@en".AsSpan());

        var result = sw.ToString();
        Assert.Equal("<http://example.org/subject> <http://example.org/label> \"Hello\"@en .\n", result);
    }

    [Fact]
    public void WriteTriple_LiteralWithDatatype_FormatsCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new NTriplesStreamWriter(sw);

        writer.WriteTriple(
            "<http://example.org/subject>".AsSpan(),
            "<http://example.org/age>".AsSpan(),
            "\"42\"^^<http://www.w3.org/2001/XMLSchema#integer>".AsSpan());

        var result = sw.ToString();
        Assert.Equal("<http://example.org/subject> <http://example.org/age> \"42\"^^<http://www.w3.org/2001/XMLSchema#integer> .\n", result);
    }

    [Fact]
    public void WriteTriple_MultipleTriples_WritesAll()
    {
        using var sw = new StringWriter();
        using var writer = new NTriplesStreamWriter(sw);

        writer.WriteTriple("<http://ex.org/s1>".AsSpan(), "<http://ex.org/p>".AsSpan(), "<http://ex.org/o1>".AsSpan());
        writer.WriteTriple("<http://ex.org/s2>".AsSpan(), "<http://ex.org/p>".AsSpan(), "<http://ex.org/o2>".AsSpan());
        writer.WriteTriple("<http://ex.org/s3>".AsSpan(), "<http://ex.org/p>".AsSpan(), "<http://ex.org/o3>".AsSpan());

        var result = sw.ToString();
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
    }

    #endregion

    #region Escape Sequences

    [Fact]
    public void WriteTriple_LiteralWithNewline_EscapesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new NTriplesStreamWriter(sw);

        writer.WriteTriple(
            "<http://example.org/s>".AsSpan(),
            "<http://example.org/p>".AsSpan(),
            "\"Hello\nWorld\"".AsSpan());

        var result = sw.ToString();
        Assert.Equal("<http://example.org/s> <http://example.org/p> \"Hello\\nWorld\" .\n", result);
    }

    [Fact]
    public void WriteTriple_LiteralWithTab_EscapesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new NTriplesStreamWriter(sw);

        writer.WriteTriple(
            "<http://example.org/s>".AsSpan(),
            "<http://example.org/p>".AsSpan(),
            "\"Hello\tWorld\"".AsSpan());

        var result = sw.ToString();
        Assert.Equal("<http://example.org/s> <http://example.org/p> \"Hello\\tWorld\" .\n", result);
    }

    [Fact]
    public void WriteTriple_LiteralWithCarriageReturn_EscapesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new NTriplesStreamWriter(sw);

        writer.WriteTriple(
            "<http://example.org/s>".AsSpan(),
            "<http://example.org/p>".AsSpan(),
            "\"Hello\rWorld\"".AsSpan());

        var result = sw.ToString();
        Assert.Equal("<http://example.org/s> <http://example.org/p> \"Hello\\rWorld\" .\n", result);
    }

    [Fact]
    public void WriteTriple_LiteralWithBackslash_EscapesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new NTriplesStreamWriter(sw);

        writer.WriteTriple(
            "<http://example.org/s>".AsSpan(),
            "<http://example.org/p>".AsSpan(),
            "\"C:\\path\\file\"".AsSpan());

        var result = sw.ToString();
        Assert.Equal("<http://example.org/s> <http://example.org/p> \"C:\\\\path\\\\file\" .\n", result);
    }

    [Fact]
    public void WriteTriple_LiteralWithEscapedQuote_PreservesEscapes()
    {
        using var sw = new StringWriter();
        using var writer = new NTriplesStreamWriter(sw);

        // Input has properly escaped quotes (as would come from parser/storage)
        // This represents: She said "hello"
        writer.WriteRawTriple(
            "<http://example.org/s>".AsSpan(),
            "<http://example.org/p>".AsSpan(),
            "\"She said \\\"hello\\\"\"".AsSpan());

        var result = sw.ToString();
        // Output should preserve the escapes
        Assert.Equal("<http://example.org/s> <http://example.org/p> \"She said \\\"hello\\\"\" .\n", result);
    }

    [Fact]
    public void WriteTriple_LiteralWithUnicode_EscapesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new NTriplesStreamWriter(sw);

        // Greek letter alpha (U+03B1)
        writer.WriteTriple(
            "<http://example.org/s>".AsSpan(),
            "<http://example.org/p>".AsSpan(),
            "\"α\"".AsSpan());

        var result = sw.ToString();
        Assert.Equal("<http://example.org/s> <http://example.org/p> \"\\u03B1\" .\n", result);
    }

    [Fact]
    public void WriteTriple_LiteralWithEmoji_EscapesCorrectly()
    {
        using var sw = new StringWriter();
        using var writer = new NTriplesStreamWriter(sw);

        // Emoji (U+1F600 - grinning face, requires surrogate pair)
        writer.WriteTriple(
            "<http://example.org/s>".AsSpan(),
            "<http://example.org/p>".AsSpan(),
            "\"😀\"".AsSpan());

        var result = sw.ToString();
        Assert.Equal("<http://example.org/s> <http://example.org/p> \"\\U0001F600\" .\n", result);
    }

    #endregion

    #region Async Writing

    [Fact]
    public async Task WriteTripleAsync_BasicIRIs_FormatsCorrectly()
    {
        using var sw = new StringWriter();
        await using var writer = new NTriplesStreamWriter(sw);

        await writer.WriteTripleAsync(
            "<http://example.org/subject>",
            "<http://example.org/predicate>",
            "<http://example.org/object>");

        await writer.FlushAsync();
        var result = sw.ToString();
        Assert.Equal("<http://example.org/subject> <http://example.org/predicate> <http://example.org/object> .\n", result);
    }

    [Fact]
    public async Task WriteTripleAsync_MultipleTriples_WritesAll()
    {
        using var sw = new StringWriter();
        await using var writer = new NTriplesStreamWriter(sw);

        await writer.WriteTripleAsync("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o1>");
        await writer.WriteTripleAsync("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o2>");
        await writer.WriteTripleAsync("<http://ex.org/s3>", "<http://ex.org/p>", "<http://ex.org/o3>");

        await writer.FlushAsync();
        var result = sw.ToString();
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
    }

    #endregion

    #region Raw Writing

    [Fact]
    public void WriteRawTriple_PreformattedTerms_WritesAsIs()
    {
        using var sw = new StringWriter();
        using var writer = new NTriplesStreamWriter(sw);

        writer.WriteRawTriple(
            "<http://example.org/subject>".AsSpan(),
            "<http://example.org/predicate>".AsSpan(),
            "\"value\"".AsSpan());

        var result = sw.ToString();
        Assert.Equal("<http://example.org/subject> <http://example.org/predicate> \"value\" .\n", result);
    }

    #endregion

    #region Roundtrip Tests

    [Fact]
    public async Task Roundtrip_WriteAndParse_ProducesSameData()
    {
        // Write some triples
        using var sw = new StringWriter();
        using (var writer = new NTriplesStreamWriter(sw))
        {
            writer.WriteTriple("<http://ex.org/Alice>".AsSpan(), "<http://ex.org/name>".AsSpan(), "\"Alice\"".AsSpan());
            writer.WriteTriple("<http://ex.org/Alice>".AsSpan(), "<http://ex.org/age>".AsSpan(), "\"30\"^^<http://www.w3.org/2001/XMLSchema#integer>".AsSpan());
            writer.WriteTriple("<http://ex.org/Alice>".AsSpan(), "<http://ex.org/knows>".AsSpan(), "<http://ex.org/Bob>".AsSpan());
        }

        var ntriples = sw.ToString();

        // Parse them back
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ntriples));
        var parser = new NTriplesStreamParser(stream);

        var triples = new System.Collections.Generic.List<(string S, string P, string O)>();
        await parser.ParseAsync((s, p, o) =>
        {
            triples.Add((s.ToString(), p.ToString(), o.ToString()));
        });

        // Verify
        Assert.Equal(3, triples.Count);
        Assert.Contains(triples, t => t.S == "<http://ex.org/Alice>" && t.P == "<http://ex.org/name>" && t.O == "\"Alice\"");
        Assert.Contains(triples, t => t.S == "<http://ex.org/Alice>" && t.P == "<http://ex.org/age>" && t.O == "\"30\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        Assert.Contains(triples, t => t.S == "<http://ex.org/Alice>" && t.P == "<http://ex.org/knows>" && t.O == "<http://ex.org/Bob>");
    }

    #endregion

    #region File Stream Tests

    [Fact]
    public void WriteTriple_ToFileStream_WritesCorrectly()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Write to file
            using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write))
            using (var tw = new StreamWriter(fs, Encoding.UTF8))
            using (var writer = new NTriplesStreamWriter(tw))
            {
                writer.WriteTriple("<http://ex.org/s>".AsSpan(), "<http://ex.org/p>".AsSpan(), "<http://ex.org/o>".AsSpan());
            }

            // Read back
            var content = File.ReadAllText(tempFile);
            Assert.Equal("<http://ex.org/s> <http://ex.org/p> <http://ex.org/o> .\n", content);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    #endregion
}
