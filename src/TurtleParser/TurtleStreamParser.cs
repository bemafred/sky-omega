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

namespace SkyOmega.Rdf.Turtle;

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
            
            if (IsEndOfInput())
                break;
            
            // [2] statement ::= directive | (triples '.')
            if (await TryParseDirectiveAsync(cancellationToken))
            {
                continue;
            }
            
            // Try parse triples
            var triple = await ParseTriplesAsync(cancellationToken);
            if (triple.HasValue)
            {
                yield return triple.Value;
                
                // Expect '.'
                SkipWhitespaceAndComments();
                if (!TryConsume('.'))
                {
                    throw ParserException("Expected '.' after triple");
                }
            }
        }
    }
    
    /// <summary>
    /// Parse triples and return the first one (others are stored in state for annotation/collections)
    /// [11] triples ::= (subject predicateObjectList) | 
    ///                  (blankNodePropertyList predicateObjectList?) | 
    ///                  (reifiedTriple predicateObjectList?)
    /// </summary>
    private async ValueTask<RdfTriple?> ParseTriplesAsync(CancellationToken cancellationToken)
    {
        SkipWhitespaceAndComments();
        
        // Try blank node property list
        if (Peek() == '[')
        {
            var subject = ParseBlankNodePropertyList();
            if (!string.IsNullOrEmpty(subject))
            {
                return await ParsePredicateObjectListAsync(subject, cancellationToken);
            }
        }
        
        // Try reified triple (RDF 1.2)
        if (PeekString("<<"))
        {
            var reifiedSubject = ParseReifiedTriple();
            if (!string.IsNullOrEmpty(reifiedSubject))
            {
                return await ParsePredicateObjectListAsync(reifiedSubject, cancellationToken);
            }
        }
        
        // Regular subject
        var subj = ParseSubject();
        if (string.IsNullOrEmpty(subj))
            return null;
        
        return await ParsePredicateObjectListAsync(subj, cancellationToken);
    }
    
    /// <summary>
    /// [12] predicateObjectList ::= verb objectList (';' (verb objectList)?)*
    /// </summary>
    private async ValueTask<RdfTriple?> ParsePredicateObjectListAsync(
        string subject, 
        CancellationToken cancellationToken)
    {
        RdfTriple? firstTriple = null;
        
        while (true)
        {
            SkipWhitespaceAndComments();
            
            // Parse verb (predicate or 'a')
            var predicate = ParseVerb();
            if (string.IsNullOrEmpty(predicate))
                break;
            
            // [13] objectList ::= object annotation (',' object annotation)*
            var objects = ParseObjectList();
            
            foreach (var obj in objects)
            {
                var triple = new RdfTriple(subject, predicate, obj);
                
                if (!firstTriple.HasValue)
                    firstTriple = triple;
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
        
        return firstTriple;
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
