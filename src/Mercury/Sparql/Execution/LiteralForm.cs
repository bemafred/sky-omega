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
    /// Canonicalize a bare numeric or boolean object token to its typed-literal lexical form so it matches the atom
    /// store: SPARQL treats <c>30</c> ≡ <c>"30"^^xsd:integer</c>, <c>3.0</c> ≡ <c>"3.0"^^xsd:decimal</c>,
    /// <c>1e0</c> ≡ <c>"1e0"^^xsd:double</c>, and <c>true</c> ≡ <c>"true"^^xsd:boolean</c> (xsd: as the full IRI —
    /// the stored lexical form). A token already in IRI / quoted-literal / blank-node form, or one that is not a
    /// valid numeric or boolean, is returned verbatim. Shared by TriplePatternScan's constant-object match and the
    /// tree executor's VALUES expansion so the two canonicalizations cannot drift.
    /// </summary>
    public static ReadOnlySpan<char> CanonicalizeNumericOrBoolean(ReadOnlySpan<char> token, out string? scratchOwner)
    {
        scratchOwner = null;
        if (token.Length == 0) return token;
        char c = token[0];
        if (c is '<' or '"' or '\'' or '_') return token; // IRI / literal / blank node — not a bare numeric or boolean

        if (token.SequenceEqual("true") || token.SequenceEqual("false"))
        {
            scratchOwner = $"\"{token}\"^^<http://www.w3.org/2001/XMLSchema#boolean>";
            return scratchOwner.AsSpan();
        }
        if ((c is '-' or '+' || (c >= '0' && c <= '9')) && TryNumericDatatype(token, out var datatype))
        {
            scratchOwner = $"\"{token}\"^^<http://www.w3.org/2001/XMLSchema#{datatype}>";
            return scratchOwner.AsSpan();
        }
        return token;
    }

    /// <summary>
    /// Classify a numeric literal token by its SPARQL lexical form: an exponent ⇒ xsd:double, a '.' ⇒ xsd:decimal,
    /// otherwise xsd:integer. Returns false (leave verbatim) for anything that is not a valid numeric literal.
    /// </summary>
    private static bool TryNumericDatatype(ReadOnlySpan<char> text, out string datatype)
    {
        datatype = "";
        bool hasDot = false, hasExp = false, hasDigit = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch is >= '0' and <= '9') hasDigit = true;
            else if (ch == '.') { if (hasDot || hasExp) return false; hasDot = true; }
            else if (ch is 'e' or 'E') { if (hasExp || !hasDigit) return false; hasExp = true; }
            else if (ch is '+' or '-') { if (i != 0 && text[i - 1] is not ('e' or 'E')) return false; } // sign only leads or follows the exponent
            else return false;
        }
        if (!hasDigit) return false;
        datatype = hasExp ? "double" : hasDot ? "decimal" : "integer";
        return true;
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
    /// past the consumed escape sequence (so caller can continue scanning). The decode,
    /// validation, and UTF-16 encoding are delegated to the shared <see cref="Rdf.RdfEscape"/>
    /// so the SPARQL literal form and the RDF streaming parsers cannot drift (docs/divergence S1a).
    /// Malformed input throws here because a SPARQL source literal is already validated at parse time.
    /// </summary>
    private static int DecodeEscapeAppend(ReadOnlySpan<char> source, int i, StringBuilder sb)
    {
        if (i + 1 >= source.Length)
            throw new InvalidOperationException(
                "Truncated escape sequence at end of literal: a SPARQL source literal " +
                "with a trailing '\\' should have been rejected at parse time.");

        var esc = source[i + 1];

        if (Rdf.RdfEscape.TryDecodeSimple(esc, out var decoded))
        {
            sb.Append(decoded);
            return i + 2;
        }

        if (esc is 'u' or 'U')
        {
            int digits = esc == 'u' ? 4 : 8;
            if (i + 2 + digits > source.Length)
                throw new InvalidOperationException($"Truncated \\{esc} escape: needs {digits} hex digits.");
            if (!Rdf.RdfEscape.TryDecodeUchar(source.Slice(i + 2, digits), out int codePoint))
                throw new InvalidOperationException(
                    $"Invalid \\{esc} unicode escape in literal: should have been rejected at parse time.");
            Rdf.RdfEscape.AppendUtf16(sb, codePoint);
            return i + 2 + digits;
        }

        throw new InvalidOperationException(
            $"Invalid escape sequence '\\{esc}' in literal: should have been rejected at parse time.");
    }
}
