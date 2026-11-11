using System;

namespace SkyOmega.Mercury.Rdf;

/// <summary>
/// What makes a language-tagged literal well-formed, the one rule the N-Triples / N-Quads, Turtle and TriG parsers
/// share. RDF 1.2 Concepts §3.4: "The language tag MUST be well-formed according to section 2.2.9 of BCP47", and a
/// literal's datatype IRI is <c>rdf:langString</c> or <c>rdf:dirLangString</c> "if and only if" it has a language
/// tag, so neither can be written as an explicit <c>^^</c> datatype; and a base direction, after <c>--</c>, is
/// <c>ltr</c> or <c>rtl</c>. The W3C RDF 1.2 negative syntax tests <c>*-langdir-bad-1…5</c> and
/// <c>trig12-base-bad-1…5</c> check all three.
/// </summary>
/// <remarks>
/// Each parser scans the tag with its own grammar loop and asks this type whether what it read is well-formed, so
/// every format accepts the same tags. Before it, N-Quads accepted ASCII only, Turtle any Unicode letter, and TriG any
/// character up to a delimiter (docs/divergence S1f).
/// </remarks>
internal static class RdfLanguageTag
{
    /// <summary>The datatype a literal has when it has a base direction (RDF 1.2).</summary>
    public const string DirLangString = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#dirLangString>";

    /// <summary>
    /// True for <c>rdf:langString</c> and <c>rdf:dirLangString</c>: the datatypes a literal has only with a language
    /// tag, which therefore cannot follow <c>^^</c>. <paramref name="datatypeIri"/> is in angle brackets, as the
    /// parsers write it.
    /// </summary>
    public static bool IsTagOnlyDatatype(ReadOnlySpan<char> datatypeIri) =>
        datatypeIri.SequenceEqual(XsdDatatype.LangString) || datatypeIri.SequenceEqual(DirLangString);

    /// <summary>
    /// True when <paramref name="langDir"/>, the text after <c>@</c>, is a well-formed language tag (BCP 47, RFC 5646
    /// §2.2.9: it matches the <c>Language-Tag</c> ABNF of §2.1), and, when it goes on with <c>--</c> (RDF 1.2
    /// <c>LANG_DIR</c>), a base direction: <c>ltr</c> or <c>rtl</c>, in lower case.
    /// </summary>
    public static bool IsWellFormed(ReadOnlySpan<char> langDir)
    {
        var direction = langDir.IndexOf("--");
        if (direction >= 0 && !IsBaseDirection(langDir[(direction + 2)..]))
            return false;

        var tag = direction < 0 ? langDir : langDir[..direction];

        if (IsIrregularGrandfathered(tag))
            return true;

        // Language-Tag = langtag / privateuse / grandfathered. The regular grandfathered tags (art-lojban, zh-min-nan,
        // …) are well-formed langtags, so only the irregular ones need their own test, above.
        var subtags = new SubtagReader(tag);
        if (!subtags.Next(out var first))
            return false;

        if (IsPrivateUseSingleton(first))
            return ReadPrivateUse(ref subtags);

        // language = 2*3ALPHA ["-" extlang] / 4ALPHA / 5*8ALPHA
        if (first.Length < 2 || first.Length > 8 || !IsAlpha(first))
            return false;

        if (!subtags.Next(out var subtag))
            return true;

        // extlang = 3ALPHA *2("-" 3ALPHA), only after a language of two or three letters
        if (first.Length <= 3)
        {
            for (int extlangs = 0; extlangs < 3 && subtag.Length == 3 && IsAlpha(subtag); extlangs++)
            {
                if (!subtags.Next(out subtag))
                    return true;
            }
        }

        // script = 4ALPHA
        if (subtag.Length == 4 && IsAlpha(subtag))
        {
            if (!subtags.Next(out subtag))
                return true;
        }

        // region = 2ALPHA / 3DIGIT
        if ((subtag.Length == 2 && IsAlpha(subtag)) || (subtag.Length == 3 && IsDigit(subtag)))
        {
            if (!subtags.Next(out subtag))
                return true;
        }

        // variant = 5*8alphanum / (DIGIT 3alphanum)
        while (IsVariant(subtag))
        {
            if (!subtags.Next(out subtag))
                return true;
        }

        // extension = singleton 1*("-" (2*8alphanum)); singleton = any alphanumeric but x
        while (subtag.Length == 1 && IsAlphanumeric(subtag) && !IsPrivateUseSingleton(subtag))
        {
            if (!subtags.Next(out subtag) || subtag.Length < 2 || subtag.Length > 8 || !IsAlphanumeric(subtag))
                return false;

            do
            {
                if (!subtags.Next(out subtag))
                    return true;
            }
            while (subtag.Length >= 2 && subtag.Length <= 8 && IsAlphanumeric(subtag));
        }

        // privateuse = "x" 1*("-" (1*8alphanum)), last
        return IsPrivateUseSingleton(subtag) && ReadPrivateUse(ref subtags);
    }

