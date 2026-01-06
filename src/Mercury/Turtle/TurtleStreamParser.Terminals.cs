// TurtleStreamParser.Terminals.cs
// Terminal parsing methods for Turtle parser

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace SkyOmega.Mercury.Rdf.Turtle;

public sealed partial class TurtleStreamParser
{
    /// <summary>
    /// [25] iri ::= IRIREF | PrefixedName
    /// </summary>
    private string ParseIri()
    {
        SkipWhitespaceAndComments();
        
        var ch = Peek();

        // IRIREF: <...>
        return ch == '<' ? ParseIriRef() :
            // PrefixedName
            ParsePrefixedName();
    }
    
    /// <summary>
    /// [38] IRIREF ::= '<' ([^#x00-#x20<>"{}|^`\] | UCHAR)* '>'
    /// Returns IRI with angle brackets for compatibility with SPARQL and QuadStore.
    /// </summary>
    private string ParseIriRef()
    {
        if (!TryConsume('<'))
            return string.Empty;

        _sb.Clear();
        _sb.Append('<'); // Include opening bracket

        while (true)
        {
            var ch = Peek();

            if (ch == -1)
                throw ParserException("Unexpected end of input in IRI reference");

            if (ch == '>')
            {
                Consume();
                _sb.Append('>'); // Include closing bracket
                break;
            }

            // Check for invalid characters
            if (ch < 0x20 || ch == '<' || ch == '"' || ch == '{' ||
                ch == '}' || ch == '|' || ch == '^' || ch == '`' || ch == '\\')
            {
                // Check if it's an escape sequence
                if (ch == '\\')
                {
                    Consume();
                    var escaped = ParseUnicodeEscape();
                    _sb.Append(escaped);
                    continue;
                }

                throw ParserException($"Invalid character in IRI reference: U+{ch:X4}");
            }

            Consume();
            _sb.Append((char)ch);
        }

        var iriStr = _sb.ToString();

        // Resolve relative IRI against base URI
        return ResolveIri(iriStr);
    }
    
    /// <summary>
    /// [26] PrefixedName ::= PNAME_LN | PNAME_NS
    /// [39] PNAME_NS ::= PN_PREFIX? ':'
    /// [40] PNAME_LN ::= PNAME_NS PN_LOCAL
    /// </summary>
    private string ParsePrefixedName()
    {
        _sb.Clear();

        // Parse prefix
        while (true)
        {
            var ch = Peek();

            if (ch == ':')
            {
                Consume();
                break;
            }

            if (ch == -1 || !IsPnCharsBase(ch) && !IsPnChars(ch) && ch != '.')
                return string.Empty;

            Consume();
            _sb.Append((char)ch);
        }

        var prefix = _sb.ToString();

        // Look up namespace
        if (!_namespaces.TryGetValue(prefix, out var namespaceUri))
            throw ParserException($"Undefined prefix: {prefix}");

        // Parse local name
        var localName = ParsePnLocal();

        // namespaceUri includes angle brackets like <http://...#>
        // We need to insert local name BEFORE the closing bracket
        if (namespaceUri.StartsWith('<') && namespaceUri.EndsWith('>'))
        {
            // Insert local name before closing bracket
            return string.Concat(namespaceUri.AsSpan(0, namespaceUri.Length - 1), localName, ">");
        }

        // Fallback: namespace doesn't have brackets, wrap everything
        return string.Concat("<", namespaceUri, localName, ">");
    }
    
    /// <summary>
    /// [59] PN_LOCAL ::= (PN_CHARS_U | ':' | [0-9] | PLX)
    ///                   ((PN_CHARS | '.' | ':' | PLX)* (PN_CHARS | ':' | PLX))?
    /// </summary>
    private string ParsePnLocal()
    {
        _sb.Clear();

        while (true)
        {
            var ch = Peek();

            if (ch == -1)
                break;

            // PN_LOCAL grammar: IsPnChars includes PN_CHARS_U, '-', digits, and combining chars
            if (IsPnChars(ch) || ch == ':')
            {
                Consume();
                _sb.Append((char)ch);
            }
            else if (ch == '.')
            {
                // Period is allowed in middle
                var next = PeekAhead(1);
                if (IsPnChars(next) || next == ':')
                {
                    Consume();
                    _sb.Append('.');
                }
                else
                {
                    break;
                }
            }
            else if (ch == '\\')
            {
                // Reserved character escape: \~.-!$&'()*+,;=/?#@%_
                Consume();
                var escaped = Peek();
                if (IsReservedCharEscape(escaped))
                {
                    Consume();
                    _sb.Append((char)escaped);
                }
                else
                {
                    throw ParserException($"Invalid escape sequence in local name: \\{(char)escaped}");
                }
            }
            else if (ch == '%')
            {
                // Percent encoding
                var encoded = ParsePercentEncoded();
                _sb.Append(encoded);
            }
            else
            {
                break;
            }
        }

        return _sb.ToString();
    }
    
