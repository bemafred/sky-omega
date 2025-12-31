// NQuadsStreamParser.cs
// Zero-GC streaming N-Quads parser
// Based on W3C RDF 1.1 N-Quads specification
// No external dependencies, only BCL
// .NET 10 / C# 14

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.Runtime.Buffers;

namespace SkyOmega.Mercury.NQuads;

/// <summary>
/// Handler for zero-allocation quad parsing.
/// Receives spans that are valid only during the callback invocation.
/// Graph is empty for the default graph.
/// </summary>
public delegate void QuadHandler(
    ReadOnlySpan<char> subject,
    ReadOnlySpan<char> predicate,
    ReadOnlySpan<char> obj,
    ReadOnlySpan<char> graph);

/// <summary>
/// Immutable record for N-Quads (allocating version).
/// Graph is null or empty for the default graph.
/// </summary>
public readonly record struct RdfQuad(string Subject, string Predicate, string Object, string? Graph)
{
    /// <summary>
    /// Convert to N-Quads format (canonical RDF serialization)
    /// </summary>
    public string ToNQuads()
    {
        var graphPart = string.IsNullOrEmpty(Graph) ? "" : $" {FormatTerm(Graph)}";
        return $"{FormatTerm(Subject)} {FormatTerm(Predicate)} {FormatTerm(Object)}{graphPart} .";
    }

    private static string FormatTerm(string term)
    {
        if (term.StartsWith("_:"))
            return term; // Blank node

        if (term.StartsWith('"'))
            return term; // Literal (already formatted)

        if (term.StartsWith('<'))
            return term; // Already in IRI format

        // IRI without brackets
        return $"<{term}>";
    }
}

