// NQuadsStreamParser.cs
// Zero-GC streaming N-Quads parser, and through NTriplesStreamParser the N-Triples parser
// Based on W3C RDF 1.1 N-Quads and N-Triples specifications
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
using SkyOmega.Mercury.Rdf;

namespace SkyOmega.Mercury.NQuads;

/// <summary>
/// Handler for zero-allocation quad parsing.
/// Receives spans that are valid only during the callback invocation.
/// Graph is empty for the default graph.
/// </summary>
internal delegate void QuadHandler(
    ReadOnlySpan<char> subject,
    ReadOnlySpan<char> predicate,
    ReadOnlySpan<char> obj,
    ReadOnlySpan<char> graph);

/// <summary>
/// Immutable record for N-Quads (allocating version).
/// Graph is null or empty for the default graph.
/// </summary>
internal readonly record struct RdfQuad(string Subject, string Predicate, string Object, string? Graph)
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
/// Zero-allocation streaming parser for RDF N-Quads format, the one reader of the N-Quads grammar and of N-Triples,
/// which is N-Quads without graph labels (<see cref="NTriples.NTriplesStreamParser"/> reads through this class).
///
/// N-Quads grammar (simplified):
/// [1] nquadsDoc    ::= statement? (EOL statement)* EOL?
/// [2] statement    ::= subject predicate object graphLabel? '.'
/// [3] subject      ::= IRIREF | BLANK_NODE_LABEL
/// [4] predicate    ::= IRIREF
/// [5] object       ::= IRIREF | BLANK_NODE_LABEL | literal
/// [6] graphLabel   ::= IRIREF | BLANK_NODE_LABEL
///
/// The input is UTF-8, decoded and validated per code point; a term may be longer than the input buffer, which is
/// refilled mid-statement. For zero-GC parsing, use ParseAsync(QuadHandler). The IAsyncEnumerable overload allocates
/// strings for compatibility.
/// </summary>
/// <remarks>
/// <para><b>Thread Safety:</b> This class is NOT thread-safe. Each instance
/// maintains internal parsing state and buffers that cannot be shared across threads.
/// Create a separate instance per thread or serialize access with locking.</para>
/// <para><b>Usage Pattern:</b> Create one instance per stream. Dispose when done
/// to return pooled buffers.</para>
/// </remarks>
internal sealed class NQuadsStreamParser : IDisposable, IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly IBufferManager _bufferManager;

    // The format named in error messages: "N-Quads", or "N-Triples" when NTriplesStreamParser reads through this class.
    private readonly string _documentFormat;

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

    internal const int DefaultBufferSize = 8192;

    public NQuadsStreamParser(Stream stream, int bufferSize = DefaultBufferSize, IBufferManager? bufferManager = null)
        : this(stream, bufferSize, bufferManager, "N-Quads")
    {
    }

    internal NQuadsStreamParser(Stream stream, int bufferSize, IBufferManager? bufferManager, string documentFormat)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _bufferManager = bufferManager ?? PooledBufferManager.Shared;
        _documentFormat = documentFormat;

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

            ReadAndEmitStatement(handler);
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

            yield return ReadStatementAllocating();
        }
    }

    private void ReadAndEmitStatement(QuadHandler handler)
    {
        ReadStatement(out var subject, out var predicate, out var obj, out var graph);
        handler(subject, predicate, obj, graph);
    }

    private RdfQuad ReadStatementAllocating()
    {
        ReadStatement(out var subject, out var predicate, out var obj, out var graph);
        return new RdfQuad(subject.ToString(), predicate.ToString(), obj.ToString(), graph.IsEmpty ? null : graph.ToString());
    }

    /// <summary>
    /// [2] statement ::= subject predicate object graphLabel? '.', then only spaces or tabs before the end of the line,
    /// a comment or the end of the input ([1]: one statement per line). Strict: throws on invalid input. The spans
    /// point into the output buffer and are valid until the next statement is read.
    /// </summary>
    private void ReadStatement(
        out ReadOnlySpan<char> subject,
        out ReadOnlySpan<char> predicate,
        out ReadOnlySpan<char> obj,
        out ReadOnlySpan<char> graph)
    {
        ResetOutputBuffer();

        // Parse subject (must be IRIREF or BLANK_NODE_LABEL)
        var ch = Peek();
        if (ch != '<' && ch != '_')
            throw ParserException($"Expected '<' or '_:' at start of subject, got {Display(ch)}");

        subject = ParseSubjectSpan();
        if (subject.IsEmpty)
            throw ParserException("Failed to parse subject");

        SkipWhitespace();

        // Parse predicate (must be IRIREF)
        if (Peek() != '<')
            throw ParserException($"Expected '<' at start of predicate, got {Display(Peek())}");

        predicate = ParsePredicateSpan();
        if (predicate.IsEmpty)
            throw ParserException("Failed to parse predicate");

        SkipWhitespace();

        // Parse object (must be IRIREF, BLANK_NODE_LABEL, or literal)
        ch = Peek();
        if (ch != '<' && ch != '_' && ch != '"')
            throw ParserException($"Expected '<', '_:', or '\"' at start of object, got {Display(ch)}");

        obj = ParseObjectSpan();
        if (obj.IsEmpty)
            throw ParserException("Failed to parse object");

        SkipWhitespace();

        // Parse optional graph label
        graph = ReadOnlySpan<char>.Empty;
        ch = Peek();
        if (ch != '.' && ch != '\n' && ch != -1)
        {
            if (ch != '<' && ch != '_')
                throw ParserException($"Expected graph label '<' or '_:', got {Display(ch)}");
            graph = ParseGraphLabelSpan();
            if (graph.IsEmpty)
                throw ParserException("Failed to parse graph label");
            SkipWhitespace();
        }

        // Expect '.'
        if (Peek() != '.')
            throw ParserException($"Expected '.' to terminate the statement, got {Display(Peek())}");
        Consume();

        // Only spaces or tabs, then the end of the line, a comment or the end of the input
        while (Peek() is ' ' or '\t')
            Consume();
        ch = Peek();
        if (ch != -1 && ch != '\n' && ch != '\r' && ch != '#')
            throw ParserException($"Unexpected character after '.': {Display(ch)}");
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

            // [8] IRIREF ::= '<' ([^#x00-#x20<>"{}|^`\] | UCHAR)* '>': a character of the class
            // (RdfIri.IsIriRefCharacter), or a UCHAR for one
            if (ch != '\\' && !RdfIri.IsIriRefCharacter(ch))
                throw ParserException($"Invalid character in IRI: U+{ch:X4}");

            if (ch == '\\')
            {
                Consume();
                var next = Peek();
                if (next == 'u')
                {
                    Consume();
                    var escaped = ParseUnicodeEscape(4);
                    if (!RdfIri.IsIriRefCharacter(escaped))
                        throw ParserException($"Invalid character in IRI: an escape for U+{escaped:X4}");
                    AppendCodePoint(escaped);
                }
                else if (next == 'U')
                {
                    Consume();
                    var escaped = ParseUnicodeEscape(8);
                    if (!RdfIri.IsIriRefCharacter(escaped))
                        throw ParserException($"Invalid character in IRI: an escape for U+{escaped:X4}");
                    AppendCodePoint(escaped);
                }
                else
                {
                    throw ParserException($"Invalid escape in IRI: \\{Display(next)}");
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

                AppendCodePoint(ch);  // Handle Unicode code points including supplementary planes
                Consume();
            }
        }

        // N-Quads and N-Triples require absolute IRIs (relative IRIs not allowed)
        if (!hasScheme)
            throw ParserException($"{_documentFormat} requires absolute IRIs (relative IRI not allowed)");

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
        if (!RdfNameChars.IsPnCharsU(ch) && !(ch >= '0' && ch <= '9'))
            throw ParserException("Invalid blank node label");

        AppendCodePoint(ch);
        Consume();

        // Rest of label: ((PN_CHARS | '.')* PN_CHARS)?
        // Key insight: '.' can appear in label but cannot be the last character
        // So if we see '.', we need to look ahead to see if it's followed by PN_CHARS
        while (true)
        {
            ch = Peek();
            if (ch == -1)
                break;

            if (RdfNameChars.IsPnChars(ch))
            {
                AppendCodePoint(ch);
                Consume();
            }
            else if (ch == '.')
            {
                // Look ahead: is '.' followed by valid PN_CHARS?
                var next = PeekAheadCodePoint(1);
                if (next != -1 && RdfNameChars.IsPnChars(next))
                {
                    // '.' is part of the label
                    AppendToOutput('.');
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
    /// The three are separate terminals, so whitespace may come between the string and its language tag or '^^', and
    /// between '^^' and the datatype IRI. The literal is returned in Mercury's term form: quoted, with its escapes
    /// decoded.
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
                ParseEscapeSequenceAndAppend();
            }
            else
            {
                AppendCodePoint(ch);  // Handle supplementary code points (> U+FFFF)
                Consume();
            }
        }

        // Check for language tag or datatype
        SkipWhitespace();
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
                    AppendToOutput('-');
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
                    throw ParserException($"Invalid character in language tag: {Display(ch)}");
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
            SkipWhitespace();

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
    /// Parse string escape sequence (N-Quads escapes) and append to output.
    /// Handles both BMP and supplementary Unicode code points.
    /// </summary>
    private void ParseEscapeSequenceAndAppend()
    {
        var ch = Peek();

        if (ch == -1)
            throw ParserException("Unexpected end of input in escape sequence");

        Consume();

        // Decode/validation shared with every RDF-family parser via RdfEscape (docs/divergence S1a).
        if (RdfEscape.TryDecodeSimple((char)ch, out var simple))
        {
            AppendToOutput(simple);
            return;
        }
        if (ch == 'u')
        {
            AppendCodePoint(ParseUnicodeEscape(4));
            return;
        }
        if (ch == 'U')
        {
            AppendCodePoint(ParseUnicodeEscape(8));
            return;
        }
        throw ParserException($"Invalid escape sequence: \\{Display(ch)}");
    }

    /// <summary>
    /// Parse unicode escape (\uXXXX or \UXXXXXXXX).
    /// Returns the full code point (may be > 0xFFFF for supplementary planes).
    /// </summary>
    private int ParseUnicodeEscape(int digits = 4)
    {
        Span<char> hex = stackalloc char[8];
        for (int i = 0; i < digits; i++)
        {
            var ch = Peek();
            if (ch == -1)
                throw ParserException("Unexpected end of input in unicode escape");
            if (ch > 0x7F)
                throw ParserException($"Invalid unicode escape: {Display(ch)} is not a hexadecimal digit");
            hex[i] = (char)ch;
            Consume();
        }

        // RdfEscape validates surrogates AND code points above U+10FFFF (the latter guard was
        // previously missing here, leaving an out-of-range \U to throw a raw exception downstream).
        if (!RdfEscape.TryDecodeUchar(hex[..digits], out int codePoint))
            throw ParserException($"Invalid unicode escape: \\{(digits == 4 ? 'u' : 'U')}{hex[..digits].ToString()}");

        return codePoint;
    }

    #endregion

    #region Buffer Management

    /// <summary>
    /// The code point at the read position, decoded from UTF-8, or -1 at the end of the input. ASCII, nearly every
    /// byte of a typical document, is read inline; the rest goes through <see cref="PeekNonAscii"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Peek()
    {
        if (_bufferPosition < _bufferLength)
        {
            byte b = _inputBuffer[_bufferPosition];
            if (b < 0x80)
                return b;
        }
        return PeekNonAscii();
    }

    /// <summary>
    /// Refills the buffer when it is exhausted mid-statement, so a term may be longer than the buffer, and when it holds
    /// only the first bytes of a multi-byte sequence. Invalid UTF-8 is an error: N-Quads and N-Triples are UTF-8.
    /// </summary>
    private int PeekNonAscii()
    {
        while (true)
        {
            if (_bufferPosition >= _bufferLength)
            {
                if (_endOfStream)
                    return -1;
                FillBufferSync();
                continue;
            }

            byte b = _inputBuffer[_bufferPosition];
            if (b < 0x80)
                return b;

            var status = Rune.DecodeFromUtf8(
                _inputBuffer.AsSpan(_bufferPosition, _bufferLength - _bufferPosition), out var rune, out _);
            if (status == OperationStatus.Done)
                return rune.Value;
            if (status == OperationStatus.NeedMoreData && !_endOfStream)
            {
                RefillOrThrow();
                continue;
            }
            throw ParserException($"Invalid UTF-8: byte 0x{b:X2}");
        }
    }

    /// <summary>
    /// The byte <paramref name="offset"/> bytes after the read position, or -1 past the end of the input. For ASCII
    /// lookahead ('_:' and '^^').
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int PeekAhead(int offset)
    {
        while (_bufferPosition + offset >= _bufferLength)
        {
            if (_endOfStream)
                return -1;
            RefillOrThrow();
        }
        return _inputBuffer[_bufferPosition + offset];
    }

    /// <summary>
    /// The code point that starts <paramref name="byteOffset"/> bytes after the read position, or -1 past the end of the
    /// input or at invalid UTF-8 (which the next <see cref="Peek"/> there reports).
    /// </summary>
    private int PeekAheadCodePoint(int byteOffset)
    {
        int b = PeekAhead(byteOffset);
        if (b < 0x80)
            return b;
        while (true)
        {
            int from = _bufferPosition + byteOffset;
            var status = Rune.DecodeFromUtf8(_inputBuffer.AsSpan(from, _bufferLength - from), out var rune, out _);
            if (status == OperationStatus.Done)
                return rune.Value;
            if (status == OperationStatus.NeedMoreData && !_endOfStream)
            {
                RefillOrThrow();
                continue;
            }
            return -1;
        }
    }

    /// <summary>
    /// Moves past the code point <see cref="Peek"/> returned: one byte for ASCII, its sequence length otherwise.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Consume()
    {
        if (_bufferPosition >= _bufferLength)
            return;

        byte b = _inputBuffer[_bufferPosition];
        int length = 1;
        if (b >= 0x80)
            Rune.DecodeFromUtf8(_inputBuffer.AsSpan(_bufferPosition, _bufferLength - _bufferPosition), out _, out length);
        _bufferPosition += length;

        if (b == '\n')
        {
            _line++;
            _column = 1;
        }
        else
        {
            _column++;
        }
    }

    private async ValueTask FillBufferAsync(CancellationToken cancellationToken)
    {
        if (_endOfStream)
            return;

        ShiftUnreadToStart();
        if (_bufferLength == _inputBuffer.Length)
            return;

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

    /// <summary>
    /// Synchronous buffer refill, for <see cref="Peek"/> and <see cref="PeekAhead"/> when the buffer runs out
    /// mid-statement (the statement reader is synchronous). Mirrors <see cref="FillBufferAsync"/>.
    /// </summary>
    private void FillBufferSync()
    {
        if (_endOfStream)
            return;

        ShiftUnreadToStart();
        if (_bufferLength == _inputBuffer.Length)
            return;

        var bytesRead = _stream.Read(_inputBuffer, _bufferLength, _inputBuffer.Length - _bufferLength);

        if (bytesRead == 0)
        {
            _endOfStream = true;
        }
        else
        {
            _bufferLength += bytesRead;
        }
    }

    /// <summary>
    /// A synchronous refill that must add input: when the buffer is already full of unread bytes, a lookahead that
    /// still needs more cannot be served, and looping would never end.
    /// </summary>
    private void RefillOrThrow()
    {
        int unread = _bufferLength - _bufferPosition;
        FillBufferSync();
        if (!_endOfStream && _bufferLength - _bufferPosition == unread)
            throw ParserException($"Lookahead past the {_inputBuffer.Length}-byte input buffer");
    }

    /// <summary>Moves the unread bytes to the start of the input buffer, making room after them.</summary>
    private void ShiftUnreadToStart()
    {
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

    /// <summary>
    /// Append a Unicode code point to the output buffer, handling surrogate pairs for code points > 0xFFFF.
    /// </summary>
    private void AppendCodePoint(int codePoint)
    {
        if (codePoint < 0x80)
        {
            AppendToOutput((char)codePoint);
            return;
        }
        // UTF-16 encoding shared with every RDF-family parser via RdfEscape (docs/divergence S1d).
        Span<char> chars = stackalloc char[2];
        int n = RdfEscape.EncodeUtf16(codePoint, chars);
        for (int i = 0; i < n; i++)
            AppendToOutput(chars[i]);
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

    #region Error Handling

    /// <summary>
    /// An error at the current line and column, named for the document's format. Internal so that
    /// <see cref="NTriples.NTriplesStreamParser"/> reports a graph label at its position.
    /// </summary>
    internal Exception ParserException(string message)
    {
        return new InvalidDataException($"{_documentFormat} parse error at line {_line}, column {_column}: {message}");
    }

    /// <summary>A code point as an error message shows it: quoted when printable ASCII, U+XXXX otherwise.</summary>
    private static string Display(int codePoint) => codePoint switch
    {
        -1 => "end of input",
        >= 0x21 and <= 0x7E => $"'{(char)codePoint}'",
        _ => $"U+{codePoint:X4}",
    };

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
