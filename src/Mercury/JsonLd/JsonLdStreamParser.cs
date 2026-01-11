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
public sealed partial class JsonLdStreamParser : IDisposable, IAsyncDisposable
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
    private readonly Dictionary<string, string> _indexProperty; // term -> @index property IRI (property-valued index)
    private readonly HashSet<string> _typeAliases; // terms aliased to @type
    private readonly HashSet<string> _idAliases; // terms aliased to @id
    private readonly HashSet<string> _graphAliases; // terms aliased to @graph
    private readonly HashSet<string> _includedAliases; // terms aliased to @included
    private readonly HashSet<string> _nestAliases; // terms aliased to @nest
    private readonly HashSet<string> _noneAliases; // terms aliased to @none
    private readonly HashSet<string> _valueAliases; // terms aliased to @value
    private readonly HashSet<string> _languageAliases; // terms aliased to @language
    private readonly HashSet<string> _jsonAliases; // terms aliased to @json
    private readonly HashSet<string> _nullTerms; // terms decoupled from @vocab (mapped to null)
    private readonly HashSet<string> _prefixable; // terms usable as prefixes in compact IRIs
    private readonly HashSet<string> _protectedTerms; // terms marked as @protected
    private readonly HashSet<string> _typeScopedProtectedTerms; // protected terms from type-scoped contexts only
    private bool _isApplyingTypeScopedContext; // true when applying type-scoped context (for tracking protected terms)

    // Processing mode: "json-ld-1.0" or "json-ld-1.1" (default)
    private readonly string _processingMode;

    // Base IRI for relative IRI resolution
    private string? _baseIri;

    // Document base IRI (original, immutable) - restored when @context: null is used
    private readonly string? _documentBaseIri;

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

    // Track property-scoped context changes when @propagate: false (c027, c028)
    // Property-scoped contexts propagate by default, but @propagate: false stops propagation to nested nodes
    private Dictionary<string, string?>? _propScopedTermChanges;     // Terms added/modified by property-scoped context
    private Dictionary<string, string?>? _propScopedCoercionChanges; // Coercions added/modified by property-scoped
    private Dictionary<string, bool?>? _propScopedContainerTypeChanges;   // @container: @type changes
    private Dictionary<string, bool?>? _propScopedContainerIndexChanges;  // @container: @index changes
    private Dictionary<string, bool?>? _propScopedContainerListChanges;   // @container: @list changes
    private Dictionary<string, bool?>? _propScopedContainerLangChanges;   // @container: @language changes
    private Dictionary<string, bool?>? _propScopedContainerGraphChanges;  // @container: @graph changes
    private Dictionary<string, bool?>? _propScopedContainerIdChanges;     // @container: @id changes
    private bool _propScopedNoPropagate;                             // If true, property-scoped context does NOT propagate

    // Track inline (embedded) context changes when @propagate: false (c028)
    // Inline contexts propagate by default, but @propagate: false stops propagation to nested nodes
    private Dictionary<string, string?>? _inlineScopedTermChanges;     // Terms added/modified by inline context
    private Dictionary<string, string?>? _inlineScopedCoercionChanges; // Coercions added/modified by inline context
    private Dictionary<string, bool?>? _inlineScopedContainerTypeChanges;   // @container: @type changes
    private Dictionary<string, bool?>? _inlineScopedContainerIndexChanges;  // @container: @index changes
    private Dictionary<string, bool?>? _inlineScopedContainerListChanges;   // @container: @list changes
    private Dictionary<string, bool?>? _inlineScopedContainerLangChanges;   // @container: @language changes
    private Dictionary<string, bool?>? _inlineScopedContainerGraphChanges;  // @container: @graph changes
    private Dictionary<string, bool?>? _inlineScopedContainerIdChanges;     // @container: @id changes
    private bool _inlineScopedNoPropagate;                           // If true, inline context does NOT propagate

    private const int DefaultBufferSize = 65536; // 64KB
    private const int OutputBufferSize = 16384;

    private const string RdfType = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>";
    private const string RdfFirst = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#first>";
    private const string RdfRest = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#rest>";
    private const string RdfNil = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#nil>";
    private const string XsdString = "http://www.w3.org/2001/XMLSchema#string";

    public JsonLdStreamParser(Stream stream, int bufferSize = DefaultBufferSize, IBufferManager? bufferManager = null)
        : this(stream, baseIri: null, processingMode: null, bufferSize, bufferManager)
    {
    }

    public JsonLdStreamParser(Stream stream, string? baseIri, int bufferSize = DefaultBufferSize, IBufferManager? bufferManager = null)
        : this(stream, baseIri, processingMode: null, bufferSize, bufferManager)
    {
    }

    public JsonLdStreamParser(Stream stream, string? baseIri, string? processingMode, int bufferSize = DefaultBufferSize, IBufferManager? bufferManager = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _baseIri = baseIri;
        _documentBaseIri = baseIri;  // Preserve original document base for @context: null reset
        _processingMode = processingMode ?? "json-ld-1.1";  // Default to 1.1
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
        _indexProperty = new Dictionary<string, string>(StringComparer.Ordinal);
        _typeAliases = new HashSet<string>(StringComparer.Ordinal);
        _idAliases = new HashSet<string>(StringComparer.Ordinal);
        _graphAliases = new HashSet<string>(StringComparer.Ordinal);
        _includedAliases = new HashSet<string>(StringComparer.Ordinal);
        _nestAliases = new HashSet<string>(StringComparer.Ordinal);
        _noneAliases = new HashSet<string>(StringComparer.Ordinal);
        _valueAliases = new HashSet<string>(StringComparer.Ordinal);
        _languageAliases = new HashSet<string>(StringComparer.Ordinal);
        _jsonAliases = new HashSet<string>(StringComparer.Ordinal);
        _nullTerms = new HashSet<string>(StringComparer.Ordinal);
        _prefixable = new HashSet<string>(StringComparer.Ordinal);
        _protectedTerms = new HashSet<string>(StringComparer.Ordinal);
        _typeScopedProtectedTerms = new HashSet<string>(StringComparer.Ordinal);
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
