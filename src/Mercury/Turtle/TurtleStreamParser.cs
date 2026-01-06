// TurtleStreamParser.cs
// Zero-GC streaming Turtle (RDF 1.2) parser
// Based on W3C RDF 1.2 Turtle specification EBNF grammar
// No external dependencies, only BCL
// .NET 10 / C# 14

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.Runtime.Buffers;

namespace SkyOmega.Mercury.Rdf.Turtle;

/// <summary>
/// Handler for zero-allocation triple parsing.
/// Receives spans that are valid only during the callback invocation.
/// </summary>
public delegate void TripleHandler(
    ReadOnlySpan<char> subject,
    ReadOnlySpan<char> predicate,
    ReadOnlySpan<char> obj);

/// <summary>
/// Zero-allocation streaming parser for RDF Turtle format.
/// Implements W3C RDF 1.2 Turtle EBNF grammar.
///
/// For zero-GC parsing, use ParseAsync(TripleHandler).
/// The IAsyncEnumerable overload allocates strings for compatibility.
/// </summary>
/// <remarks>
/// <para><b>Thread Safety:</b> This class is NOT thread-safe. Each instance
/// maintains internal parsing state and buffers that cannot be shared across threads.
/// Create a separate instance per thread or serialize access with locking.</para>
/// <para><b>Usage Pattern:</b> Create one instance per stream. The parser is
/// designed for sequential parsing of a single Turtle document. Dispose when done
/// to return pooled buffers.</para>
/// </remarks>
public sealed partial class TurtleStreamParser : IDisposable
{
    private readonly Stream _stream;
    private readonly IBufferManager _bufferManager;

    // Reusable buffers - rented from pool, never resize
    private byte[] _inputBuffer;
    private char[] _charBuffer;
    private int _bufferPosition;
    private int _bufferLength;
    private bool _endOfStream;
    private bool _isDisposed;

    // Output buffer for zero-GC string building
    private char[] _outputBuffer;
    private int _outputOffset;
    private const int OutputBufferSize = 16384; // 16KB for parsed terms

    // Parser state
    private string _baseUri;
    private readonly Dictionary<string, string> _namespaces;
    private readonly Dictionary<string, string> _blankNodes;
    private int _blankNodeCounter;

    // Reusable StringBuilder for legacy API (allocating)
    private readonly StringBuilder _sb = new StringBuilder(256);

    // Pending triples from RDF-star reification (emitted alongside main triples)
    private readonly List<RdfTriple> _pendingTriples = new();

    // Handler for zero-GC reification triple emission
    private TripleHandler? _zeroGcHandler;

    // Current parse state
    private int _line;
    private int _column;
    private int _statementStartPos; // Track where current statement started for rewind

    private const int DefaultBufferSize = 8192;

    public TurtleStreamParser(Stream stream, int bufferSize = DefaultBufferSize, IBufferManager? bufferManager = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _bufferManager = bufferManager ?? PooledBufferManager.Shared;

        _inputBuffer = _bufferManager.Rent<byte>(bufferSize).Array!;
        _charBuffer = _bufferManager.Rent<char>(bufferSize).Array!;
        _outputBuffer = _bufferManager.Rent<char>(OutputBufferSize).Array!;

        _namespaces = new Dictionary<string, string>();
        _blankNodes = new Dictionary<string, string>();
        _baseUri = string.Empty;
        _blankNodeCounter = 0;

        _line = 1;
        _column = 1;

        InitializeStandardPrefixes();
    }
    
    private void InitializeStandardPrefixes()
    {
        // RDF and common standard prefixes (include angle brackets for consistency with parsed prefixes)
        _namespaces["rdf"] = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#>";
        _namespaces["rdfs"] = "<http://www.w3.org/2000/01/rdf-schema#>";
        _namespaces["xsd"] = "<http://www.w3.org/2001/XMLSchema#>";
    }
    
    /// <summary>
    /// Parse Turtle document and yield RDF triples.
    /// Zero-allocation streaming - triples are yielded as they are parsed.
    /// </summary>
    public async IAsyncEnumerable<RdfTriple> ParseAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // [1] turtleDoc ::= statement*
        await FillBufferAsync(cancellationToken);

