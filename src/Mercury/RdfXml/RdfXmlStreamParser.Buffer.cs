// RdfXmlStreamParser.Buffer.cs
// Buffer management and low-level parsing utilities

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.Rdf;

namespace SkyOmega.Mercury.RdfXml;

internal sealed partial class RdfXmlStreamParser
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
        // UTF-16 encoding shared with every RDF-family parser via RdfEscape (docs/divergence S1d).
        Span<char> chars = stackalloc char[2];
        int n = RdfEscape.EncodeUtf16(codePoint, chars);
        for (int i = 0; i < n; i++)
            AppendToOutput(chars[i]);
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
    /// The lexical form of a parseType="Literal" property element's content (RDF/XML §7.2.17): its Exclusive XML
    /// Canonicalization (W3C xml-exc-c14n), with comments and an empty InclusiveNamespaces PrefixList, the form RDF/XML
    /// 2004 defined and the W3C tests expect (xml-canon-test001, -002).
    /// <list type="bullet">
    /// <item>An element declares only the namespaces it visibly uses: its own prefix (the default namespace when it has
    /// none) and its attributes' prefixes, and only where no output ancestor declared the same binding.</item>
    /// <item>Declarations are sorted by prefix, the default first; attributes by namespace IRI, then local name.</item>
    /// <item>An empty element is a start tag and an end tag.</item>
    /// <item>Text and attribute values have their references decoded, then &amp; &lt; &gt; (text) or &amp; &lt; "
    /// tab, LF, CR (attributes) written as references; a CDATA section is its text; line ends are LF.</item>
    /// <item>Comments and processing instructions are kept.</item>
    /// </list>
    /// The content ends at the property element's end tag, which is not part of it. The namespaces in scope at the
    /// property element are the parser's; those declared inside the literal are in scope for their element's content.
    /// </summary>
    private ReadOnlySpan<char> ParseXmlLiteralContent(ReadOnlySpan<char> elementName)
    {
        int start = _outputOffset;
        // Per open element: the bindings in scope (prefix to IRI, "" the default) and those output ancestors declared
        var inScope = new List<Dictionary<string, string>> { new(_namespaces, StringComparer.Ordinal) };
        var declared = new List<Dictionary<string, string>> { new(StringComparer.Ordinal) };

        while (!IsEndOfInput())
        {
            var ch = Peek();
            if (ch == '<' && PeekAhead(1) == '/')
            {
                Consume();
                Consume();
                var name = ReadXmlName();
                SkipWhitespace();
                TryConsume('>');
                if (inScope.Count == 1)
                    break; // the property element's end tag
                AppendToOutput("</".AsSpan());
                AppendToOutput(name.AsSpan());
                AppendToOutput('>');
                inScope.RemoveAt(inScope.Count - 1);
                declared.RemoveAt(declared.Count - 1);
            }
            else if (ch == '<' && PeekAhead(1) == '!' && PeekAhead(2) == '-')
            {
                AppendXmlLiteralMarkup("<!--", "-->");
            }
            else if (ch == '<' && PeekAhead(1) == '!' && PeekAhead(2) == '[')
            {
                // <![CDATA[ … ]]> is its text
                for (int i = 0; i < "<![CDATA[".Length; i++)
                    Consume();
                while (!IsEndOfInput() && !(Peek() == ']' && PeekAhead(1) == ']' && PeekAhead(2) == '>'))
                    AppendCanonicalText(ReadXmlLiteralChar());
                for (int i = 0; i < "]]>".Length && !IsEndOfInput(); i++)
                    Consume();
            }
            else if (ch == '<' && PeekAhead(1) == '?')
            {
                AppendXmlLiteralMarkup("<?", "?>");
            }
            else if (ch == '<')
            {
                ParseCanonicalElement(inScope, declared);
            }
            else if (ch == '&')
            {
                AppendCanonicalText(ParseEntityReference());
            }
            else
            {
                var codePoint = ReadXmlLiteralChar();
                if (codePoint == -1)
                    break;
                AppendCanonicalText(codePoint);
            }
        }

        return GetOutputSpan(start);
    }

    /// <summary>
    /// An element of an XML literal, canonicalized (<see cref="ParseXmlLiteralContent"/>), from its '&lt;'. A start tag
    /// opens a scope in <paramref name="inScope"/> and <paramref name="declared"/>; an empty element is closed at once.
    /// </summary>
    private void ParseCanonicalElement(List<Dictionary<string, string>> inScope, List<Dictionary<string, string>> declared)
    {
        Consume(); // '<'
        var tagName = ReadXmlName();
        var bindings = new Dictionary<string, string>(inScope[^1], StringComparer.Ordinal);
        var attributes = new List<(string Name, string Value)>();

        SkipWhitespace();
        while (Peek() != '>' && Peek() != '/' && !IsEndOfInput())
        {
            var attrName = ReadXmlName();
            SkipWhitespace();
            if (attrName.Length == 0 || !TryConsume('='))
                break; // not well-formed: the element ends here
            SkipWhitespace();
            var quote = Peek();
            if (quote != '"' && quote != '\'')
                break;
            Consume();

            // XML attribute-value normalization: a reference is its character, a written tab, LF or CR (a CRLF once)
            // a space
            int valueStart = _outputOffset;
            while (Peek() != quote && !IsEndOfInput())
            {
                if (Peek() == '&')
                {
                    AppendCodePoint(ParseEntityReference());
                    continue;
                }
                var codePoint = ReadUtf8Char();
                if (codePoint == -1)
                    break;
                if (codePoint == '\r' && Peek() == '\n')
                    continue;
                AppendCodePoint(codePoint is '\t' or '\n' or '\r' ? ' ' : codePoint);
            }
            var attrValue = GetOutputSpan(valueStart).ToString();
            _outputOffset = valueStart;
            TryConsume((char)quote);

            if (attrName.StartsWith("xmlns:", StringComparison.Ordinal))
                bindings[attrName[6..]] = attrValue;
            else if (attrName == "xmlns")
                bindings[""] = attrValue;
            else
                attributes.Add((attrName, attrValue));
            SkipWhitespace();
        }
        bool empty = TryConsume('/');
        TryConsume('>');

        AppendToOutput('<');
        AppendToOutput(tagName.AsSpan());

        // The namespaces the element visibly uses, sorted by prefix ("" the default, first)
        var used = new SortedSet<string>(StringComparer.Ordinal) { XmlPrefixOf(tagName) };
        foreach (var (name, _) in attributes)
        {
            var prefix = XmlPrefixOf(name);
            if (prefix.Length > 0)
                used.Add(prefix);
        }
        var outer = declared[^1];
        var nowDeclared = new Dictionary<string, string>(outer, StringComparer.Ordinal);
        foreach (var prefix in used)
        {
            if (prefix == "xml")
                continue; // the xml namespace is never declared
            var iri = bindings.TryGetValue(prefix, out var bound) ? bound : "";
            var outerIri = outer.TryGetValue(prefix, out var rendered) ? rendered : "";
            if (iri == outerIri || (prefix.Length > 0 && iri.Length == 0))
                continue; // already declared by an output ancestor, or an undeclared prefix
            AppendToOutput(prefix.Length == 0 ? " xmlns=\"".AsSpan() : " xmlns:".AsSpan());
            if (prefix.Length > 0)
            {
                AppendToOutput(prefix.AsSpan());
                AppendToOutput("=\"".AsSpan());
            }
            AppendCanonicalAttributeValue(iri);
            AppendToOutput('"');
            nowDeclared[prefix] = iri;
        }

        // Attributes sorted by namespace IRI (none first), then local name
        attributes.Sort((x, y) =>
        {
            int byNamespace = string.CompareOrdinal(XmlAttributeNamespace(x.Name, bindings), XmlAttributeNamespace(y.Name, bindings));
            return byNamespace != 0 ? byNamespace : string.CompareOrdinal(XmlLocalName(x.Name), XmlLocalName(y.Name));
        });
        foreach (var (name, value) in attributes)
        {
            AppendToOutput(' ');
            AppendToOutput(name.AsSpan());
            AppendToOutput("=\"".AsSpan());
            AppendCanonicalAttributeValue(value);
            AppendToOutput('"');
        }
        AppendToOutput('>');

        if (empty)
        {
            AppendToOutput("</".AsSpan());
            AppendToOutput(tagName.AsSpan());
            AppendToOutput('>');
            return;
        }
        inScope.Add(bindings);
        declared.Add(nowDeclared);
    }

    /// <summary>An XML name at the cursor (an element or attribute name), read past.</summary>
    private string ReadXmlName()
    {
        int nameStart = _outputOffset;
        while (IsNameChar(Peek()))
        {
            var codePoint = ReadUtf8Char();
            if (codePoint == -1)
                break;
            AppendCodePoint(codePoint);
        }
        var name = GetOutputSpan(nameStart).ToString();
        _outputOffset = nameStart;
        return name;
    }

    /// <summary>A character of an XML literal's content, with XML's line-end handling: CRLF and CR are LF.</summary>
    private int ReadXmlLiteralChar()
    {
        var codePoint = ReadUtf8Char();
        if (codePoint == '\r')
        {
            if (Peek() == '\n')
                Consume();
            return '\n';
        }
        return codePoint;
    }

    /// <summary>A comment or processing instruction, from <paramref name="open"/> through <paramref name="close"/>, as written (line ends as LF).</summary>
    private void AppendXmlLiteralMarkup(string open, string close)
    {
        for (int i = 0; i < open.Length; i++)
            Consume();
        AppendToOutput(open.AsSpan());
        while (!IsEndOfInput())
        {
            bool atClose = true;
            for (int i = 0; i < close.Length && atClose; i++)
                atClose = PeekAhead(i) == close[i];
            if (atClose)
                break;
            var codePoint = ReadXmlLiteralChar();
            if (codePoint == -1)
                break;
            AppendCodePoint(codePoint);
        }
        for (int i = 0; i < close.Length && !IsEndOfInput(); i++)
            Consume();
        AppendToOutput(close.AsSpan());
    }

    /// <summary>A text character in canonical form: &amp;, &lt;, &gt; and CR as references.</summary>
    private void AppendCanonicalText(int codePoint)
    {
        switch (codePoint)
        {
            case -1: break;
            case '&': AppendToOutput("&amp;".AsSpan()); break;
            case '<': AppendToOutput("&lt;".AsSpan()); break;
            case '>': AppendToOutput("&gt;".AsSpan()); break;
            case '\r': AppendToOutput("&#xD;".AsSpan()); break;
            default: AppendCodePoint(codePoint); break;
        }
    }

    /// <summary>An attribute value in canonical form: &amp;, &lt;, ", tab, LF and CR as references.</summary>
    private void AppendCanonicalAttributeValue(string value)
    {
        foreach (var c in value)
        {
            switch (c)
            {
                case '&': AppendToOutput("&amp;".AsSpan()); break;
                case '<': AppendToOutput("&lt;".AsSpan()); break;
                case '"': AppendToOutput("&quot;".AsSpan()); break;
                case '\t': AppendToOutput("&#x9;".AsSpan()); break;
                case '\n': AppendToOutput("&#xA;".AsSpan()); break;
                case '\r': AppendToOutput("&#xD;".AsSpan()); break;
                default: AppendToOutput(c); break;
            }
        }
    }

    private static string XmlPrefixOf(string qualifiedName)
    {
        int colon = qualifiedName.IndexOf(':');
        return colon > 0 ? qualifiedName[..colon] : "";
    }

    private static string XmlLocalName(string qualifiedName)
    {
        int colon = qualifiedName.IndexOf(':');
        return colon > 0 ? qualifiedName[(colon + 1)..] : qualifiedName;
    }

    /// <summary>An attribute's namespace IRI: none for an unprefixed one (the default namespace is not an attribute's).</summary>
    private static string XmlAttributeNamespace(string qualifiedName, Dictionary<string, string> bindings)
    {
        var prefix = XmlPrefixOf(qualifiedName);
        if (prefix.Length == 0)
            return "";
        if (prefix == "xml")
            return "http://www.w3.org/XML/1998/namespace";
        return bindings.TryGetValue(prefix, out var iri) ? iri : "";
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
