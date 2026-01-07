// TurtleStreamParser.Buffer.cs
// Buffer management and utility methods

using System;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SkyOmega.Mercury.Rdf.Turtle;

public sealed partial class TurtleStreamParser
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Peek()
    {
        if (_bufferPosition >= _bufferLength)
        {
            if (_endOfStream)
                return -1;

            // Buffer exhausted during sync parsing - main loop handles refills at statement boundaries
            return -1;
        }

        // Decode UTF-8 to get Unicode code point
        return PeekUtf8CodePoint(out _);
    }

    /// <summary>
    /// Peek the current UTF-8 code point and return its byte length.
    /// Returns -1 if at end of input.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int PeekUtf8CodePoint(out int byteLength)
    {
        if (_bufferPosition >= _bufferLength)
        {
            byteLength = 0;
            return -1;
        }

        var b0 = _inputBuffer[_bufferPosition];

        // ASCII (0x00-0x7F): single byte
        if (b0 < 0x80)
        {
            byteLength = 1;
            return b0;
        }

        // 2-byte sequence (0xC0-0xDF)
        if ((b0 & 0xE0) == 0xC0)
        {
            if (_bufferPosition + 1 >= _bufferLength)
            {
                byteLength = 1;
                return b0; // Incomplete sequence, return first byte
            }
            var b1 = _inputBuffer[_bufferPosition + 1];
            byteLength = 2;
            return ((b0 & 0x1F) << 6) | (b1 & 0x3F);
        }

        // 3-byte sequence (0xE0-0xEF)
        if ((b0 & 0xF0) == 0xE0)
        {
            if (_bufferPosition + 2 >= _bufferLength)
            {
                byteLength = 1;
                return b0; // Incomplete sequence
            }
            var b1 = _inputBuffer[_bufferPosition + 1];
            var b2 = _inputBuffer[_bufferPosition + 2];
            byteLength = 3;
            return ((b0 & 0x0F) << 12) | ((b1 & 0x3F) << 6) | (b2 & 0x3F);
        }

        // 4-byte sequence (0xF0-0xF7)
        if ((b0 & 0xF8) == 0xF0)
        {
            if (_bufferPosition + 3 >= _bufferLength)
            {
                byteLength = 1;
                return b0; // Incomplete sequence
            }
            var b1 = _inputBuffer[_bufferPosition + 1];
            var b2 = _inputBuffer[_bufferPosition + 2];
            var b3 = _inputBuffer[_bufferPosition + 3];
            byteLength = 4;
            return ((b0 & 0x07) << 18) | ((b1 & 0x3F) << 12) | ((b2 & 0x3F) << 6) | (b3 & 0x3F);
        }

        // Invalid UTF-8 lead byte, return as-is
        byteLength = 1;
        return b0;
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
    /// Append a Unicode code point to the output buffer, handling surrogate pairs for code points > 0xFFFF.
    /// </summary>
    private void AppendCodePoint(int codePoint)
    {
        if (codePoint <= 0xFFFF)
        {
            AppendToOutput((char)codePoint);
        }
        else
        {
            // Encode as surrogate pair
            codePoint -= 0x10000;
            var highSurrogate = (char)(0xD800 + (codePoint >> 10));
            var lowSurrogate = (char)(0xDC00 + (codePoint & 0x3FF));
            AppendToOutput(highSurrogate);
            AppendToOutput(lowSurrogate);
        }
    }

    /// <summary>
    /// Append a Unicode code point to StringBuilder, handling surrogate pairs for code points > 0xFFFF.
    /// </summary>
    private void AppendCodePointToSb(int codePoint)
    {
        if (codePoint <= 0xFFFF)
        {
            _sb.Append((char)codePoint);
        }
        else
        {
            // Encode as surrogate pair
            codePoint -= 0x10000;
            var highSurrogate = (char)(0xD800 + (codePoint >> 10));
            var lowSurrogate = (char)(0xDC00 + (codePoint & 0x3FF));
            _sb.Append(highSurrogate);
            _sb.Append(lowSurrogate);
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
