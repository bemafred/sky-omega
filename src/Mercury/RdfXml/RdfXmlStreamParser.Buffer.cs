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

    /// <summary>
    /// Read and decode a UTF-8 character, which may be 1-4 bytes.
    /// Returns the Unicode code point, or -1 for end of input.
    /// </summary>
    private int ReadUtf8Char()
    {
        var b1 = Peek();
        if (b1 == -1) return -1;
        Consume();

        // Single byte (ASCII): 0xxxxxxx
        if ((b1 & 0x80) == 0)
            return b1;

        // Two bytes: 110xxxxx 10xxxxxx
        if ((b1 & 0xE0) == 0xC0)
        {
            var b2 = Peek();
            if (b2 == -1) return b1; // Incomplete sequence
            Consume();
            return ((b1 & 0x1F) << 6) | (b2 & 0x3F);
        }

        // Three bytes: 1110xxxx 10xxxxxx 10xxxxxx
        if ((b1 & 0xF0) == 0xE0)
        {
            var b2 = Peek();
            if (b2 == -1) return b1;
            Consume();
            var b3 = Peek();
            if (b3 == -1) return ((b1 & 0x0F) << 6) | (b2 & 0x3F);
            Consume();
            return ((b1 & 0x0F) << 12) | ((b2 & 0x3F) << 6) | (b3 & 0x3F);
        }

        // Four bytes: 11110xxx 10xxxxxx 10xxxxxx 10xxxxxx
        if ((b1 & 0xF8) == 0xF0)
        {
            var b2 = Peek();
            if (b2 == -1) return b1;
            Consume();
            var b3 = Peek();
            if (b3 == -1) return ((b1 & 0x07) << 6) | (b2 & 0x3F);
            Consume();
            var b4 = Peek();
            if (b4 == -1) return ((b1 & 0x07) << 12) | ((b2 & 0x3F) << 6) | (b3 & 0x3F);
            Consume();
            return ((b1 & 0x07) << 18) | ((b2 & 0x3F) << 12) | ((b3 & 0x3F) << 6) | (b4 & 0x3F);
        }

        // Invalid UTF-8 start byte - return as-is
        return b1;
    }

    /// <summary>
    /// Append a Unicode code point to the output buffer.
    /// Handles supplementary characters (> 0xFFFF) as surrogate pairs.
    /// </summary>
    private void AppendCodePoint(int codePoint)
    {
        if (codePoint <= 0xFFFF)
        {
            AppendToOutput((char)codePoint);
        }
        else
        {
            // Supplementary character - emit as surrogate pair
            codePoint -= 0x10000;
            AppendToOutput((char)(0xD800 | (codePoint >> 10)));
            AppendToOutput((char)(0xDC00 | (codePoint & 0x3FF)));
        }
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
        var newBuffer = _bufferManager.Rent<char>(newSize).Array!;
        _outputBuffer.AsSpan(0, _outputOffset).CopyTo(newBuffer);
        _bufferManager.Return(_outputBuffer);
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
                    AppendCodePoint(entity);
                }
                else
                {
                    // Read UTF-8 character (may be 1-4 bytes)
                    var codePoint = ReadUtf8Char();
                    if (codePoint == -1)
                        break;
                    AppendCodePoint(codePoint);
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
    /// Uses proper UTF-8 decoding for non-ASCII characters.
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
                AppendCodePoint(entity);
            }
            else
            {
                // Read UTF-8 character (may be 1-4 bytes)
                var codePoint = ReadUtf8Char();
                if (codePoint == -1)
                    break;
                AppendCodePoint(codePoint);
            }
        }

        return GetOutputSpan(start);
    }

    /// <summary>
    /// Parse XML literal content with Exclusive XML Canonicalization (C14N).
    /// - Includes in-scope namespace declarations on each element
    /// - Converts self-closing tags to start/end tag pairs
    /// - Sorts namespace declarations by prefix
    /// </summary>
    private ReadOnlySpan<char> ParseXmlLiteralContent(ReadOnlySpan<char> elementName)
    {
        int start = _outputOffset;
        int depth = 1;

        // Collect in-scope namespaces for canonicalization (sorted by prefix)
        var inScopeNamespaces = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kvp in _namespaces)
        {
            if (!string.IsNullOrEmpty(kvp.Key) && kvp.Key != "xml" && kvp.Key != "xmlns")
            {
                inScopeNamespaces[kvp.Key] = kvp.Value;
            }
        }

        while (!IsEndOfInput() && depth > 0)
        {
            var ch = Peek();

            if (ch == '<')
            {
                Consume(); // consume '<'

                if (Peek() == '/')
                {
                    // Closing tag
                    depth--;
                    if (depth == 0)
                    {
                        // Don't include closing tag of wrapper element
                        while (Peek() != '>' && !IsEndOfInput())
                            Consume();
                        TryConsume('>');
                        break;
                    }
                    // Include closing tag
                    AppendToOutput('<');
                    AppendToOutput('/');
                    while (Peek() != '>' && !IsEndOfInput())
                    {
                        var codePoint = ReadUtf8Char();
                        if (codePoint == -1) break;
                        AppendCodePoint(codePoint);
                    }
                    if (Peek() == '>')
                    {
                        AppendToOutput('>');
                        Consume();
                    }
                }
                else if (Peek() == '!')
                {
                    // Comment or CDATA - include as-is
                    AppendToOutput('<');
                    AppendToOutput('!');
                    Consume();
                    while (Peek() != '>' && !IsEndOfInput())
                    {
                        var codePoint = ReadUtf8Char();
                        if (codePoint == -1) break;
                        AppendCodePoint(codePoint);
                    }
                    if (Peek() == '>')
                    {
                        AppendToOutput('>');
                        Consume();
                    }
                }
                else if (Peek() == '?')
                {
                    // PI - skip entirely per C14N (PIs in XMLLiteral are excluded)
                    while (!(Peek() == '?' && PeekAhead(1) == '>') && !IsEndOfInput())
                        Consume();
                    TryConsume('?');
                    TryConsume('>');
                }
                else
                {
                    // Opening tag - apply canonicalization
                    ParseCanonicalElement(inScopeNamespaces, ref depth);
                }
            }
            else
            {
                // Text content - UTF-8 decode
                var codePoint = ReadUtf8Char();
                if (codePoint == -1) break;
                AppendCodePoint(codePoint);
            }
        }

        return GetOutputSpan(start);
    }

    /// <summary>
    /// Parse and canonicalize an XML element for XMLLiteral.
    /// </summary>
    private void ParseCanonicalElement(SortedDictionary<string, string> inScopeNamespaces, ref int depth)
    {
        // Parse element name
        int nameStart = _outputOffset;
        while (IsNameChar(Peek()))
        {
            var codePoint = ReadUtf8Char();
            if (codePoint == -1) break;
            AppendCodePoint(codePoint);
        }
        var tagName = GetOutputSpan(nameStart).ToString();
        _outputOffset = nameStart; // Reset - we'll output canonicalized form

        // Parse attributes (we need to collect them for sorting)
        var elementAttrs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var elementNsDecls = new SortedDictionary<string, string>(StringComparer.Ordinal);
        bool selfClosing = false;

        SkipWhitespace();
        while (Peek() != '>' && Peek() != '/' && !IsEndOfInput())
        {
            // Parse attribute name
            int attrNameStart = _outputOffset;
            while (IsNameChar(Peek()))
            {
                var codePoint = ReadUtf8Char();
                if (codePoint == -1) break;
                AppendCodePoint(codePoint);
            }
            var attrName = GetOutputSpan(attrNameStart).ToString();
            _outputOffset = attrNameStart;

            SkipWhitespace();
            if (!TryConsume('='))
            {
                SkipWhitespace();
                continue;
            }
            SkipWhitespace();

            // Parse attribute value
            var quote = Peek();
            if (quote != '"' && quote != '\'')
                continue;
            Consume();

            int valueStart = _outputOffset;
            while (Peek() != quote && !IsEndOfInput())
            {
                if (Peek() == '&')
                {
                    var entity = ParseEntityReference();
                    AppendCodePoint(entity);
                }
                else
                {
                    var codePoint = ReadUtf8Char();
                    if (codePoint == -1) break;
                    AppendCodePoint(codePoint);
                }
            }
            var attrValue = GetOutputSpan(valueStart).ToString();
            _outputOffset = valueStart;
            TryConsume((char)quote);

            // Classify as namespace declaration or regular attribute
            if (attrName.StartsWith("xmlns:"))
            {
                var prefix = attrName[6..];
                elementNsDecls[prefix] = attrValue;
            }
            else if (attrName == "xmlns")
            {
                // Default namespace - skip for Exclusive C14N unless used
            }
            else
            {
                elementAttrs[attrName] = attrValue;
            }

            SkipWhitespace();
        }

        // Check for self-closing
        if (Peek() == '/')
        {
            selfClosing = true;
            Consume();
        }
        TryConsume('>');

        // Output canonicalized element
        AppendToOutput('<');
        AppendToOutput(tagName.AsSpan());

        // Output namespace declarations (in-scope + element's own)
        // Per RDF/XML XMLLiteral canonicalization, rdf namespace comes first, then others alphabetically
        var allNsDecls = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kvp in inScopeNamespaces)
            allNsDecls[kvp.Key] = kvp.Value;
        foreach (var kvp in elementNsDecls)
            allNsDecls[kvp.Key] = kvp.Value;

        // Output rdf namespace first if present
        if (allNsDecls.TryGetValue("rdf", out var rdfNs))
        {
            AppendToOutput(" xmlns:rdf=\"".AsSpan());
            AppendToOutput(rdfNs.AsSpan());
            AppendToOutput('"');
        }

        // Then output other namespaces alphabetically
        foreach (var kvp in allNsDecls)
        {
            if (kvp.Key == "rdf") continue; // Already output
            AppendToOutput(" xmlns:".AsSpan());
            AppendToOutput(kvp.Key.AsSpan());
            AppendToOutput("=\"".AsSpan());
            AppendToOutput(kvp.Value.AsSpan());
            AppendToOutput('"');
        }

        // Output attributes (sorted)
        foreach (var kvp in elementAttrs)
        {
            AppendToOutput(' ');
            AppendToOutput(kvp.Key.AsSpan());
            AppendToOutput("=\"".AsSpan());
            // Escape attribute value
            foreach (var c in kvp.Value)
            {
                switch (c)
                {
                    case '<': AppendToOutput("&lt;".AsSpan()); break;
                    case '>': AppendToOutput("&gt;".AsSpan()); break;
                    case '&': AppendToOutput("&amp;".AsSpan()); break;
                    case '"': AppendToOutput("&quot;".AsSpan()); break;
                    default: AppendToOutput(c); break;
                }
            }
            AppendToOutput('"');
        }

        AppendToOutput('>');

        if (selfClosing)
        {
            // C14N: self-closing becomes explicit close tag
            AppendToOutput("</".AsSpan());
            AppendToOutput(tagName.AsSpan());
            AppendToOutput('>');
        }
        else
        {
            depth++;
        }
    }

    /// <summary>
    /// Parse entity reference (&amp;name;) and return decoded code point.
    /// Returns an int to support supplementary characters (> 0xFFFF).
    /// </summary>
    private int ParseEntityReference()
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
            return value;
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