    /// <summary>
    /// [22] RDFLiteral ::= String (LANG_DIR | ('^^' iri))?
    /// </summary>
    private string ParseLiteral()
    {
        SkipWhitespaceAndComments();
        
        var ch = Peek();
        
        // Boolean literal
        if (PeekString("true"))
        {
            ConsumeString("true");
            return "\"true\"^^<http://www.w3.org/2001/XMLSchema#boolean>";
        }
        
        if (PeekString("false"))
        {
            ConsumeString("false");
            return "\"false\"^^<http://www.w3.org/2001/XMLSchema#boolean>";
        }
        
        // Numeric literal
        if (char.IsDigit((char)ch) || ch == '+' || ch == '-' || ch == '.')
        {
            return ParseNumericLiteral();
        }
        
        // String literal
        var lexicalForm = ParseStringLiteral();
        
        SkipWhitespaceAndComments();
        
        // Check for language tag or datatype
        ch = Peek();
        
        if (ch == '@')
        {
            Consume();
            var langTag = ParseLangDir();
            return string.Concat("\"", lexicalForm, "\"@", langTag);
        }

        if (ch == '^' && PeekAhead(1) == '^')
        {
            Consume(); // ^
            Consume(); // ^
            var datatype = ParseIri();
            return string.Concat("\"", lexicalForm, "\"^^<", datatype, ">");
        }

        // Plain string (implicit xsd:string)
        return string.Concat("\"", lexicalForm, "\"");
    }
    
    /// <summary>
    /// [24] String ::= STRING_LITERAL_QUOTE | STRING_LITERAL_SINGLE_QUOTE | 
    ///                 STRING_LITERAL_LONG_SINGLE_QUOTE | STRING_LITERAL_LONG_QUOTE
    /// </summary>
    private string ParseStringLiteral()
    {
        var ch = Peek();
        
        if (ch == '"')
        {
            // Check for long literal: """
            if (PeekAhead(1) == '"' && PeekAhead(2) == '"')
                return ParseLongStringLiteral('"');
            
            return ParseShortStringLiteral('"');
        }
        
        if (ch == '\'')
        {
            // Check for long literal: '''
            if (PeekAhead(1) == '\'' && PeekAhead(2) == '\'')
                return ParseLongStringLiteral('\'');
            
            return ParseShortStringLiteral('\'');
        }
        
        throw ParserException("Expected string literal");
    }
    
    /// <summary>
    /// Parse short string literal (single line)
    /// [47] STRING_LITERAL_QUOTE ::= '"' ([^#x22#x5C#x0A#x0D] | ECHAR | UCHAR)* '"'
    /// </summary>
    private string ParseShortStringLiteral(char delimiter)
    {
        if (!TryConsume(delimiter))
            throw ParserException($"Expected {delimiter}");

        _sb.Clear();

        while (true)
        {
            var ch = Peek();

            if (ch == -1)
                throw ParserException("Unexpected end of input in string literal");

            if (ch == delimiter)
            {
                Consume();
                break;
            }

            // No LF or CR in short strings
            if (ch == '\n' || ch == '\r')
                throw ParserException("Line break in short string literal");

            if (ch == '\\')
            {
                Consume();
                _sb.Append(ParseEscapeSequence());
            }
            else
            {
                Consume();
                _sb.Append((char)ch);
            }
        }

        return _sb.ToString();
    }
    