        while (!_endOfStream || _bufferPosition < _bufferLength)
        {
            SkipWhitespaceAndComments();

            // Refill buffer if running low (keep at least 25% capacity for partial statements)
            var remainingBytes = _bufferLength - _bufferPosition;
            if (remainingBytes < _inputBuffer.Length / 4 && !_endOfStream)
            {
                await FillBufferAsync(cancellationToken);
                SkipWhitespaceAndComments();
            }

            if (IsEndOfInput())
                break;

            // Mark start of statement for potential rewind on buffer exhaustion
            _statementStartPos = _bufferPosition;

            // [2] statement ::= directive | (triples '.')
            if (await TryParseDirectiveAsync(cancellationToken))
            {
                continue;
            }

            // Try parse triples - collects all triples from predicate-object lists
            var triples = ParseTriplesSync();

            if (triples.Count > 0)
            {
                foreach (var triple in triples)
                {
                    yield return triple;
                }

                // Expect '.'
                SkipWhitespaceAndComments();
                if (!TryConsume('.'))
                {
                    throw ParserException("Expected '.' after triple");
                }
            }
            else if (_bufferPosition == _statementStartPos && !IsEndOfInput())
            {
                // Parsing failed without consuming anything - try buffer refill
                if (!_endOfStream)
                {
                    await FillBufferAsync(cancellationToken);
                    continue;
                }

                // Still can't parse - there's invalid content at this position
                var ch = Peek();
                throw ParserException($"Unexpected character '{(char)ch}' (0x{ch:X2}) - cannot parse as directive or triple");
            }
            else if (triples.Count == 0 && _bufferPosition > _statementStartPos)
            {
                // Partial consumption but no triples - likely buffer boundary issue
                // Rewind to statement start and refill buffer
                if (!_endOfStream)
                {
                    _bufferPosition = _statementStartPos;
                    await FillBufferAsync(cancellationToken);
                    continue;
                }

                throw ParserException("Incomplete statement at end of input");
            }
        }
    }
    
    /// <summary>
    /// Parse triples and return all triples from predicate-object lists.
    /// [11] triples ::= (subject predicateObjectList) |
    ///                  (blankNodePropertyList predicateObjectList?) |
    ///                  (reifiedTriple predicateObjectList?)
    /// </summary>
    private List<RdfTriple> ParseTriplesSync()
    {
        SkipWhitespaceAndComments();

        string? subject = null;

        // Try blank node property list
        if (Peek() == '[')
        {
            subject = ParseBlankNodePropertyList();
        }
        // Try reified triple (RDF 1.2)
        else if (PeekString("<<"))
        {
            subject = ParseReifiedTriple();
        }
        // Regular subject
        else
        {
            subject = ParseSubject();
        }

        if (string.IsNullOrEmpty(subject))
        {
            // Still drain any pending triples from nested reification
            if (_pendingTriples.Count > 0)
            {
                var pending = new List<RdfTriple>(_pendingTriples);
                _pendingTriples.Clear();
                return pending;
            }
            return new List<RdfTriple>();
        }

        var result = ParsePredicateObjectListSync(subject);

        // Drain any pending triples from RDF-star reification
        if (_pendingTriples.Count > 0)
        {
            result.AddRange(_pendingTriples);
            _pendingTriples.Clear();
        }

        return result;
    }

    /// <summary>
    /// [12] predicateObjectList ::= verb objectList (';' (verb objectList)?)*
    /// Returns all triples from all predicate-object pairs.
    /// </summary>
    private List<RdfTriple> ParsePredicateObjectListSync(string subject)
    {
        var result = new List<RdfTriple>();

        while (true)
        {
            SkipWhitespaceAndComments();

            // Parse verb (predicate or 'a')
            var predicate = ParseVerb();
            if (string.IsNullOrEmpty(predicate))
                break;

            // [13] objectList ::= object annotation (',' object annotation)*
            var objects = ParseObjectList();

            // Add a triple for each object in the list
            foreach (var obj in objects)
            {
                result.Add(new RdfTriple(subject, predicate, obj));
            }

            SkipWhitespaceAndComments();

            // Check for continuation with ';'
            if (!TryConsume(';'))
                break;

            SkipWhitespaceAndComments();

            // Optional trailing semicolon
            if (Peek() == '.' || Peek() == ']' || Peek() == '}')
                break;
        }

        return result;
    }
    
    /// <summary>
    /// [14] verb ::= predicate | 'a'
    /// </summary>
    private string ParseVerb()
    {
        SkipWhitespaceAndComments();

        // Check for 'a' shorthand - include angle brackets for consistency
        if (Peek() == 'a' && IsWhitespaceOrTerminator(PeekAhead(1)))
        {
            Consume();
            return "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>";
        }

        return ParseIri();
    }
    
    /// <summary>
    /// [15] subject ::= iri | BlankNode | collection
    /// </summary>
    private string ParseSubject()
    {
        SkipWhitespaceAndComments();
        
        var ch = Peek();
        
        // Collection
        if (ch == '(')
            return ParseCollection();
        
        // Blank node
        if (ch == '_' && PeekAhead(1) == ':')
            return ParseBlankNode();
        
        // IRI
        return ParseIri();
    }
    
    /// <summary>
    /// [13] objectList ::= object annotation (',' object annotation)*
    /// Returns list of object IRIs
    /// </summary>
    private List<string> ParseObjectList()
    {
        var objects = new List<string>();
        
        while (true)
        {
            SkipWhitespaceAndComments();
            
            var obj = ParseObject();
            if (string.IsNullOrEmpty(obj))
                break;
            
            objects.Add(obj);
            
            // Parse annotation if present (RDF 1.2)
            ParseAnnotation();
            
            SkipWhitespaceAndComments();
            
            // Check for continuation with ','
            if (!TryConsume(','))
                break;
        }
        
        return objects;
    }
    
    /// <summary>
    /// [17] object ::= iri | BlankNode | collection | blankNodePropertyList | 
    ///                 literal | tripleTerm | reifiedTriple
    /// </summary>
    private string ParseObject()
    {
        SkipWhitespaceAndComments();
        
        var ch = Peek();
        
        // Blank node property list
        if (ch == '[')
            return ParseBlankNodePropertyList();
        
        // Collection
        if (ch == '(')
            return ParseCollection();
        
        // Blank node
        if (ch == '_' && PeekAhead(1) == ':')
            return ParseBlankNode();
        
        // Triple term (RDF 1.2) - <<( ... )>>
        if (PeekString("<<("))
            return ParseTripleTerm();
        
        // Reified triple (RDF 1.2) - << ... >>
        if (PeekString("<<"))
            return ParseReifiedTriple();
        
        // Literal
        if (ch == '"' || ch == '\'' || char.IsDigit((char)ch) || ch == '+' || ch == '-' || ch == '.')
            return ParseLiteral();
        
        // Boolean literal
        if (PeekString("true") || PeekString("false"))
            return ParseBooleanLiteral();
        
        // IRI
        return ParseIri();
    }
    
    // Continued in next part...
    
    private bool IsWhitespaceOrTerminator(int ch)
    {
        return ch == -1 || char.IsWhiteSpace((char)ch) || 
               ch == '.' || ch == ';' || ch == ',' || 
               ch == ']' || ch == ')' || ch == '>';
    }
    
    private Exception ParserException(string message)
    {
        return new InvalidDataException($"Line {_line}, Column {_column}: {message}");
    }
    
    public void Dispose()
    {
        if (_isDisposed == true)
            return;

        _bufferManager.Return(_inputBuffer);
        _bufferManager.Return(_charBuffer);
        _bufferManager.Return(_outputBuffer);

        _isDisposed = true;
    }

    #region Zero-GC API

    /// <summary>
    /// Parse Turtle document with zero allocations.
    /// Triples are emitted via the handler callback with spans valid only during the call.
    /// </summary>
    public async Task ParseAsync(TripleHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);

        // Store handler for use by ParseReifiedTripleSpan
        _zeroGcHandler = handler;

        await FillBufferAsync(cancellationToken);

        while (!_endOfStream || _bufferPosition < _bufferLength)
        {
            SkipWhitespaceAndComments();

            var remainingBytes = _bufferLength - _bufferPosition;
            if (remainingBytes < _inputBuffer.Length / 4 && !_endOfStream)
            {
                await FillBufferAsync(cancellationToken);
                SkipWhitespaceAndComments();
            }

            if (IsEndOfInput())
                break;

            _statementStartPos = _bufferPosition;

            if (await TryParseDirectiveAsync(cancellationToken))
                continue;

            // Parse triples with zero-GC - emit directly via handler
            if (!ParseTriplesZeroGC(handler))
            {
                if (_bufferPosition == _statementStartPos && !IsEndOfInput())
                {
                    if (!_endOfStream)
                    {
                        await FillBufferAsync(cancellationToken);
                        continue;
                    }

                    var ch = Peek();
                    throw ParserException($"Unexpected character '{(char)ch}' (0x{ch:X2})");
                }

                if (_bufferPosition > _statementStartPos && !_endOfStream)
                {
                    _bufferPosition = _statementStartPos;
                    await FillBufferAsync(cancellationToken);
                    continue;
                }
            }

            SkipWhitespaceAndComments();
            if (!TryConsume('.'))
                throw ParserException("Expected '.' after triple");
        }
    }

    /// <summary>
    /// Parse triples and emit via handler. Returns true if any triples were parsed.
    /// </summary>
    private bool ParseTriplesZeroGC(TripleHandler handler)
    {
        SkipWhitespaceAndComments();
        ResetOutputBuffer();

        ReadOnlySpan<char> subject;

        if (Peek() == '[')
        {
            subject = ParseBlankNodePropertyListSpan();
        }
        else if (PeekString("<<"))
        {
            subject = ParseReifiedTripleSpan();
        }
        else
        {
            subject = ParseSubjectSpan();
        }

        if (subject.IsEmpty)
            return false;

        return ParsePredicateObjectListZeroGC(subject, handler);
    }

    /// <summary>
    /// Parse predicate-object list and emit triples via handler.
    /// </summary>
    private bool ParsePredicateObjectListZeroGC(ReadOnlySpan<char> subject, TripleHandler handler)
    {
        bool emittedAny = false;

        while (true)
        {
            SkipWhitespaceAndComments();

            // Save subject position in output buffer before parsing predicate
            int subjectEnd = _outputOffset;

            var predicate = ParseVerbSpan();
            if (predicate.IsEmpty)
                break;

            // Parse objects and emit triples
            while (true)
            {
                SkipWhitespaceAndComments();

                // Reset output buffer to after predicate for each object
                int predicateEnd = _outputOffset;

                var obj = ParseObjectSpan();
                if (obj.IsEmpty)
                    break;

                // Emit triple via handler
                handler(subject, predicate, obj);
                emittedAny = true;

                // Parse annotation if present (RDF 1.2)
                ParseAnnotation();

                SkipWhitespaceAndComments();

                // Reset for next object
                _outputOffset = predicateEnd;

                if (!TryConsume(','))
                    break;
            }

            SkipWhitespaceAndComments();

            // Reset for next predicate
            _outputOffset = subjectEnd;

            if (!TryConsume(';'))
                break;

            SkipWhitespaceAndComments();

            if (Peek() == '.' || Peek() == ']' || Peek() == '}')
                break;
        }

        return emittedAny;
    }

    /// <summary>
    /// Reset output buffer for new triple.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ResetOutputBuffer() => _outputOffset = 0;

    /// <summary>
    /// Append character to output buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendToOutput(char c)
    {
        if (_outputOffset >= _outputBuffer.Length)
            GrowOutputBuffer();
        _outputBuffer[_outputOffset++] = c;
    }

    /// <summary>
    /// Append span to output buffer.
    /// </summary>
    private void AppendToOutput(ReadOnlySpan<char> span)
    {
        if (_outputOffset + span.Length > _outputBuffer.Length)
            GrowOutputBuffer(_outputOffset + span.Length);
        span.CopyTo(_outputBuffer.AsSpan(_outputOffset));
        _outputOffset += span.Length;
    }

    /// <summary>
    /// Get span from output buffer starting at given offset.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> GetOutputSpan(int startOffset)
        => _outputBuffer.AsSpan(startOffset, _outputOffset - startOffset);

    private void GrowOutputBuffer(int minSize = 0)
    {
        var newSize = Math.Max(_outputBuffer.Length * 2, minSize + 1024);
        var newBuffer = _bufferManager.Rent<char>(newSize).Array!;
        _outputBuffer.AsSpan(0, _outputOffset).CopyTo(newBuffer);
        _bufferManager.Return(_outputBuffer);
        _outputBuffer = newBuffer;
    }

    #endregion
}
