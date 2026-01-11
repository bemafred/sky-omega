// JsonLdStreamParser.Utilities.cs
// Helper methods, validation, escaping, JSON canonicalization

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SkyOmega.Mercury.NQuads;

namespace SkyOmega.Mercury.JsonLd;

public sealed partial class JsonLdStreamParser
{
    /// <summary>
    /// Check if a string looks like a JSON-LD keyword.
    /// Per JSON-LD 1.1, keywords match the regex @[A-Za-z]+ (@ followed by one or more ASCII letters).
    /// Examples: @type, @id, @context, @ignoreMe are keyword-like.
    /// Non-examples: @, @foo.bar, @123 are NOT keyword-like and can be term definitions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsKeywordLike(string value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '@')
            return false;

        // Must have at least one character after @
        if (value.Length == 1)
            return false;

        // All characters after @ must be ASCII letters (A-Z or a-z)
        for (int i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Check if a value is an actual JSON-LD keyword (not just keyword-like).
    /// </summary>
    private static bool IsJsonLdKeyword(string value)
    {
        return value switch
        {
            "@base" or "@context" or "@container" or "@direction" or "@graph" or
            "@id" or "@import" or "@included" or "@index" or "@json" or "@language" or
            "@list" or "@nest" or "@none" or "@prefix" or "@propagate" or "@protected" or
            "@reverse" or "@set" or "@type" or "@value" or "@version" or "@vocab" => true,
            _ => false
        };
    }

    private string GenerateBlankNode()
    {
        // Use 'g' prefix to avoid collision with blank nodes from input (which often use 'b')
        return $"_:g{_blankNodeCounter++}";
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EmitQuad(QuadHandler handler, string subject, string predicate, string obj, string? graph)
    {
        // Validate subject IRI (must be well-formed if it's an IRI, not a blank node)
        if (subject.StartsWith('<') && !IsWellFormedIri(subject))
            return;

        // Validate predicate IRI (predicates are always IRIs in RDF)
        if (!IsWellFormedIri(predicate))
            return;

        // Validate object IRI (if it's an IRI, not a literal or blank node)
        if (obj.StartsWith('<') && !IsWellFormedIri(obj))
            return;

        // Validate language tag (if present)
        if (obj.Contains("@") && obj.StartsWith('"'))
        {
            // Check for language-tagged literal: "value"@lang
            var lastQuote = obj.LastIndexOf('"');
            if (lastQuote > 0 && lastQuote < obj.Length - 1)
            {
                var suffix = obj.Substring(lastQuote + 1);
                if (suffix.StartsWith("@") && !suffix.Contains("^^"))
                {
                    var langTag = suffix.Substring(1);
                    if (!IsWellFormedLanguageTag(langTag))
                        return;
                }
            }
        }

        // Validate graph IRI (if it's an IRI, not a blank node)
        if (graph != null && graph.StartsWith('<') && !IsWellFormedIri(graph))
            return;

        if (graph != null)
        {
            handler(subject.AsSpan(), predicate.AsSpan(), obj.AsSpan(), graph.AsSpan());
        }
        else
        {
            handler(subject.AsSpan(), predicate.AsSpan(), obj.AsSpan(), ReadOnlySpan<char>.Empty);
        }
    }

    /// <summary>
    /// Check if an IRI is well-formed (does not contain disallowed characters).
    /// Per RFC 3987, certain characters must be percent-encoded.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsWellFormedIri(string iri)
    {
        // Strip angle brackets if present
        var toCheck = iri;
        if (iri.StartsWith('<') && iri.EndsWith('>'))
            toCheck = iri.Substring(1, iri.Length - 2);

        // Check for double hash (e111, e112) - invalid IRI pattern
        // This occurs when @vocab ends with # and term starts with #
        if (toCheck.Contains("##"))
            return false;

        // Check for disallowed characters per RFC 3987
        // These characters must be percent-encoded in IRIs
        foreach (var c in toCheck)
        {
            // Space and control characters (0x00-0x1F, 0x7F)
            if (c == ' ' || c < 0x20 || c == 0x7F)
                return false;
            // Delimiters that must be encoded: < > " { } | \ ^ `
            if (c == '<' || c == '>' || c == '"' || c == '{' || c == '}' ||
                c == '|' || c == '\\' || c == '^' || c == '`')
                return false;
        }
        return true;
    }

    /// <summary>
    /// Check if a language tag is well-formed per BCP 47.
    /// Basic validation: must not contain spaces.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsWellFormedLanguageTag(string tag)
    {
        if (string.IsNullOrEmpty(tag))
            return false;
        // Basic validation: no spaces allowed in language tags
        return !tag.Contains(' ');
    }

    private static string EscapeString(string value)
    {
        if (value.IndexOfAny(['"', '\\', '\n', '\r', '\t']) < 0)
            return value;

        var sb = new StringBuilder(value.Length + 10);
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Canonicalize a JSON element for rdf:JSON literals.
    /// Removes whitespace and sorts object keys lexicographically.
    /// </summary>
    private static string CanonicalizeJson(JsonElement element)
    {
        var sb = new StringBuilder();
        CanonicalizeJsonElement(element, sb);
        return sb.ToString();
    }

    private static void CanonicalizeJsonElement(JsonElement element, StringBuilder sb)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                // Sort keys lexicographically as per JCS
                var props = element.EnumerateObject()
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .ToList();
                for (int i = 0; i < props.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    // Key as JSON string
                    sb.Append('"');
                    CanonicalizeJsonString(props[i].Name, sb);
                    sb.Append("\":");
                    CanonicalizeJsonElement(props[i].Value, sb);
                }
                sb.Append('}');
                break;

            case JsonValueKind.Array:
                sb.Append('[');
                var items = element.EnumerateArray().ToList();
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    CanonicalizeJsonElement(items[i], sb);
                }
                sb.Append(']');
                break;

            case JsonValueKind.String:
                sb.Append('"');
                CanonicalizeJsonString(element.GetString() ?? "", sb);
                sb.Append('"');
                break;

            case JsonValueKind.Number:
                // Normalize number representation per RFC 8785 (JCS)
                // Integer: no exponent, no decimal point
                // Non-integer: use shortest representation that round-trips
                var rawNum = element.GetRawText();
                if (element.TryGetInt64(out var intVal))
                {
                    sb.Append(intVal.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                else if (element.TryGetDouble(out var dblVal))
                {
                    // Use "G16" for IEEE 754 double precision
                    // JCS requires shortest representation that round-trips with lowercase 'e'
                    var numStr = dblVal.ToString("G16", System.Globalization.CultureInfo.InvariantCulture);
                    sb.Append(numStr.Replace('E', 'e'));
                }
                else
                {
                    sb.Append(rawNum);
                }
                break;

            case JsonValueKind.True:
                sb.Append("true");
                break;

            case JsonValueKind.False:
                sb.Append("false");
                break;

            case JsonValueKind.Null:
                sb.Append("null");
                break;
        }
    }

    private static void CanonicalizeJsonString(string value, StringBuilder sb)
    {
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        // JCS requires lowercase hex in unicode escapes
                        sb.Append($"\\u{(int)c:x4}");
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
    }
}