    /// <summary>
    /// Parse long string literal (multi-line)
    /// [50] STRING_LITERAL_LONG_QUOTE ::= '"""' (('"' | '""')? ([^"\] | ECHAR | UCHAR))* '"""'
    /// </summary>
    private string ParseLongStringLiteral(char delimiter)
    {
        // Consume opening delimiter (3x)
        if (!TryConsume(delimiter) || !TryConsume(delimiter) || !TryConsume(delimiter))
            throw ParserException($"Expected {delimiter}{delimiter}{delimiter}");

        _sb.Clear();

        while (true)
        {
            var ch = Peek();

            if (ch == -1)
                throw ParserException("Unexpected end of input in long string literal");

            // Check for closing delimiter
            if (ch == delimiter)
            {
                if (PeekAhead(1) == delimiter && PeekAhead(2) == delimiter)
                {
                    Consume(); Consume(); Consume();
                    break;
                }
            }

            if (ch == '\\')
            {
                Consume();
                _sb.Append(ParseEscapeSequence());
            }
            else
            {
                Consume();
                _sb.Append((char)ch);
            }
        }

        return _sb.ToString();
    }
    
    /// <summary>
    /// [21] NumericLiteral ::= INTEGER | DECIMAL | DOUBLE
    /// </summary>
    private string ParseNumericLiteral()
    {
        _sb.Clear();

        // Optional sign
        var ch = Peek();
        if (ch == '+' || ch == '-')
        {
            Consume();
            _sb.Append((char)ch);
        }

        // Parse digits
        var hasDigits = false;
        var hasDecimal = false;
        var hasExponent = false;

        while (true)
        {
            ch = Peek();

            if (char.IsDigit((char)ch))
            {
                Consume();
                _sb.Append((char)ch);
                hasDigits = true;
            }
            else if (ch == '.' && !hasDecimal && !hasExponent)
            {
                Consume();
                _sb.Append('.');
                hasDecimal = true;
            }
            else if ((ch == 'e' || ch == 'E') && !hasExponent && hasDigits)
            {
                Consume();
                _sb.Append((char)ch);
                hasExponent = true;

                // Optional sign in exponent
                ch = Peek();
                if (ch == '+' || ch == '-')
                {
                    Consume();
                    _sb.Append((char)ch);
                }
            }
            else
            {
                break;
            }
        }

        if (!hasDigits)
            throw ParserException("Invalid numeric literal");

        var value = _sb.ToString();

        // Determine datatype
        if (hasExponent)
            return string.Concat("\"", value, "\"^^<http://www.w3.org/2001/XMLSchema#double>");

        if (hasDecimal)
            return string.Concat("\"", value, "\"^^<http://www.w3.org/2001/XMLSchema#decimal>");

        return string.Concat("\"", value, "\"^^<http://www.w3.org/2001/XMLSchema#integer>");
    }
    
    /// <summary>
    /// [42] LANG_DIR ::= '@' [a-zA-Z]+ ('-' [a-zA-Z0-9]+)* ('--' [a-zA-Z]+)?
    /// </summary>
    private string ParseLangDir()
    {
        _sb.Clear();

        // Parse language tag
        while (true)
        {
            var ch = Peek();

            if (char.IsLetter((char)ch))
            {
                Consume();
                _sb.Append(char.ToLowerInvariant((char)ch));
            }
            else if (ch == '-')
            {
                var next = PeekAhead(1);

                // Check for initial text direction: --
                if (next == '-')
                {
                    Consume(); Consume();
                    _sb.Append("--");

                    // Parse direction
                    while (char.IsLetter((char)Peek()))
                    {
                        ch = Peek();
                        Consume();
                        _sb.Append(char.ToLowerInvariant((char)ch));
                    }

                    break;
                }

                // Continue with subtag
                Consume();
                _sb.Append('-');
            }
            else
            {
                break;
            }
        }

        return _sb.ToString();
    }
    
    /// <summary>
    /// [27] BlankNode ::= BLANK_NODE_LABEL | ANON
    /// [41] BLANK_NODE_LABEL ::= '_:' (PN_CHARS_U | [0-9]) ((PN_CHARS | '.')* PN_CHARS)?
    /// </summary>
    private string ParseBlankNode()
    {
        if (!TryConsume('_') || !TryConsume(':'))
            throw ParserException("Invalid blank node");

        _sb.Clear();

        while (true)
        {
            var ch = Peek();

            if (IsPnCharsU(ch) || char.IsDigit((char)ch) || IsPnChars(ch))
            {
                Consume();
                _sb.Append((char)ch);
            }
            else if (ch == '.')
            {
                var next = PeekAhead(1);
                if (IsPnChars(next))
                {
                    Consume();
                    _sb.Append('.');
                }
                else
                {
                    break;
                }
            }
            else
            {
                break;
            }
        }

        var label = _sb.ToString();

        // Allocate or reuse blank node identifier
        if (!_blankNodes.TryGetValue(label, out var blankNodeId))
        {
            blankNodeId = string.Concat("_:b", _blankNodeCounter++.ToString());
            _blankNodes[label] = blankNodeId;
        }

        return blankNodeId;
    }
    
