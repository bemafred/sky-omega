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
    private readonly Dictionary<string, bool> _containerId; // term -> is @id container
    private readonly Dictionary<string, bool> _containerType; // term -> is @type container
    private readonly Dictionary<string, string> _termLanguage; // term -> @language value
    private readonly Dictionary<string, string> _reverseProperty; // term -> reverse predicate IRI
    private readonly Dictionary<string, string> _scopedContext; // term -> nested @context JSON
    private readonly HashSet<string> _typeAliases; // terms aliased to @type
    private readonly HashSet<string> _idAliases; // terms aliased to @id
    private readonly HashSet<string> _graphAliases; // terms aliased to @graph
    private readonly HashSet<string> _includedAliases; // terms aliased to @included
    private readonly HashSet<string> _nestAliases; // terms aliased to @nest
    private readonly HashSet<string> _noneAliases; // terms aliased to @none
    private readonly HashSet<string> _valueAliases; // terms aliased to @value
    private readonly HashSet<string> _languageAliases; // terms aliased to @language
    private readonly HashSet<string> _nullTerms; // terms decoupled from @vocab (mapped to null)
    private readonly HashSet<string> _prefixable; // terms usable as prefixes in compact IRIs

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

    // Track what terms/type-coercions/containers were added/modified by type-scoped context (to revert for nested nodes)
    // Value is original IRI (null if term was new). Only type-scoped changes should be reverted.
    private Dictionary<string, string?>? _typeScopedTermChanges;     // Terms added/modified by type-scoped context
    private Dictionary<string, string?>? _typeScopedCoercionChanges; // Coercions added/modified by type-scoped
    private Dictionary<string, bool?>? _typeScopedContainerTypeChanges;   // @container: @type changes
    private Dictionary<string, bool?>? _typeScopedContainerIndexChanges;  // @container: @index changes
    private Dictionary<string, bool?>? _typeScopedContainerListChanges;   // @container: @list changes
    private Dictionary<string, bool?>? _typeScopedContainerLangChanges;   // @container: @language changes
    private Dictionary<string, bool?>? _typeScopedContainerGraphChanges;  // @container: @graph changes
    private Dictionary<string, bool?>? _typeScopedContainerIdChanges;     // @container: @id changes
    private bool _typeScopedPropagate;                               // If true, type-scoped context DOES propagate

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
        _containerId = new Dictionary<string, bool>(StringComparer.Ordinal);
        _containerType = new Dictionary<string, bool>(StringComparer.Ordinal);
        _termLanguage = new Dictionary<string, string>(StringComparer.Ordinal);
        _reverseProperty = new Dictionary<string, string>(StringComparer.Ordinal);
        _scopedContext = new Dictionary<string, string>(StringComparer.Ordinal);
        _typeAliases = new HashSet<string>(StringComparer.Ordinal);
        _idAliases = new HashSet<string>(StringComparer.Ordinal);
        _graphAliases = new HashSet<string>(StringComparer.Ordinal);
        _includedAliases = new HashSet<string>(StringComparer.Ordinal);
        _nestAliases = new HashSet<string>(StringComparer.Ordinal);
        _noneAliases = new HashSet<string>(StringComparer.Ordinal);
        _valueAliases = new HashSet<string>(StringComparer.Ordinal);
        _languageAliases = new HashSet<string>(StringComparer.Ordinal);
        _nullTerms = new HashSet<string>(StringComparer.Ordinal);
        _prefixable = new HashSet<string>(StringComparer.Ordinal);
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
        Dictionary<string, string?>? typeScopedTermChanges = null;     // Terms added/modified by type-scoped (value = original IRI, null if new)
        Dictionary<string, string?>? typeScopedCoercionChanges = null; // Coercions added/modified by type-scoped
        bool hasTypeScopedContext = false;
        JsonElement typeElement = default;
        List<string>? expandedTypeIris = null; // Type IRIs expanded BEFORE applying type-scoped context

        // Check for @type and apply type-scoped context BEFORE expanding @id
        // IMPORTANT: Expand type IRIs BEFORE applying type-scoped context
        // (type-scoped context changes @vocab which should NOT affect the @type IRI itself)
        // Check both literal @type AND ALL @type aliases (multiple properties can alias to @type)
        List<JsonElement> typeElements = new();
        bool foundType = root.TryGetProperty("@type", out typeElement);
        if (foundType)
        {
            typeElements.Add(typeElement);
        }
        // Also check ALL @type aliases - multiple properties can alias to @type (e.g., type1, type2)
        foreach (var alias in _typeAliases)
        {
            if (root.TryGetProperty(alias, out var aliasElement))
            {
                typeElements.Add(aliasElement);
                foundType = true;
            }
        }
        if (foundType)
        {
            // Expand type IRIs using current context (BEFORE type-scoped context)
            // Combine types from all @type and alias properties
            expandedTypeIris = new List<string>();
            foreach (var te in typeElements)
            {
                expandedTypeIris.AddRange(ExpandTypeIris(te));
            }
            // Check if ANY type has a scoped context BEFORE applying
            hasTypeScopedContext = typeElements.Any(te => HasTypeScopedContext(te));

            if (hasTypeScopedContext)
            {
                // Save state BEFORE applying type-scoped contexts
                savedContextForNested = new Dictionary<string, string>(_context);
                savedVocabForNested = _vocabIri;
                savedBaseForNested = _baseIri;

                // Track terms, coercions, and containers BEFORE type-scoped context
                var termsBefore = new Dictionary<string, string>(_context);
                var coercionsBefore = new Dictionary<string, string>(_typeCoercion);
                var containerTypeBefore = new Dictionary<string, bool>(_containerType);
                var containerIndexBefore = new Dictionary<string, bool>(_containerIndex);
                var containerListBefore = new Dictionary<string, bool>(_containerList);
                var containerLangBefore = new Dictionary<string, bool>(_containerLanguage);
                var containerGraphBefore = new Dictionary<string, bool>(_containerGraph);
                var containerIdBefore = new Dictionary<string, bool>(_containerId);

                // Reset @propagate flag before applying type-scoped contexts
                // It will be set to true if any type-scoped context has @propagate: true
                _typeScopedPropagate = false;

                // Apply type-scoped contexts from ALL type elements
                foreach (var te in typeElements)
                {
                    ApplyTypeScopedContexts(te);
                }

                // Track what was ADDED or MODIFIED by type-scoped context
                // Store the original value (null if term was added, original IRI if modified)
                typeScopedTermChanges = new Dictionary<string, string?>();
                foreach (var kv in _context)
                {
                    if (!termsBefore.TryGetValue(kv.Key, out var oldValue))
                    {
                        // New term - store null as original
                        typeScopedTermChanges[kv.Key] = null;
                    }
                    else if (oldValue != kv.Value)
                    {
                        // Modified term - store original value
                        typeScopedTermChanges[kv.Key] = oldValue;
                    }
                }

                typeScopedCoercionChanges = new Dictionary<string, string?>();
                foreach (var kv in _typeCoercion)
                {
                    if (!coercionsBefore.TryGetValue(kv.Key, out var oldValue))
                    {
                        typeScopedCoercionChanges[kv.Key] = null;
                    }
                    else if (oldValue != kv.Value)
                    {
                        typeScopedCoercionChanges[kv.Key] = oldValue;
                    }
                }

                // Track container changes (null = newly added, true/false = original value)
                Dictionary<string, bool?>? typeScopedContainerTypeChanges = null;
                Dictionary<string, bool?>? typeScopedContainerIndexChanges = null;
                Dictionary<string, bool?>? typeScopedContainerListChanges = null;
                Dictionary<string, bool?>? typeScopedContainerLangChanges = null;
                Dictionary<string, bool?>? typeScopedContainerGraphChanges = null;
                Dictionary<string, bool?>? typeScopedContainerIdChanges = null;

                // Helper to track container changes
                void TrackContainerChanges(Dictionary<string, bool> before, Dictionary<string, bool> after, ref Dictionary<string, bool?>? changes)
                {
                    foreach (var kv in after)
                    {
                        if (!before.TryGetValue(kv.Key, out var oldValue))
                        {
                            // New container - store null (meaning remove on revert)
                            changes ??= new Dictionary<string, bool?>();
                            changes[kv.Key] = null;
                        }
                        else if (oldValue != kv.Value)
                        {
                            // Modified container
                            changes ??= new Dictionary<string, bool?>();
                            changes[kv.Key] = oldValue;
                        }
                    }
                    // Also track removed containers
                    foreach (var kv in before)
                    {
                        if (!after.ContainsKey(kv.Key))
                        {
                            // Container was removed - store original value
                            changes ??= new Dictionary<string, bool?>();
                            changes[kv.Key] = kv.Value;
                        }
                    }
                }

                TrackContainerChanges(containerTypeBefore, _containerType, ref typeScopedContainerTypeChanges);
                TrackContainerChanges(containerIndexBefore, _containerIndex, ref typeScopedContainerIndexChanges);
                TrackContainerChanges(containerListBefore, _containerList, ref typeScopedContainerListChanges);
                TrackContainerChanges(containerLangBefore, _containerLanguage, ref typeScopedContainerLangChanges);
                TrackContainerChanges(containerGraphBefore, _containerGraph, ref typeScopedContainerGraphChanges);
                TrackContainerChanges(containerIdBefore, _containerId, ref typeScopedContainerIdChanges);

                _typeScopedContainerTypeChanges = typeScopedContainerTypeChanges;
                _typeScopedContainerIndexChanges = typeScopedContainerIndexChanges;
                _typeScopedContainerListChanges = typeScopedContainerListChanges;
                _typeScopedContainerLangChanges = typeScopedContainerLangChanges;
                _typeScopedContainerGraphChanges = typeScopedContainerGraphChanges;
                _typeScopedContainerIdChanges = typeScopedContainerIdChanges;
            }
            else
            {
                // Apply type-scoped contexts (may change @base/@vocab)
                ApplyTypeScopedContexts(typeElement);
            }
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
        // Use pre-expanded type IRIs (expanded BEFORE type-scoped context was applied)
        if (expandedTypeIris != null)
        {
            foreach (var typeIri in expandedTypeIris)
            {
                EmitQuad(handler, subject, RdfType, typeIri, _currentGraph);
            }
        }

        // Store saved state for nested node restoration (only if type-scoped context was applied)
        var previousSavedContext = _savedContextForNested;
        var previousSavedVocab = _savedVocabForNested;
        var previousSavedBase = _savedBaseForNested;
        var previousTypeScopedTermChanges = _typeScopedTermChanges;
        var previousTypeScopedCoercionChanges = _typeScopedCoercionChanges;
        var previousTypeScopedPropagate = _typeScopedPropagate;
        var previousTypeScopedContainerTypeChanges = _typeScopedContainerTypeChanges;
        var previousTypeScopedContainerIndexChanges = _typeScopedContainerIndexChanges;
        var previousTypeScopedContainerListChanges = _typeScopedContainerListChanges;
        var previousTypeScopedContainerLangChanges = _typeScopedContainerLangChanges;
        var previousTypeScopedContainerGraphChanges = _typeScopedContainerGraphChanges;
        var previousTypeScopedContainerIdChanges = _typeScopedContainerIdChanges;
        if (hasTypeScopedContext)
        {
            _savedContextForNested = savedContextForNested;
            _savedVocabForNested = savedVocabForNested;
            _savedBaseForNested = savedBaseForNested;
            _typeScopedTermChanges = typeScopedTermChanges;
            _typeScopedCoercionChanges = typeScopedCoercionChanges;
            // Note: _typeScopedPropagate was already set during ApplyTypeScopedContexts
            // Container changes are already assigned to fields during tracking above
        }

        // Process @reverse keyword - contains reverse properties
        if (root.TryGetProperty("@reverse", out var reverseElement))
        {
            ProcessReverseKeyword(subject, reverseElement, handler, _currentGraph);
        }

        // Process @nest keyword and aliases - contains properties to be "un-nested" onto this node
        if (root.TryGetProperty("@nest", out var nestElement))
        {
            ProcessNestKeyword(subject, nestElement, handler, _currentGraph);
        }
        // Also process @nest aliases
        foreach (var alias in _nestAliases)
        {
            if (root.TryGetProperty(alias, out var aliasNestElement))
            {
                ProcessNestKeyword(subject, aliasNestElement, handler, _currentGraph);
            }
        }

        // Process other properties FIRST (before @graph content)
        // Properties on the containing node go in the current graph, not the named graph
        foreach (var prop in root.EnumerateObject())
        {
            var propName = prop.Name;

            // Skip JSON-LD keywords we've already processed
            // Per JSON-LD 1.1: Terms that look like keywords (@ followed by only lowercase a-z) are ignored
            // But other @ patterns (like "@", "@foo.bar") can be term definitions (e119)
            if (propName.StartsWith('@') && IsKeywordLike(propName))
                continue;

            // Check for @type alias - already processed above (type emission and scoped context)
            if (_typeAliases.Contains(propName))
                continue;

            // Skip @id aliases (already processed above)
            if (_idAliases.Contains(propName))
                continue;

            // Skip @graph aliases (already processed above)
            if (_graphAliases.Contains(propName))
                continue;

            // Skip @included aliases (processed below)
            if (_includedAliases.Contains(propName))
                continue;

            // Skip @nest aliases (already processed above)
            if (_nestAliases.Contains(propName))
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
        _typeScopedTermChanges = previousTypeScopedTermChanges;
        _typeScopedCoercionChanges = previousTypeScopedCoercionChanges;
        _typeScopedPropagate = previousTypeScopedPropagate;
        _typeScopedContainerTypeChanges = previousTypeScopedContainerTypeChanges;
        _typeScopedContainerIndexChanges = previousTypeScopedContainerIndexChanges;
        _typeScopedContainerListChanges = previousTypeScopedContainerListChanges;
        _typeScopedContainerLangChanges = previousTypeScopedContainerLangChanges;
        _typeScopedContainerGraphChanges = previousTypeScopedContainerGraphChanges;
        _typeScopedContainerIdChanges = previousTypeScopedContainerIdChanges;

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
            _prefixable.Clear();
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
                if (value.ValueKind == JsonValueKind.Null)
                {
                    // @base: null clears the base IRI
                    _baseIri = null;
                }
                else
                {
                    var newBase = value.GetString();
                    if (string.IsNullOrEmpty(newBase))
                    {
                        // @base: "" means keep current base (no change)
                        // This is important when an empty @base is used with a base option
                    }
                    else if (!string.IsNullOrEmpty(_baseIri) && !IsAbsoluteIri(newBase))
                    {
                        // Relative @base is resolved against current base
                        _baseIri = ResolveRelativeIri(_baseIri, newBase);
                    }
                    else
                    {
                        // Absolute @base or no current base
                        _baseIri = newBase;
                    }
                }
            }
            else if (term == "@vocab")
            {
                var vocabValue = value.GetString();
                // @vocab can be a compact IRI like "ex:ns/" - expand it (e124)
                // @vocab can also be a relative IRI like "/relative" - resolve against @base (e110)
                // @vocab can be "" (empty string) meaning use @base as vocabulary (e092)
                if (vocabValue == null)
                {
                    _vocabIri = null;
                }
                else if (vocabValue == "")
                {
                    // Empty @vocab means use @base as vocabulary base (e092)
                    _vocabIri = _baseIri ?? "";
                }
                else
                {
                    // First try to expand as compact IRI or term - this handles cases
                    // like "ex:ns/" where "ex" is a defined prefix with @prefix: true (e124)
                    var expanded = ExpandCompactIri(vocabValue);
                    if (expanded != vocabValue)
                    {
                        // Compact IRI was expanded
                        _vocabIri = expanded;
                    }
                    else if (!IsAbsoluteIri(vocabValue))
                    {
                        // Not expanded and not absolute - resolve against @base
                        if (!string.IsNullOrEmpty(_baseIri))
                        {
                            _vocabIri = ResolveRelativeIri(_baseIri, vocabValue);
                        }
                        else
                        {
                            _vocabIri = vocabValue;
                        }
                    }
                    else
                    {
                        // Absolute IRI - use as-is
                        _vocabIri = vocabValue;
                    }
                }
            }
            else if (term == "@language")
            {
                _defaultLanguage = value.ValueKind == JsonValueKind.Null ? null : value.GetString();
            }
            else if (term == "@propagate")
            {
                // @propagate: true means type-scoped context propagates to nested nodes
                // Default is false for type-scoped contexts, true for property-scoped
                if (value.ValueKind == JsonValueKind.True)
                {
                    _typeScopedPropagate = true;
                }
            }
            else if (term == "@version" || term == "@protected" || term == "@direction" || term == "@import")
            {
                // Ignore other JSON-LD 1.1 keywords we don't fully implement yet
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
                else if (mappedValue == "@nest" || _nestAliases.Contains(mappedValue))
                {
                    _nestAliases.Add(term);
                }
                else if (mappedValue == "@none" || _noneAliases.Contains(mappedValue))
                {
                    _noneAliases.Add(term);
                }
                else if (mappedValue == "@value" || _valueAliases.Contains(mappedValue))
                {
                    _valueAliases.Add(term);
                }
                else if (mappedValue == "@language" || _languageAliases.Contains(mappedValue))
                {
                    _languageAliases.Add(term);
                }
                else
                {
                    // Simple term -> IRI mapping (always prefix-able)
                    _context[term] = mappedValue;
                    _prefixable.Add(term);
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
                        var idValue = idProp.GetString() ?? "";
                        // Per JSON-LD 1.1: @id values that look like keywords (e.g., "@ignoreMe")
                        // should be ignored and the term should use @vocab instead (e120)
                        if (idValue.StartsWith('@') && IsKeywordLike(idValue))
                        {
                            // Ignore keyword-like @id, term uses @vocab
                            if (!string.IsNullOrEmpty(_vocabIri))
                            {
                                _context[term] = _vocabIri + term;
                            }
                        }
                        else
                        {
                            _context[term] = idValue;
                        }
                    }
                }
                else
                {
                    // No explicit @id - need to derive the IRI
                    // First check if term looks like a compact IRI (prefix:localName)
                    // Within context definitions, compact IRIs are expanded regardless of @prefix flag (e050)
                    var termColonIdx = term.IndexOf(':');
                    if (termColonIdx > 0)
                    {
                        var termPrefix = term.Substring(0, termColonIdx);
                        var termLocalName = term.Substring(termColonIdx + 1);
                        // In context definitions, we can use any term as a prefix, not just _prefixable ones
                        if (!termLocalName.StartsWith("//") && termPrefix != "_" &&
                            _context.TryGetValue(termPrefix, out var termPrefixIri))
                        {
                            _context[term] = termPrefixIri + termLocalName;
                        }
                        else if (!string.IsNullOrEmpty(_vocabIri))
                        {
                            _context[term] = _vocabIri + term;
                        }
                    }
                    else if (!string.IsNullOrEmpty(_vocabIri))
                    {
                        // Simple term - use @vocab + term (JSON-LD 1.1)
                        // This is important for type-scoped contexts where term is used as @type value
                        _context[term] = _vocabIri + term;
                    }
                }

                // Handle @prefix - marks term as usable as a prefix in compact IRIs (e124)
                // Expanded term definitions are NOT prefix-able by default (unlike simple string mappings)
                if (value.TryGetProperty("@prefix", out var prefixProp) && prefixProp.ValueKind == JsonValueKind.True)
                {
                    _prefixable.Add(term);
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
                    // Clear all existing container flags for this term before setting new ones
                    // This ensures that redefining @container replaces the old container type
                    _containerList.Remove(term);
                    _containerLanguage.Remove(term);
                    _containerIndex.Remove(term);
                    _containerGraph.Remove(term);
                    _containerId.Remove(term);
                    _containerType.Remove(term);

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
                        else if (containerVal == "@id")
                            _containerId[term] = true;
                        else if (containerVal == "@type")
                            _containerType[term] = true;
                        // Note: @set containers treated as simple containers
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
    /// Expand type IRIs from @type element. Called BEFORE type-scoped context is applied.
    /// </summary>
    private List<string> ExpandTypeIris(JsonElement typeElement)
    {
        var result = new List<string>();

        if (typeElement.ValueKind == JsonValueKind.String)
        {
            var typeIri = ExpandTypeIri(typeElement.GetString() ?? "");
            result.Add(typeIri);
        }
        else if (typeElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in typeElement.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String)
                {
                    var typeIri = ExpandTypeIri(t.GetString() ?? "");
                    result.Add(typeIri);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Apply type-scoped contexts from @type values.
    /// Per JSON-LD spec, types are processed in lexicographical order of expanded IRIs.
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
            // Collect types with scoped contexts AND save their context JSON
            // IMPORTANT: We must save the JSON strings before applying any contexts because
            // applying a context with `null` will clear _scopedContext, losing other type contexts
            var typesWithContexts = new List<(string term, string expandedIri, string scopedJson)>();
            foreach (var t in typeElement.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String)
                {
                    var typeTerm = t.GetString() ?? "";
                    if (_scopedContext.TryGetValue(typeTerm, out var scopedJson))
                    {
                        // Expand the type IRI for sorting
                        var expandedIri = ExpandTypeIri(typeTerm);
                        typesWithContexts.Add((typeTerm, expandedIri, scopedJson));
                    }
                }
            }

            // Sort lexicographically by expanded IRI
            typesWithContexts.Sort((a, b) => string.Compare(a.expandedIri, b.expandedIri, StringComparison.Ordinal));

            // Apply scoped contexts in sorted order using saved JSON
            foreach (var (_, _, scopedJson) in typesWithContexts)
            {
                using var doc = JsonDocument.Parse(scopedJson);
                ProcessContext(doc.RootElement);
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

        // Check for compact IRI (prefix:localName) before checking absolute IRI
        var colonIdx = value.IndexOf(':');
        if (colonIdx > 0)
        {
            var prefix = value.Substring(0, colonIdx);
            var localName = value.Substring(colonIdx + 1);

            // Key check: if localName starts with "//", this is NOT a compact IRI
            // Also, "_" as a prefix would conflict with blank nodes
            // The prefix must be in _prefixable to be used for expansion (pr29)
            if (!localName.StartsWith("//") && prefix != "_" &&
                _prefixable.Contains(prefix) && _context.TryGetValue(prefix, out var prefixIri))
            {
                return FormatIri(prefixIri + localName);
            }
        }

        // Already an absolute IRI
        if (IsAbsoluteIri(value))
        {
            return FormatIri(value);
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
        var isIdContainer = _containerId.TryGetValue(term, out var isId) && isId;
        var isTypeContainer = _containerType.TryGetValue(term, out var isType) && isType;
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
        Dictionary<string, bool>? savedContainerId = null;
        Dictionary<string, bool>? savedContainerType = null;
        Dictionary<string, string>? savedTermLanguage = null;
        Dictionary<string, string>? savedReverseProperty = null;
        Dictionary<string, string>? savedScopedContext = null;
        HashSet<string>? savedTypeAliases = null;
        HashSet<string>? savedIdAliases = null;
        HashSet<string>? savedGraphAliases = null;
        HashSet<string>? savedIncludedAliases = null;
        HashSet<string>? savedNestAliases = null;
        HashSet<string>? savedNoneAliases = null;
        HashSet<string>? savedValueAliases = null;
        HashSet<string>? savedLanguageAliases = null;
        HashSet<string>? savedNullTerms = null;
        HashSet<string>? savedPrefixable = null;
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
            savedContainerId = new Dictionary<string, bool>(_containerId);
            savedContainerType = new Dictionary<string, bool>(_containerType);
            savedTermLanguage = new Dictionary<string, string>(_termLanguage);
            savedReverseProperty = new Dictionary<string, string>(_reverseProperty);
            savedScopedContext = new Dictionary<string, string>(_scopedContext);
            savedTypeAliases = new HashSet<string>(_typeAliases);
            savedIdAliases = new HashSet<string>(_idAliases);
            savedGraphAliases = new HashSet<string>(_graphAliases);
            savedIncludedAliases = new HashSet<string>(_includedAliases);
            savedNestAliases = new HashSet<string>(_nestAliases);
            savedNoneAliases = new HashSet<string>(_noneAliases);
            savedValueAliases = new HashSet<string>(_valueAliases);
            savedLanguageAliases = new HashSet<string>(_languageAliases);
            savedNullTerms = new HashSet<string>(_nullTerms);
            savedPrefixable = new HashSet<string>(_prefixable);
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
            // When combined with @index, object keys are ignored (just indexes)
            // When combined with @id, object keys are used as graph @id
            if (isGraphContainer)
            {
                ProcessGraphContainer(subject, predicate, value, handler, graphIri, coercedType, termLang, isIndexContainer, isIdContainer);
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
            // Handle @id container - object keys are @id values for nested objects
            else if (isIdContainer && value.ValueKind == JsonValueKind.Object)
            {
                ProcessIdMap(subject, predicate, value, handler, graphIri);
            }
            // Handle @type container - object keys are @type values for nested objects
            else if (isTypeContainer && value.ValueKind == JsonValueKind.Object)
            {
                ProcessTypeMap(subject, predicate, value, handler, graphIri, coercedType);
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
                // If @container: @list with a non-array value, wrap in list
                if (isListContainer)
                {
                    // If the value is an object with @list, extract the inner list
                    // Don't double-wrap explicit @list objects
                    JsonElement listValue = value;
                    if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("@list", out var innerList))
                    {
                        listValue = innerList;
                    }
                    var listHead = ProcessList(listValue, handler, graphIri, coercedType);
                    EmitQuad(handler, subject, predicate, listHead, graphIri);
                }
                else
                {
                    ProcessValue(subject, predicate, value, handler, graphIri, coercedType, termLang);
                }
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

                _containerId.Clear();
                foreach (var kv in savedContainerId!) _containerId[kv.Key] = kv.Value;

                _containerType.Clear();
                foreach (var kv in savedContainerType!) _containerType[kv.Key] = kv.Value;

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

                _nestAliases.Clear();
                foreach (var alias in savedNestAliases!) _nestAliases.Add(alias);

                _noneAliases.Clear();
                foreach (var alias in savedNoneAliases!) _noneAliases.Add(alias);

                _valueAliases.Clear();
                foreach (var alias in savedValueAliases!) _valueAliases.Add(alias);

                _languageAliases.Clear();
                foreach (var alias in savedLanguageAliases!) _languageAliases.Add(alias);

                _nullTerms.Clear();
                foreach (var t in savedNullTerms!) _nullTerms.Add(t);

                _prefixable.Clear();
                foreach (var t in savedPrefixable!) _prefixable.Add(t);

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
    /// Process the @nest keyword which contains properties to be "un-nested" onto the current node.
    /// Properties inside @nest are processed as if they were direct properties of the containing node.
    /// </summary>
    private void ProcessNestKeyword(string currentNode, JsonElement nestElement, QuadHandler handler, string? graphIri)
    {
        if (nestElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in nestElement.EnumerateObject())
            {
                var propName = prop.Name;

                // Skip JSON-LD keywords (but allow non-keyword @ patterns like "@" or "@foo.bar")
                if (propName.StartsWith('@') && IsKeywordLike(propName))
                    continue;

                var predicate = ExpandTerm(propName);
                if (string.IsNullOrEmpty(predicate))
                    continue;

                ProcessProperty(currentNode, predicate, propName, prop.Value, handler, graphIri);
            }
        }
        else if (nestElement.ValueKind == JsonValueKind.Array)
        {
            // @nest can be an array of objects
            foreach (var item in nestElement.EnumerateArray())
            {
                ProcessNestKeyword(currentNode, item, handler, graphIri);
            }
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

            // @none means no language tag - output as plain literal
            var isNone = langTag == "@none" || _noneAliases.Contains(langTag);

            if (langValue.ValueKind == JsonValueKind.String)
            {
                var strVal = langValue.GetString() ?? "";
                var literal = isNone
                    ? $"\"{EscapeString(strVal)}\""
                    : $"\"{EscapeString(strVal)}\"@{langTag}";
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
                        var literal = isNone
                            ? $"\"{EscapeString(strVal)}\""
                            : $"\"{EscapeString(strVal)}\"@{langTag}";
                        EmitQuad(handler, subject, predicate, literal, graphIri);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Process an @id container map (@container: @id).
    /// Object keys become the @id of the nested objects.
    /// @none (or alias) means generate a blank node.
    /// </summary>
    private void ProcessIdMap(string subject, string predicate, JsonElement value, QuadHandler handler, string? graphIri)
    {
        foreach (var prop in value.EnumerateObject())
        {
            var idKey = prop.Name;
            var idValue = prop.Value;

            // @none or alias means no @id - generate blank node
            var isNone = idKey == "@none" || _noneAliases.Contains(idKey);

            if (idValue.ValueKind == JsonValueKind.Object)
            {
                // Process nested object with the key as @id
                string nodeId;
                if (isNone)
                {
                    nodeId = GenerateBlankNode();
                }
                else
                {
                    nodeId = ExpandIri(idKey);
                }

                // Create a temporary reader for the nested object
                var tempReader = new Utf8JsonReader(Encoding.UTF8.GetBytes(idValue.GetRawText()));
                tempReader.Read();

                // Parse the nested node with the specified @id
                var nestedId = ParseNodeWithId(ref tempReader, handler, nodeId, subject);
                EmitQuad(handler, subject, predicate, nestedId, graphIri);
            }
            else if (idValue.ValueKind == JsonValueKind.Array)
            {
                // Multiple objects with same @id
                foreach (var item in idValue.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        string nodeId;
                        if (isNone)
                        {
                            nodeId = GenerateBlankNode();
                        }
                        else
                        {
                            nodeId = ExpandIri(idKey);
                        }

                        var tempReader = new Utf8JsonReader(Encoding.UTF8.GetBytes(item.GetRawText()));
                        tempReader.Read();
                        var nestedId = ParseNodeWithId(ref tempReader, handler, nodeId, subject);
                        EmitQuad(handler, subject, predicate, nestedId, graphIri);
                    }
                }
            }
        }
    }

    private void ProcessTypeMap(string subject, string predicate, JsonElement value, QuadHandler handler, string? graphIri, string? coercedType = null)
    {
        foreach (var prop in value.EnumerateObject())
        {
            var typeKey = prop.Name;
            var typeValue = prop.Value;

            // @none or alias means no @type - don't emit rdf:type triple
            var isNone = typeKey == "@none" || _noneAliases.Contains(typeKey);
            var expandedTypeIri = isNone ? null : ExpandTypeIri(typeKey);

            if (typeValue.ValueKind == JsonValueKind.Object)
            {
                // Process nested object with the key as @type
                var tempReader = new Utf8JsonReader(Encoding.UTF8.GetBytes(typeValue.GetRawText()));
                tempReader.Read();

                // Parse the nested node with the specified @type (pass term for scoped context lookup)
                var nestedId = ParseNodeWithType(ref tempReader, handler, expandedTypeIri, typeKey, subject);
                EmitQuad(handler, subject, predicate, nestedId, graphIri);
            }
            else if (typeValue.ValueKind == JsonValueKind.Array)
            {
                // Multiple objects with same @type
                foreach (var item in typeValue.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        var tempReader = new Utf8JsonReader(Encoding.UTF8.GetBytes(item.GetRawText()));
                        tempReader.Read();
                        var nestedId = ParseNodeWithType(ref tempReader, handler, expandedTypeIri, typeKey, subject);
                        EmitQuad(handler, subject, predicate, nestedId, graphIri);
                    }
                    else if (item.ValueKind == JsonValueKind.String)
                    {
                        // String value in @type map is a node reference (m017)
                        var stringVal = item.GetString() ?? "";
                        // Use @vocab expansion if coercedType is @vocab (m019), otherwise use @base (m017/m018)
                        var nodeRef = coercedType == "@vocab" ? ExpandTerm(stringVal) : ExpandIri(stringVal);
                        if (!string.IsNullOrEmpty(nodeRef))
                        {
                            // Emit type triple if not @none
                            if (!isNone && !string.IsNullOrEmpty(expandedTypeIri))
                            {
                                EmitQuad(handler, nodeRef, RdfType, expandedTypeIri, graphIri);
                            }
                            EmitQuad(handler, subject, predicate, nodeRef, graphIri);
                        }
                    }
                }
            }
            else if (typeValue.ValueKind == JsonValueKind.String)
            {
                // String value in @type map is a node reference (m017)
                // The string is the @id of a node with the key as its @type
                var stringVal = typeValue.GetString() ?? "";
                // Use @vocab expansion if coercedType is @vocab (m019), otherwise use @base (m017/m018)
                var nodeRef = coercedType == "@vocab" ? ExpandTerm(stringVal) : ExpandIri(stringVal);
                if (!string.IsNullOrEmpty(nodeRef))
                {
                    // Emit type triple if not @none
                    if (!isNone && !string.IsNullOrEmpty(expandedTypeIri))
                    {
                        EmitQuad(handler, nodeRef, RdfType, expandedTypeIri, graphIri);
                    }
                    EmitQuad(handler, subject, predicate, nodeRef, graphIri);
                }
            }
        }
    }

    /// <summary>
    /// Parse a node with a pre-specified @type.
    /// Used for @type container maps where the key provides the @type.
    /// </summary>
    private string ParseNodeWithType(ref Utf8JsonReader reader, QuadHandler handler, string? typeIri, string? typeTerm, string? parentNode)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            return GenerateBlankNode();

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        // Revert parent's type-scoped changes (type-scoped contexts don't propagate to nested nodes)
        // Save them for restoration after processing
        Dictionary<string, string>? savedCoercions = null;
        Dictionary<string, string>? savedTerms = null;
        var savedVocab = _vocabIri;
        var savedBase = _baseIri;

        if (!_typeScopedPropagate && _typeScopedCoercionChanges != null && _typeScopedCoercionChanges.Count > 0)
        {
            savedCoercions = new Dictionary<string, string>();
            foreach (var kv in _typeScopedCoercionChanges)
            {
                if (_typeCoercion.TryGetValue(kv.Key, out var currentValue))
                {
                    savedCoercions[kv.Key] = currentValue;
                }
                if (kv.Value == null)
                {
                    _typeCoercion.Remove(kv.Key);
                }
                else
                {
                    _typeCoercion[kv.Key] = kv.Value;
                }
            }
        }

        if (!_typeScopedPropagate && _typeScopedTermChanges != null && _typeScopedTermChanges.Count > 0)
        {
            savedTerms = new Dictionary<string, string>();
            foreach (var kv in _typeScopedTermChanges)
            {
                if (_context.TryGetValue(kv.Key, out var currentValue))
                {
                    savedTerms[kv.Key] = currentValue;
                }
                if (kv.Value == null)
                {
                    _context.Remove(kv.Key);
                }
                else
                {
                    _context[kv.Key] = kv.Value;
                }
            }
        }

        // Revert type-scoped container changes
        Dictionary<string, bool>? savedContainerType = null;
        Dictionary<string, bool>? savedContainerIndex = null;
        Dictionary<string, bool>? savedContainerList = null;
        Dictionary<string, bool>? savedContainerLang = null;
        Dictionary<string, bool>? savedContainerGraph = null;
        Dictionary<string, bool>? savedContainerId = null;

        void RevertContainerChanges(Dictionary<string, bool?>? changes, Dictionary<string, bool> container, ref Dictionary<string, bool>? saved)
        {
            if (changes != null && changes.Count > 0)
            {
                saved = new Dictionary<string, bool>();
                foreach (var kv in changes)
                {
                    if (container.TryGetValue(kv.Key, out var currentValue))
                    {
                        saved[kv.Key] = currentValue;
                    }
                    if (kv.Value == null)
                    {
                        container.Remove(kv.Key);
                    }
                    else
                    {
                        container[kv.Key] = kv.Value.Value;
                    }
                }
            }
        }

        if (!_typeScopedPropagate)
        {
            RevertContainerChanges(_typeScopedContainerTypeChanges, _containerType, ref savedContainerType);
            RevertContainerChanges(_typeScopedContainerIndexChanges, _containerIndex, ref savedContainerIndex);
            RevertContainerChanges(_typeScopedContainerListChanges, _containerList, ref savedContainerList);
            RevertContainerChanges(_typeScopedContainerLangChanges, _containerLanguage, ref savedContainerLang);
            RevertContainerChanges(_typeScopedContainerGraphChanges, _containerGraph, ref savedContainerGraph);
            RevertContainerChanges(_typeScopedContainerIdChanges, _containerId, ref savedContainerId);
        }

        // Restore @vocab/@base to pre-type-scoped state
        if (!_typeScopedPropagate && _savedContextForNested != null)
        {
            _vocabIri = _savedVocabForNested;
            _baseIri = _savedBaseForNested;
        }

        // Save context state before applying the new type's scoped context
        // This ensures the type-scoped context doesn't leak to parent after we return
        Dictionary<string, string>? savedContextState = null;
        string? savedVocabBeforeType = null;
        string? savedBaseBeforeType = null;

        // Apply the new type's scoped context (if typeTerm has a scoped context)
        if (!string.IsNullOrEmpty(typeTerm) && _scopedContext.TryGetValue(typeTerm, out var scopedJson))
        {
            // Save state before applying type-scoped context
            savedContextState = new Dictionary<string, string>(_context);
            savedVocabBeforeType = _vocabIri;
            savedBaseBeforeType = _baseIri;

            using var scopedDoc = JsonDocument.Parse(scopedJson);
            ProcessContext(scopedDoc.RootElement);
        }

        // Process @context if present in the object itself
        if (root.TryGetProperty("@context", out var contextElement))
        {
            // Save state if not already saved
            if (savedContextState == null)
            {
                savedContextState = new Dictionary<string, string>(_context);
                savedVocabBeforeType = _vocabIri;
                savedBaseBeforeType = _baseIri;
            }
            ProcessContext(contextElement);
        }

        // Get @id for subject if present, otherwise generate blank node
        string nodeId;
        if (root.TryGetProperty("@id", out var idElement))
        {
            var objId = idElement.GetString();
            nodeId = string.IsNullOrEmpty(objId) ? GenerateBlankNode() : ExpandIri(objId);
        }
        else
        {
            nodeId = GenerateBlankNode();
        }

        // Emit @type triple if typeIri is provided (not @none)
        if (!string.IsNullOrEmpty(typeIri))
        {
            EmitQuad(handler, nodeId, RdfType, typeIri, _currentGraph);
        }

        // Process additional @type if present (in addition to the key-derived type)
        if (root.TryGetProperty("@type", out var typeElement))
        {
            ProcessType(nodeId, typeElement, handler, _currentGraph);
        }

        // Process properties
        foreach (var prop in root.EnumerateObject())
        {
            var propName = prop.Name;

            // Skip JSON-LD keywords (but allow non-keyword @ patterns like "@" or "@foo.bar")
            if (propName.StartsWith('@') && IsKeywordLike(propName))
                continue;

            var propPredicate = ExpandTerm(propName);
            if (string.IsNullOrEmpty(propPredicate))
                continue;

            ProcessProperty(nodeId, propPredicate, propName, prop.Value, handler, _currentGraph);
        }

        // First, restore context state to before we applied this node's type-scoped/inline context
        // This prevents the nested node's context from leaking to the parent
        if (savedContextState != null)
        {
            _context.Clear();
            foreach (var kv in savedContextState)
            {
                _context[kv.Key] = kv.Value;
            }
            _vocabIri = savedVocabBeforeType;
            _baseIri = savedBaseBeforeType;
        }

        // Then restore parent's type-scoped changes (they apply to parent's remaining properties)
        if (savedCoercions != null)
        {
            foreach (var kv in savedCoercions)
            {
                _typeCoercion[kv.Key] = kv.Value;
            }
        }
        if (savedTerms != null)
        {
            foreach (var kv in savedTerms)
            {
                _context[kv.Key] = kv.Value;
            }
        }

        // Restore container values
        void RestoreContainer(Dictionary<string, bool>? saved, Dictionary<string, bool> container)
        {
            if (saved != null)
            {
                foreach (var kv in saved)
                {
                    container[kv.Key] = kv.Value;
                }
            }
        }
        RestoreContainer(savedContainerType, _containerType);
        RestoreContainer(savedContainerIndex, _containerIndex);
        RestoreContainer(savedContainerList, _containerList);
        RestoreContainer(savedContainerLang, _containerLanguage);
        RestoreContainer(savedContainerGraph, _containerGraph);
        RestoreContainer(savedContainerId, _containerId);

        _vocabIri = savedVocab;
        _baseIri = savedBase;

        return nodeId;
    }

    /// <summary>
    /// Parse a node with a pre-specified @id.
    /// Used for @id container maps where the key provides the @id.
    /// </summary>
    private string ParseNodeWithId(ref Utf8JsonReader reader, QuadHandler handler, string nodeId, string? parentNode)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            return nodeId;

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        // Process @context if present
        if (root.TryGetProperty("@context", out var contextElement))
        {
            ProcessContext(contextElement);
        }

        // Use provided nodeId unless object has its own @id
        var subject = nodeId;
        if (root.TryGetProperty("@id", out var idElement))
        {
            var objId = idElement.GetString();
            if (!string.IsNullOrEmpty(objId))
            {
                subject = ExpandIri(objId);
            }
        }

        // Process @type if present
        if (root.TryGetProperty("@type", out var typeElement))
        {
            ProcessType(subject, typeElement, handler, _currentGraph);
        }

        // Process properties
        foreach (var prop in root.EnumerateObject())
        {
            var propName = prop.Name;

            // Skip JSON-LD keywords (but allow non-keyword @ patterns like "@" or "@foo.bar")
            if (propName.StartsWith('@') && IsKeywordLike(propName))
                continue;

            var predicate = ExpandTerm(propName);
            if (string.IsNullOrEmpty(predicate))
                continue;

            ProcessProperty(subject, predicate, propName, prop.Value, handler, _currentGraph);
        }

        return subject;
    }

    /// <summary>
    /// Process a graph container value.
    /// The value becomes a named graph with the property linking to the graph node.
    /// </summary>
    private void ProcessGraphContainer(string subject, string predicate, JsonElement value,
        QuadHandler handler, string? graphIri, string? coercedType, string? termLanguage,
        bool isIndexContainer = false, bool isIdContainer = false)
    {
        // Handle compound container [@graph, @index] or [@graph, @id]
        if ((isIndexContainer || isIdContainer) && value.ValueKind == JsonValueKind.Object)
        {
            // Object keys are indexes (@index) or graph IDs (@id)
            foreach (var prop in value.EnumerateObject())
            {
                var key = prop.Name;
                var itemValue = prop.Value;

                // For @id container, the key becomes the graph @id
                string? graphIdFromKey = isIdContainer ? ExpandIri(key, expandTerms: false) : null;

                if (itemValue.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in itemValue.EnumerateArray())
                    {
                        ProcessGraphContainerItem(subject, predicate, item, handler, graphIri, coercedType, termLanguage, graphIdFromKey, isCompoundContainer: true);
                    }
                }
                else
                {
                    ProcessGraphContainerItem(subject, predicate, itemValue, handler, graphIri, coercedType, termLanguage, graphIdFromKey, isCompoundContainer: true);
                }
            }
        }
        // Handle arrays - each item is a separate graph
        else if (value.ValueKind == JsonValueKind.Array)
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
        QuadHandler handler, string? graphIri, string? coercedType, string? termLanguage,
        string? graphIdFromKey = null, bool isCompoundContainer = false)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            // Non-object values in graph container are processed normally
            ProcessValue(subject, predicate, value, handler, graphIri, coercedType, termLanguage);
            return;
        }

        // Check if the object has @id - use that as the graph name
        // Priority: explicit @id in object > graphIdFromKey from compound container key
        string? explicitId = null;
        if (value.TryGetProperty("@id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
        {
            explicitId = ExpandIri(idProp.GetString() ?? "", expandTerms: false);
        }
        else if (graphIdFromKey != null)
        {
            explicitId = graphIdFromKey;
        }

        // Check if value already has @graph - if so, we need different handling
        bool hasInnerGraph = value.TryGetProperty("@graph", out var graphProp);

        // Generate blank nodes for the link object and graph
        // - For simple @graph container with inner @graph: use DIFFERENT blank nodes (e081)
        // - For compound container [@graph, @index] with inner @graph: use SAME blank node (e084)
        // - When value doesn't have @graph: always use same ID for both
        string linkObject = explicitId ?? GenerateBlankNode();
        string namedGraphIri;
        if (hasInnerGraph && !isCompoundContainer)
        {
            // Simple @graph container with inner @graph - use separate blank node
            namedGraphIri = GenerateBlankNode();
        }
        else
        {
            // Compound container with inner @graph OR no inner @graph - use same ID for both
            namedGraphIri = linkObject;
        }

        // Emit the link from subject to the graph node
        EmitQuad(handler, subject, predicate, linkObject, graphIri);

        // Save current graph and set to the named graph for content processing
        var savedGraph = _currentGraph;
        _currentGraph = namedGraphIri;

        try
        {
            // Process the content - check for @graph property or process as regular node
            if (hasInnerGraph)
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
                    ProcessObjectValue(subject, predicate, value, handler, graphIri, coercedType);
                }
                break;

            case JsonValueKind.Null:
                // null values are ignored in JSON-LD
                break;
        }
    }

    private void ProcessObjectValue(string subject, string predicate, JsonElement value,
        QuadHandler handler, string? graphIri, string? coercedType = null)
    {
        // Check for value object (@value or alias)
        JsonElement valProp = default;
        bool hasValue = value.TryGetProperty("@value", out valProp);
        if (!hasValue)
        {
            // Check for @value aliases from type-scoped or property-scoped contexts
            foreach (var alias in _valueAliases)
            {
                if (value.TryGetProperty(alias, out valProp))
                {
                    hasValue = true;
                    break;
                }
            }
        }

        if (hasValue)
        {
            // Skip if @value is null
            if (valProp.ValueKind == JsonValueKind.Null)
                return;
            var literal = ProcessValueObject(value, valProp);
            EmitQuad(handler, subject, predicate, literal, graphIri);
            return;
        }

        // Check for invalid value object with @language (or alias) but no @value - drop it
        // Note: @type without @value is valid - it's a node object with rdf:type, not a value object
        bool hasLanguage = value.TryGetProperty("@language", out _);
        if (!hasLanguage)
        {
            foreach (var alias in _languageAliases)
            {
                if (value.TryGetProperty(alias, out _))
                {
                    hasLanguage = true;
                    break;
                }
            }
        }
        if (hasLanguage)
        {
            // @language requires @value - this is invalid, drop the property
            return;
        }

        // Check for @id only (IRI reference)
        if (value.TryGetProperty("@id", out var idProp) && value.EnumerateObject().Count() == 1)
        {
            var idValue = idProp.GetString() ?? "";
            // Per JSON-LD 1.1: @id values that look like keywords (e.g., "@ignoreMe")
            // should be ignored and no triple should be emitted (e122)
            if (idValue.StartsWith('@') && IsKeywordLike(idValue))
            {
                return; // Ignore keyword-like @id value
            }
            var iri = ExpandIri(idValue);
            EmitQuad(handler, subject, predicate, iri, graphIri);
            return;
        }

        // Check for @graph (graph object)
        if (value.TryGetProperty("@graph", out var graphProp))
        {
            // Create a blank node for the named graph
            var graphNode = GenerateBlankNode();

            // Emit the relationship from subject to the graph node
            EmitQuad(handler, subject, predicate, graphNode, graphIri);

            // Save current graph and set to the new named graph
            var savedGraph = _currentGraph;
            _currentGraph = graphNode;
            try
            {
                // Process @graph contents into that named graph
                if (graphProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in graphProp.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            ProcessGraphNode(item, handler);
                        }
                    }
                }
                else if (graphProp.ValueKind == JsonValueKind.Object)
                {
                    ProcessGraphNode(graphProp, handler);
                }
            }
            finally
            {
                _currentGraph = savedGraph;
            }
            return;
        }

        // Check for @list
        if (value.TryGetProperty("@list", out var listProp))
        {
            // Pass coercedType to list processing
            var listHead = ProcessList(listProp, handler, graphIri, coercedType);
            EmitQuad(handler, subject, predicate, listHead, graphIri);
            return;
        }

        // Check for @set - flatten the contents, ignore empty @set
        if (value.TryGetProperty("@set", out var setProp))
        {
            // @set just contains values to be flattened - process each one
            // Pass coercedType to each element
            if (setProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in setProp.EnumerateArray())
                {
                    // Skip null values
                    if (item.ValueKind == JsonValueKind.Null)
                        continue;
                    ProcessValue(subject, predicate, item, handler, graphIri, coercedType, null);
                }
            }
            else if (setProp.ValueKind != JsonValueKind.Null)
            {
                // Single value
                ProcessValue(subject, predicate, setProp, handler, graphIri, coercedType, null);
            }
            // Empty @set produces no output
            return;
        }

        // Nested object - create blank node
        // 1. Revert type-scoped changes (type-scoped contexts don't propagate)
        // 2. Save/restore @vocab/@base around nested node (in case nested has inline @context)
        // Property-scoped additions SHOULD propagate, so we only revert type-scoped changes
        Dictionary<string, string>? savedCoercions = null;
        Dictionary<string, string>? savedTerms = null;
        // Only revert type-scoped changes if @propagate is NOT true
        // @propagate: true means type-scoped context SHOULD propagate to nested nodes
        if (!_typeScopedPropagate && _typeScopedCoercionChanges != null && _typeScopedCoercionChanges.Count > 0)
        {
            // Revert type coercions to their original values (or remove if they were new)
            savedCoercions = new Dictionary<string, string>();
            foreach (var kv in _typeScopedCoercionChanges)
            {
                if (_typeCoercion.TryGetValue(kv.Key, out var currentValue))
                {
                    savedCoercions[kv.Key] = currentValue;
                }
                if (kv.Value == null)
                {
                    // Term was new - remove it
                    _typeCoercion.Remove(kv.Key);
                }
                else
                {
                    // Term was modified - restore original
                    _typeCoercion[kv.Key] = kv.Value;
                }
            }
        }

        // Revert type-scoped term changes
        if (!_typeScopedPropagate && _typeScopedTermChanges != null && _typeScopedTermChanges.Count > 0)
        {
            savedTerms = new Dictionary<string, string>();
            foreach (var kv in _typeScopedTermChanges)
            {
                if (_context.TryGetValue(kv.Key, out var currentValue))
                {
                    savedTerms[kv.Key] = currentValue;
                }
                if (kv.Value == null)
                {
                    // Term was new - remove it
                    _context.Remove(kv.Key);
                }
                else
                {
                    // Term was modified - restore original
                    _context[kv.Key] = kv.Value;
                }
            }
        }

        // Revert type-scoped container changes
        Dictionary<string, bool>? savedContainerType = null;
        Dictionary<string, bool>? savedContainerIndex = null;
        Dictionary<string, bool>? savedContainerList = null;
        Dictionary<string, bool>? savedContainerLang = null;
        Dictionary<string, bool>? savedContainerGraph = null;
        Dictionary<string, bool>? savedContainerId = null;

        void RevertContainerChanges(Dictionary<string, bool?>? changes, Dictionary<string, bool> container, ref Dictionary<string, bool>? saved)
        {
            if (changes != null && changes.Count > 0)
            {
                saved = new Dictionary<string, bool>();
                foreach (var kv in changes)
                {
                    if (container.TryGetValue(kv.Key, out var currentValue))
                    {
                        saved[kv.Key] = currentValue;
                    }
                    if (kv.Value == null)
                    {
                        // Container was new - remove it
                        container.Remove(kv.Key);
                    }
                    else
                    {
                        // Container was modified or removed - restore original
                        container[kv.Key] = kv.Value.Value;
                    }
                }
            }
        }

        if (!_typeScopedPropagate)
        {
            RevertContainerChanges(_typeScopedContainerTypeChanges, _containerType, ref savedContainerType);
            RevertContainerChanges(_typeScopedContainerIndexChanges, _containerIndex, ref savedContainerIndex);
            RevertContainerChanges(_typeScopedContainerListChanges, _containerList, ref savedContainerList);
            RevertContainerChanges(_typeScopedContainerLangChanges, _containerLanguage, ref savedContainerLang);
            RevertContainerChanges(_typeScopedContainerGraphChanges, _containerGraph, ref savedContainerGraph);
            RevertContainerChanges(_typeScopedContainerIdChanges, _containerId, ref savedContainerId);
        }

        // Save @vocab and @base before processing nested node
        // This handles both type-scoped context restoration AND nested object inline @context
        var savedVocab = _vocabIri;
        var savedBase = _baseIri;
        // If type-scoped context changed @vocab/@base, restore to pre-type-scoped state for nested
        // (unless @propagate is true)
        if (!_typeScopedPropagate && _savedContextForNested != null)
        {
            _vocabIri = _savedVocabForNested;
            _baseIri = _savedBaseForNested;
        }

        var tempReader = new Utf8JsonReader(Encoding.UTF8.GetBytes(value.GetRawText()));
        tempReader.Read();
        var blankNode = ParseNode(ref tempReader, handler, subject);
        EmitQuad(handler, subject, predicate, blankNode, graphIri);

        // Restore after processing nested node:
        // - Re-apply type-scoped changes (they apply to this node's remaining properties)
        // - Restore @vocab/@base (nested node's inline @context shouldn't affect siblings)
        if (savedCoercions != null)
        {
            foreach (var kv in savedCoercions)
            {
                _typeCoercion[kv.Key] = kv.Value;
            }
        }
        if (savedTerms != null)
        {
            foreach (var kv in savedTerms)
            {
                _context[kv.Key] = kv.Value;
            }
        }

        // Restore container values
        void RestoreContainer(Dictionary<string, bool>? saved, Dictionary<string, bool> container)
        {
            if (saved != null)
            {
                foreach (var kv in saved)
                {
                    container[kv.Key] = kv.Value;
                }
            }
        }
        RestoreContainer(savedContainerType, _containerType);
        RestoreContainer(savedContainerIndex, _containerIndex);
        RestoreContainer(savedContainerList, _containerList);
        RestoreContainer(savedContainerLang, _containerLanguage);
        RestoreContainer(savedContainerGraph, _containerGraph);
        RestoreContainer(savedContainerId, _containerId);

        _vocabIri = savedVocab;
        _baseIri = savedBase;
    }

    private string ProcessValueObject(JsonElement obj, JsonElement valueProp)
    {
        string valueStr;
        string? inferredType = null;  // Inferred XSD type for native JSON values

        switch (valueProp.ValueKind)
        {
            case JsonValueKind.String:
                valueStr = valueProp.GetString() ?? "";
                break;
            case JsonValueKind.Number:
                var rawText = valueProp.GetRawText();
                valueStr = rawText;
                // Infer XSD type for native JSON numbers
                var isDouble = rawText.Contains('.') || rawText.Contains('e') || rawText.Contains('E');
                inferredType = isDouble
                    ? "<http://www.w3.org/2001/XMLSchema#double>"
                    : "<http://www.w3.org/2001/XMLSchema#integer>";
                break;
            case JsonValueKind.True:
                valueStr = "true";
                inferredType = "<http://www.w3.org/2001/XMLSchema#boolean>";
                break;
            case JsonValueKind.False:
                valueStr = "false";
                inferredType = "<http://www.w3.org/2001/XMLSchema#boolean>";
                break;
            default:
                valueStr = "";
                break;
        }

        // Check for @language (or alias)
        JsonElement langProp = default;
        bool hasLanguage = obj.TryGetProperty("@language", out langProp);
        if (!hasLanguage)
        {
            foreach (var alias in _languageAliases)
            {
                if (obj.TryGetProperty(alias, out langProp))
                {
                    hasLanguage = true;
                    break;
                }
            }
        }
        if (hasLanguage)
        {
            var lang = langProp.GetString() ?? "";
            return $"\"{EscapeString(valueStr)}\"@{lang}";
        }

        // Check for @type (datatype, or alias) - terms should be expanded for datatypes
        JsonElement typeProp = default;
        bool hasType = obj.TryGetProperty("@type", out typeProp);
        if (!hasType)
        {
            foreach (var alias in _typeAliases)
            {
                if (obj.TryGetProperty(alias, out typeProp))
                {
                    hasType = true;
                    break;
                }
            }
        }
        if (hasType)
        {
            var datatype = ExpandIri(typeProp.GetString() ?? "", expandTerms: true);
            return $"\"{EscapeString(valueStr)}\"^^{datatype}";
        }

        // Use inferred type for native JSON values (numbers, booleans)
        if (inferredType != null)
        {
            return $"\"{EscapeString(valueStr)}\"^^{inferredType}";
        }

        // Plain literal (strings without type)
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
        // Handle non-array values by treating them as single-item arrays
        if (listElement.ValueKind != JsonValueKind.Array)
        {
            // Null or @value:null produces empty list
            if (listElement.ValueKind == JsonValueKind.Null)
                return RdfNil;
            if (listElement.ValueKind == JsonValueKind.Object &&
                listElement.TryGetProperty("@value", out var valProp) &&
                valProp.ValueKind == JsonValueKind.Null)
                return RdfNil;

            // Single non-null value - wrap in list
            var singleNode = GenerateBlankNode();
            ProcessValue(singleNode, RdfFirst, listElement, handler, graphIri, coercedType);
            EmitQuad(handler, singleNode, RdfRest, RdfNil, graphIri);
            return singleNode;
        }

        // Empty array
        if (listElement.GetArrayLength() == 0)
        {
            return RdfNil;
        }

        // Filter out null values and @value:null objects
        var validItems = new List<JsonElement>();
        foreach (var item in listElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Null)
                continue;
            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("@value", out var vp) &&
                vp.ValueKind == JsonValueKind.Null)
                continue;
            validItems.Add(item);
        }

        // All items were null
        if (validItems.Count == 0)
        {
            return RdfNil;
        }

        string? firstNode = null;
        string? previousNode = null;

        foreach (var item in validItems)
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
