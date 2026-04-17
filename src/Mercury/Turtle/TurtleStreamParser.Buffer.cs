// TurtleStreamParser.Buffer.cs
// Buffer management and utility methods

using System;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SkyOmega.Mercury.Rdf.Turtle;

internal sealed partial class TurtleStreamParser
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Peek()
    {
        if (_bufferPosition >= _bufferLength)
        {
            if (_endOfStream)
                return -1;

            // Buffer exhausted during sync parsing — refill synchronously.
            // This handles constructs that span buffer boundaries (blank node
            // property lists, large statements, etc.) without requiring the
            // caller to be async-aware.
            FillBufferSync();

            if (_bufferPosition >= _bufferLength)
                return -1; // True EOF after refill

        }

        // Decode UTF-8 to get Unicode code point
        return PeekUtf8CodePoint(out _);
    }

    /// <summary>
    /// Peek the current UTF-8 code point and return its byte length.
    /// Returns -1 if at end of input. If a multi-byte sequence is split
    /// across the buffer boundary, refills the buffer to complete it.
    /// </summary>
    private int PeekUtf8CodePoint(out int byteLength)
    {
        // Ensure at least one byte is available
        if (_bufferPosition >= _bufferLength)
        {
            if (_endOfStream)
            {
                byteLength = 0;
                return -1;
            }
            FillBufferSync();
            if (_bufferPosition >= _bufferLength)
            {
                byteLength = 0;
                return -1;
            }
        }

        var b0 = _inputBuffer[_bufferPosition];

        // ASCII fast path (0x00-0x7F): single byte
        if (b0 < 0x80)
        {
            byteLength = 1;
            return b0;
        }

        // Determine expected sequence length from leader byte
        int needed;
        if ((b0 & 0xE0) == 0xC0) needed = 2;        // 0xC0-0xDF
        else if ((b0 & 0xF0) == 0xE0) needed = 3;   // 0xE0-0xEF
        else if ((b0 & 0xF8) == 0xF0) needed = 4;   // 0xF0-0xF7
        else
        {
            // Invalid leader (continuation byte at leader position, etc.)
            byteLength = 1;
            return b0;
        }

        // Ensure the full sequence is in the buffer; refill until we have
        // enough bytes or hit EOF. A single FillBufferSync may not be enough
        // when the stream returns small chunks, so loop until needed bytes
        // are present. Without this, partial sequences silently truncate to
        // the leader byte and Consume() walks into mid-codepoint.
        while (_bufferPosition + needed > _bufferLength && !_endOfStream)
        {
            FillBufferSync();
        }

        if (_bufferPosition >= _bufferLength)
        {
            byteLength = 0;
            return -1;
        }

        // Re-read leader in case FillBufferSync shifted the buffer
        b0 = _inputBuffer[_bufferPosition];

        if (_bufferPosition + needed > _bufferLength)
        {
            // Truly incomplete at stream EOF (malformed input)
            byteLength = 1;
            return b0;
        }

        // Decode the complete sequence
        switch (needed)
        {
            case 2:
                byteLength = 2;
                return ((b0 & 0x1F) << 6)
                     | (_inputBuffer[_bufferPosition + 1] & 0x3F);
            case 3:
                byteLength = 3;
                return ((b0 & 0x0F) << 12)
                     | ((_inputBuffer[_bufferPosition + 1] & 0x3F) << 6)
                     | (_inputBuffer[_bufferPosition + 2] & 0x3F);
            default: // 4
                byteLength = 4;
                return ((b0 & 0x07) << 18)
                     | ((_inputBuffer[_bufferPosition + 1] & 0x3F) << 12)
                     | ((_inputBuffer[_bufferPosition + 2] & 0x3F) << 6)
                     | (_inputBuffer[_bufferPosition + 3] & 0x3F);
        }
    }

    /// <summary>
    /// Peek the byte at <paramref name="offset"/> from the current position.
    /// Refills the buffer if the requested offset lies past the current
    /// buffer end. Returns -1 if no byte is available (true EOF or negative
    /// offset before the start of buffered data).
    /// </summary>
    private int PeekAhead(int offset)
    {
        var pos = _bufferPosition + offset;

        if (pos < 0)
            return -1;

        // Loop FillBufferSync because the stream may return less than the
        // requested count per Read (slow streams, network reads).
        while (pos >= _bufferLength)
        {
            if (_endOfStream)
                return -1;
            FillBufferSync();
            pos = _bufferPosition + offset;
            if (pos < 0)
                return -1;
        }

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
    
    // Manual loop instead of LINQ - LINQ would allocate enumerator/closure
    private bool PeekString(string str)
    {
        for (var i = 0; i < str.Length; i++)
        {
            if (PeekAhead(i) != str[i])
                return false;
        }

        return true;
    }

    // Manual loop instead of LINQ - zero-GC requirement
    private void ConsumeString(string str)
    {
        foreach (var ch in str)
        {
            if (!TryConsume(ch))
                throw ParserException($"Expected '{ch}'");
        }
    }

    // Consume N characters (used after case-insensitive match)
    private void ConsumeN(int count)
    {
        for (var i = 0; i < count; i++)
            Consume();
    }

    // Case-insensitive match for SPARQL-style keywords (PREFIX, BASE, VERSION)
    private bool PeekStringIgnoreCase(string str)
    {
        for (var i = 0; i < str.Length; i++)
        {
            var ch = PeekAhead(i);
            if (ch == -1)
                return false;
            if (char.ToUpperInvariant((char)ch) != char.ToUpperInvariant(str[i]))
                return false;
        }
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

    /// <summary>
    /// Synchronous buffer refill. Called by Peek() when the buffer is exhausted
    /// mid-statement. Handles buffer growth for large statements.
    /// </summary>
    private void FillBufferSync()
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

        // Synchronous read — no thread hops
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
    
    private bool IsEndOfInput()
    {
        return _endOfStream && _bufferPosition >= _bufferLength;
    }
    
    private void SkipWhitespaceAndComments()
    {
        while (true)
        {
            var ch = Peek();
            
            if (ch == -1)
                break;
            
            // Whitespace
            if (char.IsWhiteSpace((char)ch))
            {
                Consume();
                continue;
            }
            
            // Comment
            if (ch == '#')
            {
                // Skip to end of line
                while (true)
                {
                    ch = Peek();
                    if (ch == -1 || ch == '\n' || ch == '\r')
                        break;
                    Consume();
                }
                continue;
            }
            
            break;
        }
    }
    
    // Character classification per Turtle grammar
    // PN_CHARS_BASE includes code points beyond the BMP (U+10000-U+EFFFF)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPnCharsBase(int ch)
    {
        if (ch == -1) return false;

        // Check code point ranges per W3C Turtle grammar [163s]
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
               (ch >= 0x10000 && ch <= 0xEFFFF);  // Beyond BMP
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
        // PN_CHARS per W3C Turtle grammar [166s]
        return IsPnCharsU(ch) ||
               ch == '-' ||
               (ch >= '0' && ch <= '9') ||
               ch == 0x00B7 ||
               (ch >= 0x0300 && ch <= 0x036F) ||
               (ch >= 0x203F && ch <= 0x2040);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsReservedCharEscape(int ch)
    {
        return ch == '~' || ch == '.' || ch == '-' || ch == '!' || 
               ch == '$' || ch == '&' || ch == '\'' || ch == '(' || 
               ch == ')' || ch == '*' || ch == '+' || ch == ',' || 
               ch == ';' || ch == '=' || ch == '/' || ch == '?' || 
               ch == '#' || ch == '@' || ch == '%' || ch == '_';
    }
    
    private char ParseEscapeSequence()
    {
        var ch = Peek();
        
        if (ch == -1)
            throw ParserException("Unexpected end of input in escape sequence");
        
        Consume();
        
        // String escape sequences
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
    
    private char ParseUnicodeEscape() => ParseUnicodeEscape(4);
    
    private char ParseUnicodeEscape(int digits)
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
                _ => throw ParserException($"Invalid hex digit in unicode escape: {(char)ch}")
            };
            
            Consume();
            value = (value << 4) | hexValue;
        }
        
        // Check for surrogate code points (not allowed)
        if (value >= 0xD800 && value <= 0xDFFF)
            throw ParserException($"Invalid unicode escape: surrogate code point U+{value:X4}");

        return (char)value;
    }

    /// <summary>
    /// Parse unicode escape and return the code point as int (for \U escapes that may be > 0xFFFF).
    /// </summary>
    private int ParseUnicodeCodePoint(int digits)
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
                _ => throw ParserException($"Invalid hex digit in unicode escape: {(char)ch}")
            };

            Consume();
            value = (value << 4) | hexValue;
        }

        // Check for surrogate code points (not allowed)
        if (value >= 0xD800 && value <= 0xDFFF)
            throw ParserException($"Invalid unicode escape: surrogate code point U+{value:X4}");

        // Check for valid Unicode range
        if (value > 0x10FFFF)
            throw ParserException($"Invalid unicode escape: code point U+{value:X} exceeds maximum");

        return value;
    }

    /// <summary>
    /// Append a Unicode code point to the output buffer using Rune for proper surrogate pair handling.
    /// </summary>
    private void AppendCodePoint(int codePoint)
    {
        var rune = new Rune(codePoint);
        Span<char> chars = stackalloc char[2];
        int charsWritten = rune.EncodeToUtf16(chars);
        for (int i = 0; i < charsWritten; i++)
        {
            AppendToOutput(chars[i]);
        }
    }

    /// <summary>
    /// Append a Unicode code point to StringBuilder using Rune for proper surrogate pair handling.
    /// </summary>
    private void AppendCodePointToSb(int codePoint)
    {
        var rune = new Rune(codePoint);
        Span<char> chars = stackalloc char[2];
        int charsWritten = rune.EncodeToUtf16(chars);
        _sb.Append(chars[..charsWritten]);
    }

    /// <summary>
    /// Parse an escape sequence and append it to StringBuilder.
    /// Handles \U escapes that may produce surrogate pairs for code points > 0xFFFF.
    /// </summary>
    private void ParseAndAppendEscapeToSb()
    {
        var ch = Peek();

        if (ch == -1)
            throw ParserException("Unexpected end of input in escape sequence");

        Consume();

        switch ((char)ch)
        {
            case 't': _sb.Append('\t'); break;
            case 'b': _sb.Append('\b'); break;
            case 'n': _sb.Append('\n'); break;
            case 'r': _sb.Append('\r'); break;
            case 'f': _sb.Append('\f'); break;
            case '"': _sb.Append('"'); break;
            case '\'': _sb.Append('\''); break;
            case '\\': _sb.Append('\\'); break;
            case 'u':
                _sb.Append(ParseUnicodeEscape(4));
                break;
            case 'U':
                // \U can produce code points > 0xFFFF, need surrogate pair handling
                var codePoint = ParseUnicodeCodePoint(8);
                AppendCodePointToSb(codePoint);
                break;
            default:
                throw ParserException($"Invalid escape sequence: \\{(char)ch}");
        }
    }

    /// <summary>
    /// Parse an escape sequence and append it to the output buffer.
    /// Handles \U escapes that may produce surrogate pairs for code points > 0xFFFF.
    /// </summary>
    private void ParseAndAppendEscapeToOutput()
    {
        var ch = Peek();

        if (ch == -1)
            throw ParserException("Unexpected end of input in escape sequence");

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
                AppendToOutput(ParseUnicodeEscape(4));
                break;
            case 'U':
                // \U can produce code points > 0xFFFF, need surrogate pair handling
                var codePoint = ParseUnicodeCodePoint(8);
                AppendCodePoint(codePoint);
                break;
            default:
                throw ParserException($"Invalid escape sequence: \\{(char)ch}");
        }
    }

    private string ParsePercentEncoded()
    {
        if (!TryConsume('%'))
            throw ParserException("Expected '%'");

        _sb.Clear();
        _sb.Append('%');

        for (int i = 0; i < 2; i++)
        {
            var ch = Peek();
            if (ch == -1 || !IsHexDigit(ch))
                throw ParserException("Invalid percent encoding");

            Consume();
            _sb.Append((char)ch);
        }

        return _sb.ToString();
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsHexDigit(int ch)
    {
        return (ch >= '0' && ch <= '9') ||
               (ch >= 'A' && ch <= 'F') ||
               (ch >= 'a' && ch <= 'f');
    }
    
    private string ResolveIri(string iri)
    {
        // IRIs come in with angle brackets, e.g. "<s>" or "<http://example.org/>"
        // We need to strip brackets for resolution, then re-add them

        if (!iri.StartsWith('<') || !iri.EndsWith('>'))
            return iri;

        var innerIri = iri[1..^1]; // Strip angle brackets

        // If absolute IRI, return as-is (with brackets)
        if (Uri.IsWellFormedUriString(innerIri, UriKind.Absolute))
            return iri;

        // Resolve relative IRI against base
        if (string.IsNullOrEmpty(_baseUri))
            return iri;

        try
        {
            var baseUri = new Uri(_baseUri, UriKind.Absolute);
            var resolved = new Uri(baseUri, innerIri);
            var resolvedStr = resolved.ToString();

            // .NET Uri adds trailing slash to authority-only URIs (e.g., //g becomes http://g/)
            // RFC 3986 says path should be empty, not "/", so strip it
            // Check: innerIri starts with // and has no path (no / after authority)
            if (innerIri.StartsWith("//"))
            {
                var afterAuthority = innerIri.AsSpan(2);
                var firstDelim = afterAuthority.IndexOfAny('/', '?', '#');
                // If no delimiters, or first delim is not /, path was empty
                if (firstDelim == -1 || afterAuthority[firstDelim] != '/')
                {
                    // Strip trailing slash that .NET added
                    if (resolvedStr.EndsWith('/') && resolved.AbsolutePath == "/")
                    {
                        resolvedStr = resolvedStr[..^1];
                    }
                }
            }

            return $"<{resolvedStr}>";
        }
        catch
        {
            return iri;
        }
    }
}
