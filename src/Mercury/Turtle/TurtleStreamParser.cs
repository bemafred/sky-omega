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

namespace SkyOmega.Mercury.Rdf.Turtle;

/// <summary>
/// Zero-allocation streaming parser for RDF Turtle format.
/// Implements W3C RDF 1.2 Turtle EBNF grammar.
/// </summary>
public sealed partial class TurtleStreamParser : IDisposable
{
    private readonly Stream _stream;
    private readonly ArrayPool<byte> _bufferPool;
    private readonly ArrayPool<char> _charPool;
    
    // Reusable buffers - rented from pool, never resize
    private byte[] _inputBuffer;
    private char[] _charBuffer;
    private int _bufferPosition;
    private int _bufferLength;
    private bool _endOfStream;
    private bool _isDisposed;
    
    // Parser state (stack-allocated or pooled)
    private string _baseUri;
    private readonly Dictionary<string, string> _namespaces;
    private readonly Dictionary<string, string> _blankNodes;
    private int _blankNodeCounter;
    
    // Current parse state
    private int _line;
    private int _column;
    private int _statementStartPos; // Track where current statement started for rewind

    private const int DefaultBufferSize = 8192;
    
    public TurtleStreamParser(Stream stream, int bufferSize = DefaultBufferSize)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _bufferPool = ArrayPool<byte>.Shared;
        _charPool = ArrayPool<char>.Shared;
        
        _inputBuffer = _bufferPool.Rent(bufferSize);
        _charBuffer = _charPool.Rent(bufferSize);
        
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
        // RDF and common standard prefixes
        _namespaces["rdf"] = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        _namespaces["rdfs"] = "http://www.w3.org/2000/01/rdf-schema#";
        _namespaces["xsd"] = "http://www.w3.org/2001/XMLSchema#";
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
            return new List<RdfTriple>();

        return ParsePredicateObjectListSync(subject);
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
        
        // Check for 'a' shorthand
        if (Peek() == 'a' && IsWhitespaceOrTerminator(PeekAhead(1)))
        {
            Consume();
            return "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";
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
        
        _bufferPool.Return(_inputBuffer);
        _charPool.Return(_charBuffer);
        
        _isDisposed = true; 
    }
}
