using System;
using System.Text;

namespace SkyOmega.Mercury.Rdf;

/// <summary>
/// The single source of truth for RDF/Turtle-family string-escape decoding: W3C Turtle <c>[159s] ECHAR</c>
/// (<c>\t \b \n \r \f \" \' \\</c>) and <c>[26] UCHAR</c> (<c>\uXXXX</c> / <c>\UXXXXXXXX</c>), shared verbatim by the
/// N-Triples / N-Quads / Turtle / TriG streaming parsers and the SPARQL literal canonicalizer (<c>LiteralForm</c>).
/// </summary>
/// <remarks>
/// <para>
/// Atom identity is the substrate's foundation: the same lexical form MUST decode to the same bytes regardless of
/// which format ingested it, or one triple becomes two atoms. Before this type, each parser hand-rolled the decode and
/// they had already drifted — N-Triples validated <c>\U</c> via <c>Rune.TryCreate</c> (rejecting surrogates and code
/// points above U+10FFFF), Turtle's char-returning path truncated <c>\U</c> above U+FFFF with a raw <c>(char)</c> cast,
/// and TriG/N-Quads omitted the upper-bound guard. This type owns the decode + validation + UTF-16 encoding so they
/// cannot diverge. See <c>docs/divergence/README.md</c> (S1a).
/// </para>
/// <para>
/// <strong>Sink-agnostic.</strong> Callers read the escape body from their own stream or span and append the decoded
/// char(s) to their own buffer (a parser output array, a <see cref="StringBuilder"/>, a returned <see cref="Rune"/>);
/// the per-sink append is legitimately parser-specific. Only the decode/validation/encode — the part that must be
/// identical — lives here. The hot path (valid escapes) is allocation-free and the <c>Try</c> shape lets each caller
/// raise its own parser-specific exception on malformed input.
/// </para>
/// </remarks>
internal static class RdfEscape
{
    /// <summary>
    /// Decode an ECHAR simple escape (the char after <c>\</c>). Returns <c>false</c> for <c>u</c>/<c>U</c> (a UCHAR —
    /// use <see cref="TryDecodeUchar"/>) and for any other char (a malformed escape the caller rejects).
    /// </summary>
    public static bool TryDecodeSimple(char esc, out char decoded)
    {
        switch (esc)
        {
            case 't':  decoded = '\t'; return true;
            case 'b':  decoded = '\b'; return true;
            case 'n':  decoded = '\n'; return true;
            case 'r':  decoded = '\r'; return true;
            case 'f':  decoded = '\f'; return true;
            case '"':  decoded = '"';  return true;
            case '\'': decoded = '\''; return true;
            case '\\': decoded = '\\'; return true;
            default:   decoded = '\0'; return false;
        }
    }

    /// <summary>Decode one hex digit (0-9, A-F, a-f). Returns <c>false</c> for any other char.</summary>
    public static bool TryDecodeHexDigit(char ch, out int value)
    {
        value = ch switch
        {
            >= '0' and <= '9' => ch - '0',
            >= 'A' and <= 'F' => ch - 'A' + 10,
            >= 'a' and <= 'f' => ch - 'a' + 10,
            _ => -1
        };
        return value >= 0;
    }

    /// <summary>
    /// Decode a UCHAR hex run — 4 digits for <c>\u</c>, 8 for <c>\U</c> — to a Unicode scalar value. Returns
    /// <c>false</c> for a non-hex digit, a surrogate code point (U+D800..U+DFFF), or a value above U+10FFFF — the
    /// W3C-correct validation, applied identically everywhere.
    /// </summary>
    public static bool TryDecodeUchar(ReadOnlySpan<char> hexDigits, out int codePoint)
    {
        int value = 0;
        for (int i = 0; i < hexDigits.Length; i++)
        {
            if (!TryDecodeHexDigit(hexDigits[i], out int digit))
            {
                codePoint = 0;
                return false;
            }
            value = (value << 4) | digit;
        }

        if ((value >= 0xD800 && value <= 0xDFFF) || value > 0x10FFFF)
        {
            codePoint = 0;
            return false;
        }

        codePoint = value;
        return true;
    }

    /// <summary>
    /// Encode a Unicode scalar value (already validated by <see cref="TryDecodeUchar"/>) to UTF-16 in
    /// <paramref name="destination"/> (which must hold at least 2 chars); returns the number of chars written
    /// (1 for the BMP, 2 for a supplementary surrogate pair).
    /// </summary>
    public static int EncodeUtf16(int codePoint, Span<char> destination)
    {
        var rune = new Rune(codePoint);
        return rune.EncodeToUtf16(destination);
    }

    /// <summary>Append a validated Unicode scalar value to a <see cref="StringBuilder"/> sink.</summary>
    public static void AppendUtf16(StringBuilder builder, int codePoint)
    {
        Span<char> chars = stackalloc char[2];
        builder.Append(chars[..EncodeUtf16(codePoint, chars)]);
    }
}