    // RDF 1.2 Concepts §3.4: a base direction is "ltr" or "rtl". The section gives a case rule only for the tag (case-
    // insensitive); the W3C tests trig12-base-bad-2 and *-langdir-bad-2 reject "LTR", so the direction matches exactly.
    private static bool IsBaseDirection(ReadOnlySpan<char> direction) =>
        direction.SequenceEqual("ltr") || direction.SequenceEqual("rtl");

    /// <summary>The subtags after an <c>x</c>: one or more, each one to eight alphanumerics, to the end of the tag.</summary>
    private static bool ReadPrivateUse(ref SubtagReader subtags)
    {
        bool any = false;
        while (subtags.Next(out var subtag))
        {
            if (subtag.Length < 1 || subtag.Length > 8 || !IsAlphanumeric(subtag))
                return false;
            any = true;
        }
        return any;
    }

    private static bool IsVariant(ReadOnlySpan<char> subtag) =>
        IsAlphanumeric(subtag) &&
        ((subtag.Length >= 5 && subtag.Length <= 8) || (subtag.Length == 4 && char.IsAsciiDigit(subtag[0])));

    private static bool IsPrivateUseSingleton(ReadOnlySpan<char> subtag) =>
        subtag.Length == 1 && (subtag[0] == 'x' || subtag[0] == 'X');

    private static bool IsAlpha(ReadOnlySpan<char> subtag)
    {
        foreach (var c in subtag)
            if (!char.IsAsciiLetter(c))
                return false;
        return true;
    }

    private static bool IsDigit(ReadOnlySpan<char> subtag)
    {
        foreach (var c in subtag)
            if (!char.IsAsciiDigit(c))
                return false;
        return true;
    }

    private static bool IsAlphanumeric(ReadOnlySpan<char> subtag)
    {
        foreach (var c in subtag)
            if (!char.IsAsciiLetterOrDigit(c))
                return false;
        return true;
    }

    // irregular = "en-GB-oed" / "i-ami" / … (RFC 5646 §2.1): grandfathered tags that do not match langtag.
    private static bool IsIrregularGrandfathered(ReadOnlySpan<char> tag)
    {
        foreach (var irregular in Irregular)
            if (tag.Equals(irregular, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static readonly string[] Irregular =
    [
        "en-GB-oed", "i-ami", "i-bnn", "i-default", "i-enochian", "i-hak", "i-klingon", "i-lux", "i-mingo",
        "i-navajo", "i-pwn", "i-tao", "i-tay", "i-tsu", "sgn-BE-FR", "sgn-BE-NL", "sgn-CH-DE",
    ];

    /// <summary>
    /// Reads a tag's subtags, the text between hyphens. A tag that is empty, or starts, ends or has two hyphens in a
    /// row, has an empty subtag: <see cref="Next"/> returns it as such, and every rule above rejects an empty subtag.
    /// </summary>
    private ref struct SubtagReader
    {
        private ReadOnlySpan<char> _rest;
        private bool _done;

        public SubtagReader(ReadOnlySpan<char> tag)
        {
            _rest = tag;
            _done = false;
        }

        public bool Next(out ReadOnlySpan<char> subtag)
        {
            if (_done)
            {
                subtag = default;
                return false;
            }

            var hyphen = _rest.IndexOf('-');
            if (hyphen < 0)
            {
                subtag = _rest;
                _done = true;
            }
            else
            {
                subtag = _rest[..hyphen];
                _rest = _rest[(hyphen + 1)..];
            }
            return true;
        }
    }
}
