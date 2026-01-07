// RdfXmlStreamParser.cs
// Zero-GC streaming RDF/XML parser
// Custom XML parser targeting RDF/XML subset (not a general XML parser)
// Based on W3C RDF/XML Syntax Specification
// No external dependencies, only BCL
// .NET 10 / C# 14

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.Rdf;
using SkyOmega.Mercury.Rdf.Turtle;
using SkyOmega.Mercury.Runtime.Buffers;

namespace SkyOmega.Mercury.RdfXml;

/// <summary>
/// Element kind in RDF/XML structure.
/// </summary>
public enum ElementKind
{
    Root,           // rdf:RDF container
    Description,    // rdf:Description or typed node element
    Property,       // Property element (predicate)
    Literal         // Literal content (parseType="Literal")
}

/// <summary>
/// Zero-allocation streaming parser for RDF/XML format.
/// Implements core RDF/XML syntax specification features.
///
/// Supported features:
/// - rdf:Description with rdf:about, rdf:ID, rdf:nodeID
/// - Typed node elements (e.g., foaf:Person)
/// - Property elements with rdf:resource
/// - Property elements with literal content
/// - rdf:datatype for typed literals
/// - xml:lang for language tags
/// - rdf:parseType="Resource"
/// - rdf:parseType="Literal" (XML literal content)
/// - rdf:parseType="Collection"
/// - Nested descriptions
/// - Property attributes (shorthand)
///
/// For zero-GC parsing, use ParseAsync(TripleHandler).
/// </summary>
/// <remarks>
/// <para><b>Thread Safety:</b> This class is NOT thread-safe. Each instance
/// maintains internal parsing state and buffers that cannot be shared across threads.
/// Create a separate instance per thread or serialize access with locking.</para>
/// <para><b>Usage Pattern:</b> Create one instance per stream. Dispose when done
/// to return pooled buffers.</para>
/// </remarks>
public sealed partial class RdfXmlStreamParser : IDisposable, IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly IBufferManager _bufferManager;

    // Input buffer
    private byte[] _inputBuffer;
    private int _bufferPosition;
    private int _bufferLength;
    private bool _endOfStream;
    private bool _isDisposed;

    // Output buffer for zero-GC string building
    private char[] _outputBuffer;
    private int _outputOffset;
    private const int OutputBufferSize = 32768; // 32KB for parsed terms

    // XML state
    private readonly Dictionary<string, string> _namespaces;
    private int _blankNodeCounter;

    // Current parse state
    private int _line;
    private int _column;
    private bool _insideRdfRoot; // Track if we're inside rdf:RDF for validation
    private readonly HashSet<string> _usedIds = new(StringComparer.Ordinal); // Track rdf:ID values for uniqueness

    // Base URI for resolving relative URIs (stack for scoping)
    private readonly Stack<string> _baseUriStack = new();
    private string _baseUri = string.Empty;

    // Standard RDF namespace
    private const string RdfNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";

    private const int DefaultBufferSize = 16384;

    public RdfXmlStreamParser(Stream stream, string? baseUri = null, int bufferSize = DefaultBufferSize, IBufferManager? bufferManager = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _bufferManager = bufferManager ?? PooledBufferManager.Shared;
        _baseUri = baseUri ?? string.Empty;

        _inputBuffer = _bufferManager.Rent<byte>(bufferSize).Array!;
        _outputBuffer = _bufferManager.Rent<char>(OutputBufferSize).Array!;

        _namespaces = new Dictionary<string, string>(16);

        _line = 1;
        _column = 1;

        InitializeStandardNamespaces();
    }

    private void InitializeStandardNamespaces()
    {
        _namespaces["rdf"] = RdfNamespace;
        _namespaces["xml"] = XmlNamespace;
        _namespaces["xmlns"] = "http://www.w3.org/2000/xmlns/";
    }

    /// <summary>
    /// Parse RDF/XML document with zero allocations.
    /// Triples are emitted via the handler callback with spans valid only during the call.
    /// </summary>
    public async Task ParseAsync(TripleHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);

        await FillBufferAsync(cancellationToken);

        // Skip XML declaration and whitespace
        SkipXmlDeclaration();
        SkipWhitespace();

        // Parse root element (should be rdf:RDF or a single description)
        while (!IsEndOfInput())
        {
            SkipWhitespace();
            if (IsEndOfInput())
                break;

            // Refill buffer if running low
            var remaining = _bufferLength - _bufferPosition;
            if (remaining < _inputBuffer.Length / 4 && !_endOfStream)
            {
                await FillBufferAsync(cancellationToken);
                SkipWhitespace();
            }

            if (IsEndOfInput())
                break;

            if (Peek() == '<')
            {
                await ParseElementAsync(handler, cancellationToken);
            }
            else
            {
                // Skip unexpected content
                Consume();
            }
        }
    }

    /// <summary>
    /// Parse an XML element.
    /// </summary>
    private async Task ParseElementAsync(TripleHandler handler, CancellationToken cancellationToken)
    {
        if (!TryConsume('<'))
            return;

        // Check for closing tag, comment, or PI
        var next = Peek();
        if (next == '/')
        {
            // Closing tag - skip it
            while (Peek() != '>' && !IsEndOfInput())
                Consume();
            TryConsume('>');
            return;
        }

        if (next == '!')
        {
            // Comment or CDATA
            SkipCommentOrCData();
            return;
        }

        if (next == '?')
        {
            // Processing instruction
            SkipProcessingInstruction();
            return;
        }

        // Parse element name
        ResetOutputBuffer();
        var elementName = ParseQName();
        if (elementName.IsEmpty)
            return;

        // Store element name for later use
        var elementNameStr = elementName.ToString();

        // Parse attributes
        var attributes = ParseAttributes();

        // Determine element kind and handle accordingly
        SplitQName(elementNameStr, out var prefix, out var localName);
        var namespaceUri = ResolveNamespace(prefix);

        // Check for self-closing tag
        SkipWhitespace();
        bool selfClosing = TryConsume('/');
        if (!TryConsume('>'))
        {
            // Skip to end of tag if malformed
            while (Peek() != '>' && !IsEndOfInput())
                Consume();
            TryConsume('>');
        }

        if (namespaceUri == RdfNamespace)
        {
            if (localName == "RDF")
            {
                // Nested rdf:RDF is not allowed
                if (_insideRdfRoot)
                    throw ParserException("rdf:RDF is forbidden as a node element name");

                // Capture xml:base from rdf:RDF element (strip fragment per RFC 3986)
                if (attributes.TryGetValue("xml:base", out var xmlBase))
                {
                    var resolved = ResolveUri(xmlBase);
                    var hashIdx = resolved.IndexOf('#');
                    _baseUri = hashIdx >= 0 ? resolved[..hashIdx] : resolved;
                }

                // Root rdf:RDF element - parse children
                _insideRdfRoot = true;
                try
                {
                    if (!selfClosing)
                    {
                        await ParseRdfRootContentAsync(handler, cancellationToken);
                    }
                }
                finally
                {
                    _insideRdfRoot = false;
                }
            }
            else if (localName == "Description")
            {
                // rdf:Description element
                await ParseDescriptionElementAsync(handler, attributes, selfClosing, cancellationToken);
            }
            else
            {
                // Other RDF elements inside rdf:RDF (like rdf:Seq, rdf:Bag, etc.)
                await ParseTypedNodeElementAsync(handler, namespaceUri, localName, attributes, selfClosing, cancellationToken);
            }
        }
        else
        {
            // Typed node element (e.g., foaf:Person)
            await ParseTypedNodeElementAsync(handler, namespaceUri, localName, attributes, selfClosing, cancellationToken);
        }
    }

    /// <summary>
    /// Parse content inside rdf:RDF root element.
    /// </summary>
    private async Task ParseRdfRootContentAsync(TripleHandler handler, CancellationToken cancellationToken)
    {
        while (!IsEndOfInput())
        {
            await RefillIfNeededAsync(cancellationToken);
            SkipWhitespace();

            if (Peek() == '<')
            {
                if (PeekAhead(1) == '/')
                {
                    // Closing tag for rdf:RDF
                    SkipClosingTag();
                    return;
                }
                await ParseElementAsync(handler, cancellationToken);
            }
            else if (IsEndOfInput())
            {
                break;
            }
            else
            {
                Consume(); // Skip text content at root level
            }
        }
    }

    /// <summary>
    /// Parse rdf:Description element.
    /// </summary>
    private async Task ParseDescriptionElementAsync(
        TripleHandler handler,
        Dictionary<string, string> attributes,
        bool selfClosing,
        CancellationToken cancellationToken)
    {
        // Validate node element attributes
        ValidateNodeElementAttributes(attributes);

        // Push xml:base scope if present
        bool pushedBase = PushBaseIfPresent(attributes);

        // Determine subject (as string for async boundary crossing)
        var subjectStr = DetermineSubjectString(attributes);

        // Get current xml:lang for property attributes
        attributes.TryGetValue("xml:lang", out var xmlLang);

        // Process property attributes (shorthand properties)
        EmitPropertyAttributes(handler, subjectStr, attributes, xmlLang);

        if (!selfClosing)
        {
            // Parse property elements
            await ParsePropertyElementsAsync(handler, subjectStr, xmlLang, cancellationToken);
        }

        // Pop xml:base scope if we pushed one
        if (pushedBase)
            PopBase();
    }

    /// <summary>
    /// Parse typed node element (e.g., foaf:Person).
    /// </summary>
    private async Task ParseTypedNodeElementAsync(
        TripleHandler handler,
        string namespaceUri,
        string localName,
        Dictionary<string, string> attributes,
        bool selfClosing,
        CancellationToken cancellationToken)
    {
        // Validate node element name
        ValidateNodeElementName(namespaceUri, localName);

        // Validate node element attributes
        ValidateNodeElementAttributes(attributes);

        // Push xml:base scope if present
        bool pushedBase = PushBaseIfPresent(attributes);

        // Determine subject (as string)
        var subjectStr = DetermineSubjectString(attributes);

        // Emit rdf:type triple for typed node
        ResetOutputBuffer();
        var typeUri = BuildIri(namespaceUri.AsSpan(), localName.AsSpan());
        handler(subjectStr.AsSpan(), $"<{RdfNamespace}type>".AsSpan(), typeUri);

        // Get current xml:lang for property attributes
        attributes.TryGetValue("xml:lang", out var xmlLang);

        // Process property attributes
        EmitPropertyAttributes(handler, subjectStr, attributes, xmlLang);

        if (!selfClosing)
        {
            // Parse property elements
            await ParsePropertyElementsAsync(handler, subjectStr, xmlLang, cancellationToken);
        }

        // Pop xml:base scope if we pushed one
        if (pushedBase)
            PopBase();
    }

    /// <summary>
    /// Parse property elements inside a description.
    /// </summary>
    private async Task ParsePropertyElementsAsync(
        TripleHandler handler,
        string subjectStr,
        string? inheritedLang,
        CancellationToken cancellationToken)
    {
        // Counter for rdf:li elements (starts at 1 per W3C spec)
        int liCounter = 1;

        while (!IsEndOfInput())
        {
            await RefillIfNeededAsync(cancellationToken);
            SkipWhitespace();

            if (Peek() == '<')
            {
                if (PeekAhead(1) == '/')
                {
                    // Closing tag for description
                    SkipClosingTag();
                    return;
                }

                // Parse property element
                TryConsume('<');
                var propName = ParseQName();
                if (propName.IsEmpty)
                    continue;

                var propNameStr = propName.ToString();
                var propAttributes = ParseAttributes();

                // Validate property element attributes
                ValidatePropertyElementAttributes(propAttributes);

                // Build predicate IRI
                SplitQName(propNameStr, out var propPrefix, out var propLocal);
                var propNamespace = ResolveNamespace(propPrefix);

                // Validate property element name
                ValidatePropertyElementName(propNamespace, propLocal);

                // Handle rdf:li expansion to rdf:_1, rdf:_2, etc.
                if (propNamespace == RdfNamespace && propLocal == "li")
                {
                    propLocal = $"_{liCounter++}";
                }

                ResetOutputBuffer();
                var predicate = BuildIri(propNamespace.AsSpan(), propLocal.AsSpan());
                var predicateStr = predicate.ToString();

                SkipWhitespace();
                bool propSelfClosing = TryConsume('/');
                TryConsume('>');

                if (propSelfClosing)
                {
                    // Check for rdf:ID for reification
                    string? reifyId = null;
                    if (propAttributes.TryGetValue("rdf:ID", out reifyId) ||
                        propAttributes.TryGetValue("ID", out reifyId))
                    {
                        // Will emit reification triples after the main triple
                    }

                    // Check for rdf:parseType first
                    if (propAttributes.TryGetValue("rdf:parseType", out var selfParseType) ||
                        propAttributes.TryGetValue("parseType", out selfParseType))
                    {
                        if (selfParseType == "Resource")
                        {
                            // Self-closing parseType="Resource" creates empty blank node
                            ResetOutputBuffer();
                            var blankNode = GenerateBlankNode();
                            var objStr = blankNode.ToString();
                            handler(subjectStr.AsSpan(), predicateStr.AsSpan(), blankNode);
                            if (reifyId != null)
                                EmitReification(handler, reifyId, subjectStr, predicateStr, objStr);
                        }
                        // Other parseTypes on self-closing produce empty literal
                        else
                        {
                            ResetOutputBuffer();
                            var obj = BuildPlainLiteral(ReadOnlySpan<char>.Empty);
                            var objStr = obj.ToString();
                            handler(subjectStr.AsSpan(), predicateStr.AsSpan(), obj);
                            if (reifyId != null)
                                EmitReification(handler, reifyId, subjectStr, predicateStr, objStr);
                        }
                        continue;
                    }

                    // Get effective xml:lang for this property element
                    propAttributes.TryGetValue("xml:lang", out var propLang);
                    var effectiveLang = propLang ?? inheritedLang;

                    // Self-closing property - check for rdf:resource
                    if (propAttributes.TryGetValue("rdf:resource", out var resourceUri) ||
                        propAttributes.TryGetValue("resource", out resourceUri))
                    {
                        ResetOutputBuffer();
                        var resolvedUri = ResolveUri(resourceUri);
                        var obj = WrapIri(resolvedUri.AsSpan());
                        var objStr = obj.ToString();
                        handler(subjectStr.AsSpan(), predicateStr.AsSpan(), obj);
                        // Property attributes apply to the resource URI as subject
                        if (HasPropertyAttributes(propAttributes))
                            EmitPropertyAttributes(handler, objStr, propAttributes, effectiveLang);
                        if (reifyId != null)
                            EmitReification(handler, reifyId, subjectStr, predicateStr, objStr);
                    }
                    else if (propAttributes.TryGetValue("rdf:nodeID", out var nodeId) ||
                             propAttributes.TryGetValue("nodeID", out nodeId))
                    {
                        ResetOutputBuffer();
                        var obj = BuildBlankNode(nodeId.AsSpan());
                        var objStr = obj.ToString();
                        handler(subjectStr.AsSpan(), predicateStr.AsSpan(), obj);
                        if (reifyId != null)
                            EmitReification(handler, reifyId, subjectStr, predicateStr, objStr);
                    }
                    else if (HasPropertyAttributes(propAttributes))
                    {
                        // Property element with property attributes but no rdf:resource/nodeID
                        // Creates an implicit blank node as the object
                        ResetOutputBuffer();
                        var blankNode = GenerateBlankNode();
                        var blankNodeStr = blankNode.ToString();
                        handler(subjectStr.AsSpan(), predicateStr.AsSpan(), blankNode);
                        EmitPropertyAttributes(handler, blankNodeStr, propAttributes, effectiveLang);
                        if (reifyId != null)
                            EmitReification(handler, reifyId, subjectStr, predicateStr, blankNodeStr);
                    }
                    else
                    {
                        // Empty self-closing property element produces empty literal
                        ResetOutputBuffer();
                        var obj = BuildPlainLiteral(ReadOnlySpan<char>.Empty);
                        var objStr = obj.ToString();
                        handler(subjectStr.AsSpan(), predicateStr.AsSpan(), obj);
                        if (reifyId != null)
                            EmitReification(handler, reifyId, subjectStr, predicateStr, objStr);
                    }
                    continue;
                }

                // Check for rdf:ID for reification (non-self-closing)
                string? reifyIdNonSelf = null;
                if (propAttributes.TryGetValue("rdf:ID", out reifyIdNonSelf) ||
                    propAttributes.TryGetValue("ID", out reifyIdNonSelf))
                {
                    // Will emit reification triples after the main triple
                }

                // Get effective xml:lang for non-self-closing property
                propAttributes.TryGetValue("xml:lang", out var propLangNonSelf);
                var effectiveLangNonSelf = propLangNonSelf ?? inheritedLang;

                // Check for rdf:resource attribute
                if (propAttributes.TryGetValue("rdf:resource", out var resUri) ||
                    propAttributes.TryGetValue("resource", out resUri))
                {
                    ResetOutputBuffer();
                    var resolvedUri = ResolveUri(resUri);
                    var obj = WrapIri(resolvedUri.AsSpan());
                    var objStr = obj.ToString();
                    handler(subjectStr.AsSpan(), predicateStr.AsSpan(), obj);
                    // Property attributes apply to the resource URI as subject
                    if (HasPropertyAttributes(propAttributes))
                        EmitPropertyAttributes(handler, objStr, propAttributes, effectiveLangNonSelf);
                    if (reifyIdNonSelf != null)
                        EmitReification(handler, reifyIdNonSelf, subjectStr, predicateStr, objStr);
                    SkipToClosingTag(propNameStr);
                    continue;
                }

                // Check for rdf:parseType
                if (propAttributes.TryGetValue("rdf:parseType", out var parseType) ||
                    propAttributes.TryGetValue("parseType", out parseType))
                {
                    if (parseType == "Resource")
                    {
                        // Nested blank node
                        ResetOutputBuffer();
                        var blankNode = GenerateBlankNode();
                        var blankNodeStr = blankNode.ToString();
                        handler(subjectStr.AsSpan(), predicateStr.AsSpan(), blankNode);
                        if (reifyIdNonSelf != null)
                            EmitReification(handler, reifyIdNonSelf, subjectStr, predicateStr, blankNodeStr);
                        await ParsePropertyElementsAsync(handler, blankNodeStr, effectiveLangNonSelf, cancellationToken);
                        continue;
                    }
                    else if (parseType == "Literal")
                    {
                        // XML literal
                        ResetOutputBuffer();
                        var xmlContent = ParseXmlLiteralContent(propNameStr);
                        var literal = BuildTypedLiteral(xmlContent, $"{RdfNamespace}XMLLiteral".AsSpan());
                        var literalStr = literal.ToString();
                        handler(subjectStr.AsSpan(), predicateStr.AsSpan(), literal);
                        if (reifyIdNonSelf != null)
                            EmitReification(handler, reifyIdNonSelf, subjectStr, predicateStr, literalStr);
                        continue;
                    }
                    else if (parseType == "Collection")
                    {
                        // RDF collection - returns the list head (or rdf:nil for empty)
                        var listObjStr = await ParseCollectionAsync(handler, subjectStr, predicateStr, effectiveLangNonSelf, cancellationToken);
                        if (reifyIdNonSelf != null && !string.IsNullOrEmpty(listObjStr))
                            EmitReification(handler, reifyIdNonSelf, subjectStr, predicateStr, listObjStr);
                        continue;
                    }
                }

                // Check for nested description (skip comments/PIs first)
                SkipWhitespace();
                while (Peek() == '<' && (PeekAhead(1) == '!' || PeekAhead(1) == '?'))
                {
                    TryConsume('<');
                    if (Peek() == '!')
                        SkipCommentOrCData();
                    else if (Peek() == '?')
                        SkipProcessingInstruction();
                    SkipWhitespace();
                }

                if (Peek() == '<' && PeekAhead(1) != '/')
                {
                    // Nested element - could be nested description
                    var nestedObjStr = await ParseNestedObjectAsync(handler, effectiveLangNonSelf, cancellationToken);
                    if (!string.IsNullOrEmpty(nestedObjStr))
                    {
                        handler(subjectStr.AsSpan(), predicateStr.AsSpan(), nestedObjStr.AsSpan());
                        if (reifyIdNonSelf != null)
                            EmitReification(handler, reifyIdNonSelf, subjectStr, predicateStr, nestedObjStr);
                    }
                    SkipToClosingTag(propNameStr);
                    continue;
                }

                // Non-self-closing property element with property attributes (but no nested element)
                // Creates an implicit blank node as the object
                if (HasPropertyAttributes(propAttributes))
                {
                    ResetOutputBuffer();
                    var blankNode = GenerateBlankNode();
                    var blankNodeStr = blankNode.ToString();
                    handler(subjectStr.AsSpan(), predicateStr.AsSpan(), blankNode);
                    EmitPropertyAttributes(handler, blankNodeStr, propAttributes, effectiveLangNonSelf);
                    if (reifyIdNonSelf != null)
                        EmitReification(handler, reifyIdNonSelf, subjectStr, predicateStr, blankNodeStr);
                    SkipToClosingTag(propNameStr);
                    continue;
                }

                // Plain literal content
                ResetOutputBuffer();
                var literalContent = ParseTextContent();

                // Check for datatype or language (inherit xml:lang if not specified)
                ReadOnlySpan<char> obj2;
                if (propAttributes.TryGetValue("rdf:datatype", out var datatype) ||
                    propAttributes.TryGetValue("datatype", out datatype))
                {
                    obj2 = BuildTypedLiteral(literalContent, datatype.AsSpan());
                }
                else if (!string.IsNullOrEmpty(effectiveLangNonSelf))
                {
                    obj2 = BuildLangLiteral(literalContent, effectiveLangNonSelf.AsSpan());
                }
                else
                {
                    obj2 = BuildPlainLiteral(literalContent);
                }

                var obj2Str = obj2.ToString();
                handler(subjectStr.AsSpan(), predicateStr.AsSpan(), obj2);
                if (reifyIdNonSelf != null)
                    EmitReification(handler, reifyIdNonSelf, subjectStr, predicateStr, obj2Str);
                SkipToClosingTag(propNameStr);
            }
            else if (IsEndOfInput())
            {
                break;
            }
            else
            {
                Consume(); // Skip text content between elements
            }
        }
    }

    /// <summary>
    /// Parse nested object element (returns subject of nested description as string).
    /// </summary>
    private async Task<string> ParseNestedObjectAsync(
        TripleHandler handler,
        string? inheritedLang,
        CancellationToken cancellationToken)
    {
        if (!TryConsume('<'))
            return string.Empty;

        var elementName = ParseQName();
        if (elementName.IsEmpty)
            return string.Empty;

        var elementNameStr = elementName.ToString();
        var attributes = ParseAttributes();

        SplitQName(elementNameStr, out var prefix, out var localName);
        var namespaceUri = ResolveNamespace(prefix);

        // Validate node element name (unless it's rdf:Description)
        if (namespaceUri != RdfNamespace || localName != "Description")
        {
            ValidateNodeElementName(namespaceUri, localName);
        }

        // Validate node element attributes
        ValidateNodeElementAttributes(attributes);

        SkipWhitespace();
        bool selfClosing = TryConsume('/');
        TryConsume('>');

        // Push xml:base scope if present
        bool pushedBase = PushBaseIfPresent(attributes);

        // Get subject for nested element
        var subjectStr = DetermineSubjectString(attributes);

        // If typed node, emit type triple
        if (namespaceUri != RdfNamespace || localName != "Description")
        {
            ResetOutputBuffer();
            var typeUri = BuildIri(namespaceUri.AsSpan(), localName.AsSpan());
            handler(subjectStr.AsSpan(), $"<{RdfNamespace}type>".AsSpan(), typeUri);
        }

        // Get xml:lang (inherit from parent if not specified)
        attributes.TryGetValue("xml:lang", out var xmlLang);
        var effectiveLang = xmlLang ?? inheritedLang;

        // Process property attributes
        EmitPropertyAttributes(handler, subjectStr, attributes, effectiveLang);

        if (!selfClosing)
        {
            // Parse nested properties
            await ParsePropertyElementsAsync(handler, subjectStr, effectiveLang, cancellationToken);
        }

        // Pop xml:base scope if we pushed one
        if (pushedBase)
            PopBase();

        return subjectStr;
    }

    /// <summary>
    /// Parse rdf:parseType="Collection" content.
    /// Returns the list head blank node (or rdf:nil for empty collections) for reification.
    /// </summary>
    private async Task<string> ParseCollectionAsync(
        TripleHandler handler,
        string subjectStr,
        string predicateStr,
        string? inheritedLang,
        CancellationToken cancellationToken)
    {
        var rdfFirst = $"<{RdfNamespace}first>";
        var rdfRest = $"<{RdfNamespace}rest>";
        var rdfNil = $"<{RdfNamespace}nil>";

        string currentNodeStr = string.Empty;
        string? firstNodeStr = null;  // Track first node for reification
        bool isFirst = true;

        while (!IsEndOfInput())
        {
            await RefillIfNeededAsync(cancellationToken);
            SkipWhitespace();

            if (Peek() == '<')
            {
                if (PeekAhead(1) == '/')
                {
                    // End of collection
                    break;
                }

                // Create list node
                ResetOutputBuffer();
                var listNode = GenerateBlankNode();
                var listNodeStr = listNode.ToString();

                if (isFirst)
                {
                    handler(subjectStr.AsSpan(), predicateStr.AsSpan(), listNode);
                    firstNodeStr = listNodeStr;  // Remember first node for reification
                    isFirst = false;
                }
                else
                {
                    handler(currentNodeStr.AsSpan(), rdfRest.AsSpan(), listNode);
                }

                // Parse collection item
                var itemStr = await ParseNestedObjectAsync(handler, inheritedLang, cancellationToken);
                handler(listNodeStr.AsSpan(), rdfFirst.AsSpan(), itemStr.AsSpan());

                currentNodeStr = listNodeStr;
            }
            else if (IsEndOfInput())
            {
                break;
            }
            else
            {
                Consume();
            }
        }

        // End collection with rdf:nil
        if (!string.IsNullOrEmpty(currentNodeStr))
        {
            handler(currentNodeStr.AsSpan(), rdfRest.AsSpan(), rdfNil.AsSpan());
        }
        else if (isFirst)
        {
            // Empty collection
            handler(subjectStr.AsSpan(), predicateStr.AsSpan(), rdfNil.AsSpan());
            firstNodeStr = rdfNil;  // Empty collection - object is rdf:nil
        }

        // Skip closing tag
        SkipClosingTag();

        return firstNodeStr ?? rdfNil;
    }

    #region Subject Determination

    /// <summary>
    /// Push a new xml:base scope if the attribute is present.
    /// Returns true if a scope was pushed (caller must pop later).
    /// </summary>
    private bool PushBaseIfPresent(Dictionary<string, string> attributes)
    {
        if (attributes.TryGetValue("xml:base", out var xmlBase))
        {
            _baseUriStack.Push(_baseUri);
            // Resolve and store base, stripping any fragment per RFC 3986
            var resolved = ResolveUri(xmlBase);
            var hashIdx = resolved.IndexOf('#');
            _baseUri = hashIdx >= 0 ? resolved[..hashIdx] : resolved;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Pop the xml:base scope, restoring the previous base URI.
    /// </summary>
    private void PopBase()
    {
        if (_baseUriStack.Count > 0)
            _baseUri = _baseUriStack.Pop();
    }

    /// <summary>
    /// Determine subject from attributes (returns string for async boundary crossing).
    /// Note: xml:base is handled separately via PushBaseIfPresent.
    /// </summary>
    private string DetermineSubjectString(Dictionary<string, string> attributes)
    {
        if (attributes.TryGetValue("rdf:about", out var aboutUri) ||
            attributes.TryGetValue("about", out aboutUri))
        {
            return $"<{ResolveUri(aboutUri)}>";
        }

        if (attributes.TryGetValue("rdf:ID", out var id) ||
            attributes.TryGetValue("ID", out id))
        {
            // rdf:ID creates URI by appending #id to base URI
            var resolvedBase = string.IsNullOrEmpty(_baseUri) ? "" : _baseUri;
            return $"<{resolvedBase}#{id}>";
        }

        if (attributes.TryGetValue("rdf:nodeID", out var nodeId) ||
            attributes.TryGetValue("nodeID", out nodeId))
        {
            return $"_:{nodeId}";
        }

        // Generate anonymous blank node
        return $"_:b{_blankNodeCounter++}";
    }

    /// <summary>
    /// Resolve a URI reference against the base URI.
    /// </summary>
    private string ResolveUri(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return _baseUri;

        // Already absolute URI
        if (uri.Contains("://") || uri.StartsWith("urn:"))
            return uri;

        if (string.IsNullOrEmpty(_baseUri))
            return uri;

        // Fragment reference
        if (uri.StartsWith('#'))
        {
            // Remove any existing fragment from base
            var hashIdx = _baseUri.IndexOf('#');
            var baseWithoutFragment = hashIdx >= 0 ? _baseUri[..hashIdx] : _baseUri;
            return baseWithoutFragment + uri;
        }

        // Relative URI - resolve against base
        try
        {
            var baseUriObj = new Uri(_baseUri, UriKind.Absolute);
            var resolved = new Uri(baseUriObj, uri);
            return resolved.AbsoluteUri;
        }
        catch
        {
            // Fallback: simple concatenation
            return _baseUri + uri;
        }
    }

    /// <summary>
    /// Emit reification triples for a statement with rdf:ID.
    /// Creates: statementUri rdf:type rdf:Statement
    ///          statementUri rdf:subject subject
    ///          statementUri rdf:predicate predicate
    ///          statementUri rdf:object object
    /// </summary>
    private void EmitReification(
        TripleHandler handler,
        string statementId,
        string subjectStr,
        string predicateStr,
        string objectStr)
    {
        // Build statement URI from rdf:ID
        var statementUri = $"<{_baseUri}#{statementId}>";

        // rdf:type rdf:Statement
        ResetOutputBuffer();
        handler(statementUri.AsSpan(),
            $"<{RdfNamespace}type>".AsSpan(),
            $"<{RdfNamespace}Statement>".AsSpan());

        // rdf:subject
        handler(statementUri.AsSpan(),
            $"<{RdfNamespace}subject>".AsSpan(),
            subjectStr.AsSpan());

        // rdf:predicate
        handler(statementUri.AsSpan(),
            $"<{RdfNamespace}predicate>".AsSpan(),
            predicateStr.AsSpan());

        // rdf:object
        handler(statementUri.AsSpan(),
            $"<{RdfNamespace}object>".AsSpan(),
            objectStr.AsSpan());
    }

    /// <summary>
    /// Check if attributes dictionary contains any property attributes (non-RDF/XML namespace).
    /// </summary>
    private static bool HasPropertyAttributes(Dictionary<string, string> attributes)
    {
        foreach (var name in attributes.Keys)
        {
            // rdf:type is a property attribute
            if (name == "rdf:type" || name == "type")
                return true;

            // Skip other RDF/XML control attributes
            if (name.StartsWith("rdf:") || name.StartsWith("xml:") || name.StartsWith("xmlns"))
                continue;

            // Skip namespace declarations
            if (name.Contains(':'))
            {
                var colonIdx = name.IndexOf(':');
                var prefix = name[..colonIdx];
                if (prefix == "xmlns")
                    continue;

                // Found a property attribute
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Emit triples for property attributes (shorthand syntax).
    /// </summary>
    private void EmitPropertyAttributes(
        TripleHandler handler,
        string subjectStr,
        Dictionary<string, string> attributes,
        string? xmlLang = null)
    {
        // RDF control attributes that are NOT property attributes
        var rdfControlAttrs = new HashSet<string>
        {
            "about", "ID", "nodeID", "resource", "parseType", "datatype"
        };

        foreach (var (name, value) in attributes)
        {
            // Handle rdf:type specially - value is a URI, not a literal
            if (name == "rdf:type" || name == "type")
            {
                ResetOutputBuffer();
                var predicate = $"<{RdfNamespace}type>".AsSpan();
                var resolvedUri = ResolveUri(value);
                var obj = WrapIri(resolvedUri.AsSpan());
                handler(subjectStr.AsSpan(), predicate, obj);
                continue;
            }

            // Skip XML/namespace control attributes
            if (name.StartsWith("xml:") || name.StartsWith("xmlns"))
                continue;

            // Skip if it's a namespace declaration
            if (name.Contains(':'))
            {
                var colonIdx = name.IndexOf(':');
                var prefix = name[..colonIdx];
                if (prefix == "xmlns")
                    continue;

                // Check if this is an RDF control attribute (not a property attribute)
                if (prefix == "rdf")
                {
                    var local = name[(colonIdx + 1)..];
                    if (rdfControlAttrs.Contains(local))
                        continue;
                }

                // Property attribute (including rdf:Seq, rdf:Bag, rdf:Alt, rdf:value, etc.)
                var ns = ResolveNamespace(prefix);
                var localName = name[(colonIdx + 1)..];

                ResetOutputBuffer();
                var predicate = BuildIri(ns.AsSpan(), localName.AsSpan());

                // Use xml:lang if present
                ReadOnlySpan<char> literal;
                if (!string.IsNullOrEmpty(xmlLang))
                    literal = BuildLangLiteral(value.AsSpan(), xmlLang.AsSpan());
                else
                    literal = BuildPlainLiteral(value.AsSpan());

                handler(subjectStr.AsSpan(), predicate, literal);
            }
        }
    }

    #endregion

    #region IRI and Literal Building

    /// <summary>
    /// Build full IRI from namespace and local name.
    /// </summary>
    private ReadOnlySpan<char> BuildIri(ReadOnlySpan<char> namespaceUri, ReadOnlySpan<char> localName)
    {
        int start = _outputOffset;
        AppendToOutput('<');
        AppendToOutput(namespaceUri);
        AppendToOutput(localName);
        AppendToOutput('>');
        return GetOutputSpan(start);
    }

    /// <summary>
    /// Wrap URI in angle brackets.
    /// </summary>
    private ReadOnlySpan<char> WrapIri(ReadOnlySpan<char> uri)
    {
        int start = _outputOffset;
        AppendToOutput('<');
        AppendToOutput(uri);
        AppendToOutput('>');
        return GetOutputSpan(start);
    }

    /// <summary>
    /// Build blank node identifier.
    /// </summary>
    private ReadOnlySpan<char> BuildBlankNode(ReadOnlySpan<char> id)
    {
        int start = _outputOffset;
        AppendToOutput("_:".AsSpan());
        AppendToOutput(id);
        return GetOutputSpan(start);
    }

    /// <summary>
    /// Generate new blank node.
    /// </summary>
    private ReadOnlySpan<char> GenerateBlankNode()
    {
        int start = _outputOffset;
        AppendToOutput("_:b".AsSpan());
        AppendToOutput(_blankNodeCounter++.ToString().AsSpan());
        return GetOutputSpan(start);
    }

    /// <summary>
    /// Build plain literal. Value is NOT escaped - internal representation uses actual characters.
    /// </summary>
    private ReadOnlySpan<char> BuildPlainLiteral(ReadOnlySpan<char> value)
    {
        int start = _outputOffset;
        AppendToOutput('"');
        AppendToOutput(value);  // No escaping - internal representation uses actual characters
        AppendToOutput('"');
        return GetOutputSpan(start);
    }

    /// <summary>
    /// Build language-tagged literal. Value is NOT escaped - internal representation uses actual characters.
    /// </summary>
    private ReadOnlySpan<char> BuildLangLiteral(ReadOnlySpan<char> value, ReadOnlySpan<char> lang)
    {
        int start = _outputOffset;
        AppendToOutput('"');
        AppendToOutput(value);  // No escaping - internal representation uses actual characters
        AppendToOutput('"');
        AppendToOutput('@');
        AppendToOutput(lang);
        return GetOutputSpan(start);
    }

    /// <summary>
    /// Build typed literal. Value is NOT escaped - internal representation uses actual characters.
    /// N-Triples escaping is only for serialization, not internal representation.
    /// </summary>
    private ReadOnlySpan<char> BuildTypedLiteral(ReadOnlySpan<char> value, ReadOnlySpan<char> datatype)
    {
        int start = _outputOffset;
        AppendToOutput('"');
        AppendToOutput(value);  // No escaping - internal representation uses actual characters
        AppendToOutput('"');
        AppendToOutput("^^<".AsSpan());
        AppendToOutput(datatype);
        AppendToOutput('>');
        return GetOutputSpan(start);
    }

    /// <summary>
    /// Append string with N-Triples escaping.
    /// </summary>
    private void AppendEscapedString(ReadOnlySpan<char> value)
    {
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': AppendToOutput('\\'); AppendToOutput('"'); break;
                case '\\': AppendToOutput('\\'); AppendToOutput('\\'); break;
                case '\n': AppendToOutput('\\'); AppendToOutput('n'); break;
                case '\r': AppendToOutput('\\'); AppendToOutput('r'); break;
                case '\t': AppendToOutput('\\'); AppendToOutput('t'); break;
                default: AppendToOutput(ch); break;
            }
        }
    }

    #endregion

    #region Namespace Resolution

    /// <summary>
    /// Resolve namespace prefix to URI.
    /// </summary>
    private string ResolveNamespace(string prefix)
    {
        // For elements without prefix, look up default namespace (empty key)
        var key = prefix ?? string.Empty;

        if (_namespaces.TryGetValue(key, out var uri))
            return uri;

        return string.Empty;
    }

    /// <summary>
    /// Split QName into prefix and local name.
    /// </summary>
    private static void SplitQName(string qname, out string prefix, out string localName)
    {
        var colonIdx = qname.IndexOf(':');
        if (colonIdx < 0)
        {
            prefix = string.Empty;
            localName = qname;
        }
        else
        {
            prefix = qname[..colonIdx];
            localName = qname[(colonIdx + 1)..];
        }
    }

    #endregion

    #region Validation

    /// <summary>
    /// Validate that a value is a valid XML NCName (for rdf:ID and rdf:nodeID).
    /// NCName must start with a letter or underscore, followed by letters, digits,
    /// hyphens, underscores, or periods. It must NOT contain colons.
    /// </summary>
    private void ValidateNCName(string value, string attributeName)
    {
        if (string.IsNullOrEmpty(value))
            throw ParserException($"{attributeName} cannot be empty");

        var firstChar = value[0];
        // NCName starts with Letter or '_'
        if (!IsNCNameStartChar(firstChar))
            throw ParserException($"{attributeName} value '{value}' is not a valid NCName: must start with a letter or underscore");

        for (int i = 1; i < value.Length; i++)
        {
            if (!IsNCNameChar(value[i]))
                throw ParserException($"{attributeName} value '{value}' is not a valid NCName: invalid character '{value[i]}' at position {i}");
        }
    }

    private static bool IsNCNameStartChar(char ch)
    {
        // Letters and underscore (no colon for NCName)
        return ch == '_' ||
               (ch >= 'A' && ch <= 'Z') ||
               (ch >= 'a' && ch <= 'z') ||
               (ch >= 0xC0 && ch <= 0xD6) ||
               (ch >= 0xD8 && ch <= 0xF6) ||
               (ch >= 0xF8 && ch <= 0x2FF) ||
               (ch >= 0x370 && ch <= 0x37D) ||
               (ch >= 0x37F && ch <= 0x1FFF) ||
               (ch >= 0x200C && ch <= 0x200D) ||
               (ch >= 0x2070 && ch <= 0x218F) ||
               (ch >= 0x2C00 && ch <= 0x2FEF) ||
               (ch >= 0x3001 && ch <= 0xD7FF) ||
               (ch >= 0xF900 && ch <= 0xFDCF) ||
               (ch >= 0xFDF0 && ch <= 0xFFFD);
    }

    private static bool IsNCNameChar(char ch)
    {
        return IsNCNameStartChar(ch) ||
               ch == '-' || ch == '.' ||
               (ch >= '0' && ch <= '9') ||
               ch == 0xB7 ||
               (ch >= 0x300 && ch <= 0x36F) ||
               (ch >= 0x203F && ch <= 0x2040);
    }

    /// <summary>
    /// Validate node element attributes for conflicts and prohibited attributes.
    /// </summary>
    private void ValidateNodeElementAttributes(Dictionary<string, string> attributes)
    {
        // Check for deprecated/removed attributes
        if (attributes.ContainsKey("rdf:aboutEach") || attributes.ContainsKey("aboutEach"))
            throw ParserException("rdf:aboutEach is not allowed (removed from RDF 1.0)");

        if (attributes.ContainsKey("rdf:aboutEachPrefix") || attributes.ContainsKey("aboutEachPrefix"))
            throw ParserException("rdf:aboutEachPrefix is not allowed (removed from RDF 1.0)");

        if (attributes.ContainsKey("rdf:bagID") || attributes.ContainsKey("bagID"))
            throw ParserException("rdf:bagID is not allowed (removed from RDF 1.0)");

        // Check for rdf:li as attribute (only allowed as element)
        if (attributes.ContainsKey("rdf:li") || attributes.ContainsKey("li"))
            throw ParserException("rdf:li is not allowed as an attribute");

        // Validate rdf:ID if present
        if (attributes.TryGetValue("rdf:ID", out var id) || attributes.TryGetValue("ID", out id))
        {
            ValidateNCName(id, "rdf:ID");
            // Check for duplicate IDs (uniqueness based on resolved URI, not just ID value)
            // Two identical rdf:ID values are allowed if xml:base makes them resolve to different URIs
            // Need to consider this element's xml:base if present
            var effectiveBase = _baseUri;
            if (attributes.TryGetValue("xml:base", out var xmlBase))
            {
                // Resolve the element's xml:base against current base
                effectiveBase = ResolveUri(xmlBase);
                var hashIdx = effectiveBase.IndexOf('#');
                effectiveBase = hashIdx >= 0 ? effectiveBase[..hashIdx] : effectiveBase;
            }
            var fullUri = $"{effectiveBase}#{id}";
            if (!_usedIds.Add(fullUri))
                throw ParserException($"Duplicate rdf:ID '{id}' - resolves to same URI '{fullUri}'");
        }

        // Validate rdf:nodeID if present
        if (attributes.TryGetValue("rdf:nodeID", out var nodeId) || attributes.TryGetValue("nodeID", out nodeId))
            ValidateNCName(nodeId, "rdf:nodeID");

        // Check for conflicting subject attributes
        bool hasAbout = attributes.ContainsKey("rdf:about") || attributes.ContainsKey("about");
        bool hasID = attributes.ContainsKey("rdf:ID") || attributes.ContainsKey("ID");
        bool hasNodeID = attributes.ContainsKey("rdf:nodeID") || attributes.ContainsKey("nodeID");

        int subjectAttrs = (hasAbout ? 1 : 0) + (hasID ? 1 : 0) + (hasNodeID ? 1 : 0);
        if (subjectAttrs > 1)
            throw ParserException("Cannot have more than one of rdf:about, rdf:ID, or rdf:nodeID on a node element");
    }

    // RDF terms that are NEVER allowed as node element names (inside rdf:RDF)
    private static readonly HashSet<string> ForbiddenNodeElementNames = new(StringComparer.Ordinal)
    {
        "RDF", "ID", "about", "bagID", "parseType", "resource", "nodeID",
        "datatype", "aboutEach", "aboutEachPrefix", "li"
    };

    // RDF terms that are NEVER allowed as property element names
    private static readonly HashSet<string> ForbiddenPropertyElementNames = new(StringComparer.Ordinal)
    {
        "RDF", "Description", "ID", "about", "bagID", "parseType", "resource", "nodeID",
        "datatype", "aboutEach", "aboutEachPrefix"
        // Note: rdf:li IS allowed as property element (becomes rdf:_1, rdf:_2, etc.)
    };

    /// <summary>
    /// Validate that a node element name is allowed.
    /// Called when parsing typed node elements (not rdf:Description).
    /// </summary>
    private void ValidateNodeElementName(string namespaceUri, string localName)
    {
        if (namespaceUri == RdfNamespace && ForbiddenNodeElementNames.Contains(localName))
        {
            throw ParserException($"rdf:{localName} is forbidden as a node element name");
        }
    }

    /// <summary>
    /// Validate that a property element name is allowed.
    /// </summary>
    private void ValidatePropertyElementName(string namespaceUri, string localName)
    {
        if (namespaceUri == RdfNamespace && ForbiddenPropertyElementNames.Contains(localName))
        {
            throw ParserException($"rdf:{localName} is forbidden as a property element name");
        }
    }

    /// <summary>
    /// Validate property element attributes for conflicts.
    /// </summary>
    private void ValidatePropertyElementAttributes(Dictionary<string, string> attributes)
    {
        // rdf:bagID is not allowed (deprecated)
        if (attributes.ContainsKey("rdf:bagID") || attributes.ContainsKey("bagID"))
            throw ParserException("rdf:bagID is not allowed (removed from RDF 1.0)");

        bool hasResource = attributes.ContainsKey("rdf:resource") || attributes.ContainsKey("resource");
        bool hasNodeID = attributes.ContainsKey("rdf:nodeID") || attributes.ContainsKey("nodeID");
        bool hasParseType = attributes.ContainsKey("rdf:parseType") || attributes.ContainsKey("parseType");
        bool hasDatatype = attributes.ContainsKey("rdf:datatype") || attributes.ContainsKey("datatype");

        // Validate rdf:ID if present (also used for reification on property elements)
        if (attributes.TryGetValue("rdf:ID", out var id) || attributes.TryGetValue("ID", out id))
        {
            ValidateNCName(id, "rdf:ID");
            // Check for duplicate IDs (uniqueness based on resolved URI, not just ID value)
            var resolvedBase = string.IsNullOrEmpty(_baseUri) ? "" : _baseUri;
            var fullUri = $"{resolvedBase}#{id}";
            if (!_usedIds.Add(fullUri))
                throw ParserException($"Duplicate rdf:ID '{id}' - resolves to same URI '{fullUri}'");
        }

        // Validate rdf:nodeID if present
        if (attributes.TryGetValue("rdf:nodeID", out var nodeId) || attributes.TryGetValue("nodeID", out nodeId))
            ValidateNCName(nodeId, "rdf:nodeID");

        // rdf:resource and rdf:nodeID are mutually exclusive
        if (hasResource && hasNodeID)
            throw ParserException("Cannot have both rdf:resource and rdf:nodeID on a property element");

        // rdf:parseType conflicts with rdf:resource, rdf:nodeID, and rdf:datatype
        if (hasParseType && hasResource)
            throw ParserException("Cannot have both rdf:parseType and rdf:resource on a property element");

        if (hasParseType && hasNodeID)
            throw ParserException("Cannot have both rdf:parseType and rdf:nodeID on a property element");

        if (hasParseType && hasDatatype)
            throw ParserException("Cannot have both rdf:parseType and rdf:datatype on a property element");

        // rdf:datatype conflicts with rdf:resource and rdf:nodeID
        if (hasDatatype && hasResource)
            throw ParserException("Cannot have both rdf:datatype and rdf:resource on a property element");

        if (hasDatatype && hasNodeID)
            throw ParserException("Cannot have both rdf:datatype and rdf:nodeID on a property element");
    }

    #endregion

    #region Disposal

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _bufferManager.Return(_inputBuffer);
        _bufferManager.Return(_outputBuffer);
        _isDisposed = true;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    #endregion
}
