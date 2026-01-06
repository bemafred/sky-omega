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
            return $"<{resolved}>";
        }
        catch
        {
            return iri;
        }
    }
}
