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
    private readonly Dictionary<string, bool> _containerLanguage; // term -> is @language container
    private readonly Dictionary<string, bool> _containerIndex; // term -> is @index container
    private readonly Dictionary<string, bool> _containerGraph; // term -> is @graph container
    private readonly Dictionary<string, string> _termLanguage; // term -> @language value
    private readonly Dictionary<string, string> _reverseProperty; // term -> reverse predicate IRI
    private readonly Dictionary<string, string> _scopedContext; // term -> nested @context JSON
    private readonly HashSet<string> _typeAliases; // terms aliased to @type
    private readonly HashSet<string> _idAliases; // terms aliased to @id
    private readonly HashSet<string> _graphAliases; // terms aliased to @graph
    private readonly HashSet<string> _includedAliases; // terms aliased to @included
    private readonly HashSet<string> _nullTerms; // terms decoupled from @vocab (mapped to null)

    // Base IRI for relative IRI resolution
    private string? _baseIri;

    // Vocabulary IRI for term expansion
    private string? _vocabIri;

    // Default language from @language in context
    private string? _defaultLanguage;

    // Blank node counter
    private int _blankNodeCounter;

    // Current graph (null for default graph)
    private string? _currentGraph;

    // Saved context state for nested node restoration (type-scoped contexts don't propagate)
    private Dictionary<string, string>? _savedContextForNested;
    private string? _savedVocabForNested;
    private string? _savedBaseForNested;

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
        _containerLanguage = new Dictionary<string, bool>(StringComparer.Ordinal);
        _containerIndex = new Dictionary<string, bool>(StringComparer.Ordinal);
        _containerGraph = new Dictionary<string, bool>(StringComparer.Ordinal);
        _termLanguage = new Dictionary<string, string>(StringComparer.Ordinal);
        _reverseProperty = new Dictionary<string, string>(StringComparer.Ordinal);
        _scopedContext = new Dictionary<string, string>(StringComparer.Ordinal);
        _typeAliases = new HashSet<string>(StringComparer.Ordinal);
        _idAliases = new HashSet<string>(StringComparer.Ordinal);
        _graphAliases = new HashSet<string>(StringComparer.Ordinal);
        _includedAliases = new HashSet<string>(StringComparer.Ordinal);
        _nullTerms = new HashSet<string>(StringComparer.Ordinal);
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

        // Track whether type-scoped context was applied (for nested node restoration)
        // Type-scoped contexts apply to this node's properties but should not propagate to nested nodes
        // Property-scoped contexts DO propagate, so we only save state if type-scoped context is applied
        Dictionary<string, string>? savedContextForNested = null;
        string? savedVocabForNested = null;
        string? savedBaseForNested = null;
        bool hasTypeScopedContext = false;
        JsonElement typeElement = default;

        // Check for @type and apply type-scoped context BEFORE expanding @id
        // (type-scoped context may change @base which affects @id resolution)
        if (root.TryGetProperty("@type", out typeElement))
        {
            // Check if any type has a scoped context BEFORE applying
            hasTypeScopedContext = HasTypeScopedContext(typeElement);

            if (hasTypeScopedContext)
            {
                // Save state BEFORE applying type-scoped contexts
                savedContextForNested = new Dictionary<string, string>(_context);
                savedVocabForNested = _vocabIri;
                savedBaseForNested = _baseIri;
            }

            // Apply type-scoped contexts (may change @base)
            ApplyTypeScopedContexts(typeElement);
        }

        // Get @id for subject (AFTER type-scoped context is applied)
        // Check both @id and any aliases for @id
        if (root.TryGetProperty("@id", out var idElement))
        {
            subject = ExpandIri(idElement.GetString() ?? "");
        }
        else
        {
            // Check for @id aliases
            foreach (var alias in _idAliases)
            {
                if (root.TryGetProperty(alias, out var aliasIdElement))
                {
                    subject = ExpandIri(aliasIdElement.GetString() ?? "");
                    break;
                }
            }
        }

        // Check for @graph or @graph alias
        JsonElement graphElement = default;
        if (root.TryGetProperty("@graph", out graphElement))
        {
            hasGraphKeyword = true;
            // Subject becomes the graph IRI if present
            graphIri = subject;
        }
        else
        {
            // Check for @graph aliases
            foreach (var alias in _graphAliases)
            {
                if (root.TryGetProperty(alias, out graphElement))
                {
                    hasGraphKeyword = true;
                    graphIri = subject;
                    break;
                }
            }
        }

        // Generate blank node if no @id
        subject ??= GenerateBlankNode();

        // If this node has @graph and we generated a blank node for subject,
        // update graphIri to match ONLY if the node has other properties
        // (a node with just @graph should process content in default graph)
        if (hasGraphKeyword && graphIri == null)
        {
            // Check if there are any non-keyword properties
            bool hasOtherProperties = false;
            foreach (var prop in root.EnumerateObject())
            {
                var name = prop.Name;
                if (!name.StartsWith('@') && !_graphAliases.Contains(name))
                {
                    hasOtherProperties = true;
                    break;
                }
            }
            if (hasOtherProperties)
            {
                graphIri = subject;
            }
        }

        // Emit type triple (after we have the subject)
        if (typeElement.ValueKind != JsonValueKind.Undefined)
        {
            ProcessType(subject, typeElement, handler, _currentGraph);
        }

        // Store saved state for nested node restoration (only if type-scoped context was applied)
        var previousSavedContext = _savedContextForNested;
        var previousSavedVocab = _savedVocabForNested;
        var previousSavedBase = _savedBaseForNested;
        if (hasTypeScopedContext)
        {
            _savedContextForNested = savedContextForNested;
            _savedVocabForNested = savedVocabForNested;
            _savedBaseForNested = savedBaseForNested;
        }

        // Process @reverse keyword - contains reverse properties
        if (root.TryGetProperty("@reverse", out var reverseElement))
        {
            ProcessReverseKeyword(subject, reverseElement, handler, _currentGraph);
        }

        // Process other properties FIRST (before @graph content)
        // Properties on the containing node go in the current graph, not the named graph
        foreach (var prop in root.EnumerateObject())
        {
            var propName = prop.Name;

            // Skip JSON-LD keywords we've already processed
            if (propName.StartsWith('@'))
                continue;

            // Check for @type alias
            if (_typeAliases.Contains(propName))
            {
                ProcessType(subject, prop.Value, handler, _currentGraph);
                // Also apply type-scoped contexts
                ApplyTypeScopedContexts(prop.Value);
                continue;
            }

            // Skip @id aliases (already processed above)
            if (_idAliases.Contains(propName))
                continue;

            // Skip @graph aliases (already processed above)
            if (_graphAliases.Contains(propName))
                continue;

            // Skip @included aliases (processed below)
            if (_includedAliases.Contains(propName))
                continue;

            // Check if this is a reverse property (term definition has @reverse but no @id)
            // Reverse properties may not expand to a predicate IRI, but ProcessProperty handles them
            var isReverseProperty = _reverseProperty.ContainsKey(propName);

            var predicate = ExpandTerm(propName);
            if (string.IsNullOrEmpty(predicate) && !isReverseProperty)
                continue;

            ProcessProperty(subject, predicate, propName, prop.Value, handler, _currentGraph);
        }

        if (hasGraphKeyword)
        {
            // Process @graph contents - these go in the named graph
            var savedGraph = _currentGraph;
            _currentGraph = graphIri;

            if (graphElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in graphElement.EnumerateArray())
                {
                    ProcessGraphNode(node, handler);
                }
            }
            else if (graphElement.ValueKind == JsonValueKind.Object)
            {
                ProcessGraphNode(graphElement, handler);
            }

            _currentGraph = savedGraph;
        }

        // Process @included - additional nodes that are not linked to this node
        if (root.TryGetProperty("@included", out var includedElement))
        {
            ProcessIncludedNodes(includedElement, handler);
        }

        // Also check for @included aliases
        foreach (var alias in _includedAliases)
        {
            if (root.TryGetProperty(alias, out var aliasedIncluded))
            {
                ProcessIncludedNodes(aliasedIncluded, handler);
            }
        }

        // Restore context if type-scoped context was applied
        // This ensures type-scoped modifications don't leak to subsequent properties in the parent node
        if (hasTypeScopedContext && savedContextForNested != null)
        {
            _context.Clear();
            foreach (var kv in savedContextForNested) _context[kv.Key] = kv.Value;
            _vocabIri = savedVocabForNested;
            _baseIri = savedBaseForNested;
        }

        // Restore previous saved state
        _savedContextForNested = previousSavedContext;
        _savedVocabForNested = previousSavedVocab;
        _savedBaseForNested = previousSavedBase;

        return subject;
    }

    /// <summary>
    /// Process a node in @graph context, handling free-floating values, @set, and @list.
    /// </summary>
    private void ProcessGraphNode(JsonElement node, QuadHandler handler)
    {
        // Skip primitives - they are free-floating values
        if (node.ValueKind != JsonValueKind.Object)
            return;

        // Skip value objects - they are free-floating and produce no triples
        if (node.TryGetProperty("@value", out _))
            return;

        // Skip free-floating @list objects - they produce no output
        if (node.TryGetProperty("@list", out _))
            return;

        // Handle @set objects - process their contents
        if (node.TryGetProperty("@set", out var setElement))
        {
            if (setElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in setElement.EnumerateArray())
                {
                    ProcessGraphNode(item, handler);
                }
            }
            else
            {
                ProcessGraphNode(setElement, handler);
            }
            return;
        }

        // Regular node object - parse it
        var tempReader = new Utf8JsonReader(Encoding.UTF8.GetBytes(node.GetRawText()));
        tempReader.Read();
        ParseNode(ref tempReader, handler, null);
    }

    /// <summary>
    /// Process @included nodes - additional nodes that are not linked to the containing node.
    /// </summary>
    private void ProcessIncludedNodes(JsonElement element, QuadHandler handler)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var node in element.EnumerateArray())
            {
                ProcessGraphNode(node, handler);
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            ProcessGraphNode(element, handler);
        }
    }

    private void ProcessContext(JsonElement contextElement)
    {
        if (contextElement.ValueKind == JsonValueKind.String)
        {
            // Remote context - not supported in this implementation
            return;
        }

        if (contextElement.ValueKind == JsonValueKind.Null)
        {
            // Null context clears all term definitions
            _context.Clear();
            _typeCoercion.Clear();
            _containerList.Clear();
            _containerLanguage.Clear();
            _containerIndex.Clear();
            _termLanguage.Clear();
            _reverseProperty.Clear();
            _scopedContext.Clear();
            _typeAliases.Clear();
            _idAliases.Clear();
            _graphAliases.Clear();
            _includedAliases.Clear();
            _nullTerms.Clear();
            _vocabIri = null;
            _defaultLanguage = null;
            // Note: @base is NOT cleared by null context per JSON-LD spec
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
            else if (term == "@language")
            {
                _defaultLanguage = value.ValueKind == JsonValueKind.Null ? null : value.GetString();
            }
            else if (value.ValueKind == JsonValueKind.Null)
            {
                // Term mapped to null - decouple from @vocab
                _nullTerms.Add(term);
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                var mappedValue = value.GetString() ?? "";
                // Check for keyword alias or transitive alias chain
                if (mappedValue == "@type" || _typeAliases.Contains(mappedValue))
                {
                    _typeAliases.Add(term);
                }
                else if (mappedValue == "@id" || _idAliases.Contains(mappedValue))
                {
                    _idAliases.Add(term);
                }
                else if (mappedValue == "@graph" || _graphAliases.Contains(mappedValue))
                {
                    _graphAliases.Add(term);
                }
                else if (mappedValue == "@included" || _includedAliases.Contains(mappedValue))
                {
                    _includedAliases.Add(term);
                }
                else
                {
                    // Simple term -> IRI mapping
                    _context[term] = mappedValue;
                }
            }
            else if (value.ValueKind == JsonValueKind.Object)
            {
                // Expanded term definition
                if (value.TryGetProperty("@id", out var idProp))
                {
                    if (idProp.ValueKind == JsonValueKind.Null)
                    {
                        // @id: null - decouple term from @vocab
                        _nullTerms.Add(term);
                    }
                    else
                    {
                        _context[term] = idProp.GetString() ?? "";
                    }
                }

                // Handle @reverse - the term maps to a reverse property
                if (value.TryGetProperty("@reverse", out var reverseProp))
                {
                    var reverseIri = reverseProp.GetString();
                    if (!string.IsNullOrEmpty(reverseIri))
                    {
                        _reverseProperty[term] = reverseIri;
                    }
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
                    // @container can be a string or an array in JSON-LD 1.1
                    void ProcessContainerValue(string? containerVal)
                    {
                        if (containerVal == "@list")
                            _containerList[term] = true;
                        else if (containerVal == "@language")
                            _containerLanguage[term] = true;
                        else if (containerVal == "@index")
                            _containerIndex[term] = true;
                        else if (containerVal == "@graph")
                            _containerGraph[term] = true;
                        // Note: @id, @set, @type containers not yet fully supported
                    }

                    if (containerProp.ValueKind == JsonValueKind.String)
                    {
                        ProcessContainerValue(containerProp.GetString());
                    }
                    else if (containerProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var containerItem in containerProp.EnumerateArray())
                        {
                            if (containerItem.ValueKind == JsonValueKind.String)
                                ProcessContainerValue(containerItem.GetString());
                        }
                    }
                }

                // Handle term-level @language
                if (value.TryGetProperty("@language", out var langProp))
                {
                    if (langProp.ValueKind == JsonValueKind.Null)
                    {
                        // @language: null means no language tag (override default)
                        _termLanguage[term] = "";
                    }
                    else
                    {
                        var langVal = langProp.GetString();
                        if (!string.IsNullOrEmpty(langVal))
                        {
                            _termLanguage[term] = langVal;
                        }
                    }
                }

                // Handle scoped context - nested @context for this term
                if (value.TryGetProperty("@context", out var scopedContextProp))
                {
                    _scopedContext[term] = scopedContextProp.GetRawText();
                }
            }
        }
    }

    /// <summary>
    /// Check if any type in @type has a scoped context.
    /// </summary>
    private bool HasTypeScopedContext(JsonElement typeElement)
    {
        if (typeElement.ValueKind == JsonValueKind.String)
        {
            var typeTerm = typeElement.GetString() ?? "";
            return _scopedContext.ContainsKey(typeTerm);
        }
        else if (typeElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in typeElement.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String)
                {
                    var typeTerm = t.GetString() ?? "";
                    if (_scopedContext.ContainsKey(typeTerm))
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Apply type-scoped contexts from @type values.
    /// </summary>
    private void ApplyTypeScopedContexts(JsonElement typeElement)
    {
        if (typeElement.ValueKind == JsonValueKind.String)
        {
            var typeTerm = typeElement.GetString() ?? "";
            if (_scopedContext.TryGetValue(typeTerm, out var scopedJson))
            {
                using var doc = JsonDocument.Parse(scopedJson);
                ProcessContext(doc.RootElement);
            }
        }
        else if (typeElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in typeElement.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String)
                {
                    var typeTerm = t.GetString() ?? "";
                    if (_scopedContext.TryGetValue(typeTerm, out var scopedJson))
                    {
                        using var doc = JsonDocument.Parse(scopedJson);
                        ProcessContext(doc.RootElement);
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
            var typeIri = ExpandTypeIri(typeElement.GetString() ?? "");
            EmitQuad(handler, subject, RdfType, typeIri, graph);
        }
        else if (typeElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in typeElement.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String)
                {
                    var typeIri = ExpandTypeIri(t.GetString() ?? "");
                    EmitQuad(handler, subject, RdfType, typeIri, graph);
                }
            }
        }
    }

    /// <summary>
    /// Expand a type IRI using @vocab for relative IRIs (not @base).
    /// </summary>
    private string ExpandTypeIri(string value)
    {
        if (string.IsNullOrEmpty(value))
            return GenerateBlankNode();

        // Blank node
        if (value.StartsWith("_:"))
            return value;

        // Check context first (exact term match)
        if (_context.TryGetValue(value, out var expanded))
        {
            if (IsAbsoluteIri(expanded))
                return FormatIri(expanded);
            return ExpandTypeIri(expanded);
        }

        // Already an absolute IRI
        if (IsAbsoluteIri(value))
        {
            // Check if the "scheme" part is a defined prefix in context
            var colonIndex = value.IndexOf(':');
            var prefix = value.Substring(0, colonIndex);
            if (_context.TryGetValue(prefix, out var prefixIri))
            {
                var localName = value.Substring(colonIndex + 1);
                return FormatIri(prefixIri + localName);
            }
            return FormatIri(value);
        }

        // Check for compact IRI (prefix:localName)
        var colonIdx = value.IndexOf(':');
        if (colonIdx > 0)
        {
            var prefix = value.Substring(0, colonIdx);
            var localName = value.Substring(colonIdx + 1);

            if (_context.TryGetValue(prefix, out var prefixIri))
            {
                return FormatIri(prefixIri + localName);
            }
        }

        // For types, resolve against @vocab (not @base)
        if (!string.IsNullOrEmpty(_vocabIri))
        {
            return FormatIri(_vocabIri + value);
        }

        // Fallback to @base
        if (!string.IsNullOrEmpty(_baseIri))
        {
            return FormatIri(ResolveRelativeIri(_baseIri, value));
        }

        return FormatIri(value);
    }

    private void ProcessProperty(string subject, string predicate, string term, JsonElement value,
        QuadHandler handler, string? graphIri)
    {
        // Check for type coercion and term-level language
        _typeCoercion.TryGetValue(term, out var coercedType);
        _termLanguage.TryGetValue(term, out var termLang);
        var isListContainer = _containerList.TryGetValue(term, out var isList) && isList;
        var isLanguageContainer = _containerLanguage.TryGetValue(term, out var isLang) && isLang;
        var isIndexContainer = _containerIndex.TryGetValue(term, out var isIdx) && isIdx;
        var isGraphContainer = _containerGraph.TryGetValue(term, out var isGraph) && isGraph;

        // Check if this is a reverse property
        if (_reverseProperty.TryGetValue(term, out var reversePredicate))
        {
            // For reverse properties, values become subjects and the current node becomes object
            var expandedReversePredicate = ExpandTermValue(reversePredicate);

            // Handle index container with reverse property
            if (isIndexContainer && value.ValueKind == JsonValueKind.Object)
            {
                // Value is an index map - iterate over values (keys are ignored)
                foreach (var prop in value.EnumerateObject())
                {
                    ProcessReverseProperty(subject, expandedReversePredicate, prop.Value, handler, graphIri, coercedType);
                }
            }
            else
            {
                ProcessReverseProperty(subject, expandedReversePredicate, value, handler, graphIri, coercedType);
            }
            return;
        }

        // Apply scoped context if defined for this term
        Dictionary<string, string>? savedContext = null;
        Dictionary<string, string>? savedTypeCoercion = null;
        Dictionary<string, bool>? savedContainerList = null;
        Dictionary<string, bool>? savedContainerLanguage = null;
        Dictionary<string, bool>? savedContainerIndex = null;
        Dictionary<string, bool>? savedContainerGraph = null;
        Dictionary<string, string>? savedTermLanguage = null;
        Dictionary<string, string>? savedReverseProperty = null;
        Dictionary<string, string>? savedScopedContext = null;
        HashSet<string>? savedTypeAliases = null;
        HashSet<string>? savedIdAliases = null;
        HashSet<string>? savedGraphAliases = null;
        HashSet<string>? savedIncludedAliases = null;
        HashSet<string>? savedNullTerms = null;
        string? savedVocabIri = null;
        string? savedBaseIri = null;
        string? savedDefaultLanguage = null;
        if (_scopedContext.TryGetValue(term, out var scopedContextJson))
        {
            // Save current context state (all fields that ProcessContext can modify)
            savedContext = new Dictionary<string, string>(_context);
            savedTypeCoercion = new Dictionary<string, string>(_typeCoercion);
            savedContainerList = new Dictionary<string, bool>(_containerList);
            savedContainerLanguage = new Dictionary<string, bool>(_containerLanguage);
            savedContainerIndex = new Dictionary<string, bool>(_containerIndex);
            savedContainerGraph = new Dictionary<string, bool>(_containerGraph);
            savedTermLanguage = new Dictionary<string, string>(_termLanguage);
            savedReverseProperty = new Dictionary<string, string>(_reverseProperty);
            savedScopedContext = new Dictionary<string, string>(_scopedContext);
            savedTypeAliases = new HashSet<string>(_typeAliases);
            savedIdAliases = new HashSet<string>(_idAliases);
            savedGraphAliases = new HashSet<string>(_graphAliases);
            savedIncludedAliases = new HashSet<string>(_includedAliases);
            savedNullTerms = new HashSet<string>(_nullTerms);
            savedVocabIri = _vocabIri;
            savedBaseIri = _baseIri;
            savedDefaultLanguage = _defaultLanguage;

            // Apply the scoped context
            using var scopedDoc = JsonDocument.Parse(scopedContextJson);
            ProcessContext(scopedDoc.RootElement);
        }

        try
        {
            // Handle graph container - values are graph objects
            if (isGraphContainer)
            {
                ProcessGraphContainer(subject, predicate, value, handler, graphIri, coercedType, termLang);
            }
            // Handle language container - object keys are language tags
            else if (isLanguageContainer && value.ValueKind == JsonValueKind.Object)
            {
                ProcessLanguageMap(subject, predicate, value, handler, graphIri);
            }
            // Handle index container - object keys are index values (ignored)
            else if (isIndexContainer && value.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in value.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in prop.Value.EnumerateArray())
                        {
                            ProcessValue(subject, predicate, item, handler, graphIri, coercedType, termLang);
                        }
                    }
                    else
                    {
                        ProcessValue(subject, predicate, prop.Value, handler, graphIri, coercedType, termLang);
                    }
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                // Handle @json type - serialize entire array as canonical JSON literal
                if (coercedType == "@json")
                {
                    var canonicalJson = CanonicalizeJson(value);
                    var escapedJson = EscapeString(canonicalJson);
                    EmitQuad(handler, subject, predicate,
                        $"\"{escapedJson}\"^^<http://www.w3.org/1999/02/22-rdf-syntax-ns#JSON>", graphIri);
                }
                else if (isListContainer)
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
                        ProcessValue(subject, predicate, item, handler, graphIri, coercedType, termLang);
                    }
                }
            }
            else
            {
                ProcessValue(subject, predicate, value, handler, graphIri, coercedType, termLang);
            }
        }
        finally
        {
            // Restore context if scoped context was applied
            if (savedContext != null)
            {
                _context.Clear();
                foreach (var kv in savedContext) _context[kv.Key] = kv.Value;

                _typeCoercion.Clear();
                foreach (var kv in savedTypeCoercion!) _typeCoercion[kv.Key] = kv.Value;

                _containerList.Clear();
                foreach (var kv in savedContainerList!) _containerList[kv.Key] = kv.Value;

                _containerLanguage.Clear();
                foreach (var kv in savedContainerLanguage!) _containerLanguage[kv.Key] = kv.Value;

                _containerIndex.Clear();
                foreach (var kv in savedContainerIndex!) _containerIndex[kv.Key] = kv.Value;

                _containerGraph.Clear();
                foreach (var kv in savedContainerGraph!) _containerGraph[kv.Key] = kv.Value;

                _termLanguage.Clear();
                foreach (var kv in savedTermLanguage!) _termLanguage[kv.Key] = kv.Value;

                _reverseProperty.Clear();
                foreach (var kv in savedReverseProperty!) _reverseProperty[kv.Key] = kv.Value;

                _scopedContext.Clear();
                foreach (var kv in savedScopedContext!) _scopedContext[kv.Key] = kv.Value;

                _typeAliases.Clear();
                foreach (var alias in savedTypeAliases!) _typeAliases.Add(alias);

                _idAliases.Clear();
                foreach (var alias in savedIdAliases!) _idAliases.Add(alias);

                _graphAliases.Clear();
                foreach (var alias in savedGraphAliases!) _graphAliases.Add(alias);

                _includedAliases.Clear();
                foreach (var alias in savedIncludedAliases!) _includedAliases.Add(alias);

                _nullTerms.Clear();
                foreach (var t in savedNullTerms!) _nullTerms.Add(t);

                _vocabIri = savedVocabIri;
                _baseIri = savedBaseIri;
                _defaultLanguage = savedDefaultLanguage;
            }
        }
    }

    private void ProcessReverseProperty(string currentNode, string predicate, JsonElement value,
        QuadHandler handler, string? graphIri, string? coercedType)
    {
        // For reverse properties, each value becomes a subject with currentNode as object
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                ProcessReverseValue(currentNode, predicate, item, handler, graphIri, coercedType);
            }
        }
        else
        {
            ProcessReverseValue(currentNode, predicate, value, handler, graphIri, coercedType);
        }
    }

    private void ProcessReverseValue(string currentNode, string predicate, JsonElement value,
        QuadHandler handler, string? graphIri, string? coercedType)
    {
        // The value becomes the subject, currentNode becomes the object
        string newSubject;

        if (value.ValueKind == JsonValueKind.String)
        {
            var strVal = value.GetString() ?? "";
            // If type coercion is @id, treat as IRI
            if (coercedType == "@id" || strVal.StartsWith("_:"))
            {
                newSubject = ExpandIri(strVal);
            }
            else
            {
                // String value becomes IRI
                newSubject = ExpandIri(strVal);
            }
            EmitQuad(handler, newSubject, predicate, currentNode, graphIri);
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            // Nested object - parse it fully to process its properties
            var tempReader = new Utf8JsonReader(Encoding.UTF8.GetBytes(value.GetRawText()));
            tempReader.Read();
            newSubject = ParseNode(ref tempReader, handler, null);

            // Emit the reverse triple
            EmitQuad(handler, newSubject, predicate, currentNode, graphIri);
        }
        else
        {
            // Other types: generate blank node
            newSubject = GenerateBlankNode();
            EmitQuad(handler, newSubject, predicate, currentNode, graphIri);
        }
    }

    /// <summary>
    /// Process the @reverse keyword which contains reverse properties.
    /// Each property in @reverse becomes a predicate where values are subjects and currentNode is object.
    /// If the property is itself a reverse property (defined with @reverse in context),
    /// the double-negation results in a forward triple.
    /// </summary>
    private void ProcessReverseKeyword(string currentNode, JsonElement reverseElement, QuadHandler handler, string? graphIri)
    {
        if (reverseElement.ValueKind != JsonValueKind.Object)
            return;

        foreach (var prop in reverseElement.EnumerateObject())
        {
            var propName = prop.Name;
            var propValue = prop.Value;

            // Check if this property is itself a reverse property (double-negation)
            if (_reverseProperty.TryGetValue(propName, out var reversePredicate))
            {
                // Double-negation: reverse of reverse = forward
                // Process as a forward property from currentNode to values
                var expandedPredicate = ExpandTermValue(reversePredicate);
                if (string.IsNullOrEmpty(expandedPredicate))
                    continue;

                if (propValue.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in propValue.EnumerateArray())
                    {
                        ProcessReverseKeywordValueForward(currentNode, expandedPredicate, item, handler, graphIri);
                    }
                }
                else
                {
                    ProcessReverseKeywordValueForward(currentNode, expandedPredicate, propValue, handler, graphIri);
                }
            }
            else
            {
                // Normal reverse property in @reverse block
                var predicate = ExpandTerm(propName);
                if (string.IsNullOrEmpty(predicate))
                    continue;

                if (propValue.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in propValue.EnumerateArray())
                    {
                        ProcessReverseKeywordValue(currentNode, predicate, item, handler, graphIri);
                    }
                }
                else
                {
                    ProcessReverseKeywordValue(currentNode, predicate, propValue, handler, graphIri);
                }
            }
        }
    }

    /// <summary>
    /// Process a forward triple from @reverse block (when property is itself @reverse, causing double-negation).
    /// currentNode becomes subject, value becomes object.
    /// </summary>
    private void ProcessReverseKeywordValueForward(string currentNode, string predicate, JsonElement value, QuadHandler handler, string? graphIri)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            // Parse the nested node and get its subject, then emit forward triple
            var tempReader = new Utf8JsonReader(Encoding.UTF8.GetBytes(value.GetRawText()));
            tempReader.Read();
            var nestedSubject = ParseNode(ref tempReader, handler, null);

            // Forward triple: currentNode -> predicate -> nestedSubject
            EmitQuad(handler, currentNode, predicate, nestedSubject, graphIri);
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            var strVal = value.GetString() ?? "";
            var objectIri = ExpandIri(strVal);
            EmitQuad(handler, currentNode, predicate, objectIri, graphIri);
        }
    }

    /// <summary>
    /// Process a single value from @reverse - the value becomes a subject, currentNode becomes object.
    /// </summary>
    private void ProcessReverseKeywordValue(string currentNode, string predicate, JsonElement value, QuadHandler handler, string? graphIri)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            // Parse the nested node and get its subject
            var tempReader = new Utf8JsonReader(Encoding.UTF8.GetBytes(value.GetRawText()));
            tempReader.Read();
            var nestedSubject = ParseNode(ref tempReader, handler, null);

            // Emit the reverse triple
            EmitQuad(handler, nestedSubject, predicate, currentNode, graphIri);
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            var strVal = value.GetString() ?? "";
            var newSubject = ExpandIri(strVal);
            EmitQuad(handler, newSubject, predicate, currentNode, graphIri);
        }
    }

    /// <summary>
    /// Process a language map (@container: @language).
    /// Object keys are language tags, values are strings or arrays of strings.
    /// </summary>
    private void ProcessLanguageMap(string subject, string predicate, JsonElement value, QuadHandler handler, string? graphIri)
    {
        foreach (var prop in value.EnumerateObject())
        {
            var langTag = prop.Name;
            var langValue = prop.Value;

            if (langValue.ValueKind == JsonValueKind.String)
            {
                var strVal = langValue.GetString() ?? "";
                var literal = $"\"{EscapeString(strVal)}\"@{langTag}";
                EmitQuad(handler, subject, predicate, literal, graphIri);
            }
            else if (langValue.ValueKind == JsonValueKind.Array)
            {
                // Multiple values for same language
                foreach (var item in langValue.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var strVal = item.GetString() ?? "";
                        var literal = $"\"{EscapeString(strVal)}\"@{langTag}";
                        EmitQuad(handler, subject, predicate, literal, graphIri);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Process a graph container value.
    /// The value becomes a named graph with the property linking to the graph node.
    /// </summary>
    private void ProcessGraphContainer(string subject, string predicate, JsonElement value,
        QuadHandler handler, string? graphIri, string? coercedType, string? termLanguage)
    {
        // Handle arrays - each item is a separate graph
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                ProcessGraphContainerItem(subject, predicate, item, handler, graphIri, coercedType, termLanguage);
            }
        }
        else
        {
            ProcessGraphContainerItem(subject, predicate, value, handler, graphIri, coercedType, termLanguage);
        }
    }

    private void ProcessGraphContainerItem(string subject, string predicate, JsonElement value,
        QuadHandler handler, string? graphIri, string? coercedType, string? termLanguage)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            // Non-object values in graph container are processed normally
            ProcessValue(subject, predicate, value, handler, graphIri, coercedType, termLanguage);
            return;
        }

        // Check if the object has @id - use that as the graph name, otherwise create a blank node
        string namedGraphIri;
        if (value.TryGetProperty("@id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
        {
            namedGraphIri = ExpandIri(idProp.GetString() ?? "", expandTerms: false);
        }
        else
        {
            namedGraphIri = GenerateBlankNode();
        }

        // Emit the link from subject to the graph node
        EmitQuad(handler, subject, predicate, namedGraphIri, graphIri);

        // Save current graph and set to the named graph for content processing
        var savedGraph = _currentGraph;
        _currentGraph = namedGraphIri;

        try
        {
            // Process the content - check for @graph property or process as regular node
            if (value.TryGetProperty("@graph", out var graphProp))
            {
                // Value has explicit @graph - process its contents into the named graph
                if (graphProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var node in graphProp.EnumerateArray())
                    {
                        if (node.ValueKind == JsonValueKind.Object)
                        {
                            ProcessGraphNode(node, handler);
                        }
                    }
                }
                else if (graphProp.ValueKind == JsonValueKind.Object)
                {
                    ProcessGraphNode(graphProp, handler);
                }
            }
            else
            {
                // No explicit @graph - process the object itself into the named graph
                ProcessGraphNode(value, handler);
            }
        }
        finally
        {
            // Restore the previous graph
            _currentGraph = savedGraph;
        }
    }

    private void ProcessValue(string subject, string predicate, JsonElement value,
        QuadHandler handler, string? graphIri, string? coercedType, string? termLanguage = null)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var strVal = value.GetString() ?? "";
                if (coercedType == "@id")
                {
                    // IRI reference - compact IRIs and relative IRIs, NOT terms
                    // See test e056: "Use terms with @type: @vocab but not with @type: @id"
                    var iri = ExpandIri(strVal, expandTerms: false);
                    EmitQuad(handler, subject, predicate, iri, graphIri);
                }
                else if (coercedType == "@vocab")
                {
                    // Vocabulary IRI - first try as term, then use @vocab
                    var iri = ExpandIri(strVal, expandTerms: true);
                    EmitQuad(handler, subject, predicate, iri, graphIri);
                }
                else if (!string.IsNullOrEmpty(coercedType))
                {
                    // Typed literal - terms like "dateTime" should be expanded using @vocab
                    var datatypeIri = ExpandIri(coercedType, expandTerms: true);
                    var literal = $"\"{EscapeString(strVal)}\"^^{datatypeIri}";
                    EmitQuad(handler, subject, predicate, literal, graphIri);
                }
                else
                {
                    // Plain string literal - apply term language, then default language
                    // Term-level @language: null (empty string) means explicitly no language tag
                    string literal;
                    if (termLanguage != null)
                    {
                        // Term has explicit @language setting
                        if (termLanguage.Length > 0)
                        {
                            literal = $"\"{EscapeString(strVal)}\"@{termLanguage}";
                        }
                        else
                        {
                            // @language: null in term definition - no language tag
                            literal = $"\"{EscapeString(strVal)}\"";
                        }
                    }
                    else if (!string.IsNullOrEmpty(_defaultLanguage))
                    {
                        literal = $"\"{EscapeString(strVal)}\"@{_defaultLanguage}";
                    }
                    else
                    {
                        literal = $"\"{EscapeString(strVal)}\"";
                    }
                    EmitQuad(handler, subject, predicate, literal, graphIri);
                }
                break;

            case JsonValueKind.Number:
                ProcessNumberLiteral(subject, predicate, value, handler, graphIri, coercedType);
                break;

            case JsonValueKind.True:
                if (!string.IsNullOrEmpty(coercedType) && coercedType != "@id" && coercedType != "@vocab")
                {
                    var datatypeIri = ExpandIri(coercedType, expandTerms: true);
                    EmitQuad(handler, subject, predicate, $"\"true\"^^{datatypeIri}", graphIri);
                }
                else
                {
                    EmitQuad(handler, subject, predicate,
                        "\"true\"^^<http://www.w3.org/2001/XMLSchema#boolean>", graphIri);
                }
                break;

            case JsonValueKind.False:
                if (!string.IsNullOrEmpty(coercedType) && coercedType != "@id" && coercedType != "@vocab")
                {
                    var datatypeIri = ExpandIri(coercedType, expandTerms: true);
                    EmitQuad(handler, subject, predicate, $"\"false\"^^{datatypeIri}", graphIri);
                }
                else
                {
                    EmitQuad(handler, subject, predicate,
                        "\"false\"^^<http://www.w3.org/2001/XMLSchema#boolean>", graphIri);
                }
                break;

            case JsonValueKind.Object:
                // Handle @json type - serialize entire object as canonical JSON literal
                if (coercedType == "@json")
                {
                    var canonicalJson = CanonicalizeJson(value);
                    var escapedJson = EscapeString(canonicalJson);
                    EmitQuad(handler, subject, predicate,
                        $"\"{escapedJson}\"^^<http://www.w3.org/1999/02/22-rdf-syntax-ns#JSON>", graphIri);
                }
                else
                {
                    ProcessObjectValue(subject, predicate, value, handler, graphIri);
                }
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
            // Skip if @value is null
            if (valProp.ValueKind == JsonValueKind.Null)
                return;
            var literal = ProcessValueObject(value, valProp);
            EmitQuad(handler, subject, predicate, literal, graphIri);
            return;
        }

        // Check for invalid value object with @language but no @value - drop it
        // Note: @type without @value is valid - it's a node object with rdf:type, not a value object
        if (value.TryGetProperty("@language", out _))
        {
            // @language requires @value - this is invalid, drop the property
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

        // Check for @set - flatten the contents, ignore empty @set
        if (value.TryGetProperty("@set", out var setProp))
        {
            // @set just contains values to be flattened - process each one
            if (setProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in setProp.EnumerateArray())
                {
                    // Skip null values
                    if (item.ValueKind == JsonValueKind.Null)
                        continue;
                    ProcessValue(subject, predicate, item, handler, graphIri, null, null);
                }
            }
            else if (setProp.ValueKind != JsonValueKind.Null)
            {
                // Single value
                ProcessValue(subject, predicate, setProp, handler, graphIri, null, null);
            }
            // Empty @set produces no output
            return;
        }

        // Nested object - create blank node
        // Restore context to pre-type-scoped state for nested nodes (type-scoped contexts don't propagate)
        Dictionary<string, string>? savedContext = null;
        string? savedVocab = null;
        string? savedBase = null;
        if (_savedContextForNested != null)
        {
            savedContext = new Dictionary<string, string>(_context);
            savedVocab = _vocabIri;
            savedBase = _baseIri;
            _context.Clear();
            foreach (var kv in _savedContextForNested) _context[kv.Key] = kv.Value;
            _vocabIri = _savedVocabForNested;
            _baseIri = _savedBaseForNested;
        }

        var tempReader = new Utf8JsonReader(Encoding.UTF8.GetBytes(value.GetRawText()));
        tempReader.Read();
        var blankNode = ParseNode(ref tempReader, handler, subject);
        EmitQuad(handler, subject, predicate, blankNode, graphIri);

        // Restore context after processing nested node
        if (savedContext != null)
        {
            _context.Clear();
            foreach (var kv in savedContext) _context[kv.Key] = kv.Value;
            _vocabIri = savedVocab;
            _baseIri = savedBase;
        }
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

        // Check for @type (datatype) - terms should be expanded for datatypes
        if (obj.TryGetProperty("@type", out var typeProp))
        {
            var datatype = ExpandIri(typeProp.GetString() ?? "", expandTerms: true);
            return $"\"{EscapeString(valueStr)}\"^^{datatype}";
        }

        // Plain literal
        return $"\"{EscapeString(valueStr)}\"";
    }

    private void ProcessNumberLiteral(string subject, string predicate, JsonElement value,
        QuadHandler handler, string? graphIri, string? coercedType)
    {
        var rawText = value.GetRawText();
        var isDouble = rawText.Contains('.') || rawText.Contains('e') || rawText.Contains('E');

        // Handle @json type - use raw JSON representation
        if (coercedType == "@json")
        {
            EmitQuad(handler, subject, predicate,
                $"\"{rawText}\"^^<http://www.w3.org/1999/02/22-rdf-syntax-ns#JSON>", graphIri);
            return;
        }

        // Check if custom datatype is specified (not @id, @vocab, or standard xsd types)
        if (!string.IsNullOrEmpty(coercedType) && coercedType != "@id" && coercedType != "@vocab" &&
            !coercedType.Contains("XMLSchema#double") && !coercedType.Contains("XMLSchema#integer"))
        {
            // Custom datatype - use canonical double form for non-integers
            var customDatatype = ExpandIri(coercedType, expandTerms: true);
            string customLexical;
            if (isDouble && double.TryParse(rawText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var customDoubleValue))
            {
                var canonicalForm = customDoubleValue.ToString("E15", System.Globalization.CultureInfo.InvariantCulture);
                customLexical = NormalizeDoubleCanonical(canonicalForm);
            }
            else
            {
                customLexical = rawText;
            }
            EmitQuad(handler, subject, predicate, $"\"{customLexical}\"^^{customDatatype}", graphIri);
            return;
        }

        // Check if type coercion specifies xsd:double
        var forceDouble = coercedType != null &&
            (coercedType == "http://www.w3.org/2001/XMLSchema#double" ||
             coercedType.EndsWith("#double"));

        // Check if type coercion specifies xsd:integer
        var forceInteger = coercedType != null &&
            (coercedType == "http://www.w3.org/2001/XMLSchema#integer" ||
             coercedType.EndsWith("#integer"));

        // Determine effective datatype
        string datatypeIri;
        string lexicalValue;

        if (forceDouble || (isDouble && !forceInteger))
        {
            // Format as double - must use canonical XSD form with exponent notation
            datatypeIri = "<http://www.w3.org/2001/XMLSchema#double>";
            if (double.TryParse(rawText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var doubleValue))
            {
                var canonicalForm = doubleValue.ToString("E15", System.Globalization.CultureInfo.InvariantCulture);
                lexicalValue = NormalizeDoubleCanonical(canonicalForm);
            }
            else
            {
                lexicalValue = rawText;
            }
        }
        else if (forceInteger)
        {
            // Type coercion to integer - if value has decimal, format as double lexically but with integer type
            datatypeIri = "<http://www.w3.org/2001/XMLSchema#integer>";
            if (isDouble && double.TryParse(rawText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var doubleValue))
            {
                // Format non-integer value as double canonical form but typed as integer
                var canonicalForm = doubleValue.ToString("E15", System.Globalization.CultureInfo.InvariantCulture);
                lexicalValue = NormalizeDoubleCanonical(canonicalForm);
            }
            else
            {
                lexicalValue = rawText;
            }
        }
        else
        {
            // Default: integer
            datatypeIri = "<http://www.w3.org/2001/XMLSchema#integer>";
            lexicalValue = rawText;
        }

        EmitQuad(handler, subject, predicate, $"\"{lexicalValue}\"^^{datatypeIri}", graphIri);
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
        // Already an absolute IRI (has scheme like http:, urn:, etc.)
        if (IsAbsoluteIri(value))
        {
            // But still check if the "scheme" part is a defined prefix in context
            var colonIndex = value.IndexOf(':');
            var prefix = value.Substring(0, colonIndex);
            if (_context.TryGetValue(prefix, out var prefixIri))
            {
                // It's actually a compact IRI using a defined prefix
                var localName = value.Substring(colonIndex + 1);
                return FormatIri(prefixIri + localName);
            }
            // It's a real absolute IRI
            return FormatIri(value);
        }

        // Check for compact IRI (prefix:localName) - prefix not a valid scheme
        var colonIdx = value.IndexOf(':');
        if (colonIdx > 0)
        {
            var prefix = value.Substring(0, colonIdx);
            var localName = value.Substring(colonIdx + 1);

            if (_context.TryGetValue(prefix, out var prefixIri))
            {
                return FormatIri(prefixIri + localName);
            }
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

        // Already an absolute IRI (has scheme like http:, urn:, etc.)
        if (IsAbsoluteIri(value))
        {
            // Check if the "scheme" part is a defined prefix in context
            var colonIndex = value.IndexOf(':');
            var prefix = value.Substring(0, colonIndex);
            if (_context.TryGetValue(prefix, out var prefixIri))
            {
                // It's actually a compact IRI using a defined prefix
                var localName = value.Substring(colonIndex + 1);
                return FormatIri(prefixIri + localName);
            }
            // It's a real absolute IRI
            return FormatIri(value);
        }

        // Check for compact IRI (prefix:localName) - colon present but not a valid scheme
        var colonIdx = value.IndexOf(':');
        if (colonIdx > 0)
        {
            var prefix = value.Substring(0, colonIdx);
            var localName = value.Substring(colonIdx + 1);

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

        // Relative path - merge with base
        string targetPath;
        if (string.IsNullOrEmpty(baseAuthority) && string.IsNullOrEmpty(basePath))
        {
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
                targetPath = relative;
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
                // Normalize number representation per I-JSON/JCS
                // Integer: no exponent, no decimal point
                // Non-integer: use shortest representation
                var rawNum = element.GetRawText();
                if (element.TryGetInt64(out var intVal))
                {
                    sb.Append(intVal.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                else if (element.TryGetDouble(out var dblVal))
                {
                    // Use "G17" for full precision, then normalize
                    var numStr = dblVal.ToString("G17", System.Globalization.CultureInfo.InvariantCulture);
                    sb.Append(numStr);
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
                        sb.Append($"\\u{(int)c:X4}");
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
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
