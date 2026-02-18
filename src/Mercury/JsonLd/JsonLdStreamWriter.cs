// JsonLdStreamWriter.cs
// Zero-GC streaming RDF to JSON-LD writer
// Based on W3C JSON-LD 1.1 specification
// No external dependencies, only BCL
// .NET 10 / C# 14

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SkyOmega.Mercury.JsonLd;

/// <summary>
/// JSON-LD output form.
/// </summary>
internal enum JsonLdForm
{
    /// <summary>Expanded form - explicit IRIs, arrays for all values.</summary>
    Expanded,

    /// <summary>Compacted form - uses @context to shorten IRIs.</summary>
    Compacted
}

/// <summary>
/// Streaming RDF to JSON-LD writer.
/// Collects quads and outputs JSON-LD on flush/dispose.
///
/// Usage:
/// <code>
/// using var writer = new JsonLdStreamWriter(textWriter);
/// writer.RegisterPrefix("foaf", "http://xmlns.com/foaf/0.1/");
/// writer.WriteQuad(subject, predicate, obj, graph);
/// writer.Flush();
/// </code>
/// </summary>
internal sealed class JsonLdStreamWriter : IDisposable, IAsyncDisposable
{
    private readonly TextWriter _writer;
    private readonly JsonLdForm _form;
    private readonly bool _prettyPrint;
    private bool _isDisposed;

    // Collected quads grouped by graph, then by subject
    private readonly Dictionary<string, Dictionary<string, List<(string Predicate, string Object)>>> _graphs;

    // Prefix mappings for compacted form
    private readonly Dictionary<string, string> _prefixes; // prefix -> IRI
    private readonly Dictionary<string, string> _iriToPrefixes; // IRI -> prefix (reverse lookup)

    private const string RdfType = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";
    private const string RdfFirst = "http://www.w3.org/1999/02/22-rdf-syntax-ns#first";
    private const string RdfRest = "http://www.w3.org/1999/02/22-rdf-syntax-ns#rest";
    private const string RdfNil = "http://www.w3.org/1999/02/22-rdf-syntax-ns#nil";

    public JsonLdStreamWriter(TextWriter writer, JsonLdForm form = JsonLdForm.Expanded, bool prettyPrint = true)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _form = form;
        _prettyPrint = prettyPrint;
        _graphs = new Dictionary<string, Dictionary<string, List<(string, string)>>>(StringComparer.Ordinal);
        _prefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        _iriToPrefixes = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Register a prefix for compacted form output.
    /// </summary>
    public void RegisterPrefix(string prefix, string iri)
    {
        _prefixes[prefix] = iri;
        _iriToPrefixes[iri] = prefix;
    }

    /// <summary>
    /// Write a quad to be included in JSON-LD output.
    /// </summary>
    public void WriteQuad(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj, ReadOnlySpan<char> graph = default)
    {
        var graphKey = graph.IsEmpty ? "" : graph.ToString();
        var subjectKey = subject.ToString();
        var predicateStr = predicate.ToString();
        var objectStr = obj.ToString();

        if (!_graphs.TryGetValue(graphKey, out var subjects))
        {
            subjects = new Dictionary<string, List<(string, string)>>(StringComparer.Ordinal);
            _graphs[graphKey] = subjects;
        }

        if (!subjects.TryGetValue(subjectKey, out var predicates))
        {
            predicates = new List<(string, string)>();
            subjects[subjectKey] = predicates;
        }

        predicates.Add((predicateStr, objectStr));
    }

    /// <summary>
    /// Write a quad using string parameters.
    /// </summary>
    public void WriteQuad(string subject, string predicate, string obj, string? graph = null)
    {
        if (graph != null)
        {
            WriteQuad(subject.AsSpan(), predicate.AsSpan(), obj.AsSpan(), graph.AsSpan());
        }
        else
        {
            WriteQuad(subject.AsSpan(), predicate.AsSpan(), obj.AsSpan(), ReadOnlySpan<char>.Empty);
        }
    }

    /// <summary>
    /// Flush all collected quads to JSON-LD output.
    /// </summary>
    public void Flush()
    {
        WriteJsonLd();
        _writer.Flush();
    }

