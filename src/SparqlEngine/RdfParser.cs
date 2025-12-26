using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using SparqlEngine.Temporal;

namespace SparqlEngine;

/// <summary>
/// Zero-allocation N-Triples parser using streaming and Span&lt;T&gt;
/// </summary>
public ref struct NTriplesParser
{
    private ReadOnlySpan<char> _source;
    private int _position;

    public NTriplesParser(ReadOnlySpan<char> source)
    {
        _source = source;
        _position = 0;
    }

    /// <summary>
    /// Parse N-Triples format and add to store without intermediate allocations
    /// </summary>
    public void Parse(MultiTemporalStore store)
    {
        while (!IsAtEnd())
        {
            SkipWhitespace();
            if (IsAtEnd() || Peek() == '#')
            {
                SkipLine();
                continue;
            }

            var subject = ParseSubject();
            SkipWhitespace();

            var predicate = ParsePredicate();
            SkipWhitespace();

            var obj = ParseObject();
            SkipWhitespace();

            if (Peek() != '.')
                throw new ParseException("Expected '.' at end of triple");
            Advance();

            store.AddCurrent(subject, predicate, obj);
            SkipWhitespace();
        }
    }

    public ReadOnlySpan<char> ParseSubject()
    {
        var ch = Peek();
        if (ch == '<')
            return ParseIriRef();
        if (ch == '_')
            return ParseBlankNode();

        throw new ParseException("Expected IRI or blank node for subject");
    }

    public ReadOnlySpan<char> ParsePredicate()
    {
        if (Peek() != '<')
            throw new ParseException("Expected IRI for predicate");

        return ParseIriRef();
    }

    public ReadOnlySpan<char> ParseObject()
    {
        var ch = Peek();
        if (ch == '<')
            return ParseIriRef();
        if (ch == '_')
            return ParseBlankNode();
        if (ch == '"')
            return ParseLiteral();

        throw new ParseException("Expected IRI, blank node, or literal for object");
    }

    private ReadOnlySpan<char> ParseIriRef()
    {
        if (Peek() != '<')
            throw new ParseException("Expected '<'");

        var start = _position;
        Advance(); // Skip '<'

        while (!IsAtEnd() && Peek() != '>')
        {
            if (Peek() == '\\')
            {
                Advance(); // Skip escape
                if (!IsAtEnd())
                    Advance();
            }
            else
            {
                Advance();
            }
        }

        if (IsAtEnd())
            throw new ParseException("Unterminated IRI reference");

        Advance(); // Skip '>'
        return _source[start.._position];
    }

    private ReadOnlySpan<char> ParseBlankNode()
    {
        if (Peek() != '_' || PeekNext() != ':')
            throw new ParseException("Expected blank node '_:'");

        var start = _position;
        Advance(); // '_'
        Advance(); // ':'

        while (!IsAtEnd() && !IsWhitespace(Peek()) && Peek() != '.')
            Advance();

        return _source[start.._position];
    }

    private ReadOnlySpan<char> ParseLiteral()
    {
        if (Peek() != '"')
            throw new ParseException("Expected '\"'");

        var start = _position;
        Advance(); // Skip opening '"'

        while (!IsAtEnd())
        {
            var ch = Peek();
            if (ch == '\\')
            {
                Advance(); // Skip escape
                if (!IsAtEnd())
                    Advance();
            }
            else if (ch == '"')
            {
                Advance(); // Skip closing '"'
                break;
            }
            else
            {
                Advance();
            }
        }

        // Handle optional language tag or datatype
        if (!IsAtEnd())
        {
            var next = Peek();
            if (next == '@')
            {
                // Language tag
                Advance();
                while (!IsAtEnd() && !IsWhitespace(Peek()) && Peek() != '.')
                    Advance();
            }
            else if (next == '^' && PeekNext() == '^')
            {
                // Datatype
                Advance(); // '^'
                Advance(); // '^'
                if (Peek() == '<')
                {
                    while (!IsAtEnd() && Peek() != '>')
                        Advance();
                    if (!IsAtEnd())
                        Advance(); // '>'
                }
            }
        }

        return _source[start.._position];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SkipWhitespace()
    {
        while (!IsAtEnd() && IsWhitespace(Peek()))
            Advance();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SkipLine()
    {
        while (!IsAtEnd() && Peek() != '\n')
            Advance();
        if (!IsAtEnd())
            Advance();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsWhitespace(char ch) => ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsAtEnd() => _position >= _source.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char Peek() => IsAtEnd() ? '\0' : _source[_position];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char PeekNext() => _position + 1 >= _source.Length ? '\0' : _source[_position + 1];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char Advance() => IsAtEnd() ? '\0' : _source[_position++];
}

/// <summary>
/// Streaming file parser that processes RDF data in chunks
/// </summary>
public sealed class StreamingRdfLoader : IDisposable
{
    private const int BufferSize = 64 * 1024; // 64KB buffer
    private readonly char[] _buffer;
    private readonly ArrayPool<char> _pool;

    public StreamingRdfLoader()
    {
        _pool = ArrayPool<char>.Shared;
        _buffer = _pool.Rent(BufferSize);
    }

    /// <summary>
    /// Load N-Triples file without loading entire file into memory
    /// </summary>
    public void LoadNTriples(string filePath, MultiTemporalStore store)
    {
        using var reader = new StreamReader(filePath);
        var lineBuffer = _pool.Rent(8192);

        try
        {
            int lineLength = 0;
            bool inLiteral = false;

            while (!reader.EndOfStream)
            {
                var ch = (char)reader.Read();

                if (lineLength >= lineBuffer.Length)
                {
                    // Resize buffer
                    var newBuffer = _pool.Rent(lineBuffer.Length * 2);
                    Array.Copy(lineBuffer, newBuffer, lineLength);
                    _pool.Return(lineBuffer);
                    lineBuffer = newBuffer;
                }

                lineBuffer[lineLength++] = ch;

                // Track if we're inside a literal
                if (ch == '"' && (lineLength < 2 || lineBuffer[lineLength - 2] != '\\'))
                {
                    inLiteral = !inLiteral;
                }

                // End of triple (not inside literal)
                if (ch == '.' && !inLiteral)
                {
                    // Parse the complete triple
                    ReadOnlySpan<char> line = lineBuffer.AsSpan(0, lineLength);
                    var parser = new NTriplesParser(line);

                    try
                    {
                        // Parse subject
                        var subject = parser.ParseSubject();

                        // Parse predicate
                        var predicate = parser.ParsePredicate();

                        // Parse object
                        var obj = parser.ParseObject();

                        store.AddCurrent(subject, predicate, obj);
                    }
                    catch
                    {
                        // Skip malformed triples
                    }

                    lineLength = 0;
                }
            }
        }
        finally
        {
            _pool.Return(lineBuffer);
        }
    }

    public void Dispose()
    {
        _pool.Return(_buffer);
    }
}

public class ParseException : Exception
{
    public ParseException(string message) : base(message) { }
}
