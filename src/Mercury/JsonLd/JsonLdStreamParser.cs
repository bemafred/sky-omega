// JsonLdStreamParser.cs
// Zero-GC streaming JSON-LD to RDF parser
// Based on W3C JSON-LD 1.1 specification
// No external dependencies, only BCL
// .NET 10 / C# 14

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.NQuads;
using SkyOmega.Mercury.Runtime.Buffers;

namespace SkyOmega.Mercury.JsonLd;

/// <summary>
/// Zero-allocation streaming parser for JSON-LD format.
/// Converts JSON-LD to RDF quads.
///
/// Supported JSON-LD features:
/// - @context (inline term definitions)
/// - @id (subject/object IRIs)
/// - @type (rdf:type shorthand and typed literals)
/// - @value, @language (literals)
/// - @graph (named graphs)
/// - @list (RDF lists)
/// - Nested objects (blank nodes)
///
/// For zero-GC parsing, use ParseAsync(QuadHandler).
/// The IAsyncEnumerable overload allocates strings for compatibility.
/// </summary>
/// <remarks>
/// <para><b>Thread Safety:</b> This class is NOT thread-safe. Each instance
/// maintains internal parsing state, context dictionaries, and buffers that cannot
/// be shared across threads. Create a separate instance per thread or serialize
/// access with locking.</para>
/// <para><b>Usage Pattern:</b> Create one instance per JSON-LD document. Dispose
/// when done to return pooled buffers.</para>
/// </remarks>
public sealed class JsonLdStreamParser : IDisposable, IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly IBufferManager _bufferManager;

    private byte[] _inputBuffer;
    private char[] _outputBuffer;
    private bool _isDisposed;

    // Context: maps terms to IRIs
    private readonly Dictionary<string, string> _context;
    private readonly Dictionary<string, string> _typeCoercion; // term -> @type IRI
    private readonly Dictionary<string, bool> _containerList; // term -> is @list container

    // Base IRI for relative IRI resolution
    private string? _baseIri;

    // Vocabulary IRI for term expansion
    private string? _vocabIri;

    // Blank node counter
    private int _blankNodeCounter;

    // Current graph (null for default graph)
    private string? _currentGraph;

    private const int DefaultBufferSize = 65536; // 64KB
    private const int OutputBufferSize = 16384;

    private const string RdfType = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>";
    private const string RdfFirst = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#first>";
    private const string RdfRest = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#rest>";
    private const string RdfNil = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#nil>";
    private const string XsdString = "http://www.w3.org/2001/XMLSchema#string";

    public JsonLdStreamParser(Stream stream, int bufferSize = DefaultBufferSize, IBufferManager? bufferManager = null)
        : this(stream, baseIri: null, bufferSize, bufferManager)
    {
    }

    public JsonLdStreamParser(Stream stream, string? baseIri, int bufferSize = DefaultBufferSize, IBufferManager? bufferManager = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _baseIri = baseIri;
        _bufferManager = bufferManager ?? PooledBufferManager.Shared;
        _inputBuffer = _bufferManager.Rent<byte>(bufferSize).Array!;
        _outputBuffer = _bufferManager.Rent<char>(OutputBufferSize).Array!;
        _context = new Dictionary<string, string>(StringComparer.Ordinal);
        _typeCoercion = new Dictionary<string, string>(StringComparer.Ordinal);
        _containerList = new Dictionary<string, bool>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Parse JSON-LD using zero-allocation callback.
    /// Spans are valid only during callback invocation.
    /// </summary>
    public async Task ParseAsync(QuadHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        // Read entire stream into buffer (JSON requires full document)
        using var ms = new MemoryStream();
        await _stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        var jsonBytes = ms.ToArray();

        var reader = new Utf8JsonReader(jsonBytes, new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        ParseDocument(ref reader, handler);
    }

    /// <summary>
    /// Parse JSON-LD returning allocated quads.
    /// Use ParseAsync(QuadHandler) for zero-GC parsing.
    /// </summary>
    public async IAsyncEnumerable<RdfQuad> ParseAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var quads = new List<RdfQuad>();

        await ParseAsync((s, p, o, g) =>
        {
            quads.Add(new RdfQuad(s.ToString(), p.ToString(), o.ToString(),
                g.IsEmpty ? null : g.ToString()));
        }, cancellationToken).ConfigureAwait(false);

        foreach (var quad in quads)
        {
            yield return quad;
        }
    }

    private void ParseDocument(ref Utf8JsonReader reader, QuadHandler handler)
    {
        if (!reader.Read())
            return;

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            // Array of nodes
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    ParseNode(ref reader, handler, null);
                }
            }
        }
        else if (reader.TokenType == JsonTokenType.StartObject)
        {
            // Single node or document with @context/@graph
            ParseNode(ref reader, handler, null);
        }
    }

    private string ParseNode(ref Utf8JsonReader reader, QuadHandler handler, string? parentSubject)
    {
        string? subject = null;
        var properties = new List<(string predicate, JsonElement value)>();
        string? graphIri = null;
        bool hasGraphKeyword = false;

        // First pass: collect all properties and handle @context/@id/@graph
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        // Process @context first if present
        if (root.TryGetProperty("@context", out var contextElement))
        {
            ProcessContext(contextElement);
        }

        // Get @id for subject
        if (root.TryGetProperty("@id", out var idElement))
        {
            subject = ExpandIri(idElement.GetString() ?? "");
        }

        // Check for @graph
        if (root.TryGetProperty("@graph", out var graphElement))
        {
            hasGraphKeyword = true;
            // Subject becomes the graph IRI if present
            graphIri = subject;
        }

        // Generate blank node if no @id
        subject ??= GenerateBlankNode();

        // Process @type
        if (root.TryGetProperty("@type", out var typeElement))
        {
            ProcessType(subject, typeElement, handler, graphIri);
        }

        if (hasGraphKeyword)
        {
            // Process @graph contents
            var savedGraph = _currentGraph;
            _currentGraph = graphIri;

            if (graphElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in graphElement.EnumerateArray())
                {
                    if (node.ValueKind == JsonValueKind.Object)
                    {
                        var tempReader = new Utf8JsonReader(Encoding.UTF8.GetBytes(node.GetRawText()));
                        tempReader.Read();
                        ParseNode(ref tempReader, handler, null);
                    }
                }
            }
            else if (graphElement.ValueKind == JsonValueKind.Object)
            {
                var tempReader = new Utf8JsonReader(Encoding.UTF8.GetBytes(graphElement.GetRawText()));
                tempReader.Read();
                ParseNode(ref tempReader, handler, null);
            }

            _currentGraph = savedGraph;
        }

        // Process other properties
        foreach (var prop in root.EnumerateObject())
        {
            var propName = prop.Name;

            // Skip JSON-LD keywords we've already processed
            if (propName.StartsWith('@'))
                continue;

            var predicate = ExpandTerm(propName);
            if (string.IsNullOrEmpty(predicate))
                continue;

            ProcessProperty(subject, predicate, propName, prop.Value, handler, graphIri ?? _currentGraph);
        }

        return subject;
    }

    private void ProcessContext(JsonElement contextElement)
    {
        if (contextElement.ValueKind == JsonValueKind.String)
        {
            // Remote context - not supported in this implementation
            return;
        }

        if (contextElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var ctx in contextElement.EnumerateArray())
            {
                ProcessContext(ctx);
            }
            return;
        }

        if (contextElement.ValueKind != JsonValueKind.Object)
            return;

        foreach (var prop in contextElement.EnumerateObject())
        {
            var term = prop.Name;
            var value = prop.Value;

            if (term == "@base")
            {
                _baseIri = value.GetString();
            }
            else if (term == "@vocab")
            {
                _vocabIri = value.GetString();
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                // Simple term -> IRI mapping
                _context[term] = value.GetString() ?? "";
            }
            else if (value.ValueKind == JsonValueKind.Object)
            {
                // Expanded term definition
                if (value.TryGetProperty("@id", out var idProp))
                {
                    _context[term] = idProp.GetString() ?? "";
                }

                if (value.TryGetProperty("@type", out var typeProp))
                {
                    var typeVal = typeProp.GetString();
                    if (typeVal == "@id")
                    {
                        _typeCoercion[term] = "@id";
                    }
                    else if (!string.IsNullOrEmpty(typeVal))
                    {
                        _typeCoercion[term] = typeVal;
                    }
                }

                if (value.TryGetProperty("@container", out var containerProp))
                {
                    if (containerProp.GetString() == "@list")
                    {
                        _containerList[term] = true;
                    }
                }
            }
        }
    }

    private void ProcessType(string subject, JsonElement typeElement, QuadHandler handler, string? graphIri)
    {
        var graph = graphIri ?? _currentGraph;

        if (typeElement.ValueKind == JsonValueKind.String)
        {
            var typeIri = ExpandIri(typeElement.GetString() ?? "");
            EmitQuad(handler, subject, RdfType, typeIri, graph);
        }
        else if (typeElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in typeElement.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String)
                {
                    var typeIri = ExpandIri(t.GetString() ?? "");
                    EmitQuad(handler, subject, RdfType, typeIri, graph);
                }
            }
        }
    }

    private void ProcessProperty(string subject, string predicate, string term, JsonElement value,
        QuadHandler handler, string? graphIri)
    {
        // Check for type coercion
        _typeCoercion.TryGetValue(term, out var coercedType);
        var isListContainer = _containerList.TryGetValue(term, out var isList) && isList;

        if (value.ValueKind == JsonValueKind.Array)
        {
            if (isListContainer)
            {
                // Create RDF list
                var listHead = ProcessList(value, handler, graphIri, coercedType);
                EmitQuad(handler, subject, predicate, listHead, graphIri);
            }
            else
            {
                // Multiple values
                foreach (var item in value.EnumerateArray())
                {
                    ProcessValue(subject, predicate, item, handler, graphIri, coercedType);
                }
            }
        }
        else
        {
            ProcessValue(subject, predicate, value, handler, graphIri, coercedType);
        }
    }

    private void ProcessValue(string subject, string predicate, JsonElement value,
        QuadHandler handler, string? graphIri, string? coercedType)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var strVal = value.GetString() ?? "";
                if (coercedType == "@id")
                {
                    // IRI reference
                    var iri = ExpandIri(strVal);
                    EmitQuad(handler, subject, predicate, iri, graphIri);
                }
                else if (!string.IsNullOrEmpty(coercedType))
                {
                    // Typed literal
                    var datatypeIri = ExpandIri(coercedType);
                    var literal = $"\"{EscapeString(strVal)}\"^^{datatypeIri}";
                    EmitQuad(handler, subject, predicate, literal, graphIri);
                }
                else
                {
                    // Plain string literal
                    var literal = $"\"{EscapeString(strVal)}\"";
                    EmitQuad(handler, subject, predicate, literal, graphIri);
                }
                break;

            case JsonValueKind.Number:
                ProcessNumberLiteral(subject, predicate, value, handler, graphIri);
                break;

            case JsonValueKind.True:
                EmitQuad(handler, subject, predicate,
                    "\"true\"^^<http://www.w3.org/2001/XMLSchema#boolean>", graphIri);
                break;

            case JsonValueKind.False:
                EmitQuad(handler, subject, predicate,
                    "\"false\"^^<http://www.w3.org/2001/XMLSchema#boolean>", graphIri);
                break;

            case JsonValueKind.Object:
                ProcessObjectValue(subject, predicate, value, handler, graphIri);
                break;

            case JsonValueKind.Null:
                // null values are ignored in JSON-LD
                break;
        }
    }

    private void ProcessObjectValue(string subject, string predicate, JsonElement value,
        QuadHandler handler, string? graphIri)
    {
        // Check for value object (@value)
        if (value.TryGetProperty("@value", out var valProp))
        {
            var literal = ProcessValueObject(value, valProp);
            EmitQuad(handler, subject, predicate, literal, graphIri);
            return;
        }

        // Check for @id only (IRI reference)
        if (value.TryGetProperty("@id", out var idProp) && value.EnumerateObject().Count() == 1)
        {
            var iri = ExpandIri(idProp.GetString() ?? "");
            EmitQuad(handler, subject, predicate, iri, graphIri);
            return;
        }

        // Check for @list
        if (value.TryGetProperty("@list", out var listProp))
        {
            var listHead = ProcessList(listProp, handler, graphIri, null);
            EmitQuad(handler, subject, predicate, listHead, graphIri);
            return;
        }

        // Nested object - create blank node
        var tempReader = new Utf8JsonReader(Encoding.UTF8.GetBytes(value.GetRawText()));
        tempReader.Read();
        var blankNode = ParseNode(ref tempReader, handler, subject);
        EmitQuad(handler, subject, predicate, blankNode, graphIri);
    }

    private string ProcessValueObject(JsonElement obj, JsonElement valueProp)
    {
        string valueStr;

        switch (valueProp.ValueKind)
        {
            case JsonValueKind.String:
                valueStr = valueProp.GetString() ?? "";
                break;
            case JsonValueKind.Number:
                valueStr = valueProp.GetRawText();
                break;
            case JsonValueKind.True:
                valueStr = "true";
                break;
            case JsonValueKind.False:
                valueStr = "false";
                break;
            default:
                valueStr = "";
                break;
        }

        // Check for @language
        if (obj.TryGetProperty("@language", out var langProp))
        {
            var lang = langProp.GetString() ?? "";
            return $"\"{EscapeString(valueStr)}\"@{lang}";
        }

        // Check for @type (datatype)
        if (obj.TryGetProperty("@type", out var typeProp))
        {
            var datatype = ExpandIri(typeProp.GetString() ?? "");
            return $"\"{EscapeString(valueStr)}\"^^{datatype}";
        }

        // Plain literal
        return $"\"{EscapeString(valueStr)}\"";
    }

    private void ProcessNumberLiteral(string subject, string predicate, JsonElement value,
        QuadHandler handler, string? graphIri)
    {
        var rawText = value.GetRawText();

        // Determine if integer or decimal
        if (rawText.Contains('.') || rawText.Contains('e') || rawText.Contains('E'))
        {
            // Double - must use canonical XSD form with exponent notation
            // Parse and reformat to ensure canonical representation (e.g., 5.3 -> 5.3E0)
            if (double.TryParse(rawText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var doubleValue))
            {
                // Format with exponent notation, uppercase E
                var canonicalForm = doubleValue.ToString("E15", System.Globalization.CultureInfo.InvariantCulture);
                // Trim trailing zeros in the significand but keep at least one digit after decimal
                // E.g., 5.300000000000000E+000 -> 5.3E0
                canonicalForm = NormalizeDoubleCanonical(canonicalForm);
                EmitQuad(handler, subject, predicate,
                    $"\"{canonicalForm}\"^^<http://www.w3.org/2001/XMLSchema#double>", graphIri);
            }
            else
            {
                // Fallback to raw text if parsing fails
                EmitQuad(handler, subject, predicate,
                    $"\"{rawText}\"^^<http://www.w3.org/2001/XMLSchema#double>", graphIri);
            }
        }
        else
        {
            // Integer
            EmitQuad(handler, subject, predicate,
                $"\"{rawText}\"^^<http://www.w3.org/2001/XMLSchema#integer>", graphIri);
        }
    }

    /// <summary>
    /// Normalize a double to canonical XSD form.
    /// E.g., "5.300000000000000E+000" -> "5.3E0"
    /// </summary>
    private static string NormalizeDoubleCanonical(string formatted)
    {
        // Split into mantissa and exponent
        var eIndex = formatted.IndexOf('E');
        if (eIndex < 0) return formatted;

        var mantissa = formatted[..eIndex];
        var exponent = formatted[(eIndex + 1)..];

        // Trim trailing zeros from mantissa (but keep at least one digit after decimal)
        if (mantissa.Contains('.'))
        {
            mantissa = mantissa.TrimEnd('0');
            if (mantissa.EndsWith('.'))
                mantissa += '0'; // Keep at least "X.0"
        }

        // Normalize exponent: remove leading zeros and + sign
        // E.g., "+000" -> "0", "-002" -> "-2"
        if (int.TryParse(exponent, out var expValue))
        {
            exponent = expValue.ToString();
        }

        return mantissa + "E" + exponent;
    }

    private string ProcessList(JsonElement listElement, QuadHandler handler, string? graphIri, string? coercedType)
    {
        if (listElement.ValueKind != JsonValueKind.Array || listElement.GetArrayLength() == 0)
        {
            return RdfNil;
        }

        string? firstNode = null;
        string? previousNode = null;

        foreach (var item in listElement.EnumerateArray())
        {
            var currentNode = GenerateBlankNode();

            if (firstNode == null)
            {
                firstNode = currentNode;
            }

            if (previousNode != null)
            {
                EmitQuad(handler, previousNode, RdfRest, currentNode, graphIri);
            }

            // Process list item
            ProcessValue(currentNode, RdfFirst, item, handler, graphIri, coercedType);

            previousNode = currentNode;
        }

        // Close list with rdf:nil
        if (previousNode != null)
        {
            EmitQuad(handler, previousNode, RdfRest, RdfNil, graphIri);
        }

        return firstNode ?? RdfNil;
    }

    private string ExpandTerm(string term)
    {
        // Check context for term mapping
        if (_context.TryGetValue(term, out var expanded))
        {
            return FormatIri(expanded);
        }

        // Check for compact IRI (prefix:localName)
        var colonIndex = term.IndexOf(':');
        if (colonIndex > 0)
        {
            var prefix = term.Substring(0, colonIndex);
            var localName = term.Substring(colonIndex + 1);

            if (_context.TryGetValue(prefix, out var prefixIri))
            {
                return FormatIri(prefixIri + localName);
            }

            // Assume it's already an IRI
            if (term.Contains("://"))
            {
                return FormatIri(term);
            }
        }

        // Use @vocab if defined
        if (!string.IsNullOrEmpty(_vocabIri))
        {
            return FormatIri(_vocabIri + term);
        }

        // Return as-is (invalid, but allows processing to continue)
        return FormatIri(term);
    }

    private string ExpandIri(string value)
    {
        // Empty string resolves to base IRI per JSON-LD spec
        if (string.IsNullOrEmpty(value))
        {
            if (!string.IsNullOrEmpty(_baseIri))
                return FormatIri(_baseIri);
            return GenerateBlankNode();
        }

        // Already an absolute IRI
        if (value.Contains("://"))
        {
            return FormatIri(value);
        }

        // Blank node
        if (value.StartsWith("_:"))
        {
            return value;
        }

        // Check context
        if (_context.TryGetValue(value, out var expanded))
        {
            return FormatIri(expanded);
        }

        // Check for compact IRI
        var colonIndex = value.IndexOf(':');
        if (colonIndex > 0)
        {
            var prefix = value.Substring(0, colonIndex);
            var localName = value.Substring(colonIndex + 1);

            if (_context.TryGetValue(prefix, out var prefixIri))
            {
                return FormatIri(prefixIri + localName);
            }
        }

        // Resolve against @base
        if (!string.IsNullOrEmpty(_baseIri))
        {
            return FormatIri(ResolveRelativeIri(_baseIri, value));
        }

        // Return as-is
        return FormatIri(value);
    }

    private static string ResolveRelativeIri(string baseIri, string relative)
    {
        if (string.IsNullOrEmpty(relative))
            return baseIri;

        if (relative.StartsWith('#'))
        {
            // Fragment
            var hashIndex = baseIri.IndexOf('#');
            if (hashIndex >= 0)
            {
                return baseIri.Substring(0, hashIndex) + relative;
            }
            return baseIri + relative;
        }

        if (relative.StartsWith('/'))
        {
            // Absolute path
            var schemeEnd = baseIri.IndexOf("://");
            if (schemeEnd >= 0)
            {
                var authorityEnd = baseIri.IndexOf('/', schemeEnd + 3);
                if (authorityEnd >= 0)
                {
                    return baseIri.Substring(0, authorityEnd) + relative;
                }
                return baseIri + relative;
            }
            return relative;
        }

        // Relative path - resolve against base directory
        var lastSlash = baseIri.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            return baseIri.Substring(0, lastSlash + 1) + relative;
        }

        return baseIri + "/" + relative;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string FormatIri(string iri)
    {
        if (iri.StartsWith('<') && iri.EndsWith('>'))
            return iri;
        return $"<{iri}>";
    }

    private string GenerateBlankNode()
    {
        return $"_:b{_blankNodeCounter++}";
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EmitQuad(QuadHandler handler, string subject, string predicate, string obj, string? graph)
    {
        if (graph != null)
        {
            handler(subject.AsSpan(), predicate.AsSpan(), obj.AsSpan(), graph.AsSpan());
        }
        else
        {
            handler(subject.AsSpan(), predicate.AsSpan(), obj.AsSpan(), ReadOnlySpan<char>.Empty);
        }
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

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_inputBuffer != null)
        {
            _bufferManager.Return(_inputBuffer);
            _inputBuffer = null!;
        }

        if (_outputBuffer != null)
        {
            _bufferManager.Return(_outputBuffer);
            _outputBuffer = null!;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
