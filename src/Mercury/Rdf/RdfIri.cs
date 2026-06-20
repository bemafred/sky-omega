using System;
using System.Text;

namespace SkyOmega.Mercury.Rdf;

/// <summary>
/// The single source of truth for resolving a relative IRI reference against a base IRI, implementing the
/// RFC 3986 §5.2 reference-resolution algorithm (RFC 3987 §6.5 uses the same algorithm for IRIs). Shared by the
/// Turtle / TriG / RDF-XML / N-Triples-family parsers so that the same (base, reference) pair resolves to the
/// same IRI regardless of input format.
/// </summary>
/// <remarks>
/// <para>
/// Before this type each parser resolved relative IRIs its own way — Turtle and RDF-XML deferred to BCL
/// <see cref="Uri"/> (which over-normalises: it appends a path "/" to authority-only references, so Turtle
/// carried a hand fix-up that RDF-XML lacked, and RDF-XML's <c>.AbsoluteUri</c> diverged further from Turtle's
/// <c>.ToString()</c>), while TriG and JSON-LD hand-rolled two slightly different RFC-3986 transcriptions. The
/// same relative IRI could therefore mint a different atom depending on format — the atom-identity hazard the
/// divergence audit flagged (docs/divergence S1b). This type is the one algorithm, grounded directly in the
/// RFC 3986 §5.4 normative test vectors rather than in any prior implementation.
/// </para>
/// <para>
/// <strong>RDF semantics, not generic URI processing.</strong> An <em>absolute</em> reference (one with a
/// scheme) is returned <em>verbatim</em>: RDF treats IRIs as opaque strings (§5.2-style dot-segment removal of
/// an already-absolute IRI would change its identity, which RDF does not do). Only genuinely relative references
/// are transformed. Percent-encoding and case normalisation are likewise <em>not</em> applied — RDF IRI equality
/// is character-by-character.
/// </para>
/// </remarks>
internal static class RdfIri
{
    /// <summary>
    /// Resolve <paramref name="reference"/> against the absolute <paramref name="baseIri"/> per RFC 3986 §5.2.
    /// An absolute reference is returned verbatim; an empty <paramref name="baseIri"/> returns the reference
    /// unchanged (nothing to resolve against).
    /// </summary>
    public static string Resolve(string baseIri, string reference)
    {
        var r = Parse(reference);

        // Absolute reference (has a scheme): RDF stores it verbatim — no normalisation.
        if (r.Scheme is not null)
            return reference;

        if (string.IsNullOrEmpty(baseIri))
            return reference;

        var b = Parse(baseIri);

        // RFC 3986 §5.2.2 Transform References (strict mode).
        string? tAuthority;
        string tPath;
        string? tQuery;

        if (r.Authority is not null)
        {
            tAuthority = r.Authority;
            tPath = RemoveDotSegments(r.Path);
            tQuery = r.Query;
        }
        else
        {
            if (r.Path.Length == 0)
            {
                tPath = b.Path;
                tQuery = r.Query ?? b.Query;
            }
            else
            {
                tPath = r.Path[0] == '/'
                    ? RemoveDotSegments(r.Path)
                    : RemoveDotSegments(Merge(b, r.Path));
                tQuery = r.Query;
            }
            tAuthority = b.Authority;
        }

        return Recompose(b.Scheme, tAuthority, tPath, tQuery, r.Fragment);
    }

    private readonly record struct Components(string? Scheme, string? Authority, string Path, string? Query, string? Fragment);