    private string ParseBooleanLiteral()
    {
        if (PeekString("true"))
        {
            ConsumeString("true");
            return "\"true\"^^<http://www.w3.org/2001/XMLSchema#boolean>";
        }

        if (PeekString("false"))
        {
            ConsumeString("false");
            return "\"false\"^^<http://www.w3.org/2001/XMLSchema#boolean>";
        }

        throw ParserException("Invalid boolean literal");
    }

    #region Zero-GC Span-Based Parsing

    /// <summary>
    /// Parse subject and return span into output buffer.
    /// </summary>
    private ReadOnlySpan<char> ParseSubjectSpan()
    {
        SkipWhitespaceAndComments();

        var ch = Peek();

        if (ch == '(')
            return ParseCollectionSpan();

        if (ch == '_' && PeekAhead(1) == ':')
            return ParseBlankNodeSpan();

        return ParseIriSpan();
    }

    /// <summary>
    /// Parse verb (predicate or 'a') and return span.
    /// </summary>
    private ReadOnlySpan<char> ParseVerbSpan()
    {
        SkipWhitespaceAndComments();

        if (Peek() == 'a' && IsWhitespaceOrTerminator(PeekAhead(1)))
        {
            Consume();
            int start = _outputOffset;
            // Include angle brackets for consistency with other IRIs
            AppendToOutput("<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>".AsSpan());
            return GetOutputSpan(start);
        }

        return ParseIriSpan();
    }

    /// <summary>
    /// Parse object and return span into output buffer.
    /// </summary>
    private ReadOnlySpan<char> ParseObjectSpan()
    {
        SkipWhitespaceAndComments();

        var ch = Peek();

        if (ch == '[')
            return ParseBlankNodePropertyListSpan();

        if (ch == '(')
            return ParseCollectionSpan();

        if (ch == '_' && PeekAhead(1) == ':')
            return ParseBlankNodeSpan();

        if (PeekString("<<("))
            return ParseTripleTermSpan();

        if (PeekString("<<"))
            return ParseReifiedTripleSpan();

        if (ch == '"' || ch == '\'' || char.IsDigit((char)ch) || ch == '+' || ch == '-' || ch == '.')
            return ParseLiteralSpan();

        if (PeekString("true") || PeekString("false"))
            return ParseBooleanLiteralSpan();

        return ParseIriSpan();
    }

    /// <summary>
    /// Parse IRI and return span into output buffer.
    /// </summary>
    private ReadOnlySpan<char> ParseIriSpan()
    {
        SkipWhitespaceAndComments();

        var ch = Peek();
        return ch == '<' ? ParseIriRefSpan() : ParsePrefixedNameSpan();
    }

    /// <summary>
    /// Parse IRIREF and return span into output buffer.
    /// Returns IRI with angle brackets for compatibility with SPARQL and QuadStore.
    /// </summary>
    private ReadOnlySpan<char> ParseIriRefSpan()
    {
        if (!TryConsume('<'))
            return ReadOnlySpan<char>.Empty;

        int start = _outputOffset;
        AppendToOutput('<'); // Include opening bracket

        while (true)
        {
            var ch = Peek();

            if (ch == -1)
                throw ParserException("Unexpected end of input in IRI reference");

            if (ch == '>')
            {
                Consume();
                AppendToOutput('>'); // Include closing bracket
                break;
            }

            if (ch < 0x20 || ch == '<' || ch == '"' || ch == '{' ||
                ch == '}' || ch == '|' || ch == '^' || ch == '`' || ch == '\\')
            {
                if (ch == '\\')
                {
                    Consume();
                    var escaped = ParseUnicodeEscape();
                    AppendToOutput(escaped);
                    continue;
                }

                throw ParserException($"Invalid character in IRI reference: U+{ch:X4}");
            }

            Consume();
            AppendToOutput((char)ch);
        }

        var iriSpan = GetOutputSpan(start);

        // Resolve relative IRI - this may need to append to buffer
        return ResolveIriSpan(iriSpan, start);
    }

