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

            // Parse a quad - N-Quads must be strictly valid
            ParseQuadZeroGC(handler);
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

            // Parse quad - throws on invalid input per W3C N-Quads spec
            yield return ParseQuadAllocating();
        }
    }

    /// <summary>
    /// Parse a single quad with zero allocations.
    /// Strict W3C N-Quads validation - throws on invalid input.
    /// </summary>
    private void ParseQuadZeroGC(QuadHandler handler)
    {
        ResetOutputBuffer();

        // Parse subject (must be IRIREF or BLANK_NODE_LABEL)
        var ch = Peek();
        if (ch != '<' && ch != '_')
            throw ParserException($"Expected '<' or '_:' at start of subject, got '{(char)ch}'");

        var subject = ParseSubjectSpan();
        if (subject.IsEmpty)
            throw ParserException("Failed to parse subject");

        SkipWhitespace();

        // Parse predicate (must be IRIREF)
        if (Peek() != '<')
            throw ParserException($"Expected '<' at start of predicate, got '{(char)Peek()}'");

        var predicate = ParsePredicateSpan();
        if (predicate.IsEmpty)
            throw ParserException("Failed to parse predicate");

        SkipWhitespace();

        // Parse object (must be IRIREF, BLANK_NODE_LABEL, or literal)
        ch = Peek();
        if (ch != '<' && ch != '_' && ch != '"')
            throw ParserException($"Expected '<', '_:', or '\"' at start of object, got '{(char)ch}'");

        var obj = ParseObjectSpan();
        if (obj.IsEmpty)
            throw ParserException("Failed to parse object");

        SkipWhitespace();

        // Parse optional graph label
        ReadOnlySpan<char> graph = ReadOnlySpan<char>.Empty;
        ch = Peek();
        if (ch != '.' && ch != '\n' && ch != -1)
        {
            if (ch != '<' && ch != '_')
                throw ParserException($"Expected graph label '<' or '_:', got '{(char)ch}'");
            graph = ParseGraphLabelSpan();
            if (graph.IsEmpty)
                throw ParserException("Failed to parse graph label");
            SkipWhitespace();
        }

        // Expect '.'
        if (Peek() != '.')
            throw ParserException($"Expected '.' to terminate quad, got '{(char)Peek()}'");
        Consume();

        // Check for trailing characters (only whitespace and comments allowed)
        ch = Peek();
        if (ch != -1 && ch != '\n' && ch != '\r' && ch != ' ' && ch != '\t' && ch != '#')
            throw ParserException($"Unexpected character after '.': '{(char)ch}'");

        // Emit quad
        handler(subject, predicate, obj, graph);
    }

    /// <summary>
    /// Parse a single quad (allocating strings).
    /// Strict W3C N-Quads validation - throws on invalid input.
    /// </summary>
    private RdfQuad ParseQuadAllocating()
    {
        ResetOutputBuffer();

        // Parse subject (must be IRIREF or BLANK_NODE_LABEL)
        var ch = Peek();
        if (ch != '<' && ch != '_')
            throw ParserException($"Expected '<' or '_:' at start of subject, got '{(char)ch}'");

        var subject = ParseSubjectSpan();
        if (subject.IsEmpty)
            throw ParserException("Failed to parse subject");

        SkipWhitespace();

        // Parse predicate (must be IRIREF)
        if (Peek() != '<')
            throw ParserException($"Expected '<' at start of predicate, got '{(char)Peek()}'");

        var predicate = ParsePredicateSpan();
        if (predicate.IsEmpty)
            throw ParserException("Failed to parse predicate");

        SkipWhitespace();

        // Parse object (must be IRIREF, BLANK_NODE_LABEL, or literal)
        ch = Peek();
        if (ch != '<' && ch != '_' && ch != '"')
            throw ParserException($"Expected '<', '_:', or '\"' at start of object, got '{(char)ch}'");

        var obj = ParseObjectSpan();
        if (obj.IsEmpty)
            throw ParserException("Failed to parse object");

        SkipWhitespace();

        // Parse optional graph label
        string? graph = null;
        ch = Peek();
        if (ch != '.' && ch != '\n' && ch != -1)
        {
            if (ch != '<' && ch != '_')
                throw ParserException($"Expected graph label '<' or '_:', got '{(char)ch}'");
            var graphSpan = ParseGraphLabelSpan();
            if (graphSpan.IsEmpty)
                throw ParserException("Failed to parse graph label");
            graph = graphSpan.ToString();
            SkipWhitespace();
        }

        // Expect '.'
        if (Peek() != '.')
            throw ParserException($"Expected '.' to terminate quad, got '{(char)Peek()}'");
        Consume();

        // Check for trailing characters (only whitespace and comments allowed)
        ch = Peek();
        if (ch != -1 && ch != '\n' && ch != '\r' && ch != ' ' && ch != '\t' && ch != '#')
            throw ParserException($"Unexpected character after '.': '{(char)ch}'");

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
    /// Validates IRI per N-Quads spec: no control chars, disallowed chars, absolute IRI required.
    /// </summary>
    private ReadOnlySpan<char> ParseIriRefSpan()
    {
        if (Peek() != '<')
            return ReadOnlySpan<char>.Empty;

        int start = _outputOffset;
        AppendToOutput('<');
        Consume();

        bool hasScheme = false;
        int colonPos = -1;

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

            // Validate disallowed characters per N-Quads spec
            // IRIREF ::= '<' ([^#x00-#x20<>"{}|^`\] | UCHAR)* '>'
            if (ch <= 0x20) // Control chars and space
                throw ParserException($"Invalid character in IRI: U+{ch:X4}");
            if (ch == '"' || ch == '{' || ch == '}' || ch == '|' || ch == '^' || ch == '`')
                throw ParserException($"Invalid character in IRI: '{(char)ch}'");

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
                // Track scheme detection
                if (ch == ':' && colonPos == -1)
                {
                    colonPos = _outputOffset - start;
                    // Check if we have valid scheme chars before the colon
                    // Scheme = ALPHA *( ALPHA / DIGIT / "+" / "-" / "." )
                    if (colonPos > 1) // At least one char after '<'
                    {
                        var schemeSpan = GetOutputSpan(start + 1); // Skip '<'
                        if (schemeSpan.Length > 0 && char.IsLetter(schemeSpan[0]))
                        {
                            hasScheme = true;
                            for (int i = 1; i < schemeSpan.Length; i++)
                            {
                                var sc = schemeSpan[i];
                                if (!char.IsLetterOrDigit(sc) && sc != '+' && sc != '-' && sc != '.')
                                {
                                    hasScheme = false;
                                    break;
                                }
                            }
                        }
                    }
                }

                AppendToOutput((char)ch);
                Consume();
            }
        }

        // N-Quads requires absolute IRIs (relative IRIs not allowed)
        if (!hasScheme)
            throw ParserException("N-Quads requires absolute IRIs (relative IRI not allowed)");

        return GetOutputSpan(start);
    }

    /// <summary>
    /// BLANK_NODE_LABEL ::= '_:' (PN_CHARS_U | [0-9]) ((PN_CHARS | '.')* PN_CHARS)?
    /// Per grammar, label cannot end with '.', so '.' followed by non-PN_CHARS is the terminator.
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

        // Rest of label: ((PN_CHARS | '.')* PN_CHARS)?
        // Key insight: '.' can appear in label but cannot be the last character
        // So if we see '.', we need to look ahead to see if it's followed by PN_CHARS
        while (true)
        {
            ch = Peek();
            if (ch == -1)
                break;

            if (IsPnChars(ch))
            {
                AppendToOutput((char)ch);
                Consume();
            }
            else if (ch == '.')
            {
                // Look ahead: is '.' followed by valid PN_CHARS?
                var next = PeekAhead(1);
                if (next != -1 && IsPnChars(next))
                {
                    // '.' is part of the label
                    AppendToOutput((char)ch);
                    Consume();
                }
                else
                {
                    // '.' is the statement terminator, not part of label
                    break;
                }
            }
            else
            {
                break;
            }
        }

        return GetOutputSpan(start);
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
            // Language tag: LANGTAG ::= '@' [a-zA-Z]+ ('-' [a-zA-Z0-9]+)*
            AppendToOutput('@');
            Consume();

            // First character must be a letter
            var firstChar = Peek();
            if (firstChar == -1 || !((firstChar >= 'a' && firstChar <= 'z') || (firstChar >= 'A' && firstChar <= 'Z')))
                throw ParserException("Language tag must start with a letter");

            // Parse the language tag
            bool afterHyphen = false;
            while (true)
            {
                var ch = Peek();
                if (ch == -1 || IsWhitespace(ch) || ch == '.')
                    break;

                if (ch == '-')
                {
                    AppendToOutput((char)ch);
                    Consume();
                    afterHyphen = true;
                }
                else if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z'))
                {
                    AppendToOutput((char)ch);
                    Consume();
                    afterHyphen = false;
                }
                else if (ch >= '0' && ch <= '9')
                {
                    // Digits only allowed after hyphen
                    if (!afterHyphen && _outputOffset > start + 1)
                    {
                        // Check if we've had a hyphen in this subtag
                        var tagSpan = GetOutputSpan(start);
                        var lastHyphenIdx = tagSpan.LastIndexOf('-');
                        if (lastHyphenIdx == -1)
                            throw ParserException("Language tag: digits only allowed after hyphen");
                    }
                    AppendToOutput((char)ch);
                    Consume();
                    afterHyphen = false;
                }
                else
                {
                    throw ParserException($"Invalid character in language tag: '{(char)ch}'");
                }
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
