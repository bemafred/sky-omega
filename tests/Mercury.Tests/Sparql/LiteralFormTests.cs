using System;
using SkyOmega.Mercury.Sparql.Execution;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// Direct unit tests for LiteralForm.Canonicalize (ADR-044 Part 1).
///
/// Coverage: fast path (no escapes), slow path for every escape kind, non-literal
/// pass-through, edge cases around empty / langtag-suffixed / datatype-suffixed
/// literals, and the byte-output convergence test (SPARQL "a\"b" canonicalizes to
/// the same 5 bytes Turtle would store).
/// </summary>
public class LiteralFormTests
{
    private static string Canon(string input)
    {
        var result = LiteralForm.Canonicalize(input.AsSpan(), out _);
        return result.ToString();
    }

    [Fact]
    public void NonLiteralStartingWithAngleBracket_PassesThrough()
    {
        Assert.Equal("<http://example.org/s>", Canon("<http://example.org/s>"));
    }

    [Fact]
    public void NonLiteralBlankNode_PassesThrough()
    {
        Assert.Equal("_:b1", Canon("_:b1"));
    }

    [Fact]
    public void EmptySpan_PassesThrough()
    {
        Assert.Equal("", Canon(""));
    }

    [Fact]
    public void LiteralWithoutEscapes_FastPath_VerbatimSpan()
    {
        var input = "\"hello\"";
        var span = LiteralForm.Canonicalize(input.AsSpan(), out var scratch);

        Assert.Null(scratch);
        Assert.True(span == input.AsSpan(), "fast-path span must equal the input span exactly");
    }

    [Fact]
    public void LiteralWithoutEscapes_WithLangTag_FastPath()
    {
        var input = "\"hello\"@en";
        var span = LiteralForm.Canonicalize(input.AsSpan(), out var scratch);

        Assert.Null(scratch);
        Assert.True(span == input.AsSpan());
    }

    [Fact]
    public void LiteralWithoutEscapes_WithDatatype_FastPath()
    {
        var input = "\"42\"^^<http://www.w3.org/2001/XMLSchema#integer>";
        var span = LiteralForm.Canonicalize(input.AsSpan(), out var scratch);

        Assert.Null(scratch);
        Assert.True(span == input.AsSpan());
    }

    [Fact]
    public void EscapedQuote_DecodesToRawQuote()
    {
        // SPARQL "a\"b" → 5-byte canonical "a"b" (matches Turtle/N-Triples ingestion).
        var result = Canon("\"a\\\"b\"");
        Assert.Equal("\"a\"b\"", result);
        Assert.Equal(5, result.Length);
    }

    [Fact]
    public void EscapedBackslash_DecodesToSingleBackslash()
    {
        Assert.Equal("\"a\\b\"", Canon("\"a\\\\b\""));
    }

    [Fact]
    public void EscapedNewline_DecodesToLF()
    {
        Assert.Equal("\"a\nb\"", Canon("\"a\\nb\""));
    }

    [Fact]
    public void EscapedCarriageReturn_DecodesToCR()
    {
        Assert.Equal("\"a\rb\"", Canon("\"a\\rb\""));
    }

    [Fact]
    public void EscapedTab_DecodesToTab()
    {
        Assert.Equal("\"a\tb\"", Canon("\"a\\tb\""));
    }

    [Fact]
    public void EscapedBackspace_DecodesToBackspace()
    {
        Assert.Equal("\"a\bb\"", Canon("\"a\\bb\""));
    }

    [Fact]
    public void EscapedFormFeed_DecodesToFF()
    {
        Assert.Equal("\"a\fb\"", Canon("\"a\\fb\""));
    }

    [Fact]
    public void EscapedSingleQuote_DecodesToApostrophe()
    {
        Assert.Equal("\"a'b\"", Canon("\"a\\'b\""));
    }

    [Fact]
    public void UnicodeEscape_4HexDigits_BMP()
    {
        // A = 'A'
        Assert.Equal("\"A\"", Canon("\"\\u0041\""));
    }

    [Fact]
    public void UnicodeEscape_4HexDigits_BMP_LowerHex()
    {
        // é = 'é'
        Assert.Equal("\"é\"", Canon("\"\\u00e9\""));
    }