    /// <summary>
    /// Resolve IRI against base, returning span.
    /// </summary>
    private ReadOnlySpan<char> ResolveIriSpan(ReadOnlySpan<char> iri, int iriStart)
    {
        // If absolute IRI, return as-is
        if (iri.Contains("://".AsSpan(), StringComparison.Ordinal))
            return iri;

        // No base URI, return as-is
        if (string.IsNullOrEmpty(_baseUri))
            return iri;

        // Need to resolve - replace with resolved IRI
        try
        {
            var baseUri = new Uri(_baseUri, UriKind.Absolute);
            var resolved = new Uri(baseUri, iri.ToString()); // Unavoidable allocation for Uri
            _outputOffset = iriStart; // Reset to overwrite
            AppendToOutput(resolved.ToString().AsSpan());
            return GetOutputSpan(iriStart);
        }
        catch
        {
            return iri;
        }
    }

    /// <summary>
    /// Parse prefixed name and return span.
    /// </summary>
    private ReadOnlySpan<char> ParsePrefixedNameSpan()
    {
        int prefixStart = _outputOffset;

        // Parse prefix
        while (true)
        {
            var ch = Peek();

            if (ch == ':')
            {
                Consume();
                break;
            }

            if (ch == -1 || !IsPnCharsBase(ch) && !IsPnChars(ch) && ch != '.')
                return ReadOnlySpan<char>.Empty;

            Consume();
            AppendToOutput((char)ch);
        }

        var prefixSpan = GetOutputSpan(prefixStart);
        var prefix = prefixSpan.ToString(); // Need string for dictionary lookup

        if (!_namespaces.TryGetValue(prefix, out var namespaceUri))
            throw ParserException($"Undefined prefix: {prefix}");

        // Replace prefix with namespace URI
        // namespaceUri includes angle brackets like <http://...#>
        // We need to insert local name BEFORE the closing bracket
        _outputOffset = prefixStart;

        if (namespaceUri.StartsWith('<') && namespaceUri.EndsWith('>'))
        {
            // Append namespace without closing bracket
            AppendToOutput(namespaceUri.AsSpan(0, namespaceUri.Length - 1));

            // Parse and append local name
            ParsePnLocalSpan();

            // Append closing bracket
            AppendToOutput('>');
        }
        else
        {
            // Fallback: namespace doesn't have brackets, wrap everything
            AppendToOutput('<');
            AppendToOutput(namespaceUri.AsSpan());
            ParsePnLocalSpan();
            AppendToOutput('>');
        }

        return GetOutputSpan(prefixStart);
    }

