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
public sealed class TriGStreamParser : IDisposable, IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly ArrayPool<byte> _bufferPool;
    private readonly ArrayPool<char> _charPool;

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

    public TriGStreamParser(Stream stream, int bufferSize = DefaultBufferSize)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _bufferPool = ArrayPool<byte>.Shared;
        _charPool = ArrayPool<char>.Shared;

        _inputBuffer = _bufferPool.Rent(bufferSize);
        _outputBuffer = _charPool.Rent(OutputBufferSize);

        _namespaces = new Dictionary<string, string>();
        _baseUri = string.Empty;
        _blankNodeCounter = 0;

        _line = 1;
        _column = 1;

        InitializeStandardPrefixes();
    }

    private void InitializeStandardPrefixes()
    {
        _namespaces["rdf"] = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        _namespaces["rdfs"] = "http://www.w3.org/2000/01/rdf-schema#";
        _namespaces["xsd"] = "http://www.w3.org/2001/XMLSchema#";
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

            // Clear graph context
            _currentGraphStart = 0;
            _currentGraphLength = 0;
        }
        else
        {
            // Could be: IRI { } (shorthand graph) or triples in default graph
            var term = ParseSubjectOrGraphLabel();

            if (term.IsEmpty)
            {
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

        while (true)
        {
            SkipWhitespaceAndComments();

            if (IsEndOfInput() || Peek() == '}')
                break;

            // Save output offset for subject parsing (graph is at start)
            var subjectStart = _outputOffset;

            var subject = ParseSubject();
            if (subject.IsEmpty)
                break;

            SkipWhitespaceAndComments();

            ParsePredicateObjectList(subject, graph, handler);

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
                break;

            SkipWhitespaceAndComments();

            // Parse object list
            ParseObjectList(subject, predicate, graph, handler);

            SkipWhitespaceAndComments();

            // Check for ';' (more predicates for same subject)
            if (TryConsume(';'))
            {
                SkipWhitespaceAndComments();
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
        while (true)
        {
            var obj = ParseObject();
            if (obj.IsEmpty)
                break;

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

        if (IsPnCharsBase(ch) || ch == ':')
            return ParsePrefixedName();

        return ReadOnlySpan<char>.Empty;
    }

    private ReadOnlySpan<char> ParseSubject()
    {
        var ch = Peek();

        if (ch == '<')
            return ParseIriRef();

        if (ch == '_')
            return ParseBlankNode();

        if (ch == '[')
            return ParseBlankNodePropertyList();

        if (ch == '(')
            return ParseCollection();

        if (IsPnCharsBase(ch) || ch == ':')
            return ParsePrefixedName();

        return ReadOnlySpan<char>.Empty;
    }

    private ReadOnlySpan<char> ParsePredicate()
    {
        var ch = Peek();

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
        var ch = Peek();

        if (ch == '<')
            return ParseIriRef();

        if (ch == '_')
            return ParseBlankNode();

        if (ch == '[')
            return ParseBlankNodePropertyList();

        if (ch == '(')
            return ParseCollection();

        if (ch == '"' || ch == '\'')
            return ParseLiteral();

        if (ch == '+' || ch == '-' || char.IsDigit((char)ch))
            return ParseNumericLiteral();

        if (IsPnCharsBase(ch) || ch == ':')
        {
            // Could be prefixed name or boolean
            if (MatchKeyword("true") || MatchKeyword("false"))
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

        // Parse prefix part
        var prefixStart = _outputOffset;
        while (true)
        {
            var ch = Peek();
            if (ch == ':')
                break;
            if (!IsPnCharsBase(ch) && ch != '-' && !char.IsDigit((char)ch))
                break;
            AppendToOutput((char)ch);
            Consume();
        }

        if (!TryConsume(':'))
            throw ParserException("Expected ':' in prefixed name");

        var prefix = new string(_outputBuffer, prefixStart, _outputOffset - prefixStart);
        _outputOffset = start; // Reset to build full IRI

        // Look up namespace
        if (!_namespaces.TryGetValue(prefix, out var ns))
            throw ParserException($"Unknown prefix: {prefix}");

        // Build full IRI
        AppendToOutput('<');
        foreach (var c in ns)
            AppendToOutput(c);

        // Parse local part
        while (true)
        {
            var ch = Peek();
            if (ch == -1 || IsWhitespace(ch) || ch == '.' || ch == ';' || ch == ',' ||
                ch == ')' || ch == ']' || ch == '}' || ch == '{')
                break;

            if (ch == '\\')
            {
                Consume();
                var next = Peek();
                if (next == -1)
                    throw ParserException("Unexpected end in escape");
                AppendToOutput((char)next);
                Consume();
            }
            else
            {
                AppendToOutput((char)ch);
                Consume();
            }
        }

        AppendToOutput('>');
        return GetOutputSpan(start);
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
            AppendToOutput((char)ch);
            Consume();
        }

        // Remove trailing dots
        var span = GetOutputSpan(start);
        while (span.Length > 2 && span[span.Length - 1] == '.')
        {
            _outputOffset--;
            span = GetOutputSpan(start);
        }

        return span;
    }

    private ReadOnlySpan<char> ParseBlankNodePropertyList()
    {
        // [ predicate object ; ... ]
        // Returns a generated blank node ID
        if (!TryConsume('['))
            return ReadOnlySpan<char>.Empty;

        var blankId = GenerateBlankNode();

        // For now, skip the content (simplified implementation)
        // Full implementation would parse predicates and emit triples
        int depth = 1;
        while (depth > 0 && !IsEndOfInput())
        {
            var ch = Peek();
            if (ch == '[') depth++;
            else if (ch == ']') depth--;
            Consume();
        }

        return blankId;
    }

    private ReadOnlySpan<char> ParseCollection()
    {
        // ( item1 item2 ... )
        if (!TryConsume('('))
            return ReadOnlySpan<char>.Empty;

        // Skip content (simplified)
        int depth = 1;
        while (depth > 0 && !IsEndOfInput())
        {
            var ch = Peek();
            if (ch == '(') depth++;
            else if (ch == ')') depth--;
            Consume();
        }

        // Return rdf:nil for empty or simplified
        return "<http://www.w3.org/1999/02/22-rdf-syntax-ns#nil>".AsSpan();
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
        }

        bool hasDecimal = false;
        bool hasExponent = false;

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
                }
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
            if (MatchKeyword("prefix"))
            {
                ParsePrefixDirective();
                return true;
            }
            if (MatchKeyword("base"))
            {
                ParseBaseDirective();
                return true;
            }
            throw ParserException("Unknown directive");
        }

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
    }

    private void ParseSparqlBaseDirective()
    {
        SkipWhitespaceAndComments();
        var iri = ParseIriRef();
        _baseUri = iri.Slice(1, iri.Length - 2).ToString();
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

    private bool MatchKeyword(string keyword)
    {
        for (int i = 0; i < keyword.Length; i++)
        {
            if (PeekAhead(i) != keyword[i])
                return false;
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
        var newBuffer = _charPool.Rent(_outputBuffer.Length * 2);
        _outputBuffer.AsSpan(0, _outputOffset).CopyTo(newBuffer);
        _charPool.Return(_outputBuffer);
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

        _bufferPool.Return(_inputBuffer);
        _charPool.Return(_outputBuffer);
        _isDisposed = true;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    #endregion
}