    /// <summary>
    /// Flush all collected quads asynchronously.
    /// </summary>
    public async Task FlushAsync()
    {
        WriteJsonLd();
        await _writer.FlushAsync().ConfigureAwait(false);
    }

    private void WriteJsonLd()
    {
        var options = new JsonWriterOptions
        {
            Indented = _prettyPrint
        };

        using var stream = new MemoryStream();
        using (var jsonWriter = new Utf8JsonWriter(stream, options))
        {
            if (_form == JsonLdForm.Compacted && _prefixes.Count > 0)
            {
                WriteCompactedForm(jsonWriter);
            }
            else
            {
                WriteExpandedForm(jsonWriter);
            }
        }

        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        _writer.Write(reader.ReadToEnd());
    }

    private void WriteExpandedForm(Utf8JsonWriter writer)
    {
        var hasNamedGraphs = _graphs.Keys.Any(g => !string.IsNullOrEmpty(g));
        var hasDefaultGraph = _graphs.ContainsKey("");

        if (hasNamedGraphs && hasDefaultGraph)
        {
            // Multiple graphs - use array with @graph
            writer.WriteStartArray();

            // Default graph subjects
            if (_graphs.TryGetValue("", out var defaultSubjects))
            {
                foreach (var (subject, predicates) in defaultSubjects)
                {
                    WriteSubjectExpanded(writer, subject, predicates);
                }
            }

            // Named graphs
            foreach (var (graphIri, subjects) in _graphs)
            {
                if (string.IsNullOrEmpty(graphIri)) continue;

                writer.WriteStartObject();
                writer.WriteString("@id", StripBrackets(graphIri));
                writer.WritePropertyName("@graph");
                writer.WriteStartArray();

                foreach (var (subject, predicates) in subjects)
                {
                    WriteSubjectExpanded(writer, subject, predicates);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }
        else if (hasNamedGraphs)
        {
            // Only named graphs
            writer.WriteStartArray();

            foreach (var (graphIri, subjects) in _graphs)
            {
                writer.WriteStartObject();
                writer.WriteString("@id", StripBrackets(graphIri));
                writer.WritePropertyName("@graph");
                writer.WriteStartArray();

                foreach (var (subject, predicates) in subjects)
                {
                    WriteSubjectExpanded(writer, subject, predicates);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }
        else
        {
            // Default graph only
            var subjects = _graphs.GetValueOrDefault("") ?? new Dictionary<string, List<(string, string)>>();

            if (subjects.Count == 1)
            {
                // Single subject - output as object
                var (subject, predicates) = subjects.First();
                WriteSubjectExpanded(writer, subject, predicates);
            }
            else
            {
                // Multiple subjects - output as array
                writer.WriteStartArray();
                foreach (var (subject, predicates) in subjects)
                {
                    WriteSubjectExpanded(writer, subject, predicates);
                }
                writer.WriteEndArray();
            }
        }
    }

    private void WriteSubjectExpanded(Utf8JsonWriter writer, string subject, List<(string Predicate, string Object)> predicates)
    {
        writer.WriteStartObject();

        // @id
        if (!subject.StartsWith("_:"))
        {
            writer.WriteString("@id", StripBrackets(subject));
        }
        else
        {
            writer.WriteString("@id", subject);
        }

        // Group predicates
        var grouped = predicates
            .GroupBy(p => StripBrackets(p.Predicate))
            .ToDictionary(g => g.Key, g => g.Select(x => x.Object).ToList());

        foreach (var (predicate, objects) in grouped)
        {
            if (predicate == RdfType)
            {
                // @type
                writer.WritePropertyName("@type");
                writer.WriteStartArray();
                foreach (var obj in objects)
                {
                    writer.WriteStringValue(StripBrackets(obj));
                }
                writer.WriteEndArray();
            }
            else
            {
                // Regular predicate
                writer.WritePropertyName(predicate);
                writer.WriteStartArray();

                foreach (var obj in objects)
                {
                    WriteValueExpanded(writer, obj);
                }

                writer.WriteEndArray();
            }
        }

        writer.WriteEndObject();
    }

    private void WriteValueExpanded(Utf8JsonWriter writer, string value)
    {
        if (value.StartsWith("_:"))
        {
            // Blank node reference
            writer.WriteStartObject();
            writer.WriteString("@id", value);
            writer.WriteEndObject();
        }
        else if (value.StartsWith('<') && value.EndsWith('>'))
        {
            // IRI reference
            writer.WriteStartObject();
            writer.WriteString("@id", StripBrackets(value));
            writer.WriteEndObject();
        }
        else if (value.StartsWith('"'))
        {
            // Literal
            WriteLiteralExpanded(writer, value);
        }
        else
        {
            // Assume IRI
            writer.WriteStartObject();
            writer.WriteString("@id", value);
            writer.WriteEndObject();
        }
    }

    private void WriteLiteralExpanded(Utf8JsonWriter writer, string literal)
    {
        writer.WriteStartObject();

        // Parse literal: "value"^^<datatype> or "value"@lang or "value"
        var (value, datatype, language) = ParseLiteral(literal);

        writer.WriteString("@value", value);

        if (!string.IsNullOrEmpty(language))
        {
            writer.WriteString("@language", language);
        }
        else if (!string.IsNullOrEmpty(datatype) && datatype != "http://www.w3.org/2001/XMLSchema#string")
        {
            writer.WriteString("@type", datatype);
        }

        writer.WriteEndObject();
    }

    private void WriteCompactedForm(Utf8JsonWriter writer)
    {
        var hasNamedGraphs = _graphs.Keys.Any(g => !string.IsNullOrEmpty(g));

        writer.WriteStartObject();

        // Write @context
        writer.WritePropertyName("@context");
        writer.WriteStartObject();

        foreach (var (prefix, iri) in _prefixes)
        {
            writer.WriteString(prefix, iri);
        }

        writer.WriteEndObject();

        if (hasNamedGraphs)
        {
            // Multiple graphs
            writer.WritePropertyName("@graph");
            writer.WriteStartArray();

            foreach (var (graphIri, subjects) in _graphs)
            {
                if (string.IsNullOrEmpty(graphIri))
                {
                    foreach (var (subject, predicates) in subjects)
                    {
                        WriteSubjectCompacted(writer, subject, predicates);
                    }
                }
                else
                {
                    writer.WriteStartObject();
                    writer.WriteString("@id", CompactIri(StripBrackets(graphIri)));
                    writer.WritePropertyName("@graph");
                    writer.WriteStartArray();

                    foreach (var (subject, predicates) in subjects)
                    {
                        WriteSubjectCompacted(writer, subject, predicates);
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
        }
        else
        {
            // Single graph - inline properties if single subject
            var subjects = _graphs.GetValueOrDefault("") ?? new Dictionary<string, List<(string, string)>>();

            if (subjects.Count == 1)
            {
                var (subject, predicates) = subjects.First();
                WriteSubjectPropertiesCompacted(writer, subject, predicates);
            }
            else
            {
                writer.WritePropertyName("@graph");
                writer.WriteStartArray();

                foreach (var (subject, predicates) in subjects)
                {
                    WriteSubjectCompacted(writer, subject, predicates);
                }

                writer.WriteEndArray();
            }
        }

        writer.WriteEndObject();
    }

    private void WriteSubjectCompacted(Utf8JsonWriter writer, string subject, List<(string Predicate, string Object)> predicates)
    {
        writer.WriteStartObject();
        WriteSubjectPropertiesCompacted(writer, subject, predicates);
        writer.WriteEndObject();
    }

    private void WriteSubjectPropertiesCompacted(Utf8JsonWriter writer, string subject, List<(string Predicate, string Object)> predicates)
    {
        // @id
        if (!subject.StartsWith("_:"))
        {
            writer.WriteString("@id", CompactIri(StripBrackets(subject)));
        }
        else
        {
            writer.WriteString("@id", subject);
        }

        // Group predicates
        var grouped = predicates
            .GroupBy(p => StripBrackets(p.Predicate))
            .ToDictionary(g => g.Key, g => g.Select(x => x.Object).ToList());

        foreach (var (predicate, objects) in grouped)
        {
            var compactPredicate = predicate == RdfType ? "@type" : CompactIri(predicate);
            writer.WritePropertyName(compactPredicate);

            if (objects.Count == 1)
            {
                WriteValueCompacted(writer, objects[0], predicate == RdfType);
            }
            else
            {
                writer.WriteStartArray();
                foreach (var obj in objects)
                {
                    WriteValueCompacted(writer, obj, predicate == RdfType);
                }
                writer.WriteEndArray();
            }
        }
    }

    private void WriteValueCompacted(Utf8JsonWriter writer, string value, bool isType)
    {
        if (value.StartsWith("_:"))
        {
            if (isType)
            {
                writer.WriteStringValue(value);
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteString("@id", value);
                writer.WriteEndObject();
            }
        }
        else if (value.StartsWith('<') && value.EndsWith('>'))
        {
            var iri = StripBrackets(value);
            if (isType)
            {
                writer.WriteStringValue(CompactIri(iri));
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteString("@id", CompactIri(iri));
                writer.WriteEndObject();
            }
        }
        else if (value.StartsWith('"'))
        {
            WriteLiteralCompacted(writer, value);
        }
        else
        {
            if (isType)
            {
                writer.WriteStringValue(CompactIri(value));
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteString("@id", CompactIri(value));
                writer.WriteEndObject();
            }
        }
    }

    private void WriteLiteralCompacted(Utf8JsonWriter writer, string literal)
    {
        var (value, datatype, language) = ParseLiteral(literal);

        if (!string.IsNullOrEmpty(language))
        {
            writer.WriteStartObject();
            writer.WriteString("@value", value);
            writer.WriteString("@language", language);
            writer.WriteEndObject();
        }
        else if (!string.IsNullOrEmpty(datatype) && datatype != "http://www.w3.org/2001/XMLSchema#string")
        {
            // Check for native JSON types
            if (datatype == "http://www.w3.org/2001/XMLSchema#integer" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intVal))
            {
                writer.WriteNumberValue(intVal);
            }
            else if ((datatype == "http://www.w3.org/2001/XMLSchema#double" ||
                      datatype == "http://www.w3.org/2001/XMLSchema#decimal") &&
                     double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleVal))
            {
                writer.WriteNumberValue(doubleVal);
            }
            else if (datatype == "http://www.w3.org/2001/XMLSchema#boolean")
            {
                writer.WriteBooleanValue(value == "true" || value == "1");
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteString("@value", value);
                writer.WriteString("@type", CompactIri(datatype));
                writer.WriteEndObject();
            }
        }
        else
        {
            // Plain string
            writer.WriteStringValue(value);
        }
    }

    private string CompactIri(string iri)
    {
        foreach (var (prefix, ns) in _prefixes)
        {
            if (iri.StartsWith(ns))
            {
                return $"{prefix}:{iri.Substring(ns.Length)}";
            }
        }
        return iri;
    }

    private static (string Value, string? Datatype, string? Language) ParseLiteral(string literal)
    {
        if (!literal.StartsWith('"'))
            return (literal, null, null);

        // Find closing quote (handle escaped quotes)
        var i = 1;
        var sb = new StringBuilder();

        while (i < literal.Length)
        {
            if (literal[i] == '\\' && i + 1 < literal.Length)
            {
                var next = literal[i + 1];
                switch (next)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    default: sb.Append(next); break;
                }
                i += 2;
            }
            else if (literal[i] == '"')
            {
                i++;
                break;
            }
            else
            {
                sb.Append(literal[i]);
                i++;
            }
        }

        var value = sb.ToString();

        // Check for language tag or datatype
        if (i < literal.Length)
        {
            if (literal[i] == '@')
            {
                return (value, null, literal.Substring(i + 1));
            }
            else if (i + 1 < literal.Length && literal[i] == '^' && literal[i + 1] == '^')
            {
                var datatype = literal.Substring(i + 2);
                return (value, StripBrackets(datatype), null);
            }
        }

        return (value, null, null);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string StripBrackets(string iri)
    {
        if (iri.StartsWith('<') && iri.EndsWith('>'))
            return iri.Substring(1, iri.Length - 2);
        return iri;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Flush();
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        await FlushAsync().ConfigureAwait(false);
    }
}
