using SkyOmega.Mercury.Sparql.Execution.Operators;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// The single SPARQL prefixed-name expander. Resolves <c>prefix:local</c> to a full IRI against the
/// query's <see cref="PrefixMapping"/> set, and — per W3C SPARQL 1.1 §4.1.3 — raises a
/// <see cref="SparqlParseException"/> for an <b>undefined</b> prefix rather than silently returning the
/// unexpanded token (which then matched no atom and produced an empty, silently-wrong result). Full IRIs
/// (<c>&lt;…&gt;</c>), literals (<c>"…"</c>), blank nodes (<c>_:…</c>), bare tokens with no colon, and the
/// <c>a</c> keyword pass through untouched.
///
/// <para>Was duplicated across five executors (QueryExecutor, ConstructResults, QueryResults.Patterns,
/// UpdateExecutor, TriplePatternScan), each with the same silent <c>return term</c> on a prefix miss.</para>
///
/// <para>Zero-GC on the pass-through and lookup; one string allocation per genuine expansion (the built
/// IRI), held by the caller's <paramref name="scratch"/> field so the returned span stays valid.</para>
/// </summary>
internal static class PrefixExpander
{
    /// <summary>Resolve a prefixed name to its full IRI string, or <c>null</c> when <paramref name="term"/>
    /// is not a prefixed name needing expansion (full IRI / literal / blank node / bare token — the caller
    /// passes those through unchanged). Returns rdf:type for the <c>a</c> keyword. THROWS for an undefined
    /// prefix. The returned string is the caller's to hold (assign to its scratch field) so a span over it
    /// stays valid — returning the string rather than a <c>ref</c>-span keeps the ref-struct callers legal.</summary>
    public static string? TryExpand(ReadOnlySpan<char> term, PrefixMapping[]? prefixes, ReadOnlySpan<char> source)
    {
        // Full IRI / literal / blank node / empty — not a prefixed name (caller passes through).
        if (term.Length == 0 || term[0] == '<' || term[0] == '"' || term[0] == '_')
            return null;

        // 'a' keyword — shorthand for rdf:type.
        if (term.Length == 1 && term[0] == 'a')
            return SyntheticTermHelper.RdfType;

        int colonIdx = term.IndexOf(':');
        if (colonIdx < 0)
            return null; // a bare token with no colon is not a prefixed name (caller passes through)

        ReadOnlySpan<char> prefixWithColon = term.Slice(0, colonIdx + 1); // stored prefixes include the trailing ':'
        ReadOnlySpan<char> localPart = term.Slice(colonIdx + 1);

        if (prefixes is not null)
        {
            foreach (PrefixMapping m in prefixes)
            {
                if (prefixWithColon.SequenceEqual(source.Slice(m.PrefixStart, m.PrefixLength)))
                {
                    // Stored IRI is bracketed, e.g. "<http://example.org/>"; strip and rebuild with the local part.
                    ReadOnlySpan<char> iri = source.Slice(m.IriStart, m.IriLength);
                    if (iri.Length >= 2 && iri[0] == '<' && iri[^1] == '>')
                        iri = iri.Slice(1, iri.Length - 2);
                    return string.Concat("<".AsSpan(), iri, localPart, ">".AsSpan());
                }
            }
        }

        // Undefined prefix — a static error per W3C SPARQL 1.1 §4.1.3, not an empty match.
        throw new SparqlParseException($"Undefined prefix '{prefixWithColon.ToString()}' in '{term.ToString()}'");
    }

    /// <summary>Build the <see cref="PrefixMapping"/> set from a parsed <see cref="Prologue"/> — the form
    /// the executors that hold only a prologue pass to <see cref="Expand"/>. Null when no prefixes.</summary>
    public static PrefixMapping[]? Extract(in Prologue prologue)
    {
        if (prologue.PrefixCount == 0)
            return null;
        var mappings = new PrefixMapping[prologue.PrefixCount];
        for (int i = 0; i < prologue.PrefixCount; i++)
        {
            var (ps, pl, irs, irl) = prologue.GetPrefix(i);
            mappings[i] = new PrefixMapping { PrefixStart = ps, PrefixLength = pl, IriStart = irs, IriLength = irl };
        }
        return mappings;
    }
}