    [Fact]
    public void UnicodeEscape_8HexDigits_BMP()
    {
        // \U00000041 = 'A' (BMP via 8-digit form)
        Assert.Equal("\"A\"", Canon("\"\\U00000041\""));
    }

    [Fact]
    public void UnicodeEscape_8HexDigits_AboveBMP_ProducesSurrogatePair()
    {
        // \U0001F600 = 😀 (U+1F600, encodes as surrogate pair D83D DE00)
        var result = Canon("\"\\U0001F600\"");
        Assert.Equal("\"😀\"", result);
        // Wrapper " + 2 surrogate-pair chars + wrapper " = 4 chars.
        Assert.Equal(4, result.Length);
    }

    [Fact]
    public void SurrogateCodePoint_Rejected()
    {
        // U+D800 is a surrogate, not allowed as a Unicode code point.
        Assert.Throws<InvalidOperationException>(() =>
            LiteralForm.Canonicalize("\"\\uD800\"".AsSpan(), out _));
    }

    [Fact]
    public void CodePointAboveMax_Rejected()
    {
        // U+110000 exceeds U+10FFFF.
        Assert.Throws<InvalidOperationException>(() =>
            LiteralForm.Canonicalize("\"\\U00110000\"".AsSpan(), out _));
    }

    [Fact]
    public void MixedEscapes_AllDecoded()
    {
        // "a\"b\\c\nd" → canonical "a"b\c<LF>d"
        var result = Canon("\"a\\\"b\\\\c\\nd\"");
        Assert.Equal("\"a\"b\\c\nd\"", result);
    }

    [Fact]
    public void LangTaggedLiteralWithEscapes_DecodesContentPreservesTag()
    {
        // "a\"b"@sv → "a"b"@sv (5 + 3 = 8 chars)
        var result = Canon("\"a\\\"b\"@sv");
        Assert.Equal("\"a\"b\"@sv", result);
        Assert.Equal(8, result.Length);
    }

    [Fact]
    public void DatatypedLiteralWithEscapes_DecodesContentPreservesType()
    {
        var result = Canon("\"a\\\"b\"^^<http://example.org/foo>");
        Assert.Equal("\"a\"b\"^^<http://example.org/foo>", result);
    }

    [Fact]
    public void EmptyLiteralWithEscapeInDatatype_SlowPath_DecodesNothing()
    {
        // The "\" in the source is the datatype IRI tail-bracket only via `^^<>`;
        // but in our verbatim form, no \ appears here. This is a control: literal
        // "" with no escapes should fast-path.
        var input = "\"\"";
        var span = LiteralForm.Canonicalize(input.AsSpan(), out var scratch);
        Assert.Null(scratch);
        Assert.True(span == input.AsSpan());
    }

    [Fact]
    public void TripleEscapedQuote_AllDecoded()
    {
        // "a\"\"\"b" → "a"""b" (canonical wrapped-decoded; closing wrapper is the last ")
        var result = Canon("\"a\\\"\\\"\\\"b\"");
        Assert.Equal("\"a\"\"\"b\"", result);
        Assert.Equal(7, result.Length);
    }

    [Fact]
    public void LiteralEndingWithEscapedQuote_DecodesCorrectly()
    {
        // "abc\"" → "abc"" (6 chars)
        var result = Canon("\"abc\\\"\"");
        Assert.Equal("\"abc\"\"", result);
        Assert.Equal(6, result.Length);
    }

    [Fact]
    public void SingleEscapedQuoteLiteral_DecodesToBareQuote()
    {
        // "\"" → """ (3 chars)
        var result = Canon("\"\\\"\"");
        Assert.Equal("\"\"\"", result);
        Assert.Equal(3, result.Length);
    }

    [Fact]
    public void TwoEscapedQuotes_DecodeIndependently()
    {
        // SPARQL writes """ as the escape for "; canonical form should be """
        var result = Canon("\"\\u0022\"");
        Assert.Equal("\"\"\"", result);
        Assert.Equal(3, result.Length);
    }

