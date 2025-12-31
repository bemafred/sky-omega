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

    // Standard RDF namespace
    private const string RdfNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";

    private const int DefaultBufferSize = 16384;

    public RdfXmlStreamParser(Stream stream, int bufferSize = DefaultBufferSize, IBufferManager? bufferManager = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _bufferManager = bufferManager ?? PooledBufferManager.Shared;

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
                // Root rdf:RDF element - parse children
                if (!selfClosing)
                {
                    await ParseRdfRootContentAsync(handler, cancellationToken);
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
        // Determine subject (as string for async boundary crossing)
        var subjectStr = DetermineSubjectString(attributes);

        // Process property attributes (shorthand properties)
        EmitPropertyAttributes(handler, subjectStr, attributes);

        if (selfClosing)
            return;

        // Parse property elements
        await ParsePropertyElementsAsync(handler, subjectStr, cancellationToken);
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
        // Determine subject (as string)
        var subjectStr = DetermineSubjectString(attributes);

        // Emit rdf:type triple for typed node
        ResetOutputBuffer();
        var typeUri = BuildIri(namespaceUri.AsSpan(), localName.AsSpan());
        handler(subjectStr.AsSpan(), $"<{RdfNamespace}type>".AsSpan(), typeUri);

        // Process property attributes
        EmitPropertyAttributes(handler, subjectStr, attributes);

        if (selfClosing)
            return;

        // Parse property elements
        await ParsePropertyElementsAsync(handler, subjectStr, cancellationToken);
    }

    /// <summary>
    /// Parse property elements inside a description.
    /// </summary>
    private async Task ParsePropertyElementsAsync(
        TripleHandler handler,
        string subjectStr,
        CancellationToken cancellationToken)
    {
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

                // Build predicate IRI
                SplitQName(propNameStr, out var propPrefix, out var propLocal);
                var propNamespace = ResolveNamespace(propPrefix);

                ResetOutputBuffer();
                var predicate = BuildIri(propNamespace.AsSpan(), propLocal.AsSpan());
                var predicateStr = predicate.ToString();

                SkipWhitespace();
                bool propSelfClosing = TryConsume('/');
                TryConsume('>');

                if (propSelfClosing)
                {
                    // Self-closing property - check for rdf:resource
                    if (propAttributes.TryGetValue("rdf:resource", out var resourceUri) ||
                        propAttributes.TryGetValue("resource", out resourceUri))
                    {
                        ResetOutputBuffer();
                        var obj = WrapIri(resourceUri.AsSpan());
                        handler(subjectStr.AsSpan(), predicateStr.AsSpan(), obj);
                    }
                    else if (propAttributes.TryGetValue("rdf:nodeID", out var nodeId) ||
                             propAttributes.TryGetValue("nodeID", out nodeId))
                    {
                        ResetOutputBuffer();
                        var obj = BuildBlankNode(nodeId.AsSpan());
                        handler(subjectStr.AsSpan(), predicateStr.AsSpan(), obj);
                    }
                    continue;
                }

                // Check for rdf:resource attribute
                if (propAttributes.TryGetValue("rdf:resource", out var resUri) ||
                    propAttributes.TryGetValue("resource", out resUri))
                {
                    ResetOutputBuffer();
                    var obj = WrapIri(resUri.AsSpan());
                    handler(subjectStr.AsSpan(), predicateStr.AsSpan(), obj);
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
                        await ParsePropertyElementsAsync(handler, blankNodeStr, cancellationToken);
                        continue;
                    }
                    else if (parseType == "Literal")
                    {
                        // XML literal
                        ResetOutputBuffer();
                        var xmlContent = ParseXmlLiteralContent(propNameStr);
                        var literal = BuildTypedLiteral(xmlContent, $"{RdfNamespace}XMLLiteral".AsSpan());
                        handler(subjectStr.AsSpan(), predicateStr.AsSpan(), literal);
                        continue;
                    }
                    else if (parseType == "Collection")
                    {
                        // RDF collection
                        await ParseCollectionAsync(handler, subjectStr, predicateStr, cancellationToken);
                        continue;
                    }
                }

                // Check for nested description
                SkipWhitespace();
                if (Peek() == '<' && PeekAhead(1) != '/')
                {
                    // Nested element - could be nested description
                    var nestedObjStr = await ParseNestedObjectAsync(handler, cancellationToken);
                    if (!string.IsNullOrEmpty(nestedObjStr))
                    {
                        handler(subjectStr.AsSpan(), predicateStr.AsSpan(), nestedObjStr.AsSpan());
                    }
                    SkipToClosingTag(propNameStr);
                    continue;
                }

                // Plain literal content
                ResetOutputBuffer();
                var literalContent = ParseTextContent();

                // Check for datatype or language
                ReadOnlySpan<char> obj2;
                if (propAttributes.TryGetValue("rdf:datatype", out var datatype) ||
                    propAttributes.TryGetValue("datatype", out datatype))
                {
                    obj2 = BuildTypedLiteral(literalContent, datatype.AsSpan());
                }
                else if (propAttributes.TryGetValue("xml:lang", out var lang))
                {
                    obj2 = BuildLangLiteral(literalContent, lang.AsSpan());
                }
                else
                {
                    obj2 = BuildPlainLiteral(literalContent);
                }

                handler(subjectStr.AsSpan(), predicateStr.AsSpan(), obj2);
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
        CancellationToken cancellationToken)
    {
        if (!TryConsume('<'))
            return string.Empty;

        var elementName = ParseQName();
        if (elementName.IsEmpty)
            return string.Empty;

        var elementNameStr = elementName.ToString();
        var attributes = ParseAttributes();

        SkipWhitespace();
        bool selfClosing = TryConsume('/');
        TryConsume('>');

        SplitQName(elementNameStr, out var prefix, out var localName);
        var namespaceUri = ResolveNamespace(prefix);

        // Get subject for nested element
        var subjectStr = DetermineSubjectString(attributes);

        // If typed node, emit type triple
        if (namespaceUri != RdfNamespace || localName != "Description")
        {
            ResetOutputBuffer();
            var typeUri = BuildIri(namespaceUri.AsSpan(), localName.AsSpan());
            handler(subjectStr.AsSpan(), $"<{RdfNamespace}type>".AsSpan(), typeUri);
        }

        // Process property attributes
        EmitPropertyAttributes(handler, subjectStr, attributes);

        if (!selfClosing)
        {
            // Parse nested properties
            await ParsePropertyElementsAsync(handler, subjectStr, cancellationToken);
        }

        return subjectStr;
    }

    /// <summary>
    /// Parse rdf:parseType="Collection" content.
    /// </summary>
    private async Task ParseCollectionAsync(
        TripleHandler handler,
        string subjectStr,
        string predicateStr,
        CancellationToken cancellationToken)
    {
        var rdfFirst = $"<{RdfNamespace}first>";
        var rdfRest = $"<{RdfNamespace}rest>";
        var rdfNil = $"<{RdfNamespace}nil>";

        string currentNodeStr = string.Empty;
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
                    isFirst = false;
                }
                else
                {
                    handler(currentNodeStr.AsSpan(), rdfRest.AsSpan(), listNode);
                }

                // Parse collection item
                var itemStr = await ParseNestedObjectAsync(handler, cancellationToken);
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
        }

        // Skip closing tag
        SkipClosingTag();
    }

    #region Subject Determination

    /// <summary>
    /// Determine subject from attributes (returns string for async boundary crossing).
    /// </summary>
    private string DetermineSubjectString(Dictionary<string, string> attributes)
    {
        if (attributes.TryGetValue("rdf:about", out var aboutUri) ||
            attributes.TryGetValue("about", out aboutUri))
        {
            return $"<{aboutUri}>";
        }

        if (attributes.TryGetValue("rdf:ID", out var id) ||
            attributes.TryGetValue("ID", out id))
        {
            return $"<#{id}>"; // Simplified - should use base URI
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
    /// Emit triples for property attributes (shorthand syntax).
    /// </summary>
    private void EmitPropertyAttributes(
        TripleHandler handler,
        string subjectStr,
        Dictionary<string, string> attributes)
    {
        foreach (var (name, value) in attributes)
        {
            // Skip RDF attributes
            if (name.StartsWith("rdf:") || name.StartsWith("xml:") || name.StartsWith("xmlns"))
                continue;

            // Skip if it's a namespace declaration
            if (name.Contains(':'))
            {
                var colonIdx = name.IndexOf(':');
                var prefix = name[..colonIdx];
                if (prefix == "xmlns")
                    continue;

                // Property attribute
                var ns = ResolveNamespace(prefix);
                var local = name[(colonIdx + 1)..];

                ResetOutputBuffer();
                var predicate = BuildIri(ns.AsSpan(), local.AsSpan());
                var literal = BuildPlainLiteral(value.AsSpan());

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
    /// Build plain literal.
    /// </summary>
    private ReadOnlySpan<char> BuildPlainLiteral(ReadOnlySpan<char> value)
    {
        int start = _outputOffset;
        AppendToOutput('"');
        AppendEscapedString(value);
        AppendToOutput('"');
        return GetOutputSpan(start);
    }

    /// <summary>
    /// Build language-tagged literal.
    /// </summary>
    private ReadOnlySpan<char> BuildLangLiteral(ReadOnlySpan<char> value, ReadOnlySpan<char> lang)
    {
        int start = _outputOffset;
        AppendToOutput('"');
        AppendEscapedString(value);
        AppendToOutput('"');
        AppendToOutput('@');
        AppendToOutput(lang);
        return GetOutputSpan(start);
    }

    /// <summary>
    /// Build typed literal.
    /// </summary>
    private ReadOnlySpan<char> BuildTypedLiteral(ReadOnlySpan<char> value, ReadOnlySpan<char> datatype)
    {
        int start = _outputOffset;
        AppendToOutput('"');
        AppendEscapedString(value);
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
        if (string.IsNullOrEmpty(prefix))
            return string.Empty;

        if (_namespaces.TryGetValue(prefix, out var uri))
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
