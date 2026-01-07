// TriGStreamParser.cs
// Zero-GC streaming TriG (RDF 1.1) parser
// Based on W3C RDF 1.1 TriG specification
// No external dependencies, only BCL
// .NET 10 / C# 14

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.NQuads;
using SkyOmega.Mercury.Runtime.Buffers;

namespace SkyOmega.Mercury.TriG;

/// <summary>
/// Zero-allocation streaming parser for RDF TriG format.
/// TriG is Turtle extended with named graph support.
///
/// TriG grammar (simplified):
/// [1] trigDoc       ::= (directive | block)*
/// [2] block         ::= triplesOrGraph | wrappedGraph
/// [3] triplesOrGraph ::= labelOrSubject (wrappedGraph | predicateObjectList '.')
/// [4] wrappedGraph  ::= '{' triplesBlock? '}'
/// [5] labelOrSubject ::= iri | BlankNode
///
/// For zero-GC parsing, use ParseAsync(QuadHandler).
/// The IAsyncEnumerable overload allocates strings for compatibility.
/// </summary>
/// <remarks>
/// <para><b>Thread Safety:</b> This class is NOT thread-safe. Each instance
/// maintains internal parsing state and buffers that cannot be shared across threads.
/// Create a separate instance per thread or serialize access with locking.</para>
/// <para><b>Usage Pattern:</b> Create one instance per stream. Dispose when done
/// to return pooled buffers.</para>
/// </remarks>
public sealed class TriGStreamParser : IDisposable, IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly IBufferManager _bufferManager;

    private byte[] _inputBuffer;
    private char[] _outputBuffer;
    private int _bufferPosition;
    private int _bufferLength;
    private bool _endOfStream;
    private bool _isDisposed;

    private int _outputOffset;
    private const int OutputBufferSize = 16384;

    // Parser state
    private string _baseUri;
    private readonly Dictionary<string, string> _namespaces;
    private int _blankNodeCounter;

    // Current graph context
    private int _currentGraphStart;
    private int _currentGraphLength;

    private int _line;
    private int _column;

    private const int DefaultBufferSize = 8192;

    /// <summary>
    /// Creates a TriG parser for the given stream.
    /// </summary>
    public TriGStreamParser(Stream stream, int bufferSize = DefaultBufferSize, IBufferManager? bufferManager = null)
        : this(stream, null, bufferSize, bufferManager)
    {
    }

    /// <summary>
    /// Creates a TriG parser with a document base URI for resolving relative IRIs.
    /// </summary>
    /// <param name="stream">The input stream containing TriG content.</param>
    /// <param name="documentBaseUri">The base URI for the document (typically the document URL).
    /// Used for resolving relative IRIs per RFC3986. Can be overridden by @base directives.</param>
    /// <param name="bufferSize">Buffer size for reading the stream.</param>
    /// <param name="bufferManager">Optional buffer manager for pooling.</param>
    public TriGStreamParser(Stream stream, string? documentBaseUri, int bufferSize = DefaultBufferSize, IBufferManager? bufferManager = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _bufferManager = bufferManager ?? PooledBufferManager.Shared;

        _inputBuffer = _bufferManager.Rent<byte>(bufferSize).Array!;
        _outputBuffer = _bufferManager.Rent<char>(OutputBufferSize).Array!;

        _namespaces = new Dictionary<string, string>();
        _baseUri = documentBaseUri ?? string.Empty;
        _blankNodeCounter = 0;

        _line = 1;
        _column = 1;

        InitializeStandardPrefixes();
    }

    private void InitializeStandardPrefixes()
    {
        // Include angle brackets for consistency with parsed prefixes
        _namespaces["rdf"] = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#>";
        _namespaces["rdfs"] = "<http://www.w3.org/2000/01/rdf-schema#>";
        _namespaces["xsd"] = "<http://www.w3.org/2001/XMLSchema#>";
    }

    /// <summary>
    /// Parse TriG document with zero allocations.
    /// Quads are emitted via the handler callback with spans valid only during the call.
    /// </summary>
    public async Task ParseAsync(QuadHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);

        await FillBufferAsync(cancellationToken);

        while (!IsEndOfInput())
        {
            SkipWhitespaceAndComments();

            if (IsEndOfInput())
                break;

            var remaining = _bufferLength - _bufferPosition;
            if (remaining < _inputBuffer.Length / 4 && !_endOfStream)
            {
                await FillBufferAsync(cancellationToken);
                SkipWhitespaceAndComments();
            }

            if (IsEndOfInput())
                break;

            // Try directive first (@prefix, @base, PREFIX, BASE)
            if (TryParseDirective())
                continue;

            // Parse block (graph or triples)
            ParseBlock(handler);
        }
    }

    /// <summary>
    /// Parse TriG document and yield RDF quads (allocates strings).
    /// </summary>
    public async IAsyncEnumerable<RdfQuad> ParseAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var quads = new List<RdfQuad>();

        await ParseAsync((s, p, o, g) =>
        {
            quads.Add(new RdfQuad(s.ToString(), p.ToString(), o.ToString(),
                g.IsEmpty ? null : g.ToString()));
        }, cancellationToken);

        foreach (var quad in quads)
        {
            yield return quad;
        }
    }

    #region Block Parsing

    private void ParseBlock(QuadHandler handler)
    {
        ResetOutputBuffer();

        // Check if this is an anonymous/default graph block: { ... }
        if (Peek() == '{')
        {
            Consume();
            _currentGraphStart = 0;
            _currentGraphLength = 0;

            ParseTriplesBlock(handler);

            SkipWhitespaceAndComments();
            if (!TryConsume('}'))
                throw ParserException("Expected '}' to close graph block");

            return;
        }

        // Check if this is a GRAPH keyword
        if (MatchKeyword("GRAPH"))
        {
            SkipWhitespaceAndComments();

            // Parse graph IRI
            var graphIri = ParseIriOrPrefixedName();
            if (graphIri.IsEmpty)
                throw ParserException("Expected graph IRI after GRAPH");

            _currentGraphStart = 0;
            _currentGraphLength = graphIri.Length;
            graphIri.CopyTo(_outputBuffer);
            _outputOffset = graphIri.Length;

            SkipWhitespaceAndComments();

            // Expect '{'
            if (!TryConsume('{'))
                throw ParserException("Expected '{' after graph IRI");

            // Parse triples inside graph
            ParseTriplesBlock(handler);

            // Expect '}'
            SkipWhitespaceAndComments();
            if (!TryConsume('}'))
                throw ParserException("Expected '}' to close graph block");

            // GRAPH blocks should NOT be followed by '.'
            SkipWhitespaceAndComments();
            if (Peek() == '.')
                throw ParserException("GRAPH block must not be followed by '.'");

            // Clear graph context
            _currentGraphStart = 0;
            _currentGraphLength = 0;
        }
        else
        {
            // Check for collection - not allowed as graph label
            if (Peek() == '(')
            {
                // This could be a collection in a triple, but not as graph name
                // We need to check if it's followed by '{' - that would be invalid
                // Parse collection and check what follows
                var coll = ParseCollectionWithHandler(ReadOnlySpan<char>.Empty, null);
                SkipWhitespaceAndComments();
                if (Peek() == '{')
                {
                    throw ParserException("A graph may not be named with a collection");
                }
                // Check for free-standing list (list followed by just '.' is invalid)
                if (Peek() == '.')
                {
                    throw ParserException("Collection used as subject requires a predicate-object list");
                }
                // Otherwise this is a collection as subject in default graph
                ParsePredicateObjectList(coll, ReadOnlySpan<char>.Empty, handler);
                SkipWhitespaceAndComments();
                TryConsume('.');
                return;
            }

            // Could be: IRI { } (shorthand graph) or triples in default graph
            var term = ParseSubjectOrGraphLabel();

            if (term.IsEmpty)
            {
                // Check if it's a blankNodePropertyList [...] which can't be a graph label
                if (Peek() == '[')
                {
                    // Parse as subject in default graph
                    term = ParseSubjectWithHandler(ReadOnlySpan<char>.Empty, handler);
                    if (term.IsEmpty)
                    {
                        SkipToEndOfStatement();
                        return;
                    }
                    SkipWhitespaceAndComments();
                    // Check if followed by '{' - that's an error
                    if (Peek() == '{')
                    {
                        throw ParserException("A graph may not be named with a blankNodePropertyList");
                    }
                    // Parse rest of predicate-object list if any
                    var ch = Peek();
                    if (ch != '.' && ch != '}' && ch != -1)
                    {
                        ParsePredicateObjectList(term, ReadOnlySpan<char>.Empty, handler);
                    }
                    SkipWhitespaceAndComments();
                    TryConsume('.');
                    return;
                }
                SkipToEndOfStatement();
                return;
            }

            SkipWhitespaceAndComments();

            if (Peek() == '{')
            {
                // This is a graph block with shorthand syntax: <iri> { ... }
                Consume();

                _currentGraphStart = 0;
                _currentGraphLength = term.Length;
                // term is already in output buffer at offset 0

                ParseTriplesBlock(handler);

                SkipWhitespaceAndComments();
                if (!TryConsume('}'))
                    throw ParserException("Expected '}' to close graph block");

                _currentGraphStart = 0;
                _currentGraphLength = 0;
            }
            else
            {
                // This is triples in default graph
                // Check if subject has no predicate (like N-Quads graph IRI without braces)
                SkipWhitespaceAndComments();
                var nextCh = Peek();
                if (nextCh == '.' || nextCh == -1)
                    throw ParserException("Subject requires a predicate-object list (TriG requires {} for graphs, not N-Quads syntax)");

                // term is the subject, continue parsing predicate-object list
                ParsePredicateObjectList(term, ReadOnlySpan<char>.Empty, handler);

                SkipWhitespaceAndComments();
                TryConsume('.');
            }
        }
    }

    private void ParseTriplesBlock(QuadHandler handler)
    {
        var graph = _currentGraphLength > 0
            ? _outputBuffer.AsSpan(_currentGraphStart, _currentGraphLength)
            : ReadOnlySpan<char>.Empty;

        // Copy graph to stable storage since output buffer will be reused
        var graphStr = graph.IsEmpty ? null : graph.ToString();

        while (true)
        {
            SkipWhitespaceAndComments();

            if (IsEndOfInput() || Peek() == '}')
                break;

            // Get graph span (may need to reconstruct from string)
            var graphSpan = graphStr != null ? graphStr.AsSpan() : ReadOnlySpan<char>.Empty;

            // Check for free-standing collection (not allowed without predicate-object)
            if (Peek() == '(')
            {
                // Parse the collection
                var coll = ParseCollectionWithHandler(graphSpan, handler);
                SkipWhitespaceAndComments();
                var ch = Peek();
                if (ch == '.' || ch == '}' || ch == -1)
                    throw ParserException("Collection used as subject requires a predicate-object list");
                // Parse predicate-object list
                ParsePredicateObjectList(coll, graphSpan, handler);
                SkipWhitespaceAndComments();
                TryConsume('.');
                continue;
            }

            var subject = ParseSubjectWithHandler(graphSpan, handler);
            if (subject.IsEmpty)
                break;

            SkipWhitespaceAndComments();

            // Check for sole blankNodePropertyList (just [ ... ] without predicate-object)
            // Only allowed for non-empty blank nodes that have internal predicates
            var ch2 = Peek();
            if (ch2 == '.' || ch2 == '}' || ch2 == -1)
            {
                // If subject is a blank node from [...] with internal predicates, that's OK
                // But if it's just an IRI or labeled blank node, it needs a predicate
                // The way to tell is: blank node property lists start with '[' and have internal triples
                // But we've already parsed it. We need to track if it was a bnode property list.
                // For now, check if subject starts with '_:b' (generated) - those are from property lists
                // Other subjects (IRIs, labeled bnodes) need predicates
                if (!subject.StartsWith("_:b".AsSpan()))
                    throw ParserException("Subject requires a predicate-object list");
                TryConsume('.');
                continue;
            }

            ParsePredicateObjectList(subject, graphSpan, handler);

            SkipWhitespaceAndComments();

            // Consume optional '.' between triples
            TryConsume('.');
        }
    }

    private void ParsePredicateObjectList(ReadOnlySpan<char> subject, ReadOnlySpan<char> graph, QuadHandler handler)
    {
        while (true)
        {
            SkipWhitespaceAndComments();

            if (IsEndOfInput() || Peek() == '.' || Peek() == '}')
                break;

            var predicate = ParsePredicate();
            if (predicate.IsEmpty)
            {
                // Check if there's a blank node in predicate position (not allowed)
                var ch = Peek();
                if (ch == '[' || ch == '_')
                    throw ParserException("Blank nodes cannot be used as predicates");
                break;
            }

            SkipWhitespaceAndComments();

            // Parse object list
            ParseObjectList(subject, predicate, graph, handler);

            SkipWhitespaceAndComments();

            // Check for ';' (more predicates for same subject)
            // Handle multiple consecutive semicolons: s p o ;; p2 o2
            bool hasSemi = false;
            while (TryConsume(';'))
            {
                hasSemi = true;
                SkipWhitespaceAndComments();
            }

            if (hasSemi)
            {
                // Check if there's actually another predicate (could be trailing ';')
                var ch = Peek();
                if (ch == '.' || ch == '}' || ch == -1)
                    break;
                continue;
            }

            break;
        }
    }

    private void ParseObjectList(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> graph, QuadHandler handler)
    {
        bool hasObject = false;
        while (true)
        {
            var obj = ParseObjectWithHandler(graph, handler);
            if (obj.IsEmpty)
            {
                if (!hasObject)
                    throw ParserException("Expected object after predicate");
                break;
            }

            hasObject = true;
            handler(subject, predicate, obj, graph);

            SkipWhitespaceAndComments();

            // Check for ',' (more objects)
            if (!TryConsume(','))
                break;

            SkipWhitespaceAndComments();
        }
    }

    #endregion

    #region Term Parsing

    private ReadOnlySpan<char> ParseSubjectOrGraphLabel()
    {
        var ch = Peek();

        if (ch == '<')
            return ParseIriRef();

        if (ch == '_')
            return ParseBlankNode();

        // For graph labels, only labeled blank nodes (_:b1) and empty anonymous [] are valid
        // Non-empty [...] is NOT a valid graph label
        // But we can't determine that here - we need to check if followed by '{'
        // So return empty and let caller handle it
        if (ch == '[')
        {
            // Check if it's an empty blank node [] (possibly with whitespace inside)
            if (IsEmptyBlankNode())
            {
                Consume(); // [
                SkipWhitespaceAndComments();
                Consume(); // ]
                return GenerateBlankNode();
            }
            // Non-empty [...] - return empty, caller will parse as subject
            return ReadOnlySpan<char>.Empty;
        }

        // Collection () cannot be a graph label - only accept if it's followed by a predicate
        // This will be validated at the block level

        if (IsPnCharsBase(ch) || ch == ':')
            return ParsePrefixedName();

        return ReadOnlySpan<char>.Empty;
    }

    private bool IsEmptyBlankNode()
    {
        // Check if we have [] (possibly with whitespace inside)
        if (Peek() != '[')
            return false;
        int offset = 1;
        while (IsWhitespace(PeekAhead(offset)))
            offset++;
        return PeekAhead(offset) == ']';
    }

    private ReadOnlySpan<char> ParseSubject()
    {
        return ParseSubjectWithHandler(ReadOnlySpan<char>.Empty, null);
    }

    private ReadOnlySpan<char> ParseSubjectWithHandler(ReadOnlySpan<char> graph, QuadHandler? handler)
    {
        var ch = Peek();

        if (ch == '<')
            return ParseIriRef();

        if (ch == '_')
            return ParseBlankNode();

        if (ch == '[')
            return ParseBlankNodePropertyListWithHandler(graph, handler);

        if (ch == '(')
            return ParseCollectionWithHandler(graph, handler);

        if (IsPnCharsBase(ch) || ch == ':')
            return ParsePrefixedName();

        return ReadOnlySpan<char>.Empty;
    }

    private ReadOnlySpan<char> ParsePredicate()
    {
        var ch = Peek();

        // N3 '=' is not valid in TriG
        if (ch == '=')
            throw ParserException("'=' is not a valid predicate in TriG (N3 syntax not supported)");

        // 'a' shorthand for rdf:type
        // Must not be followed by ':' (which would make it a prefix like 'a:b')
        // and must not be followed by PN_CHARS (which would make it part of a prefixed name)
        if (ch == 'a' && PeekAhead(1) != ':' && !IsPnChars(PeekAhead(1)))
        {
            Consume();
            return "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>".AsSpan();
        }

        if (ch == '<')
            return ParseIriRef();

        if (IsPnCharsBase(ch) || ch == ':')
            return ParsePrefixedName();

        return ReadOnlySpan<char>.Empty;
    }

    private ReadOnlySpan<char> ParseObject()
    {
        return ParseObjectWithHandler(ReadOnlySpan<char>.Empty, null);
    }

    private ReadOnlySpan<char> ParseObjectWithHandler(ReadOnlySpan<char> graph, QuadHandler? handler)
    {
        var ch = Peek();

        if (ch == '<')
            return ParseIriRef();

        if (ch == '_')
            return ParseBlankNode();

        if (ch == '[')
            return ParseBlankNodePropertyListWithHandler(graph, handler);

        if (ch == '(')
            return ParseCollectionWithHandler(graph, handler);

        if (ch == '"' || ch == '\'')
            return ParseLiteral();

        if (ch == '+' || ch == '-' || char.IsDigit((char)ch))
            return ParseNumericLiteral();

        // Decimal without leading digits: .5 -> 0.5
        if (ch == '.' && char.IsDigit((char)PeekAhead(1)))
            return ParseNumericLiteral();

        if (IsPnCharsBase(ch) || ch == ':')
        {
            // Could be prefixed name or boolean
            // Boolean literals must be lowercase (case-sensitive)
            if (MatchKeyword("true", caseSensitive: true) || MatchKeyword("false", caseSensitive: true))
            {
                return ch == 't'
                    ? "\"true\"^^<http://www.w3.org/2001/XMLSchema#boolean>".AsSpan()
                    : "\"false\"^^<http://www.w3.org/2001/XMLSchema#boolean>".AsSpan();
            }
            return ParsePrefixedName();
        }

        return ReadOnlySpan<char>.Empty;
    }

    private ReadOnlySpan<char> ParseIriOrPrefixedName()
    {
        var ch = Peek();

        if (ch == '<')
            return ParseIriRef();

        if (ch == '_')
            return ParseBlankNode();

        // Anonymous blank node: [] for GRAPH [] { ... }
        // But non-empty [...] cannot be used as graph name
        if (ch == '[')
        {
            if (IsEmptyBlankNode())
            {
                Consume(); // [
                SkipWhitespaceAndComments();
                Consume(); // ]
                return GenerateBlankNode();
            }
            // Non-empty [...] cannot be graph name - throw error
            throw ParserException("A graph may not be named with a blankNodePropertyList");
        }

        if (IsPnCharsBase(ch) || ch == ':')
            return ParsePrefixedName();

        return ReadOnlySpan<char>.Empty;
    }

    private ReadOnlySpan<char> ParseIriRef()
    {
        if (Peek() != '<')
            return ReadOnlySpan<char>.Empty;

        int start = _outputOffset;
        AppendToOutput('<');
        Consume(); // Consume '<'

        // Read the IRI content
        int contentStart = _outputOffset;
        while (true)
        {
            var ch = Peek();

            if (ch == -1)
                throw ParserException("Unterminated IRI");

            if (ch == '>')
            {
                Consume();
                break;
            }

            if (ch == '\\')
            {
                Consume();
                var next = Peek();
                if (next == 'u')
                {
                    Consume();
                    AppendCodePoint(ParseUnicodeEscape(4));
                }
                else if (next == 'U')
                {
                    Consume();
                    AppendCodePoint(ParseUnicodeEscape(8));
                }
                else
                {
                    throw ParserException($"Invalid escape in IRI: \\{(char)next}");
                }
            }
            else if (ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r')
            {
                throw ParserException("Invalid character in IRI: whitespace not allowed");
            }
            else
            {
                AppendCodePoint(ch);  // Handle Unicode code points including supplementary planes
                Consume();
            }
        }

        int contentEnd = _outputOffset;
        int contentLen = contentEnd - contentStart;

        // Get the IRI content for analysis
        var iriContent = _outputBuffer.AsSpan(contentStart, contentLen);

        // Check if this is an absolute IRI (has scheme like "http:" or "urn:")
        bool hasScheme = false;
        for (int i = 0; i < iriContent.Length; i++)
        {
            var c = iriContent[i];
            if (c == ':')
            {
                hasScheme = i > 0; // scheme must have at least one char before ':'
                break;
            }
            if (c == '/' || c == '?' || c == '#')
                break; // These chars before ':' mean no scheme
            if (i == 0 && !char.IsLetter(c))
                break; // Scheme must start with letter
            if (i > 0 && !char.IsLetterOrDigit(c) && c != '+' && c != '-' && c != '.')
                break; // Scheme can only contain these chars
        }

        if (hasScheme || string.IsNullOrEmpty(_baseUri))
        {
            // Absolute IRI or no base - just close with >
            AppendToOutput('>');
        }
        else
        {
            // Resolve relative IRI against base URI per RFC3986
            var iriContentCopy = iriContent.ToString();
            var resolved = ResolveIri(_baseUri, iriContentCopy);

            // Reset and build resolved IRI
            _outputOffset = start;
            AppendToOutput('<');
            foreach (var c in resolved)
                AppendToOutput(c);
            AppendToOutput('>');
        }

        return GetOutputSpan(start);
    }

    private ReadOnlySpan<char> ParsePrefixedName()
    {
        int start = _outputOffset;

        // Parse prefix part - can include dots, dashes, underscores, digits (but not as first char)
        // PN_PREFIX ::= PN_CHARS_BASE ((PN_CHARS | '.')* PN_CHARS)?
        // PN_CHARS includes PN_CHARS_U (which has '_') plus '-' and digits
        // e.g: "e.g:", "ex_2:", "ex-2:" are all valid prefixes
        var prefixStart = _outputOffset;
        bool isFirstPrefixChar = true;
        while (true)
        {
            var ch = Peek();
            if (ch == ':')
                break;
            // First char must be PN_CHARS_BASE, subsequent chars can be PN_CHARS or '.'
            if (isFirstPrefixChar)
            {
                if (!IsPnCharsBase(ch))
                    break;
            }
            else
            {
                // PN_CHARS = PN_CHARS_U | '-' | [0-9] | #x00B7 | [#x0300-#x036F] | [#x203F-#x2040]
                // Use IsPnChars() which includes all these ranges, plus allow '.' between chars
                if (!IsPnChars(ch) && ch != '.')
                    break;
            }
            AppendCodePoint(ch);
            Consume();
            isFirstPrefixChar = false;
        }

        // Check for trailing dots in prefix and remove them (they're not part of prefix)
        while (_outputOffset > prefixStart && _outputBuffer[_outputOffset - 1] == '.')
        {
            _outputOffset--;
            // We need to "un-consume" the dots - but we can't, so error if this happens
            // Actually, in TriG grammar dots in prefix are allowed, so this shouldn't happen
        }

        if (!TryConsume(':'))
            throw ParserException("Expected ':' in prefixed name");

        var prefix = new string(_outputBuffer, prefixStart, _outputOffset - prefixStart);
        _outputOffset = start; // Reset to build full IRI

        // Look up namespace
        if (!_namespaces.TryGetValue(prefix, out var ns))
            throw ParserException($"Unknown prefix: {prefix}");

        // Build full IRI
        // Namespace may have angle brackets (from @prefix parsing) or not (pre-initialized)
        if (ns.StartsWith('<') && ns.EndsWith('>'))
        {
            // Namespace has brackets - append without closing bracket
            foreach (var c in ns.AsSpan(0, ns.Length - 1))
                AppendToOutput(c);
        }
        else
        {
            // Namespace lacks brackets - add opening bracket
            AppendToOutput('<');
            foreach (var c in ns)
                AppendToOutput(c);
        }

        // Parse local part - dots allowed but not as the last character
        bool isFirstLocalChar = true;
        while (true)
        {
            var ch = Peek();
            // Stop at whitespace, punctuation (except '.'), comment start, block delimiters, or quote chars
            // Include '(' and '[' which start collections/blank nodes
            // Include '"' and '\'' which start literals
            if (ch == -1 || IsWhitespace(ch) || ch == ';' || ch == ',' ||
                ch == '(' || ch == ')' || ch == '[' || ch == ']' || ch == '}' || ch == '{' || ch == '#' ||
                ch == '"' || ch == '\'')
                break;

            // Local name cannot start with '-'
            if (isFirstLocalChar && ch == '-')
                throw ParserException("Local name must not begin with '-'");

            // Unescaped ~ and ^ are not allowed in local names
            if (ch == '~')
                throw ParserException("'~' must be escaped in local name");
            if (ch == '^')
                throw ParserException("'^' is not allowed in local name");

            // Handle '.' specially - only include if followed by valid local name char
            if (ch == '.')
            {
                var next = PeekAhead(1);
                // Dot followed by valid PN_CHARS is part of local name
                // Otherwise it's the statement terminator
                if (!IsPnChars(next) && next != '.')
                    break;
                AppendToOutput('.');  // '.' is ASCII, no surrogate pair needed
                Consume();
                isFirstLocalChar = false;
                continue;
            }

            // Handle percent escapes - must be %HH where H is hex digit
            if (ch == '%')
            {
                var h1 = PeekAhead(1);
                var h2 = PeekAhead(2);
                if (!IsHexDigit(h1) || !IsHexDigit(h2))
                    throw ParserException("Invalid percent escape in local name (requires two hex digits)");
                AppendToOutput((char)ch);
                Consume();
                AppendToOutput((char)h1);
                Consume();
                AppendToOutput((char)h2);
                Consume();
                isFirstLocalChar = false;
                continue;
            }

            if (ch == '\\')
            {
                Consume();
                var next = Peek();
                if (next == -1)
                    throw ParserException("Unexpected end in escape");
                // Only certain characters can be escaped in local names
                // \u escapes are NOT allowed in prefixed names (unlike IRIs)
                if (next == 'u' || next == 'U')
                    throw ParserException("Unicode escapes (\\u, \\U) are not allowed in prefixed names");
                // Only PN_LOCAL_ESC characters can be escaped: _~.-!$&'()*+,;=/?#@%
                if (!IsPnLocalEsc(next))
                    throw ParserException($"Invalid escape in local name: \\{(char)next}");
                AppendToOutput((char)next);
                Consume();
            }
            else
            {
                AppendCodePoint(ch);  // Handle Unicode code points including supplementary planes
                Consume();
            }
            isFirstLocalChar = false;
        }

        AppendToOutput('>');
        return GetOutputSpan(start);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsHexDigit(int ch)
    {
        return (ch >= '0' && ch <= '9') ||
               (ch >= 'A' && ch <= 'F') ||
               (ch >= 'a' && ch <= 'f');
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPnLocalEsc(int ch)
    {
        // PN_LOCAL_ESC ::= '\' ('_' | '~' | '.' | '-' | '!' | '$' | '&' | "'" | '(' | ')' | '*' | '+' | ',' | ';' | '=' | '/' | '?' | '#' | '@' | '%')
        return ch == '_' || ch == '~' || ch == '.' || ch == '-' || ch == '!' ||
               ch == '$' || ch == '&' || ch == '\'' || ch == '(' || ch == ')' ||
               ch == '*' || ch == '+' || ch == ',' || ch == ';' || ch == '=' ||
               ch == '/' || ch == '?' || ch == '#' || ch == '@' || ch == '%';
    }

    private ReadOnlySpan<char> ParseBlankNode()
    {
        if (Peek() != '_' || PeekAhead(1) != ':')
            return ReadOnlySpan<char>.Empty;

        int start = _outputOffset;
        AppendToOutput('_');
        Consume();
        AppendToOutput(':');
        Consume();

        var ch = Peek();
        if (!IsPnCharsU(ch) && !char.IsDigit((char)ch))
            throw ParserException("Invalid blank node label");

        AppendCodePoint(ch);  // Handle Unicode code points including supplementary planes
        Consume();

        while (true)
        {
            ch = Peek();
            if (!IsPnChars(ch) && ch != '.')
                break;

            // Handle '.' specially
            // BLANK_NODE_LABEL ::= '_:' ( PN_CHARS_U | [0-9] ) ((PN_CHARS | '.')* PN_CHARS)?
            // Dots are allowed but not as the last character
            if (ch == '.')
            {
                var next = PeekAhead(1);
                // Dot followed by PN_CHARS is part of label - continue
                if (IsPnChars(next))
                {
                    AppendToOutput('.');  // '.' is ASCII
                    Consume();
                    continue;
                }
                // Dot followed by dot is part of label (might have more dots)
                if (next == '.')
                {
                    AppendToOutput('.');  // '.' is ASCII
                    Consume();
                    continue;
                }
                // Dot followed by non-PN_CHARS: this is a statement terminator
                // BUT if what follows looks like it could be predicate (IRI, prefixed name),
                // then the user likely intended _:label. as a blank node ending in dot, which is illegal
                if (next == ' ' || next == '\t')
                {
                    // Look ahead past whitespace to see what comes next
                    int offset = 2;
                    while (IsWhitespace(PeekAhead(offset)))
                        offset++;
                    var afterWs = PeekAhead(offset);
                    // If followed by something that could be a predicate, the user likely meant
                    // to include the dot in the label
                    if (afterWs == '<' || afterWs == ':' || IsPnCharsBase(afterWs) || afterWs == 'a')
                    {
                        throw ParserException("Blank node label must not end with '.'");
                    }
                }
                // Otherwise, dot is statement terminator - stop here
                break;
            }

            AppendCodePoint(ch);  // Handle Unicode code points including supplementary planes
            Consume();
        }

        return GetOutputSpan(start);
    }

    private ReadOnlySpan<char> ParseBlankNodePropertyListWithHandler(ReadOnlySpan<char> graph, QuadHandler? handler)
    {
        // [ predicate object ; ... ]
        // Returns a generated blank node ID and emits triples for the content
        if (!TryConsume('['))
            return ReadOnlySpan<char>.Empty;

        // Generate blank node - need to copy to stable storage since output buffer will be reused
        var blankIdSpan = GenerateBlankNode();
        var blankId = blankIdSpan.ToString();

        SkipWhitespaceAndComments();

        // Check for empty blank node: []
        if (Peek() == ']')
        {
            Consume();
            // Return the blank node ID in output buffer
            int start = _outputOffset;
            AppendString(blankId);
            return GetOutputSpan(start);
        }

        // Parse predicate-object list with blank node as subject
        if (handler != null)
        {
            ParseBlankNodePredicateObjectList(blankId, graph, handler);
        }
        else
        {
            // No handler - skip content (shouldn't happen in practice)
            int depth = 1;
            while (depth > 0 && !IsEndOfInput())
            {
                var ch = Peek();
                if (ch == '[') depth++;
                else if (ch == ']') depth--;
                Consume();
            }
        }

        SkipWhitespaceAndComments();

        if (!TryConsume(']'))
            throw ParserException("Expected ']' to close blank node property list");

        // Return the blank node ID in output buffer
        int resultStart = _outputOffset;
        AppendString(blankId);
        return GetOutputSpan(resultStart);
    }

    private void ParseBlankNodePredicateObjectList(string blankNodeSubject, ReadOnlySpan<char> graph, QuadHandler handler)
    {
        while (true)
        {
            SkipWhitespaceAndComments();

            if (Peek() == ']')
                break;

            var predicate = ParsePredicate();
            if (predicate.IsEmpty)
                break;

            // Copy predicate since we'll reuse the buffer
            var predicateStr = predicate.ToString();

            SkipWhitespaceAndComments();

            // Parse object list for this predicate
            while (true)
            {
                var obj = ParseObjectWithHandler(graph, handler);
                if (obj.IsEmpty)
                    break;

                // Emit triple: blankNode predicate object
                handler(blankNodeSubject.AsSpan(), predicateStr.AsSpan(), obj, graph);

                SkipWhitespaceAndComments();

                // Check for ',' (more objects)
                if (!TryConsume(','))
                    break;

                SkipWhitespaceAndComments();
            }

            SkipWhitespaceAndComments();

            // Check for ';' (more predicates)
            // Handle multiple consecutive semicolons
            bool hasSemi = false;
            while (TryConsume(';'))
            {
                hasSemi = true;
                SkipWhitespaceAndComments();
            }

            if (hasSemi)
            {
                // Check if there's actually another predicate (could be trailing ';')
                if (Peek() == ']')
                    break;
                continue;
            }

            break;
        }
    }

    private ReadOnlySpan<char> ParseCollectionWithHandler(ReadOnlySpan<char> graph, QuadHandler? handler)
    {
        // ( item1 item2 ... )
        // Returns first node of the list (or rdf:nil for empty)
        // Emits triples: node rdf:first item; node rdf:rest nextNode
        if (!TryConsume('('))
            return ReadOnlySpan<char>.Empty;

        SkipWhitespaceAndComments();

        // Check for empty collection: ()
        if (Peek() == ')')
        {
            Consume();
            return "<http://www.w3.org/1999/02/22-rdf-syntax-ns#nil>".AsSpan();
        }

        const string rdfFirst = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#first>";
        const string rdfRest = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#rest>";
        const string rdfNil = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#nil>";

        string? firstNode = null;
        string? currentNode = null;

        while (true)
        {
            SkipWhitespaceAndComments();

            if (Peek() == ')')
            {
                Consume();
                break;
            }

            // Parse item
            var item = ParseObjectWithHandler(graph, handler);
            if (item.IsEmpty)
                break;

            // Generate a new blank node for this list cell
            var nodeSpan = GenerateBlankNode();
            var node = nodeSpan.ToString();

            if (firstNode == null)
                firstNode = node;

            // Link previous node to this one
            if (currentNode != null && handler != null)
            {
                handler(currentNode.AsSpan(), rdfRest.AsSpan(), node.AsSpan(), graph);
            }

            // Emit: node rdf:first item
            if (handler != null)
            {
                handler(node.AsSpan(), rdfFirst.AsSpan(), item, graph);
            }

            currentNode = node;

            SkipWhitespaceAndComments();
        }

        // Close the list with rdf:nil
        if (currentNode != null && handler != null)
        {
            handler(currentNode.AsSpan(), rdfRest.AsSpan(), rdfNil.AsSpan(), graph);
        }

        // Return the first node (or nil if empty)
        if (firstNode != null)
        {
            int start = _outputOffset;
            AppendString(firstNode);
            return GetOutputSpan(start);
        }

        return rdfNil.AsSpan();
    }

    private ReadOnlySpan<char> ParseLiteral()
    {
        var ch = Peek();
        if (ch != '"' && ch != '\'')
            return ReadOnlySpan<char>.Empty;

        int start = _outputOffset;
        var quote = (char)ch;

        // Check for long string (""" or ''')
        bool isLong = PeekAhead(1) == quote && PeekAhead(2) == quote;

        AppendToOutput('"');
        Consume();

        if (isLong)
        {
            Consume();
            Consume();
            ParseLongStringContent(quote);
        }
        else
        {
            ParseStringContent(quote);
        }

        AppendToOutput('"');

        // Check for language tag or datatype
        ch = Peek();
        if (ch == '@')
        {
            AppendToOutput('@');
            Consume();
            // Language tag must start with a letter (RFC 5646)
            ch = Peek();
            if (ch == -1 || !((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z')))
                throw ParserException("Language tag must start with a letter");
            while (true)
            {
                ch = Peek();
                if (ch == -1 || IsWhitespace(ch) || ch == '.' || ch == ';' || ch == ',' ||
                    ch == ')' || ch == ']' || ch == '}')
                    break;
                AppendToOutput((char)ch);
                Consume();
            }
        }
        else if (ch == '^' && PeekAhead(1) == '^')
        {
            AppendToOutput('^');
            Consume();
            AppendToOutput('^');
            Consume();
            ParseIriOrPrefixedName(); // Datatype IRI (appended to output)
        }

        return GetOutputSpan(start);
    }

    private void ParseStringContent(char quote)
    {
        while (true)
        {
            var ch = Peek();
            if (ch == -1)
                throw ParserException("Unterminated string");
            if (ch == quote)
            {
                Consume();
                break;
            }
            if (ch == '\\')
            {
                Consume();
                ParseEscapeSequenceAndAppend();
            }
            else
            {
                AppendCodePoint(ch);
                Consume();
            }
        }
    }

    private void ParseLongStringContent(char quote)
    {
        while (true)
        {
            var ch = Peek();
            if (ch == -1)
                throw ParserException("Unterminated long string");

            if (ch == quote && PeekAhead(1) == quote && PeekAhead(2) == quote)
            {
                Consume();
                Consume();
                Consume();
                break;
            }

            if (ch == '\\')
            {
                Consume();
                ParseEscapeSequenceAndAppend();
            }
            else
            {
                AppendCodePoint(ch);
                Consume();
            }
        }
    }

    private ReadOnlySpan<char> ParseNumericLiteral()
    {
        int start = _outputOffset;
        AppendToOutput('"');

        var ch = Peek();
        if (ch == '+' || ch == '-')
        {
            AppendToOutput((char)ch);
            Consume();
            ch = Peek();
        }

        bool hasDecimal = false;
        bool hasExponent = false;

        // Handle decimal starting with '.' (e.g., .5)
        if (ch == '.' && char.IsDigit((char)PeekAhead(1)))
        {
            hasDecimal = true;
            AppendToOutput((char)ch);
            Consume();
        }

        while (true)
        {
            ch = Peek();
            if (char.IsDigit((char)ch))
            {
                AppendToOutput((char)ch);
                Consume();
            }
            else if (ch == '.' && !hasDecimal && !hasExponent)
            {
                // Decimal point must be followed by at least one digit or exponent
                // Otherwise it's a statement terminator, not part of the number
                var afterDot = PeekAhead(1);
                if (!char.IsDigit((char)afterDot) && afterDot != 'e' && afterDot != 'E')
                    break; // Don't consume the dot - it's a statement terminator
                hasDecimal = true;
                AppendToOutput((char)ch);
                Consume();
            }
            else if ((ch == 'e' || ch == 'E') && !hasExponent)
            {
                hasExponent = true;
                AppendToOutput((char)ch);
                Consume();
                ch = Peek();
                if (ch == '+' || ch == '-')
                {
                    AppendToOutput((char)ch);
                    Consume();
                    ch = Peek();
                }
                // Exponent must be followed by at least one digit
                if (!char.IsDigit((char)ch))
                    throw ParserException("Exponent must have at least one digit");
            }
            else
            {
                break;
            }
        }

        AppendToOutput('"');
        AppendToOutput('^');
        AppendToOutput('^');

        if (hasExponent)
            AppendString("<http://www.w3.org/2001/XMLSchema#double>");
        else if (hasDecimal)
            AppendString("<http://www.w3.org/2001/XMLSchema#decimal>");
        else
            AppendString("<http://www.w3.org/2001/XMLSchema#integer>");

        return GetOutputSpan(start);
    }

    private ReadOnlySpan<char> GenerateBlankNode()
    {
        int start = _outputOffset;
        AppendString("_:b");
        var id = _blankNodeCounter++;
        foreach (var c in id.ToString())
            AppendToOutput(c);
        return GetOutputSpan(start);
    }

    #endregion

    #region Directives

    private bool TryParseDirective()
    {
        var ch = Peek();

        if (ch == '@')
        {
            Consume();
            // Turtle directives @prefix and @base are case-sensitive (must be lowercase)
            if (MatchKeyword("prefix", caseSensitive: true))
            {
                ParsePrefixDirective();
                return true;
            }
            if (MatchKeyword("base", caseSensitive: true))
            {
                ParseBaseDirective();
                return true;
            }
            throw ParserException("Unknown directive (note: @prefix and @base are case-sensitive)");
        }

        // SPARQL-style PREFIX and BASE are case-insensitive
        if (MatchKeyword("PREFIX"))
        {
            ParseSparqlPrefixDirective();
            return true;
        }

        if (MatchKeyword("BASE"))
        {
            ParseSparqlBaseDirective();
            return true;
        }

        return false;
    }

    private void ParsePrefixDirective()
    {
        SkipWhitespaceAndComments();

        // Parse prefix name
        var prefix = ParsePrefixName();

        SkipWhitespaceAndComments();

        // Expect ':'
        if (!TryConsume(':'))
            throw ParserException("Expected ':' after prefix name");

        SkipWhitespaceAndComments();

        // Parse namespace IRI
        var iri = ParseIriRef();
        var ns = iri.Slice(1, iri.Length - 2).ToString(); // Remove < >

        _namespaces[prefix] = ns;

        SkipWhitespaceAndComments();
        TryConsume('.');
    }

    private void ParseBaseDirective()
    {
        SkipWhitespaceAndComments();
        var iri = ParseIriRef();
        _baseUri = iri.Slice(1, iri.Length - 2).ToString();
        SkipWhitespaceAndComments();
        TryConsume('.');
    }

    private void ParseSparqlPrefixDirective()
    {
        SkipWhitespaceAndComments();
        var prefix = ParsePrefixName();
        SkipWhitespaceAndComments();
        if (!TryConsume(':'))
            throw ParserException("Expected ':' after prefix");
        SkipWhitespaceAndComments();
        var iri = ParseIriRef();
        var ns = iri.Slice(1, iri.Length - 2).ToString();
        _namespaces[prefix] = ns;
        // SPARQL-style PREFIX must not end with '.'
        SkipWhitespaceAndComments();
        if (Peek() == '.')
            throw ParserException("SPARQL-style PREFIX must not end with '.'");
    }

    private void ParseSparqlBaseDirective()
    {
        SkipWhitespaceAndComments();
        var iri = ParseIriRef();
        _baseUri = iri.Slice(1, iri.Length - 2).ToString();
        // SPARQL-style BASE must not end with '.'
        SkipWhitespaceAndComments();
        if (Peek() == '.')
            throw ParserException("SPARQL-style BASE must not end with '.'");
    }

    private string ParsePrefixName()
    {
        int start = _outputOffset;
        while (true)
        {
            var ch = Peek();
            if (ch == ':' || ch == -1 || IsWhitespace(ch))
                break;
            AppendCodePoint(ch);  // Handle Unicode code points including supplementary planes
            Consume();
        }
        var span = GetOutputSpan(start);
        var result = span.ToString();
        _outputOffset = start; // Reset
        return result;
    }

    #endregion

    #region Escape Handling

    /// <summary>
    /// Parse string escape sequence and append to output.
    /// Handles both BMP and supplementary Unicode code points.
    /// </summary>
    private void ParseEscapeSequenceAndAppend()
    {
        var ch = Peek();
        if (ch == -1)
            throw ParserException("Unexpected end in escape sequence");

        Consume();

        switch ((char)ch)
        {
            case 't': AppendToOutput('\t'); break;
            case 'b': AppendToOutput('\b'); break;
            case 'n': AppendToOutput('\n'); break;
            case 'r': AppendToOutput('\r'); break;
            case 'f': AppendToOutput('\f'); break;
            case '"': AppendToOutput('"'); break;
            case '\'': AppendToOutput('\''); break;
            case '\\': AppendToOutput('\\'); break;
            case 'u':
                var codePoint4 = ParseUnicodeEscape(4);
                AppendCodePoint(codePoint4);
                break;
            case 'U':
                var codePoint8 = ParseUnicodeEscape(8);
                AppendCodePoint(codePoint8);
                break;
            default:
                throw ParserException($"Invalid escape: \\{(char)ch}");
        }
    }

    /// <summary>
    /// Parse unicode escape (\uXXXX or \UXXXXXXXX).
    /// Returns the full code point (may be > 0xFFFF for supplementary planes).
    /// </summary>
    private int ParseUnicodeEscape(int digits)
    {
        var value = 0;
        for (int i = 0; i < digits; i++)
        {
            var ch = Peek();
            if (ch == -1)
                throw ParserException("Unexpected end in unicode escape");

            var hexValue = ch switch
            {
                >= '0' and <= '9' => ch - '0',
                >= 'A' and <= 'F' => ch - 'A' + 10,
                >= 'a' and <= 'f' => ch - 'a' + 10,
                _ => throw ParserException($"Invalid hex digit: {(char)ch}")
            };

            Consume();
            value = (value << 4) | hexValue;
        }

        // Reject surrogate code points (U+D800-U+DFFF)
        if (value >= 0xD800 && value <= 0xDFFF)
            throw ParserException($"Surrogate code points are not allowed: U+{value:X4}");

        return value;
    }

    #endregion

    #region Buffer Management

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Peek()
    {
        if (_bufferPosition >= _bufferLength)
            return _endOfStream ? -1 : -1;

        // Decode UTF-8 to get Unicode code point
        return PeekUtf8CodePoint(out _);
    }

    /// <summary>
    /// Peek the current UTF-8 code point and return its byte length.
    /// Returns -1 if at end of input.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int PeekUtf8CodePoint(out int byteLength)
    {
        if (_bufferPosition >= _bufferLength)
        {
            byteLength = 0;
            return -1;
        }

        var b0 = _inputBuffer[_bufferPosition];

        // ASCII (0x00-0x7F): single byte
        if (b0 < 0x80)
        {
            byteLength = 1;
            return b0;
        }

        // 2-byte sequence (0xC0-0xDF)
        if ((b0 & 0xE0) == 0xC0)
        {
            if (_bufferPosition + 1 >= _bufferLength)
            {
                byteLength = 1;
                return b0; // Incomplete sequence, return first byte
            }
            var b1 = _inputBuffer[_bufferPosition + 1];
            byteLength = 2;
            return ((b0 & 0x1F) << 6) | (b1 & 0x3F);
        }

        // 3-byte sequence (0xE0-0xEF)
        if ((b0 & 0xF0) == 0xE0)
        {
            if (_bufferPosition + 2 >= _bufferLength)
            {
                byteLength = 1;
                return b0; // Incomplete sequence
            }
            var b1 = _inputBuffer[_bufferPosition + 1];
            var b2 = _inputBuffer[_bufferPosition + 2];
            byteLength = 3;
            return ((b0 & 0x0F) << 12) | ((b1 & 0x3F) << 6) | (b2 & 0x3F);
        }

        // 4-byte sequence (0xF0-0xF7)
        if ((b0 & 0xF8) == 0xF0)
        {
            if (_bufferPosition + 3 >= _bufferLength)
            {
                byteLength = 1;
                return b0; // Incomplete sequence
            }
            var b1 = _inputBuffer[_bufferPosition + 1];
            var b2 = _inputBuffer[_bufferPosition + 2];
            var b3 = _inputBuffer[_bufferPosition + 3];
            byteLength = 4;
            return ((b0 & 0x07) << 18) | ((b1 & 0x3F) << 12) | ((b2 & 0x3F) << 6) | (b3 & 0x3F);
        }

        // Invalid UTF-8 lead byte, return as-is
        byteLength = 1;
        return b0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int PeekAhead(int offset)
    {
        var pos = _bufferPosition + offset;
        if (pos >= _bufferLength)
            return -1;
        return _inputBuffer[pos];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Consume()
    {
        if (_bufferPosition >= _bufferLength)
            return;

        // Get the byte length of the current UTF-8 code point
        PeekUtf8CodePoint(out var byteLength);

        var ch = _inputBuffer[_bufferPosition];
        _bufferPosition += byteLength;

        if (ch == '\n')
        {
            _line++;
            _column = 1;
        }
        else
        {
            _column++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryConsume(char expected)
    {
        if (Peek() != expected)
            return false;
        Consume();
        return true;
    }

    private bool MatchKeyword(string keyword, bool caseSensitive = false)
    {
        for (int i = 0; i < keyword.Length; i++)
        {
            var ch = PeekAhead(i);
            if (caseSensitive)
            {
                if (ch != keyword[i])
                    return false;
            }
            else
            {
                // Case-insensitive comparison for SPARQL keywords
                if (char.ToUpperInvariant((char)ch) != char.ToUpperInvariant(keyword[i]))
                    return false;
            }
        }

        // Ensure not part of larger word
        var next = PeekAhead(keyword.Length);
        if (next != -1 && (char.IsLetterOrDigit((char)next) || next == '_'))
            return false;

        for (int i = 0; i < keyword.Length; i++)
            Consume();

        return true;
    }

    private async ValueTask FillBufferAsync(CancellationToken ct)
    {
        if (_endOfStream)
            return;

        if (_bufferPosition > 0 && _bufferPosition < _bufferLength)
        {
            var remaining = _bufferLength - _bufferPosition;
            Array.Copy(_inputBuffer, _bufferPosition, _inputBuffer, 0, remaining);
            _bufferLength = remaining;
            _bufferPosition = 0;
        }
        else if (_bufferPosition >= _bufferLength)
        {
            _bufferPosition = 0;
            _bufferLength = 0;
        }

        var bytesRead = await _stream.ReadAsync(
            _inputBuffer.AsMemory(_bufferLength, _inputBuffer.Length - _bufferLength), ct);

        if (bytesRead == 0)
            _endOfStream = true;
        else
            _bufferLength += bytesRead;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsEndOfInput() => _endOfStream && _bufferPosition >= _bufferLength;

    private void SkipWhitespaceAndComments()
    {
        while (true)
        {
            var ch = Peek();
            if (ch == -1)
                break;

            if (IsWhitespace(ch))
            {
                Consume();
                continue;
            }

            if (ch == '#')
            {
                while (true)
                {
                    ch = Peek();
                    if (ch == -1 || ch == '\n')
                    {
                        if (ch == '\n') Consume();
                        break;
                    }
                    Consume();
                }
                continue;
            }

            break;
        }
    }

    private void SkipToEndOfStatement()
    {
        while (true)
        {
            var ch = Peek();
            if (ch == -1 || ch == '.')
            {
                if (ch == '.') Consume();
                break;
            }
            Consume();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsWhitespace(int ch) => ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n';

    #endregion

    #region Output Buffer

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ResetOutputBuffer() => _outputOffset = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendToOutput(char c)
    {
        if (_outputOffset >= _outputBuffer.Length)
            GrowOutputBuffer();
        _outputBuffer[_outputOffset++] = c;
    }

    /// <summary>
    /// Append a Unicode code point to the output buffer, handling surrogate pairs for code points > 0xFFFF.
    /// </summary>
    private void AppendCodePoint(int codePoint)
    {
        if (codePoint <= 0xFFFF)
        {
            AppendToOutput((char)codePoint);
        }
        else
        {
            // Encode as surrogate pair
            var adjusted = codePoint - 0x10000;
            AppendToOutput((char)(0xD800 + (adjusted >> 10)));
            AppendToOutput((char)(0xDC00 + (adjusted & 0x3FF)));
        }
    }

    private void AppendString(string s)
    {
        foreach (var c in s)
            AppendToOutput(c);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> GetOutputSpan(int start) =>
        _outputBuffer.AsSpan(start, _outputOffset - start);

    private void GrowOutputBuffer()
    {
        var newBuffer = _bufferManager.Rent<char>(_outputBuffer.Length * 2).Array!;
        _outputBuffer.AsSpan(0, _outputOffset).CopyTo(newBuffer);
        _bufferManager.Return(_outputBuffer);
        _outputBuffer = newBuffer;
    }

    #endregion

    #region IRI Resolution (RFC3986)

    /// <summary>
    /// Resolves a relative IRI reference against a base IRI per RFC3986.
    /// </summary>
    private static string ResolveIri(string baseUri, string reference)
    {
        if (string.IsNullOrEmpty(reference))
            return baseUri;

        // Parse base URI components
        ParseUri(baseUri, out var baseScheme, out var baseAuthority, out var basePath, out var baseQuery, out _);

        // Use null to indicate "no fragment" vs "" for "empty fragment" (e.g., bar#)
        string scheme;
        string? authority, query, fragment;
        string path;

        // Check if reference has its own scheme
        int colonPos = reference.IndexOf(':');
        int slashPos = reference.IndexOf('/');
        int queryPos = reference.IndexOf('?');
        int fragPos = reference.IndexOf('#');

        bool refHasScheme = colonPos > 0 &&
                            (slashPos < 0 || colonPos < slashPos) &&
                            (queryPos < 0 || colonPos < queryPos) &&
                            (fragPos < 0 || colonPos < fragPos);

        if (refHasScheme)
        {
            // Reference has scheme - use it as-is (with path normalization)
            ParseUri(reference, out scheme, out authority, out path, out query, out fragment);
            path = RemoveDotSegments(path);
        }
        else if (reference.StartsWith("//"))
        {
            // Reference has authority
            scheme = baseScheme;
            ParseUri("x:" + reference, out _, out authority, out path, out query, out fragment);
            path = RemoveDotSegments(path);
        }
        else if (reference.Length == 0)
        {
            // Empty reference - use base
            scheme = baseScheme;
            authority = baseAuthority;
            path = basePath;
            query = baseQuery;
            fragment = null;
        }
        else if (reference[0] == '?')
        {
            // Reference is query
            scheme = baseScheme;
            authority = baseAuthority;
            path = basePath;
            query = reference.Substring(1);
            fragPos = query.IndexOf('#');
            if (fragPos >= 0)
            {
                fragment = query.Substring(fragPos + 1);  // "" for empty fragment is OK
                query = query.Substring(0, fragPos);
            }
            else
            {
                fragment = null;  // No fragment present
            }
        }
        else if (reference[0] == '#')
        {
            // Reference is fragment - include the # even if fragment is empty
            // For empty fragment (#), we still want to output the #
            var fragResult = new System.Text.StringBuilder();
            fragResult.Append(baseScheme);
            fragResult.Append(':');
            if (baseAuthority != null)
            {
                fragResult.Append("//");
                fragResult.Append(baseAuthority);
            }
            fragResult.Append(basePath);
            if (!string.IsNullOrEmpty(baseQuery))
            {
                fragResult.Append('?');
                fragResult.Append(baseQuery);
            }
            fragResult.Append('#');
            fragResult.Append(reference.Substring(1));
            return fragResult.ToString();
        }
        else if (reference[0] == '/')
        {
            // Reference has absolute path
            scheme = baseScheme;
            authority = baseAuthority;
            ParseUri(baseScheme + "://" + (baseAuthority ?? "") + reference,
                out _, out _, out path, out query, out fragment);
            path = RemoveDotSegments(path);
        }
        else
        {
            // Reference has relative path - merge with base
            scheme = baseScheme;
            authority = baseAuthority;

            // Merge paths
            if (string.IsNullOrEmpty(baseAuthority) && string.IsNullOrEmpty(basePath))
            {
                path = "/" + reference;
            }
            else
            {
                int lastSlash = basePath.LastIndexOf('/');
                if (lastSlash >= 0)
                    path = basePath.Substring(0, lastSlash + 1) + reference;
                else
                    path = reference;
            }

            // Extract query and fragment from merged path
            fragPos = path.IndexOf('#');
            if (fragPos >= 0)
            {
                fragment = path.Substring(fragPos + 1);  // "" for empty fragment is OK
                path = path.Substring(0, fragPos);
            }
            else
            {
                fragment = null;  // No fragment present
            }

            queryPos = path.IndexOf('?');
            if (queryPos >= 0)
            {
                query = path.Substring(queryPos + 1);
                path = path.Substring(0, queryPos);
            }
            else
            {
                query = null;  // No query present
            }

            path = RemoveDotSegments(path);
        }

        // Recompose the URI
        var result = new System.Text.StringBuilder();
        result.Append(scheme);
        result.Append(':');

        if (authority != null)
        {
            result.Append("//");
            result.Append(authority);
        }

        result.Append(path);

        if (query != null)
        {
            result.Append('?');
            result.Append(query);
        }

        if (fragment != null)
        {
            result.Append('#');
            result.Append(fragment);
        }

        return result.ToString();
    }

    /// <summary>
    /// Parses a URI into its components.
    /// Returns null for query/fragment when the delimiter is not present (vs "" when present but empty).
    /// </summary>
    private static void ParseUri(string uri, out string scheme, out string? authority,
        out string path, out string? query, out string? fragment)
    {
        scheme = "";
        authority = null;
        path = "";
        query = null;     // null = no ? in URI
        fragment = null;  // null = no # in URI

        if (string.IsNullOrEmpty(uri))
            return;

        int pos = 0;

        // Extract scheme
        int colonPos = uri.IndexOf(':');
        if (colonPos > 0)
        {
            scheme = uri.Substring(0, colonPos);
            pos = colonPos + 1;
        }

        // Extract fragment (# present means fragment, even if empty)
        int fragPos = uri.IndexOf('#', pos);
        string remaining;
        if (fragPos >= 0)
        {
            fragment = uri.Substring(fragPos + 1);  // "" if nothing after #
            remaining = uri.Substring(pos, fragPos - pos);
        }
        else
        {
            fragment = null;  // No # in URI
            remaining = uri.Substring(pos);
        }

        // Extract query (? present means query, even if empty)
        int queryPos = remaining.IndexOf('?');
        if (queryPos >= 0)
        {
            query = remaining.Substring(queryPos + 1);  // "" if nothing after ?
            remaining = remaining.Substring(0, queryPos);
        }
        else
        {
            query = null;  // No ? in URI
        }

        // Extract authority and path
        if (remaining.StartsWith("//"))
        {
            int pathStart = remaining.IndexOf('/', 2);
            if (pathStart >= 0)
            {
                authority = remaining.Substring(2, pathStart - 2);
                path = remaining.Substring(pathStart);
            }
            else
            {
                authority = remaining.Substring(2);
                path = "";
            }
        }
        else
        {
            path = remaining;
        }
    }

    /// <summary>
    /// Removes dot segments from a path per RFC3986 section 5.2.4.
    /// </summary>
    private static string RemoveDotSegments(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        var output = new System.Text.StringBuilder();
        int i = 0;

        while (i < path.Length)
        {
            // A: If the input buffer begins with a prefix of "../" or "./"
            if (path.Length - i >= 3 && path[i] == '.' && path[i + 1] == '.' && path[i + 2] == '/')
            {
                i += 3;
                continue;
            }
            if (path.Length - i >= 2 && path[i] == '.' && path[i + 1] == '/')
            {
                i += 2;
                continue;
            }

            // B: If the input buffer begins with a prefix of "/./" or "/."
            if (path.Length - i >= 3 && path[i] == '/' && path[i + 1] == '.' && path[i + 2] == '/')
            {
                i += 2;
                continue;
            }
            if (path.Length - i == 2 && path[i] == '/' && path[i + 1] == '.')
            {
                output.Append('/');
                i += 2;
                continue;
            }

            // C: If the input buffer begins with a prefix of "/../" or "/.."
            if (path.Length - i >= 4 && path[i] == '/' && path[i + 1] == '.' && path[i + 2] == '.' && path[i + 3] == '/')
            {
                i += 3;
                RemoveLastSegment(output);
                continue;
            }
            if (path.Length - i == 3 && path[i] == '/' && path[i + 1] == '.' && path[i + 2] == '.')
            {
                RemoveLastSegment(output);
                output.Append('/');
                i += 3;
                continue;
            }

            // D: if the input buffer consists only of "." or ".."
            if ((path.Length - i == 1 && path[i] == '.') ||
                (path.Length - i == 2 && path[i] == '.' && path[i + 1] == '.'))
            {
                break;
            }

            // E: move the first path segment (including initial "/" if any) to output
            if (path[i] == '/')
            {
                output.Append('/');
                i++;
            }

            while (i < path.Length && path[i] != '/')
            {
                output.Append(path[i]);
                i++;
            }
        }

        return output.ToString();
    }

    /// <summary>
    /// Removes the last segment from the output buffer.
    /// </summary>
    private static void RemoveLastSegment(System.Text.StringBuilder output)
    {
        int lastSlash = -1;
        for (int i = output.Length - 1; i >= 0; i--)
        {
            if (output[i] == '/')
            {
                lastSlash = i;
                break;
            }
        }

        if (lastSlash >= 0)
            output.Length = lastSlash;
        else
            output.Clear();
    }

    #endregion

    #region Character Classification

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPnCharsBase(int ch)
    {
        if (ch == -1) return false;
        // Check code point ranges per W3C TriG grammar
        return (ch >= 'A' && ch <= 'Z') ||
               (ch >= 'a' && ch <= 'z') ||
               (ch >= 0x00C0 && ch <= 0x00D6) ||
               (ch >= 0x00D8 && ch <= 0x00F6) ||
               (ch >= 0x00F8 && ch <= 0x02FF) ||
               (ch >= 0x0370 && ch <= 0x037D) ||
               (ch >= 0x037F && ch <= 0x1FFF) ||
               (ch >= 0x200C && ch <= 0x200D) ||
               (ch >= 0x2070 && ch <= 0x218F) ||
               (ch >= 0x2C00 && ch <= 0x2FEF) ||
               (ch >= 0x3001 && ch <= 0xD7FF) ||
               (ch >= 0xF900 && ch <= 0xFDCF) ||
               (ch >= 0xFDF0 && ch <= 0xFFFD) ||
               (ch >= 0x10000 && ch <= 0xEFFFF);  // Supplementary planes
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPnCharsU(int ch) => IsPnCharsBase(ch) || ch == '_';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPnChars(int ch)
    {
        if (ch == -1) return false;
        // PN_CHARS per W3C grammar - includes PN_CHARS_U plus combining chars and digits
        return IsPnCharsU(ch) ||
               ch == '-' ||
               (ch >= '0' && ch <= '9') ||
               ch == 0x00B7 ||
               (ch >= 0x0300 && ch <= 0x036F) ||
               (ch >= 0x203F && ch <= 0x2040);
    }

    #endregion

    #region Error Handling

    private Exception ParserException(string message) =>
        new InvalidDataException($"TriG parse error at line {_line}, column {_column}: {message}");

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