/// <summary>
/// Zero-allocation streaming parser for RDF N-Quads format.
/// Implements W3C RDF 1.1 N-Quads specification.
///
/// N-Quads grammar (simplified):
/// [1] nquadsDoc    ::= (statement | comment | EOL)*
/// [2] statement    ::= subject predicate object graphLabel? '.'
/// [3] subject      ::= IRIREF | BLANK_NODE_LABEL
/// [4] predicate    ::= IRIREF
/// [5] object       ::= IRIREF | BLANK_NODE_LABEL | literal
/// [6] graphLabel   ::= IRIREF | BLANK_NODE_LABEL
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
public sealed class NQuadsStreamParser : IDisposable, IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly IBufferManager _bufferManager;

    // Reusable buffers - rented from pool
    private byte[] _inputBuffer;
    private char[] _outputBuffer;
    private int _bufferPosition;
    private int _bufferLength;
    private bool _endOfStream;
    private bool _isDisposed;

    // Output buffer for zero-GC string building
    private int _outputOffset;
    private const int OutputBufferSize = 16384; // 16KB for parsed terms

    // Current parse state
    private int _line;
    private int _column;

    private const int DefaultBufferSize = 8192;

    public NQuadsStreamParser(Stream stream, int bufferSize = DefaultBufferSize, IBufferManager? bufferManager = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _bufferManager = bufferManager ?? PooledBufferManager.Shared;

        _inputBuffer = _bufferManager.Rent<byte>(bufferSize).Array!;
        _outputBuffer = _bufferManager.Rent<char>(OutputBufferSize).Array!;

        _line = 1;
        _column = 1;
    }

    /// <summary>
    /// Parse N-Quads document with zero allocations.
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

            // Refill buffer if running low
            var remaining = _bufferLength - _bufferPosition;
            if (remaining < _inputBuffer.Length / 4 && !_endOfStream)
            {
                await FillBufferAsync(cancellationToken);
                SkipWhitespaceAndComments();
            }

            if (IsEndOfInput())
                break;

            // Try to parse a quad
            if (!ParseQuadZeroGC(handler))
            {
                // Skip to next line on parse failure
                SkipToEndOfLine();
            }
        }
    }

    /// <summary>
    /// Parse N-Quads document and yield RDF quads (allocates strings).
    /// </summary>
    public async IAsyncEnumerable<RdfQuad> ParseAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
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

            var quad = ParseQuadAllocating();
            if (quad.HasValue)
            {
                yield return quad.Value;
            }
            else
            {
                SkipToEndOfLine();
            }
        }
    }

    /// <summary>
    /// Parse a single quad with zero allocations.
    /// </summary>
    private bool ParseQuadZeroGC(QuadHandler handler)
    {
        ResetOutputBuffer();

        // Parse subject
        var subject = ParseSubjectSpan();
        if (subject.IsEmpty)
            return false;

        SkipWhitespace();

        // Parse predicate
        var predicate = ParsePredicateSpan();
        if (predicate.IsEmpty)
            return false;

        SkipWhitespace();

        // Parse object
        var obj = ParseObjectSpan();
        if (obj.IsEmpty)
            return false;

        SkipWhitespace();

        // Parse optional graph label
        ReadOnlySpan<char> graph = ReadOnlySpan<char>.Empty;
        var ch = Peek();
        if (ch != '.' && ch != '\n' && ch != -1)
        {
            graph = ParseGraphLabelSpan();
            SkipWhitespace();
        }

        // Expect '.'
        if (!TryConsume('.'))
            return false;

        // Emit quad
        handler(subject, predicate, obj, graph);
        return true;
    }

    /// <summary>
    /// Parse a single quad (allocating strings).
    /// </summary>
    private RdfQuad? ParseQuadAllocating()
    {
        ResetOutputBuffer();

        var subject = ParseSubjectSpan();
        if (subject.IsEmpty)
            return null;

        SkipWhitespace();

        var predicate = ParsePredicateSpan();
        if (predicate.IsEmpty)
            return null;

        SkipWhitespace();

        var obj = ParseObjectSpan();
        if (obj.IsEmpty)
            return null;

        SkipWhitespace();

        // Parse optional graph label
        string? graph = null;
        var ch = Peek();
        if (ch != '.' && ch != '\n' && ch != -1)
        {
            var graphSpan = ParseGraphLabelSpan();
            if (!graphSpan.IsEmpty)
                graph = graphSpan.ToString();
            SkipWhitespace();
        }

        if (!TryConsume('.'))
            return null;

        return new RdfQuad(subject.ToString(), predicate.ToString(), obj.ToString(), graph);
    }

    #region Terminal Parsing

    /// <summary>
    /// [3] subject ::= IRIREF | BLANK_NODE_LABEL
    /// </summary>
    private ReadOnlySpan<char> ParseSubjectSpan()
    {
        var ch = Peek();

        if (ch == '<')
            return ParseIriRefSpan();

        if (ch == '_')
            return ParseBlankNodeSpan();

        return ReadOnlySpan<char>.Empty;
    }

    /// <summary>
    /// [4] predicate ::= IRIREF
    /// </summary>
    private ReadOnlySpan<char> ParsePredicateSpan()
    {
        if (Peek() != '<')
            return ReadOnlySpan<char>.Empty;

        return ParseIriRefSpan();
    }

    /// <summary>
    /// [5] object ::= IRIREF | BLANK_NODE_LABEL | literal
    /// </summary>
    private ReadOnlySpan<char> ParseObjectSpan()
    {
        var ch = Peek();

        if (ch == '<')
            return ParseIriRefSpan();

        if (ch == '_')
            return ParseBlankNodeSpan();

        if (ch == '"')
            return ParseLiteralSpan();

        return ReadOnlySpan<char>.Empty;
    }

    /// <summary>
    /// [6] graphLabel ::= IRIREF | BLANK_NODE_LABEL
    /// </summary>
    private ReadOnlySpan<char> ParseGraphLabelSpan()
    {
        var ch = Peek();

        if (ch == '<')
            return ParseIriRefSpan();

        if (ch == '_')
            return ParseBlankNodeSpan();

        return ReadOnlySpan<char>.Empty;
    }

    /// <summary>
    /// IRIREF ::= '&lt;' ([^#x00-#x20&lt;&gt;"{}|^`\] | UCHAR)* '&gt;'
    /// Returns IRI with angle brackets included.
    /// </summary>
    private ReadOnlySpan<char> ParseIriRefSpan()
    {
        if (Peek() != '<')
            return ReadOnlySpan<char>.Empty;

        int start = _outputOffset;
        AppendToOutput('<');
        Consume();

        while (true)
        {
            var ch = Peek();

            if (ch == -1)
                throw ParserException("Unterminated IRI reference");

            if (ch == '>')
            {
                AppendToOutput('>');
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
                    var escaped = ParseUnicodeEscape(4);
                    AppendToOutput(escaped);
                }
                else if (next == 'U')
                {
                    Consume();
                    var escaped = ParseUnicodeEscape(8);
                    AppendToOutput(escaped);
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

        return GetOutputSpan(start);
    }

    /// <summary>
    /// BLANK_NODE_LABEL ::= '_:' (PN_CHARS_U | [0-9]) ((PN_CHARS | '.')* PN_CHARS)?
    /// </summary>
    private ReadOnlySpan<char> ParseBlankNodeSpan()
    {
        if (Peek() != '_' || PeekAhead(1) != ':')
            return ReadOnlySpan<char>.Empty;

        int start = _outputOffset;
        AppendToOutput('_');
        Consume();
        AppendToOutput(':');
        Consume();

        // First character after _:
        var ch = Peek();
        if (!IsPnCharsU(ch) && !char.IsDigit((char)ch))
            throw ParserException("Invalid blank node label");

        AppendToOutput((char)ch);
        Consume();

        // Rest of label
        while (true)
        {
            ch = Peek();
            if (ch == -1 || (!IsPnChars(ch) && ch != '.'))
                break;

            AppendToOutput((char)ch);
            Consume();
        }

        // Remove trailing dots (not part of label)
        var span = GetOutputSpan(start);
        while (span.Length > 2 && span[span.Length - 1] == '.')
        {
            _outputOffset--;
            span = GetOutputSpan(start);
        }

        return span;
    }

    /// <summary>
    /// literal ::= STRING_LITERAL_QUOTE ('^^' IRIREF | LANGTAG)?
    /// </summary>
    private ReadOnlySpan<char> ParseLiteralSpan()
    {
        if (Peek() != '"')
            return ReadOnlySpan<char>.Empty;

        int start = _outputOffset;
        AppendToOutput('"');
        Consume();

        // Parse string content
        while (true)
        {
            var ch = Peek();

            if (ch == -1)
                throw ParserException("Unterminated string literal");

            if (ch == '"')
            {
                AppendToOutput('"');
                Consume();
                break;
            }

            if (ch == '\\')
            {
                Consume();
                var escaped = ParseEscapeSequence();
                AppendToOutput(escaped);
            }
            else
            {
                AppendToOutput((char)ch);
                Consume();
            }
        }

        // Check for language tag or datatype
        var next = Peek();
        if (next == '@')
        {
            // Language tag
            AppendToOutput('@');
            Consume();

            while (true)
            {
                var ch = Peek();
                if (ch == -1 || IsWhitespace(ch) || ch == '.')
                    break;

                AppendToOutput((char)ch);
                Consume();
            }
        }
        else if (next == '^' && PeekAhead(1) == '^')
        {
            // Datatype
            AppendToOutput('^');
            Consume();
            AppendToOutput('^');
            Consume();

            // Parse datatype IRI
            var datatypeIri = ParseIriRefSpan();
            if (datatypeIri.IsEmpty)
                throw ParserException("Expected datatype IRI");

            // datatypeIri is already appended to output buffer
        }

        return GetOutputSpan(start);
    }

    #endregion

    #region Escape Handling

    /// <summary>
    /// Parse string escape sequence (N-Quads escapes).
    /// </summary>
    private char ParseEscapeSequence()
    {
        var ch = Peek();

        if (ch == -1)
            throw ParserException("Unexpected end of input in escape sequence");

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
            _ => throw ParserException($"Invalid escape sequence: \\{(char)ch}")
        };
    }

    /// <summary>
    /// Parse unicode escape (\uXXXX or \UXXXXXXXX).
    /// </summary>
    private char ParseUnicodeEscape(int digits = 4)
    {
        var value = 0;

        for (int i = 0; i < digits; i++)
        {
            var ch = Peek();

            if (ch == -1)
                throw ParserException("Unexpected end of input in unicode escape");

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

        // Reject surrogate code points
        if (value >= 0xD800 && value <= 0xDFFF)
            throw ParserException($"Invalid unicode: surrogate U+{value:X4}");

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

    private async ValueTask FillBufferAsync(CancellationToken cancellationToken)
    {
        if (_endOfStream)
            return;

        // Shift remaining data to beginning
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

        // Fill remaining space
        var bytesRead = await _stream.ReadAsync(
            _inputBuffer.AsMemory(_bufferLength, _inputBuffer.Length - _bufferLength),
            cancellationToken);

        if (bytesRead == 0)
        {
            _endOfStream = true;
        }
        else
        {
            _bufferLength += bytesRead;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsEndOfInput()
    {
        return _endOfStream && _bufferPosition >= _bufferLength;
    }

    private void SkipWhitespace()
    {
        while (true)
        {
            var ch = Peek();
            if (ch == -1 || !IsWhitespace(ch))
                break;
            Consume();
        }
    }

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
                SkipToEndOfLine();
                continue;
            }

            break;
        }
    }

    private void SkipToEndOfLine()
    {
        while (true)
        {
            var ch = Peek();
            if (ch == -1 || ch == '\n')
            {
                if (ch == '\n')
                    Consume();
                break;
            }
            Consume();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsWhitespace(int ch)
    {
        return ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n';
    }

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> GetOutputSpan(int startOffset)
        => _outputBuffer.AsSpan(startOffset, _outputOffset - startOffset);

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
    private static bool IsPnCharsU(int ch)
    {
        return IsPnCharsBase(ch) || ch == '_';
    }

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

    private Exception ParserException(string message)
    {
        return new InvalidDataException($"N-Quads parse error at line {_line}, column {_column}: {message}");
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