    [Fact]
    public void ConvergenceTest_SparqlBackslashQuote_EqualsTurtleWrappedDecoded()
    {
        // The convergence target per ADR-044: SPARQL source "a\"b" must produce
        // the same 5-byte canonical form that Turtle produces from "a\"b".
        // Turtle's stored form (verified by probe 2026-05-17): bytes 22 61 22 62 22.
        var canon = Canon("\"a\\\"b\"");
        var expectedBytes = new char[] { '"', 'a', '"', 'b', '"' };
        Assert.Equal(new string(expectedBytes), canon);
    }

    [Fact]
    public void ConvergenceTest_SparqlUnicodeEscape_EqualsTurtleWrappedDecoded()
    {
        // SPARQL "AB" and Turtle "AB" both canonicalize to "AB".
        Assert.Equal("\"AB\"", Canon("\"\\u0041\\u0042\""));
    }

    [Fact]
    public void ScratchOwner_BoundOnSlowPath_NullOnFastPath()
    {
        // Fast path
        LiteralForm.Canonicalize("\"plain\"".AsSpan(), out var s1);
        Assert.Null(s1);

        // Slow path
        LiteralForm.Canonicalize("\"a\\\"b\"".AsSpan(), out var s2);
        Assert.NotNull(s2);
        Assert.Equal("\"a\"b\"", s2);
    }

    [Fact]
    public void ScratchOwner_TwoCalls_DoNotAliasEachOther()
    {
        // The aliasing-safe property promised by the helper: each slow-path call
        // returns a span into a fresh immutable string. Two consecutive slow-path
        // calls bound to two different scratch fields keep both spans alive
        // independently.
        var span1 = LiteralForm.Canonicalize("\"a\\\"x\"".AsSpan(), out var owner1);
        var span2 = LiteralForm.Canonicalize("\"b\\\"y\"".AsSpan(), out var owner2);

        Assert.NotNull(owner1);
        Assert.NotNull(owner2);
        Assert.Equal("\"a\"x\"", span1.ToString());
        Assert.Equal("\"b\"y\"", span2.ToString());
        Assert.NotSame(owner1, owner2);
    }

    // ---- CanonicalizeContent (unwrapped form, used by FilterEvaluator and BindExpressionEvaluator) ----

    [Fact]
    public void CanonicalizeContent_NoEscapes_FastPath_VerbatimSpan()
    {
        var input = "hello";
        var span = LiteralForm.CanonicalizeContent(input.AsSpan(), out var scratch);
        Assert.Null(scratch);
        Assert.True(span == input.AsSpan());
    }

    [Fact]
    public void CanonicalizeContent_EmptySpan_FastPath()
    {
        var span = LiteralForm.CanonicalizeContent("".AsSpan(), out var scratch);
        Assert.Null(scratch);
        Assert.Equal(0, span.Length);
    }

    [Fact]
    public void CanonicalizeContent_EscapedQuote_DecodesToRawQuote()
    {
        var span = LiteralForm.CanonicalizeContent("a\\\"b".AsSpan(), out var scratch);
        Assert.NotNull(scratch);
        Assert.Equal("a\"b", span.ToString());
        Assert.Equal(3, span.Length);
    }

    [Fact]
    public void CanonicalizeContent_AllEscapes_Decoded()
    {
        // Input: a \" b \\ c \n d A → decoded: a " b \ c <LF> d A
        var span = LiteralForm.CanonicalizeContent("a\\\"b\\\\c\\nd\\u0041".AsSpan(), out _);
        Assert.Equal("a\"b\\c\ndA", span.ToString());
    }

    [Fact]
    public void CanonicalizeContent_UnicodeEscape_AboveBMP_SurrogatePair()
    {
        var span = LiteralForm.CanonicalizeContent("x\\U0001F600y".AsSpan(), out _);
        Assert.Equal("x😀y", span.ToString());
    }

    [Fact]
    public void IriWithBackslashEscape_NotCanonicalized()
    {
        // Defensive: an IRI shape (starting with '<') passes through even if it
        // somehow contains '\' — only literals (starting with '"') are decoded.
        // In practice IRIs cannot contain '\' per RFC 3987, but the contract
        // documents that non-literal inputs are pass-through.
        var input = "<urn:ex\\backslash>";
        var span = LiteralForm.Canonicalize(input.AsSpan(), out var scratch);
        Assert.Null(scratch);
        Assert.True(span == input.AsSpan());
    }
}
