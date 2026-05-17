using System;
using System.Text;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// Canonicalization of SPARQL source literals to the wrapped-decoded atom-store form.
///
/// Per ADR-044: the streaming parsers (Turtle, N-Triples, N-Quads, TriG) decode escape
/// sequences at parse time, producing a stored form like <c>"a"b"</c> for a logical
/// literal whose abstract value is <c>a"b</c>. The SPARQL parser by contrast retains
/// verbatim source spans like <c>"a\"b"</c>. This helper bridges the asymmetry: each
/// SPARQL-source-literal materialization site that flows to atom storage or
/// atom-store-match comparison runs the span through <see cref="Canonicalize"/> so the
/// stored / compared form converges on the streaming-parser canonical bytes.
///
/// Fast path: a literal with no <c>\</c> in it returns verbatim (zero allocation).
/// Slow path: allocates a new immutable <see cref="string"/> with escape sequences
/// decoded; the caller binds the <c>out</c> string to a position-specific field to
/// keep the returned span alive. Strings are immutable, so rebinding the field does
/// not invalidate previously-returned spans into earlier strings — the same idiom the
/// existing <c>_expandedSubject</c> / <c>_expandedPredicate</c> / <c>_expandedObject</c>
/// fields already use for prefix expansion.
///
/// Non-literal inputs (URIs <c>&lt;...&gt;</c>, blank nodes <c>_:...</c>, numerics,
/// booleans) pass through unchanged — they cannot contain SPARQL escape sequences.
///
/// Decode coverage matches <c>TurtleStreamParser.ParseAndAppendEscapeToSb</c>:
/// <c>\"</c>, <c>\\</c>, <c>\n</c>, <c>\r</c>, <c>\t</c>, <c>\b</c>, <c>\f</c>,
/// <c>\'</c>, <c>\uXXXX</c>, <c>\UXXXXXXXX</c>. Surrogate code points and code points
/// above U+10FFFF are rejected (already validated upstream by
/// <c>SparqlParser.ValidateUnicodeEscape{4,8}</c>; the rejection here is defensive).
/// </summary>
internal static class LiteralForm
{
    public static ReadOnlySpan<char> Canonicalize(
        ReadOnlySpan<char> sourceLiteral,
        out string? scratchOwner)
    {
        scratchOwner = null;

        if (sourceLiteral.Length == 0 || sourceLiteral[0] != '"')
            return sourceLiteral;

        if (sourceLiteral.IndexOf('\\') < 0)
            return sourceLiteral;

        var sb = new StringBuilder(sourceLiteral.Length);
        sb.Append('"');

        int i = 1;
        bool closedWrapper = false;

        while (i < sourceLiteral.Length)
        {
            var ch = sourceLiteral[i];

            if (ch == '"')
            {
                sb.Append('"');
                i++;
                closedWrapper = true;
                break;
            }

            if (ch == '\\')
                i = DecodeEscapeAppend(sourceLiteral, i, sb);
            else
            {
                sb.Append(ch);
                i++;
            }
        }

        if (!closedWrapper)
            throw new InvalidOperationException(
                "Unterminated literal: opening '\"' has no matching close. " +
                "Should have been rejected at parse time.");

        if (i < sourceLiteral.Length)
            sb.Append(sourceLiteral.Slice(i));

        scratchOwner = sb.ToString();
        return scratchOwner.AsSpan();
    }

    /// <summary>
    /// Canonicalize the unwrapped content of a SPARQL literal — i.e., the bytes
    /// between the opening and closing wrapper quotes, with no surrounding `"`,
    /// no `@lang`, no `^^<iri>` suffix. Used by FilterEvaluator and
    /// BindExpressionEvaluator, which parse literal content directly from filter /
    /// BIND expression text and need the decoded form to compare against the
    /// canonical-stored atoms' lexical forms.
    ///
    /// Fast path: no `\` → return verbatim span unchanged.
    /// Slow path: decode escapes into a new immutable string.
    /// </summary>
    public static ReadOnlySpan<char> CanonicalizeContent(
        ReadOnlySpan<char> sourceContent,
        out string? scratchOwner)
    {
        scratchOwner = null;

        if (sourceContent.IndexOf('\\') < 0)
            return sourceContent;

        var sb = new StringBuilder(sourceContent.Length);
        int i = 0;

        while (i < sourceContent.Length)
        {
            var ch = sourceContent[i];
            if (ch == '\\')
                i = DecodeEscapeAppend(sourceContent, i, sb);
            else
            {
                sb.Append(ch);
                i++;
            }
        }

        scratchOwner = sb.ToString();
        return scratchOwner.AsSpan();
    }

    /// <summary>
    /// Decode a single escape sequence at <paramref name="i"/> (the '\') and append
    /// the decoded char(s) to <paramref name="sb"/>. Returns the index immediately
    /// past the consumed escape sequence (so caller can continue scanning).
    /// </summary>
    private static int DecodeEscapeAppend(ReadOnlySpan<char> source, int i, StringBuilder sb)
    {
        if (i + 1 >= source.Length)
            throw new InvalidOperationException(
                "Truncated escape sequence at end of literal: a SPARQL source literal " +
                "with a trailing '\\' should have been rejected at parse time.");

        var esc = source[i + 1];
        switch (esc)
        {
            case 't':  sb.Append('\t'); return i + 2;
            case 'b':  sb.Append('\b'); return i + 2;
            case 'n':  sb.Append('\n'); return i + 2;
            case 'r':  sb.Append('\r'); return i + 2;
            case 'f':  sb.Append('\f'); return i + 2;
            case '"':  sb.Append('"'); return i + 2;
            case '\'': sb.Append('\''); return i + 2;
            case '\\': sb.Append('\\'); return i + 2;

            case 'u':
                if (i + 6 > source.Length)
                    throw new InvalidOperationException("Truncated \\u escape: needs 4 hex digits.");
                AppendCodePoint(sb, ParseHex(source.Slice(i + 2, 4)));
                return i + 6;

            case 'U':
                if (i + 10 > source.Length)
                    throw new InvalidOperationException("Truncated \\U escape: needs 8 hex digits.");
                AppendCodePoint(sb, ParseHex(source.Slice(i + 2, 8)));
                return i + 10;

            default:
                throw new InvalidOperationException(
                    $"Invalid escape sequence '\\{esc}' in literal: should have been rejected at parse time.");
        }
    }

    private static int ParseHex(ReadOnlySpan<char> hex)
    {
        int value = 0;
        for (int i = 0; i < hex.Length; i++)
        {
            var ch = hex[i];
            int digit = ch switch
            {
                >= '0' and <= '9' => ch - '0',
                >= 'a' and <= 'f' => ch - 'a' + 10,
                >= 'A' and <= 'F' => ch - 'A' + 10,
                _ => throw new InvalidOperationException(
                    $"Invalid hex digit '{ch}' in unicode escape.")
            };
            value = (value << 4) | digit;
        }
        return value;
    }

    private static void AppendCodePoint(StringBuilder sb, int codePoint)
    {
        if (codePoint >= 0xD800 && codePoint <= 0xDFFF)
            throw new InvalidOperationException(
                $"Invalid unicode escape: surrogate code point U+{codePoint:X4}.");
        if (codePoint > 0x10FFFF)
            throw new InvalidOperationException(
                $"Invalid unicode escape: code point U+{codePoint:X} exceeds U+10FFFF.");

        var rune = new System.Text.Rune(codePoint);
        Span<char> chars = stackalloc char[2];
        int written = rune.EncodeToUtf16(chars);
        sb.Append(chars[..written]);
    }
}
