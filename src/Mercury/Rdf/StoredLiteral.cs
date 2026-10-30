namespace SkyOmega.Mercury.Rdf;

/// <summary>
/// A literal in the form Mercury stores and passes terms in: <c>"</c>, the lexical form with its escapes decoded,
/// <c>"</c>, then nothing, <c>@</c> and a language tag (with <c>--</c> and a direction in RDF 1.2), or <c>^^</c> and a
/// datatype IRI in angle brackets (ADR-044).
/// </summary>
/// <remarks>
/// <para>The closing quote is the last <c>"</c>: the lexical form may hold any character, a quote included, and the
/// suffix holds none, since a language tag is letters, digits and hyphens and an IRIREF excludes <c>"</c>. A forward
/// scan that skips <c>\"</c> reads the source form, not this one; on a stored <c>"say "hi""</c> it stops at the
/// first internal quote.</para>
/// <para>The writers, the result serializers, the expression evaluator and the full-text index split literals here.
/// What a lexical form needs when it is written (N-Triples, XML, JSON or CSV escaping) is each writer's.</para>
/// </remarks>
internal static class StoredLiteral
{
    /// <summary>
    /// Splits a stored literal into its lexical form and its suffix (empty, <c>@lang</c> or <c>^^&lt;iri&gt;</c>).
    /// False when <paramref name="term"/> is not a literal: it does not start with <c>"</c> or has no closing quote.
    /// </summary>
    public static bool TrySplit(ReadOnlySpan<char> term, out ReadOnlySpan<char> lexical, out ReadOnlySpan<char> suffix)
    {
        if (term.Length >= 2 && term[0] == '"')
        {
            int close = term.LastIndexOf('"');
            if (close > 0)
            {
                lexical = term[1..close];
                suffix = term[(close + 1)..];
                return true;
            }
        }
        lexical = default;
        suffix = default;
        return false;
    }

    /// <summary>The same split of a literal's UTF-8 bytes, for the full-text index.</summary>
    public static bool TrySplit(ReadOnlySpan<byte> term, out ReadOnlySpan<byte> lexical, out ReadOnlySpan<byte> suffix)
    {
        if (term.Length >= 2 && term[0] == (byte)'"')
        {
            int close = term.LastIndexOf((byte)'"');
            if (close > 0)
            {
                lexical = term[1..close];
                suffix = term[(close + 1)..];
                return true;
            }
        }
        lexical = default;
        suffix = default;
        return false;
    }
}