    /// <summary>
    /// Parse PN_LOCAL directly into output buffer.
    /// </summary>
    private void ParsePnLocalSpan()
    {
        while (true)
        {
            var ch = Peek();

            if (ch == -1)
                break;

            // PN_LOCAL grammar: IsPnChars includes PN_CHARS_U, '-', digits, and combining chars
            if (IsPnChars(ch) || ch == ':')
            {
                Consume();
                AppendToOutput((char)ch);
            }
            else if (ch == '.')
            {
                var next = PeekAhead(1);
                if (IsPnChars(next) || next == ':')
                {
                    Consume();
                    AppendToOutput('.');
                }
                else
                {
                    break;
                }
            }
            else if (ch == '\\')
            {
                Consume();
                var escaped = Peek();
                if (IsReservedCharEscape(escaped))
                {
                    Consume();
                    AppendToOutput((char)escaped);
                }
                else
                {
                    throw ParserException($"Invalid escape sequence in local name: \\{(char)escaped}");
                }
            }
            else if (ch == '%')
            {
                ParsePercentEncodedSpan();
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>
    /// Parse percent-encoded sequence into output buffer.
    /// </summary>
    private void ParsePercentEncodedSpan()
    {
        if (!TryConsume('%'))
            throw ParserException("Expected '%'");

        AppendToOutput('%');

        for (int i = 0; i < 2; i++)
        {
            var ch = Peek();
            if (ch == -1 || !IsHexDigit(ch))
                throw ParserException("Invalid percent encoding");

            Consume();
            AppendToOutput((char)ch);
        }
    }

    /// <summary>
    /// Parse literal and return span.
    /// </summary>
    private ReadOnlySpan<char> ParseLiteralSpan()
    {
        SkipWhitespaceAndComments();

        var ch = Peek();

        // Boolean literal
        if (PeekString("true"))
        {
            ConsumeString("true");
            int start = _outputOffset;
            AppendToOutput("\"true\"^^<http://www.w3.org/2001/XMLSchema#boolean>".AsSpan());
            return GetOutputSpan(start);
        }

        if (PeekString("false"))
        {
            ConsumeString("false");
            int start = _outputOffset;
            AppendToOutput("\"false\"^^<http://www.w3.org/2001/XMLSchema#boolean>".AsSpan());
            return GetOutputSpan(start);
        }

        // Numeric literal
        if (char.IsDigit((char)ch) || ch == '+' || ch == '-' || ch == '.')
            return ParseNumericLiteralSpan();

        // String literal
        return ParseStringLiteralSpan();
    }

    /// <summary>
    /// Parse string literal with optional language tag or datatype.
    /// </summary>
    private ReadOnlySpan<char> ParseStringLiteralSpan()
    {
        int start = _outputOffset;
        var ch = Peek();

        // Determine delimiter and parse content
        if (ch == '"')
        {
            if (PeekAhead(1) == '"' && PeekAhead(2) == '"')
                ParseLongStringContentSpan('"');
            else
                ParseShortStringContentSpan('"');
        }
        else if (ch == '\'')
        {
            if (PeekAhead(1) == '\'' && PeekAhead(2) == '\'')
                ParseLongStringContentSpan('\'');
            else
                ParseShortStringContentSpan('\'');
        }
        else
        {
            throw ParserException("Expected string literal");
        }

        SkipWhitespaceAndComments();
        ch = Peek();

        // Language tag
        if (ch == '@')
        {
            Consume();
            AppendToOutput('@');
            ParseLangDirSpan();
        }
        // Datatype
        else if (ch == '^' && PeekAhead(1) == '^')
        {
            Consume(); Consume();
            AppendToOutput("^^<".AsSpan());
            int dtStart = _outputOffset;
            var dtSpan = ParseIriSpan();
            // The IRI is already in the buffer, just close with >
            AppendToOutput('>');
        }

        return GetOutputSpan(start);
    }

    /// <summary>
    /// Parse short string content including quotes.
    /// </summary>
    private void ParseShortStringContentSpan(char delimiter)
    {
        if (!TryConsume(delimiter))
            throw ParserException($"Expected {delimiter}");

        AppendToOutput('"');

        while (true)
        {
            var ch = Peek();

            if (ch == -1)
                throw ParserException("Unexpected end of input in string literal");

            if (ch == delimiter)
            {
                Consume();
                break;
            }

            if (ch == '\n' || ch == '\r')
                throw ParserException("Line break in short string literal");

            if (ch == '\\')
            {
                Consume();
                AppendToOutput(ParseEscapeSequence());
            }
            else
            {
                Consume();
                AppendToOutput((char)ch);
            }
        }

        AppendToOutput('"');
    }

    /// <summary>
    /// Parse long string content including quotes.
    /// </summary>
    private void ParseLongStringContentSpan(char delimiter)
    {
        if (!TryConsume(delimiter) || !TryConsume(delimiter) || !TryConsume(delimiter))
            throw ParserException($"Expected {delimiter}{delimiter}{delimiter}");

        AppendToOutput('"');

        while (true)
        {
            var ch = Peek();

            if (ch == -1)
                throw ParserException("Unexpected end of input in long string literal");

            if (ch == delimiter)
            {
                if (PeekAhead(1) == delimiter && PeekAhead(2) == delimiter)
                {
                    Consume(); Consume(); Consume();
                    break;
                }
            }

            if (ch == '\\')
            {
                Consume();
                AppendToOutput(ParseEscapeSequence());
            }
            else
            {
                Consume();
                AppendToOutput((char)ch);
            }
        }

        AppendToOutput('"');
    }

    /// <summary>
    /// Parse numeric literal and return span.
    /// </summary>
    private ReadOnlySpan<char> ParseNumericLiteralSpan()
    {
        int start = _outputOffset;
        AppendToOutput('"');

        // Optional sign
        var ch = Peek();
        if (ch == '+' || ch == '-')
        {
            Consume();
            AppendToOutput((char)ch);
        }

        var hasDigits = false;
        var hasDecimal = false;
        var hasExponent = false;

        while (true)
        {
            ch = Peek();

            if (char.IsDigit((char)ch))
            {
                Consume();
                AppendToOutput((char)ch);
                hasDigits = true;
            }
            else if (ch == '.' && !hasDecimal && !hasExponent)
            {
                Consume();
                AppendToOutput('.');
                hasDecimal = true;
            }
            else if ((ch == 'e' || ch == 'E') && !hasExponent && hasDigits)
            {
                Consume();
                AppendToOutput((char)ch);
                hasExponent = true;

                ch = Peek();
                if (ch == '+' || ch == '-')
                {
                    Consume();
                    AppendToOutput((char)ch);
                }
            }
            else
            {
                break;
            }
        }

        if (!hasDigits)
            throw ParserException("Invalid numeric literal");

        AppendToOutput('"');

        // Append datatype
        if (hasExponent)
            AppendToOutput("^^<http://www.w3.org/2001/XMLSchema#double>".AsSpan());
        else if (hasDecimal)
            AppendToOutput("^^<http://www.w3.org/2001/XMLSchema#decimal>".AsSpan());
        else
            AppendToOutput("^^<http://www.w3.org/2001/XMLSchema#integer>".AsSpan());

        return GetOutputSpan(start);
    }

    /// <summary>
    /// Parse language tag directly into output buffer.
    /// </summary>
    private void ParseLangDirSpan()
    {
        while (true)
        {
            var ch = Peek();

            if (char.IsLetter((char)ch))
            {
                Consume();
                AppendToOutput(char.ToLowerInvariant((char)ch));
            }
            else if (ch == '-')
            {
                var next = PeekAhead(1);

                if (next == '-')
                {
                    Consume(); Consume();
                    AppendToOutput('-');
                    AppendToOutput('-');

                    while (char.IsLetter((char)Peek()))
                    {
                        ch = Peek();
                        Consume();
                        AppendToOutput(char.ToLowerInvariant((char)ch));
                    }

                    break;
                }

                Consume();
                AppendToOutput('-');
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>
    /// Parse blank node and return span.
    /// </summary>
    private ReadOnlySpan<char> ParseBlankNodeSpan()
    {
        if (!TryConsume('_') || !TryConsume(':'))
            throw ParserException("Invalid blank node");

        int labelStart = _outputOffset;

        while (true)
        {
            var ch = Peek();

            if (IsPnCharsU(ch) || char.IsDigit((char)ch) || IsPnChars(ch))
            {
                Consume();
                AppendToOutput((char)ch);
            }
            else if (ch == '.')
            {
                var next = PeekAhead(1);
                if (IsPnChars(next))
                {
                    Consume();
                    AppendToOutput('.');
                }
                else
                {
                    break;
                }
            }
            else
            {
                break;
            }
        }

        var labelSpan = GetOutputSpan(labelStart);
        var label = labelSpan.ToString();

        // Get or create blank node ID
        if (!_blankNodes.TryGetValue(label, out var blankNodeId))
        {
            blankNodeId = string.Concat("_:b", _blankNodeCounter++.ToString());
            _blankNodes[label] = blankNodeId;
        }

        // Replace label with blank node ID
        _outputOffset = labelStart;
        AppendToOutput(blankNodeId.AsSpan());
        return GetOutputSpan(labelStart);
    }

    /// <summary>
    /// Parse boolean literal and return span.
    /// </summary>
    private ReadOnlySpan<char> ParseBooleanLiteralSpan()
    {
        int start = _outputOffset;

        if (PeekString("true"))
        {
            ConsumeString("true");
            AppendToOutput("\"true\"^^<http://www.w3.org/2001/XMLSchema#boolean>".AsSpan());
        }
        else if (PeekString("false"))
        {
            ConsumeString("false");
            AppendToOutput("\"false\"^^<http://www.w3.org/2001/XMLSchema#boolean>".AsSpan());
        }
        else
        {
            throw ParserException("Invalid boolean literal");
        }

        return GetOutputSpan(start);
    }

    #endregion
}