    /// <summary>
    /// Split an IRI into its five RFC 3986 §3 components. A component is <c>null</c> when <em>undefined</em>
    /// (no delimiter present) and <c>""</c> when present-but-empty — the distinction the resolution algorithm
    /// depends on (e.g. <c>?</c> with no query vs no <c>?</c> at all). Path is always non-null (possibly empty).
    /// </summary>
    private static Components Parse(string iri)
    {
        string rest = iri;
        string? fragment = null, query = null, scheme = null, authority = null;

        int hash = rest.IndexOf('#');
        if (hash >= 0) { fragment = rest.Substring(hash + 1); rest = rest.Substring(0, hash); }

        int q = rest.IndexOf('?');
        if (q >= 0) { query = rest.Substring(q + 1); rest = rest.Substring(0, q); }

        // scheme ":" — scheme = ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ), and the ':' must precede any '/'.
        int colon = rest.IndexOf(':');
        if (colon > 0 && IsScheme(rest.AsSpan(0, colon)))
        {
            int slash = rest.IndexOf('/');
            if (slash < 0 || colon < slash)
            {
                scheme = rest.Substring(0, colon);
                rest = rest.Substring(colon + 1);
            }
        }

        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            int pathStart = rest.IndexOf('/', 2);
            if (pathStart < 0) { authority = rest.Substring(2); rest = ""; }
            else { authority = rest.Substring(2, pathStart - 2); rest = rest.Substring(pathStart); }
        }

        return new Components(scheme, authority, rest, query, fragment);
    }

    /// <summary>RFC 3986 §5.2.3 Merge: combine a relative-path reference with the base path.</summary>
    private static string Merge(in Components b, string refPath)
    {
        if (b.Authority is not null && b.Path.Length == 0)
            return "/" + refPath;

        int lastSlash = b.Path.LastIndexOf('/');
        return lastSlash < 0 ? refPath : b.Path.Substring(0, lastSlash + 1) + refPath;
    }

    /// <summary>RFC 3986 §5.2.4 Remove Dot Segments — the normative buffer algorithm.</summary>
    private static string RemoveDotSegments(string path)
    {
        if (path.Length == 0) return path;

        string input = path;
        var output = new StringBuilder(path.Length);

        while (input.Length > 0)
        {
            if (input.StartsWith("../", StringComparison.Ordinal)) input = input.Substring(3);          // A
            else if (input.StartsWith("./", StringComparison.Ordinal)) input = input.Substring(2);       // A
            else if (input.StartsWith("/./", StringComparison.Ordinal)) input = "/" + input.Substring(3);// B
            else if (input == "/.") input = "/";                                                          // B
            else if (input.StartsWith("/../", StringComparison.Ordinal)) { input = "/" + input.Substring(4); RemoveLastSegment(output); } // C
            else if (input == "/..") { input = "/"; RemoveLastSegment(output); }                          // C
            else if (input == "." || input == "..") input = "";                                           // D
            else                                                                                          // E
            {
                int start = input[0] == '/' ? 1 : 0;
                int next = input.IndexOf('/', start);
                if (next < 0) { output.Append(input); input = ""; }
                else { output.Append(input, 0, next); input = input.Substring(next); }
            }
        }

        return output.ToString();
    }

    private static void RemoveLastSegment(StringBuilder output)
    {
        for (int i = output.Length - 1; i >= 0; i--)
        {
            if (output[i] == '/') { output.Length = i; return; }
        }
        output.Length = 0;
    }

    /// <summary>RFC 3986 §5.3 Component Recomposition.</summary>
    private static string Recompose(string? scheme, string? authority, string path, string? query, string? fragment)
    {
        var sb = new StringBuilder(path.Length + 16);
        if (scheme is not null) { sb.Append(scheme); sb.Append(':'); }
        if (authority is not null) { sb.Append("//"); sb.Append(authority); }
        sb.Append(path);
        if (query is not null) { sb.Append('?'); sb.Append(query); }
        if (fragment is not null) { sb.Append('#'); sb.Append(fragment); }
        return sb.ToString();
    }

    private static bool IsScheme(ReadOnlySpan<char> s)
    {
        if (s.Length == 0 || !IsAlpha(s[0])) return false;
        for (int i = 1; i < s.Length; i++)
        {
            char c = s[i];
            if (!(IsAlpha(c) || (c >= '0' && c <= '9') || c == '+' || c == '-' || c == '.'))
                return false;
        }
        return true;
    }

    private static bool IsAlpha(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
}
