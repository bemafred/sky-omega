// RdfXmlStreamParser.Buffer.cs
// Buffer management and low-level parsing utilities

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace SkyOmega.Mercury.RdfXml;

public sealed partial class RdfXmlStreamParser
{
    #region Buffer Peek/Consume

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

    private async ValueTask RefillIfNeededAsync(CancellationToken cancellationToken)
    {
        var remaining = _bufferLength - _bufferPosition;
        if (remaining < _inputBuffer.Length / 4 && !_endOfStream)
        {
            await FillBufferAsync(cancellationToken);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsEndOfInput()
    {
        return _endOfStream && _bufferPosition >= _bufferLength;
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

    private void AppendToOutput(ReadOnlySpan<char> span)
    {
        if (_outputOffset + span.Length > _outputBuffer.Length)
            GrowOutputBuffer(_outputOffset + span.Length);
        span.CopyTo(_outputBuffer.AsSpan(_outputOffset));
        _outputOffset += span.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> GetOutputSpan(int startOffset)
        => _outputBuffer.AsSpan(startOffset, _outputOffset - startOffset);

    private void GrowOutputBuffer(int minSize = 0)
    {
        var newSize = Math.Max(_outputBuffer.Length * 2, minSize + 1024);
        var newBuffer = _charPool.Rent(newSize);
        _outputBuffer.AsSpan(0, _outputOffset).CopyTo(newBuffer);
        _charPool.Return(_outputBuffer);
        _outputBuffer = newBuffer;
    }


    #endregion

    #region Whitespace Handling

    private void SkipWhitespace()
    {
        while (true)
        {
            var ch = Peek();
            if (ch == -1 || !IsXmlWhitespace(ch))
                break;
            Consume();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsXmlWhitespace(int ch)
    {
        return ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n';
    }

    #endregion

    #region XML Structure Parsing

    /// <summary>
    /// Skip XML declaration (&lt;?xml ... ?&gt;).
    /// </summary>
    private void SkipXmlDeclaration()
    {
        SkipWhitespace();

        if (Peek() == '<' && PeekAhead(1) == '?')
        {
            while (!IsEndOfInput())
            {
                if (Peek() == '?' && PeekAhead(1) == '>')
                {
                    Consume();
                    Consume();
                    break;
                }
                Consume();
            }
        }
    }

    /// <summary>
    /// Skip comment (&lt;!-- ... --&gt;) or CDATA (&lt;![CDATA[ ... ]]&gt;).
    /// </summary>
    private void SkipCommentOrCData()
    {
        Consume(); // '!'

        if (Peek() == '-' && PeekAhead(1) == '-')
        {
            // Comment
            Consume();
            Consume();

            while (!IsEndOfInput())
            {
                if (Peek() == '-' && PeekAhead(1) == '-' && PeekAhead(2) == '>')
                {
                    Consume();
                    Consume();
                    Consume();
                    break;
                }
                Consume();
            }
        }
        else if (Peek() == '[')
        {
            // CDATA
            while (!IsEndOfInput())
            {
                if (Peek() == ']' && PeekAhead(1) == ']' && PeekAhead(2) == '>')
                {
                    Consume();
                    Consume();
                    Consume();
                    break;
                }
                Consume();
            }
        }
        else
        {
            // DOCTYPE or other - skip to >
            while (Peek() != '>' && !IsEndOfInput())
                Consume();
            TryConsume('>');
        }
    }

    /// <summary>
    /// Skip processing instruction (&lt;? ... ?&gt;).
    /// </summary>
    private void SkipProcessingInstruction()
    {
        Consume(); // '?'

        while (!IsEndOfInput())
        {
            if (Peek() == '?' && PeekAhead(1) == '>')
            {
                Consume();
                Consume();
                break;
            }
            Consume();
        }
    }

    /// <summary>
    /// Skip closing tag.
    /// </summary>
    private void SkipClosingTag()
    {
        while (Peek() != '>' && !IsEndOfInput())
            Consume();
        TryConsume('>');
    }

    /// <summary>
    /// Skip to closing tag with specific name.
    /// </summary>
    private void SkipToClosingTag(ReadOnlySpan<char> expectedName)
    {
        int depth = 1;

        while (!IsEndOfInput() && depth > 0)
        {
            if (Peek() == '<')
            {
                Consume();
                if (Peek() == '/')
                {
                    depth--;
                    if (depth == 0)
                    {
                        // Skip rest of closing tag
                        while (Peek() != '>' && !IsEndOfInput())
                            Consume();
                        TryConsume('>');
                        return;
                    }
                }
                else if (Peek() != '!' && Peek() != '?')
                {
                    // Opening tag - check if self-closing
                    bool selfClosing = false;
                    while (Peek() != '>' && !IsEndOfInput())
                    {
                        if (Peek() == '/')
                            selfClosing = true;
                        Consume();
                    }
                    TryConsume('>');

                    if (!selfClosing)
                        depth++;
                }
            }
            else
            {
                Consume();
            }
        }
    }

    /// <summary>
    /// Parse QName (qualified name).
    /// </summary>
    private ReadOnlySpan<char> ParseQName()
    {
        SkipWhitespace();

        int start = _outputOffset;

        // First character must be name start char
        var ch = Peek();
        if (!IsNameStartChar(ch))
            return ReadOnlySpan<char>.Empty;

        AppendToOutput((char)ch);
        Consume();

        // Rest of name
        while (true)
        {
            ch = Peek();
            if (!IsNameChar(ch))
                break;

            AppendToOutput((char)ch);
            Consume();
        }

        return GetOutputSpan(start);
    }

    /// <summary>
    /// Parse element/attribute name (may be qualified).
    /// </summary>
    private string ParseName()
    {
        SkipWhitespace();

        int start = _outputOffset;

        var ch = Peek();
        if (!IsNameStartChar(ch))
            return string.Empty;

        AppendToOutput((char)ch);
        Consume();

        while (true)
        {
            ch = Peek();
            if (!IsNameChar(ch))
                break;

            AppendToOutput((char)ch);
            Consume();
        }

        return GetOutputSpan(start).ToString();
    }

    /// <summary>
    /// Parse attributes and namespace declarations.
    /// Returns dictionary mapping attribute name to value (as strings for async boundary crossing).
    /// </summary>
    private Dictionary<string, string> ParseAttributes()
    {
        var attributes = new Dictionary<string, string>();

        while (true)
        {
            SkipWhitespace();

            var ch = Peek();
            if (ch == '>' || ch == '/' || ch == -1)
                break;

            // Parse attribute name
            var name = ParseName();
            if (string.IsNullOrEmpty(name))
                break;

            SkipWhitespace();

            // Expect '='
            if (!TryConsume('='))
            {
                // Attribute without value - skip
                continue;
            }

            SkipWhitespace();

            // Parse attribute value
            var quote = Peek();
            if (quote != '"' && quote != '\'')
            {
                // Unquoted value - skip
                continue;
            }
            Consume();

            int valueStart = _outputOffset;

            while (true)
            {
                ch = Peek();
                if (ch == quote || ch == -1)
                {
                    Consume();
                    break;
                }

                if (ch == '&')
                {
                    // Entity reference
                    var entity = ParseEntityReference();
                    AppendToOutput(entity);
                }
                else
                {
                    AppendToOutput((char)ch);
                    Consume();
                }
            }

            var value = _outputBuffer.AsSpan(valueStart, _outputOffset - valueStart).ToString();
            attributes[name] = value;

            // Handle namespace declarations
            if (name.StartsWith("xmlns:"))
            {
                var prefix = name[6..];
                _namespaces[prefix] = value;
            }
            else if (name == "xmlns")
            {
                _namespaces[""] = value;
            }
        }

        return attributes;
    }

    /// <summary>
    /// Parse text content until &lt;.
    /// </summary>
    private ReadOnlySpan<char> ParseTextContent()
    {
        int start = _outputOffset;

        while (true)
        {
            var ch = Peek();
            if (ch == '<' || ch == -1)
                break;

            if (ch == '&')
            {
                var entity = ParseEntityReference();
                AppendToOutput(entity);
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
    /// Parse XML literal content (preserves XML structure).
    /// </summary>
    private ReadOnlySpan<char> ParseXmlLiteralContent(ReadOnlySpan<char> elementName)
    {
        int start = _outputOffset;
        int depth = 1;

        while (!IsEndOfInput() && depth > 0)
        {
            var ch = Peek();

            if (ch == '<')
            {
                AppendToOutput('<');
                Consume();

                if (Peek() == '/')
                {
                    depth--;
                    if (depth == 0)
                    {
                        // Don't include closing tag in content
                        _outputOffset--; // Remove the '<'
                        SkipToClosingTag(elementName);
                        break;
                    }
                }
                else if (Peek() != '!' && Peek() != '?')
                {
                    // Opening tag
                    // Check for self-closing
                    bool selfClosing = false;
                    while (Peek() != '>' && !IsEndOfInput())
                    {
                        var c = Peek();
                        if (c == '/')
                            selfClosing = true;
                        AppendToOutput((char)c);
                        Consume();
                    }
                    if (Peek() == '>')
                    {
                        AppendToOutput('>');
                        Consume();
                    }

                    if (!selfClosing)
                        depth++;
                }
                else
                {
                    // Comment or PI - include as-is
                    while (Peek() != '>' && !IsEndOfInput())
                    {
                        AppendToOutput((char)Peek());
                        Consume();
                    }
                    if (Peek() == '>')
                    {
                        AppendToOutput('>');
                        Consume();
                    }
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
    /// Parse entity reference (&amp;name;) and return decoded character.
    /// </summary>
    private char ParseEntityReference()
    {
        Consume(); // '&'

        if (Peek() == '#')
        {
            Consume();

            // Numeric entity
            int value = 0;
            bool isHex = false;

            if (Peek() == 'x' || Peek() == 'X')
            {
                Consume();
                isHex = true;
            }

            while (Peek() != ';' && !IsEndOfInput())
            {
                var ch = Peek();
                if (isHex)
                {
                    value = (value << 4) | (ch switch
                    {
                        >= '0' and <= '9' => ch - '0',
                        >= 'A' and <= 'F' => ch - 'A' + 10,
                        >= 'a' and <= 'f' => ch - 'a' + 10,
                        _ => 0
                    });
                }
                else
                {
                    value = value * 10 + (ch - '0');
                }
                Consume();
            }

            TryConsume(';');
            return (char)value;
        }
        else
        {
            // Named entity
            int nameStart = _outputOffset;
            while (Peek() != ';' && !IsEndOfInput())
            {
                AppendToOutput((char)Peek());
                Consume();
            }
            TryConsume(';');

            var entityName = GetOutputSpan(nameStart);
            _outputOffset = nameStart; // Reset - we just wanted to read the name

            return entityName switch
            {
                var s when s.SequenceEqual("lt".AsSpan()) => '<',
                var s when s.SequenceEqual("gt".AsSpan()) => '>',
                var s when s.SequenceEqual("amp".AsSpan()) => '&',
                var s when s.SequenceEqual("apos".AsSpan()) => '\'',
                var s when s.SequenceEqual("quot".AsSpan()) => '"',
                _ => '?' // Unknown entity
            };
        }
    }

    #endregion

    #region Character Classification

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsNameStartChar(int ch)
    {
        if (ch == -1) return false;
        return ch == ':' || ch == '_' ||
               (ch >= 'A' && ch <= 'Z') ||
               (ch >= 'a' && ch <= 'z') ||
               (ch >= 0xC0 && ch <= 0xD6) ||
               (ch >= 0xD8 && ch <= 0xF6) ||
               (ch >= 0xF8 && ch <= 0x2FF) ||
               (ch >= 0x370 && ch <= 0x37D) ||
               (ch >= 0x37F && ch <= 0x1FFF) ||
               (ch >= 0x200C && ch <= 0x200D) ||
               (ch >= 0x2070 && ch <= 0x218F) ||
               (ch >= 0x2C00 && ch <= 0x2FEF) ||
               (ch >= 0x3001 && ch <= 0xD7FF) ||
               (ch >= 0xF900 && ch <= 0xFDCF) ||
               (ch >= 0xFDF0 && ch <= 0xFFFD);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsNameChar(int ch)
    {
        return IsNameStartChar(ch) ||
               ch == '-' || ch == '.' ||
               (ch >= '0' && ch <= '9') ||
               ch == 0xB7 ||
               (ch >= 0x300 && ch <= 0x36F) ||
               (ch >= 0x203F && ch <= 0x2040);
    }

    #endregion

    #region Error Handling

    private Exception ParserException(string message)
    {
        return new InvalidDataException($"RDF/XML parse error at line {_line}, column {_column}: {message}");
    }

    #endregion
}
