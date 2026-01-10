// JsonLdStreamParser.Iris.cs
// IRI expansion and resolution

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SkyOmega.Mercury.JsonLd;

public sealed partial class JsonLdStreamParser
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
    {
        if (string.IsNullOrEmpty(relative))
            return baseIri;

        // Parse base IRI components
        var (baseScheme, baseAuthority, basePath, baseQuery) = ParseIriComponents(baseIri);

        // Check if base is hierarchical (has ://) or not (like tag:example)
        var isHierarchical = baseIri.Contains("://");

        // Reference starts with scheme - it's already absolute
        if (IsAbsoluteIri(relative))
            return relative;

        // For non-hierarchical URIs (like tag:, urn:), handle specially
        if (!isHierarchical)
        {
            // For fragment-only, append to base
            if (relative.StartsWith('#'))
            {
                var hashIndex = baseIri.IndexOf('#');
                var basePart = hashIndex >= 0 ? baseIri.Substring(0, hashIndex) : baseIri;
                return basePart + relative;
            }

            // Check if the base has a path-like structure (contains /)
            // For tag:example/foo with relative "a", expect tag:example/a
            var colonIdx = baseIri.IndexOf(':');
            var baseNonHierPath = baseIri.Substring(colonIdx + 1);
            if (baseNonHierPath.Contains('/'))
            {
                // Merge paths: remove last segment from base, add relative
                var lastSlash = baseNonHierPath.LastIndexOf('/');
                var mergedPath = baseNonHierPath.Substring(0, lastSlash + 1) + relative;
                return baseScheme + ":" + RemoveDotSegments(mergedPath);
            }

            // No path structure - just replace: scheme:relative
            return baseScheme + ":" + relative;
        }

        // Reference starts with // - authority reference
        if (relative.StartsWith("//"))
        {
            var (_, refAuth, refPath, refQuery) = ParseIriComponents(baseScheme + ":" + relative);
            var result = baseScheme + "://" + refAuth + RemoveDotSegments(refPath);
            if (!string.IsNullOrEmpty(refQuery))
                result += "?" + refQuery;
            return result;
        }

        // Reference starts with ? - query-only reference (keep base path)
        if (relative.StartsWith('?'))
        {
            // Remove query and fragment from base, append new query
            var queryIdx = relative.IndexOf('#');
            var fragment = queryIdx >= 0 ? relative.Substring(queryIdx) : "";
            var query = queryIdx >= 0 ? relative.Substring(1, queryIdx - 1) : relative.Substring(1);
            return baseScheme + "://" + baseAuthority + basePath + "?" + query + fragment;
        }

        // Reference starts with # - fragment-only reference
        if (relative.StartsWith('#'))
        {
            // Remove fragment from base path/query, append new fragment
            var hashIndex = baseIri.IndexOf('#');
            var basePart = hashIndex >= 0 ? baseIri.Substring(0, hashIndex) : baseIri;
            return basePart + relative;
        }

        // Reference starts with / - absolute path reference
        if (relative.StartsWith('/'))
        {
            // Parse relative for query/fragment
            var qIdx = relative.IndexOf('?');
            var hIdx = relative.IndexOf('#');
            var pathEnd = qIdx >= 0 ? qIdx : (hIdx >= 0 ? hIdx : relative.Length);
            var refPath = relative.Substring(0, pathEnd);
            var rest = relative.Substring(pathEnd);
            return baseScheme + "://" + baseAuthority + RemoveDotSegments(refPath) + rest;
        }

        // Relative path - merge with base per RFC 3986 Section 5.2.3
        string targetPath;
        if (!string.IsNullOrEmpty(baseAuthority) && string.IsNullOrEmpty(basePath))
        {
            // Base has authority but empty path - prepend "/" to relative (e129)
            targetPath = "/" + relative;
        }
        else if (string.IsNullOrEmpty(basePath))
        {
            // No authority, no path - just use relative
            targetPath = "/" + relative;
        }
        else
        {
            // Remove last segment from base path
            var lastSlash = basePath.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                targetPath = basePath.Substring(0, lastSlash + 1) + relative;
            }
            else
            {
                // No slash in path but path is not empty - replace it
                targetPath = "/" + relative;
            }
        }

        // Split path from query/fragment before removing dot segments
        var relQIdx = targetPath.IndexOf('?');
        var relHIdx = targetPath.IndexOf('#');
        var pathEndIdx = relQIdx >= 0 ? relQIdx : (relHIdx >= 0 ? relHIdx : targetPath.Length);
        var pathOnly = targetPath.Substring(0, pathEndIdx);
        var suffix = targetPath.Substring(pathEndIdx);

        return baseScheme + "://" + baseAuthority + RemoveDotSegments(pathOnly) + suffix;
    }

    /// <summary>
    /// Parse IRI into components (scheme, authority, path, query).
    /// Fragment is not returned as it's handled separately.
    /// </summary>
    private static (string scheme, string authority, string path, string query) ParseIriComponents(string iri)
    {
        // Remove fragment
        var hashIdx = iri.IndexOf('#');
        if (hashIdx >= 0)
            iri = iri.Substring(0, hashIdx);

        // Extract scheme
        var schemeEnd = iri.IndexOf(':');
        if (schemeEnd < 0)
            return ("", "", iri, "");

        var scheme = iri.Substring(0, schemeEnd);
        var rest = iri.Substring(schemeEnd + 1);

        // Extract authority
        string authority = "";
        string pathPart = rest;
        if (rest.StartsWith("//"))
        {
            rest = rest.Substring(2);
            var pathStart = rest.IndexOf('/');
            var queryStart = rest.IndexOf('?');
            var authEnd = pathStart >= 0 ? pathStart : (queryStart >= 0 ? queryStart : rest.Length);
            authority = rest.Substring(0, authEnd);
            pathPart = rest.Substring(authEnd);
        }

        // Extract query
        var qIdx = pathPart.IndexOf('?');
        string path = qIdx >= 0 ? pathPart.Substring(0, qIdx) : pathPart;
        string query = qIdx >= 0 ? pathPart.Substring(qIdx + 1) : "";

        return (scheme, authority, path, query);
    }

    /// <summary>
    /// Remove dot segments from a path per RFC 3986 Section 5.2.4.
    /// </summary>
    private static string RemoveDotSegments(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        // Use a list as a stack of segments
        var segments = new List<string>();
        var i = 0;

        while (i < path.Length)
        {
            // A: If the input buffer starts with a prefix of "../" or "./"
            if (path.AsSpan(i).StartsWith("../"))
            {
                i += 3;
                continue;
            }
            if (path.AsSpan(i).StartsWith("./"))
            {
                i += 2;
                continue;
            }

            // B: If the input buffer starts with a prefix of "/./" or "/."
            if (path.AsSpan(i).StartsWith("/./"))
            {
                i += 2; // Replace with "/"
                continue;
            }
            if (i + 2 == path.Length && path.AsSpan(i).StartsWith("/."))
            {
                // "/." at end - replace with "/"
                segments.Add("/");
                break;
            }

            // C: If the input buffer starts with a prefix of "/../" or "/.."
            if (path.AsSpan(i).StartsWith("/../"))
            {
                i += 3; // Replace with "/"
                if (segments.Count > 0)
                    segments.RemoveAt(segments.Count - 1);
                continue;
            }
            if (i + 3 == path.Length && path.AsSpan(i).StartsWith("/.."))
            {
                // "/.." at end - replace with "/" and pop
                if (segments.Count > 0)
                    segments.RemoveAt(segments.Count - 1);
                segments.Add("/");
                break;
            }

            // D: If the input buffer consists only of "." or ".."
            if (path.Substring(i) == "." || path.Substring(i) == "..")
            {
                break;
            }

            // E: Move first path segment (including initial "/" if any) to output
            var segStart = i;
            if (path[i] == '/')
                i++;
            while (i < path.Length && path[i] != '/')
                i++;

            segments.Add(path.Substring(segStart, i - segStart));
        }

        return string.Join("", segments);
    }

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
    /// Check if a string is an absolute IRI (has a scheme per RFC 3986).
    /// Scheme = ALPHA *( ALPHA / DIGIT / "+" / "-" / "." )
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAbsoluteIri(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        // Find the first colon
        var colonIndex = value.IndexOf(':');
        if (colonIndex <= 0)
            return false;

        // Scheme must start with ALPHA
        var firstChar = value[0];
        if (!((firstChar >= 'A' && firstChar <= 'Z') || (firstChar >= 'a' && firstChar <= 'z')))
            return false;

        // Rest of scheme must be ALPHA / DIGIT / "+" / "-" / "."
        for (int i = 1; i < colonIndex; i++)
        {
            var c = value[i];
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                  (c >= '0' && c <= '9') || c == '+' || c == '-' || c == '.'))
            {
                return false;
            }
        }

        return true;
    }

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
