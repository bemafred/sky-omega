// JsonLdStreamParser.Iris.cs
// IRI expansion and resolution

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Rdf;

namespace SkyOmega.Mercury.JsonLd;

internal sealed partial class JsonLdStreamParser
{
    private string ExpandTerm(string term)
    {
        // Check if term is explicitly set to null (decoupled from @vocab)
        if (_nullTerms.Contains(term))
            return "";

        // Check context for term mapping
        if (_context.TryGetValue(term, out var expanded))
        {
            // The expanded term might itself be a compact IRI that needs further expansion
            return ExpandTermValue(expanded);
        }

        return ExpandTermValue(term);
    }

    private string ExpandTermValue(string value)
    {
        // Check for compact IRI (prefix:localName) before checking absolute IRI
        var colonIdx = value.IndexOf(':');
        if (colonIdx > 0)
        {
            var prefix = value.Substring(0, colonIdx);
            var localName = value.Substring(colonIdx + 1);

            // Key check: if localName starts with "//", this is NOT a compact IRI
            // e.g., "http://example.org" should NOT be expanded using "http" prefix
            // Also, "_" as a prefix would conflict with blank nodes
            // The prefix must be in _prefixable to be used for expansion (pr29)
            if (!localName.StartsWith("//") && prefix != "_" &&
                _prefixable.Contains(prefix) && _context.TryGetValue(prefix, out var prefixIri))
            {
                return FormatIri(prefixIri + localName);
            }
        }

        // Already an absolute IRI (has scheme like http:, urn:, etc.)
        if (IsAbsoluteIri(value))
        {
            return FormatIri(value);
        }

        // Use @vocab if defined
        if (!string.IsNullOrEmpty(_vocabIri))
        {
            return FormatIri(_vocabIri + value);
        }

        // Term cannot be expanded to absolute IRI - return empty to drop the property
        // Per JSON-LD spec, predicates must be absolute IRIs
        return string.Empty;
    }

    /// <summary>
    /// Expand an IRI value, optionally including term expansion.
    /// </summary>
    /// <param name="value">The IRI value to expand.</param>
    /// <param name="expandTerms">If true, check for term definitions. For @id keyword values, this should be false.
    /// For type-coerced @id values (@type: "@id"), this should be true.</param>
    private string ExpandIri(string value, bool expandTerms = false)
    {
        // Empty string resolves to base IRI per JSON-LD spec
        if (string.IsNullOrEmpty(value))
        {
            if (!string.IsNullOrEmpty(_baseIri))
                return FormatIri(_baseIri);
            return GenerateBlankNode();
        }

        // Handle JSON-LD keywords that map to RDF IRIs
        if (value == "@json")
        {
            return "<http://www.w3.org/1999/02/22-rdf-syntax-ns#JSON>";
        }

        // Blank node
        if (value.StartsWith("_:"))
        {
            return value;
        }

        // Term expansion applies for type-coerced @id values and datatypes
        // See test e048: "Terms are ignored in @id" (expandTerms=false for @id keyword)
        if (expandTerms)
        {
            if (_context.TryGetValue(value, out var expanded))
            {
                // The expanded value might itself need resolution
                if (IsAbsoluteIri(expanded))
                    return FormatIri(expanded);
                // Recursively expand
                return ExpandIri(expanded, true);
            }
            // If term not in context but @vocab is set, apply @vocab
            // This handles datatypes like "dateTime" -> vocab#dateTime
            if (!string.IsNullOrEmpty(_vocabIri) && !value.Contains(':'))
            {
                return FormatIri(_vocabIri + value);
            }
        }

        // Check for compact IRI (prefix:localName) before checking absolute IRI
        // A compact IRI is "prefix:localName" where prefix is defined in context and
        // the localName does NOT start with "//" (which would make it a scheme://authority pattern)
        var colonIdx = value.IndexOf(':');
        if (colonIdx > 0)
        {
            var prefix = value.Substring(0, colonIdx);
            var localName = value.Substring(colonIdx + 1);

            // Key check: if localName starts with "//", this is NOT a compact IRI
            // e.g., "http://example.org" should NOT be expanded using "http" prefix
            // Also, "_" as a prefix would conflict with blank nodes ("_:...")
            // The prefix must be in _prefixable to be used for expansion (pr29)
            if (!localName.StartsWith("//") && prefix != "_" &&
                _prefixable.Contains(prefix) && _context.TryGetValue(prefix, out var prefixIri))
            {
                return FormatIri(prefixIri + localName);
            }
        }

        // Already an absolute IRI (has scheme like http:, urn:, etc.)
        // At this point we've already checked for compact IRIs, so this is a real absolute IRI
        if (IsAbsoluteIri(value))
        {
            return FormatIri(value);
        }

        // Resolve against @base
        if (!string.IsNullOrEmpty(_baseIri))
        {
            return FormatIri(ResolveRelativeIri(_baseIri, value));
        }

        // @base is null/empty - relative IRI cannot be resolved (li14)
        // Return null to signal the IRI should be dropped
        return null!;
    }

    /// <summary>
    /// Resolve a relative IRI against a base IRI per RFC 3986 Section 5.
    /// </summary>
    private static string ResolveRelativeIri(string baseIri, string relative)
        => RdfIri.Resolve(baseIri, relative);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string FormatIri(string iri)
    {
        // Already formatted
        if (iri.StartsWith('<') && iri.EndsWith('>'))
            return iri;
        // Blank nodes should not be wrapped in angle brackets
        if (iri.StartsWith("_:"))
            return iri;
        return $"<{iri}>";
    }

    /// <summary>
    /// Check if a string is an absolute IRI (has a scheme per RFC 3986). Delegates to the shared
    /// <see cref="RdfIri.HasScheme"/> so every parser's absolute-vs-relative test is one rule (docs/divergence S1e).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAbsoluteIri(string value) => RdfIri.HasScheme(value);

    /// <summary>
    /// Expand a compact IRI (prefix:localName) or term using the context.
    /// Used for @vocab values which can be compact IRIs (e124) or terms (e125).
    /// </summary>
    private string ExpandCompactIri(string value)
    {
        // First, check if value is a term that resolves to an IRI
        if (_context.TryGetValue(value, out var termIri))
        {
            return termIri;
        }

        // Check for compact IRI (prefix:localName)
        var colonIdx = value.IndexOf(':');
        if (colonIdx > 0)
        {
            var prefix = value.Substring(0, colonIdx);
            var localName = value.Substring(colonIdx + 1);

            // Key check: if localName starts with "//", this is NOT a compact IRI
            // Also, "_" as a prefix would conflict with blank nodes
            // The prefix must be in _prefixable (simple term mapping or expanded def with @prefix: true)
            if (!localName.StartsWith("//") && prefix != "_" &&
                _prefixable.Contains(prefix) && _context.TryGetValue(prefix, out var prefixIri))
            {
                return prefixIri + localName;
            }
        }

        // Not a compact IRI or prefix not defined - return as-is
        return value;
    }
}
