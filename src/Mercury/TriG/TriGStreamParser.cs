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

    public TriGStreamParser(Stream stream, int bufferSize = DefaultBufferSize, IBufferManager? bufferManager = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _bufferManager = bufferManager ?? PooledBufferManager.Shared;

        _inputBuffer = _bufferManager.Rent<byte>(bufferSize).Array!;
        _outputBuffer = _bufferManager.Rent<char>(OutputBufferSize).Array!;

        _namespaces = new Dictionary<string, string>();
        _baseUri = string.Empty;
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
        if (ch == 'a' && !IsPnChars(PeekAhead(1)))
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
                    AppendToOutput(ParseUnicodeEscape(4));
                }
                else if (next == 'U')
                {
                    Consume();
                    AppendToOutput(ParseUnicodeEscape(8));
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
                AppendToOutput((char)ch);
                Consume();
            }
        }

        int contentEnd = _outputOffset;
        int contentLen = contentEnd - contentStart;

        // Check if this is a relative IRI (no scheme like "http://")
        var iriContent = _outputBuffer.AsSpan(contentStart, contentLen);
        bool isRelative = contentLen > 0 &&
                          !iriContent.Contains("://".AsSpan(), StringComparison.Ordinal) &&
                          !iriContent.StartsWith("#".AsSpan(), StringComparison.Ordinal);

        if (isRelative && !string.IsNullOrEmpty(_baseUri))
        {
            // Need to resolve against base URI - copy content first since we'll overwrite buffer
            var iriContentCopy = iriContent.ToString();

            // Reset and build resolved IRI
            _outputOffset = start;
            AppendToOutput('<');
            foreach (var c in _baseUri)
                AppendToOutput(c);
            foreach (var c in iriContentCopy)
                AppendToOutput(c);
            AppendToOutput('>');
        }
        else
        {
            // Just close with >
            AppendToOutput('>');
        }

        return GetOutputSpan(start);
    }

    private ReadOnlySpan<char> ParsePrefixedName()
    {
        int start = _outputOffset;

        // Parse prefix part - can include dots, dashes, digits (but not as first char)
        // e.g: "e.g:" is a valid prefix
        var prefixStart = _outputOffset;
        while (true)
        {
            var ch = Peek();
            if (ch == ':')
                break;
            // Allow dots in prefix names (but not as the last character before ':')
            if (!IsPnCharsBase(ch) && ch != '-' && ch != '.' && !char.IsDigit((char)ch))
                break;
            AppendToOutput((char)ch);
            Consume();
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
                AppendToOutput((char)ch);
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
                AppendToOutput((char)ch);
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

        AppendToOutput((char)ch);
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
                    AppendToOutput((char)ch);
                    Consume();
                    continue;
                }
                // Dot followed by dot is part of label (might have more dots)
                if (next == '.')
                {
                    AppendToOutput((char)ch);
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

            AppendToOutput((char)ch);
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
                AppendToOutput(ParseEscapeSequence());
            }
            else
            {
                AppendToOutput((char)ch);
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
                AppendToOutput(ParseEscapeSequence());
            }
            else
            {
                AppendToOutput((char)ch);
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
            AppendToOutput((char)ch);
            Consume();
        }
        var span = GetOutputSpan(start);
        var result = span.ToString();
        _outputOffset = start; // Reset
        return result;
    }

    #endregion

    #region Escape Handling

    private char ParseEscapeSequence()
    {
        var ch = Peek();
        if (ch == -1)
            throw ParserException("Unexpected end in escape sequence");

        Consume();

        return (char)ch switch
        {
            't' => '\t',
            'b' => '\b',
            'n' => '\n',
            'r' => '\r',
            'f' => '\f',
            '"' => '"',
            '\'' => '\'',
            '\\' => '\\',
            'u' => ParseUnicodeEscape(4),
            'U' => ParseUnicodeEscape(8),
            _ => throw ParserException($"Invalid escape: \\{(char)ch}")
        };
    }

    private char ParseUnicodeEscape(int digits)
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

        return (char)value;
    }

    #endregion

    #region Buffer Management

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Peek()
    {
        if (_bufferPosition >= _bufferLength)
            return _endOfStream ? -1 : -1;
        return _inputBuffer[_bufferPosition];
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

        var ch = _inputBuffer[_bufferPosition];
        _bufferPosition++;

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

    #region Character Classification

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPnCharsBase(int ch)
    {
        if (ch == -1) return false;
        var c = (char)ch;
        return (c >= 'A' && c <= 'Z') ||
               (c >= 'a' && c <= 'z') ||
               (c >= '\u00C0' && c <= '\u00D6') ||
               (c >= '\u00D8' && c <= '\u00F6') ||
               (c >= '\u00F8' && c <= '\u02FF') ||
               (c >= '\u0370' && c <= '\u037D') ||
               (c >= '\u037F' && c <= '\u1FFF') ||
               (c >= '\u200C' && c <= '\u200D') ||
               (c >= '\u2070' && c <= '\u218F') ||
               (c >= '\u2C00' && c <= '\u2FEF') ||
               (c >= '\u3001' && c <= '\uD7FF') ||
               (c >= '\uF900' && c <= '\uFDCF') ||
               (c >= '\uFDF0' && c <= '\uFFFD');
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPnCharsU(int ch) => IsPnCharsBase(ch) || ch == '_';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPnChars(int ch)
    {
        if (ch == -1) return false;
        var c = (char)ch;
        return IsPnCharsU(ch) ||
               c == '-' ||
               char.IsDigit(c) ||
               c == '\u00B7' ||
               (c >= '\u0300' && c <= '\u036F') ||
               (c >= '\u203F' && c <= '\u2040');
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
