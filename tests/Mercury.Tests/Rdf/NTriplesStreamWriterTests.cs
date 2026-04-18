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

    #region Literals With Unescaped Quotes (regression for bulk-load-gradient-2026-04-17)

    // The Turtle parser unescapes `\"` → `"` in memory. The writer used to
    // treat the first internal `"` as the close quote (forward scan with
    // escape tracking), which broke when the escape info was gone. Fix:
    // backward suffix-aware close-quote detection. These tests lock that in.

    [Fact]
    public void WriteTriple_PlainLiteralWithInternalQuotes_ReEscapes()
    {
        // Simulates in-memory form emitted by Turtle parser after unescaping \"
        var literal = "\"Entity[\"HistoricalCountry\", \"Belgium\"]\"";

        using var sw = new StringWriter();
        using var writer = new NTriplesStreamWriter(sw);
        writer.WriteTriple("<http://ex/s>".AsSpan(), "<http://ex/p>".AsSpan(), literal.AsSpan());

        var expected = "<http://ex/s> <http://ex/p> \"Entity[\\\"HistoricalCountry\\\", \\\"Belgium\\\"]\" .\n";
        Assert.Equal(expected, sw.ToString());
    }

    [Fact]
    public void WriteTriple_LangTaggedLiteralWithInternalQuotes_ReEscapes()
    {
        var literal = "\"say \"hi\" please\"@en";

        using var sw = new StringWriter();
        using var writer = new NTriplesStreamWriter(sw);
        writer.WriteTriple("<http://ex/s>".AsSpan(), "<http://ex/p>".AsSpan(), literal.AsSpan());

        var expected = "<http://ex/s> <http://ex/p> \"say \\\"hi\\\" please\"@en .\n";
        Assert.Equal(expected, sw.ToString());
    }

    [Fact]
    public void WriteTriple_DatatypedLiteralWithInternalQuotes_ReEscapes()
    {
        var literal = "\"a\"b\"^^<http://www.w3.org/2001/XMLSchema#string>";

        using var sw = new StringWriter();
        using var writer = new NTriplesStreamWriter(sw);
        writer.WriteTriple("<http://ex/s>".AsSpan(), "<http://ex/p>".AsSpan(), literal.AsSpan());

        var expected = "<http://ex/s> <http://ex/p> \"a\\\"b\"^^<http://www.w3.org/2001/XMLSchema#string> .\n";
        Assert.Equal(expected, sw.ToString());
    }

    [Fact]
    public void WriteTriple_LiteralWithInternalBackslashAndQuote_ReEscapesBoth()
    {
        // Lexical value is: a"b\c → in-memory after parser unescape is `"a"b\c"`
        var literal = "\"a\"b\\c\"";

        using var sw = new StringWriter();
        using var writer = new NTriplesStreamWriter(sw);
        writer.WriteTriple("<http://ex/s>".AsSpan(), "<http://ex/p>".AsSpan(), literal.AsSpan());

        var expected = "<http://ex/s> <http://ex/p> \"a\\\"b\\\\c\" .\n";
        Assert.Equal(expected, sw.ToString());
    }

    [Fact]
    public void WriteTriple_EmptyLiteral_PlainPreserved()
    {
        using var sw = new StringWriter();
        using var writer = new NTriplesStreamWriter(sw);
        writer.WriteTriple("<http://ex/s>".AsSpan(), "<http://ex/p>".AsSpan(), "\"\"".AsSpan());
        Assert.Equal("<http://ex/s> <http://ex/p> \"\" .\n", sw.ToString());
    }

    [Fact]
    public void WriteTriple_EmptyLiteralWithLang_LangPreserved()
    {
        using var sw = new StringWriter();
        using var writer = new NTriplesStreamWriter(sw);
        writer.WriteTriple("<http://ex/s>".AsSpan(), "<http://ex/p>".AsSpan(), "\"\"@en".AsSpan());
        Assert.Equal("<http://ex/s> <http://ex/p> \"\"@en .\n", sw.ToString());
    }

    [Fact]
    public void WriteTriple_LexicalEndsWithQuoteAndHasLang_ClosingFound()
    {
        // Lexical value is literally: prefix" (ends with a quote)
        // In-memory form: `"prefix""@en`
        var literal = "\"prefix\"\"@en";

        using var sw = new StringWriter();
        using var writer = new NTriplesStreamWriter(sw);
        writer.WriteTriple("<http://ex/s>".AsSpan(), "<http://ex/p>".AsSpan(), literal.AsSpan());

        var expected = "<http://ex/s> <http://ex/p> \"prefix\\\"\"@en .\n";
        Assert.Equal(expected, sw.ToString());
    }

    [Fact]
    public async Task WriteTripleAsync_PlainLiteralWithInternalQuotes_ReEscapes()
    {
        var literal = "\"Entity[\"HistoricalCountry\", \"Belgium\"]\"";

        using var sw = new StringWriter();
        await using var writer = new NTriplesStreamWriter(sw);
        await writer.WriteTripleAsync(
            "<http://ex/s>".AsMemory(),
            "<http://ex/p>".AsMemory(),
            literal.AsMemory());

        var expected = "<http://ex/s> <http://ex/p> \"Entity[\\\"HistoricalCountry\\\", \\\"Belgium\\\"]\" .\n";
        Assert.Equal(expected, sw.ToString());
    }

    // End-to-end round-trip: Turtle parse → N-Triples write → N-Triples parse.
    // This is the coverage gap that let the bug through W3C conformance: writers
    // were never tested against their own readers for the convert combination.

    [Fact]
    public async Task RoundTrip_TurtleLiteralWithEscapedQuotes_ParsesBack()
    {
        var turtle = "@prefix ex: <http://example.org/> .\n" +
                     "ex:s ex:p \"Entity[\\\"HistoricalCountry\\\", \\\"Belgium\\\"]\" .\n";

        // Convert Turtle → N-Triples via the same pipeline the CLI uses
        var ntMem = new MemoryStream();
        var ntWriter = new StreamWriter(ntMem, leaveOpen: true);
        var writer = new NTriplesStreamWriter(ntWriter);
        var ttlStream = new MemoryStream(Encoding.UTF8.GetBytes(turtle));
        var parser = new SkyOmega.Mercury.Rdf.Turtle.TurtleStreamParser(ttlStream);
        await parser.ParseAsync((s, p, o) => writer.WriteTriple(s, p, o));
        writer.Flush();
        ntWriter.Flush();
        var ntText = Encoding.UTF8.GetString(ntMem.ToArray());

        // Now parse the N-Triples back to ensure it round-trips without error
        var ntBytes = Encoding.UTF8.GetBytes(ntText);
        using var ntInput = new MemoryStream(ntBytes);
        var ntParser = new SkyOmega.Mercury.NTriples.NTriplesStreamParser(ntInput);
        var roundTripped = new List<(string S, string P, string O)>();
        await ntParser.ParseAsync((s, p, o) =>
            roundTripped.Add((s.ToString(), p.ToString(), o.ToString())));

        Assert.Single(roundTripped);
        // Lexical value should survive the round trip unchanged
        Assert.Contains("HistoricalCountry", roundTripped[0].O);
        Assert.Contains("Belgium", roundTripped[0].O);
    }

    #endregion
}
