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
    /// </summary>
    private string ParseIriRef()
    {
        if (!TryConsume('<'))
            return string.Empty;
        
        var sb = new StringBuilder(128);
        
        while (true)
        {
            var ch = Peek();
            
            if (ch == -1)
                throw ParserException("Unexpected end of input in IRI reference");
            
            if (ch == '>')
            {
                Consume();
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
                    sb.Append(escaped);
                    continue;
                }
                
                throw ParserException($"Invalid character in IRI reference: U+{ch:X4}");
            }
            
            Consume();
            sb.Append((char)ch);
        }
        
        var iriStr = sb.ToString();
        
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
        var sb = new StringBuilder(64);
        
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
            sb.Append((char)ch);
        }
        
        var prefix = sb.ToString();
        
        // Look up namespace
        if (!_namespaces.TryGetValue(prefix, out var namespaceUri))
            throw ParserException($"Undefined prefix: {prefix}");
        
        // Parse local name
        var localName = ParsePnLocal();
        
        return namespaceUri + localName;
    }
    
    /// <summary>
    /// [59] PN_LOCAL ::= (PN_CHARS_U | ':' | [0-9] | PLX) 
    ///                   ((PN_CHARS | '.' | ':' | PLX)* (PN_CHARS | ':' | PLX))?
    /// </summary>
    private string ParsePnLocal()
    {
        var sb = new StringBuilder(64);
        
        while (true)
        {
            var ch = Peek();
            
            if (ch == -1)
                break;
            
            // Check if valid PN_LOCAL character
            if (IsPnCharsU(ch) || ch == ':' || char.IsDigit((char)ch))
            {
                Consume();
                sb.Append((char)ch);
            }
            else if (ch == '.')
            {
                // Period is allowed in middle
                var next = PeekAhead(1);
                if (IsPnChars(next) || next == ':')
                {
                    Consume();
                    sb.Append('.');
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
                    sb.Append((char)escaped);
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
                sb.Append(encoded);
            }
            else
            {
                break;
            }
        }
        
        return sb.ToString();
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
            return $"\"{lexicalForm}\"@{langTag}";
        }
        
        if (ch == '^' && PeekAhead(1) == '^')
        {
            Consume(); // ^
            Consume(); // ^
            var datatype = ParseIri();
            return $"\"{lexicalForm}\"^^<{datatype}>";
        }
        
        // Plain string (implicit xsd:string)
        return $"\"{lexicalForm}\"";
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
        
        var sb = new StringBuilder(256);
        
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
                sb.Append(ParseEscapeSequence());
            }
            else
            {
                Consume();
                sb.Append((char)ch);
            }
        }
        
        return sb.ToString();
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
        
        var sb = new StringBuilder(1024);
        
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
                sb.Append(ParseEscapeSequence());
            }
            else
            {
                Consume();
                sb.Append((char)ch);
            }
        }
        
        return sb.ToString();
    }
    
    /// <summary>
    /// [21] NumericLiteral ::= INTEGER | DECIMAL | DOUBLE
    /// </summary>
    private string ParseNumericLiteral()
    {
        var sb = new StringBuilder(32);
        
        // Optional sign
        var ch = Peek();
        if (ch == '+' || ch == '-')
        {
            Consume();
            sb.Append((char)ch);
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
                sb.Append((char)ch);
                hasDigits = true;
            }
            else if (ch == '.' && !hasDecimal && !hasExponent)
            {
                Consume();
                sb.Append('.');
                hasDecimal = true;
            }
            else if ((ch == 'e' || ch == 'E') && !hasExponent && hasDigits)
            {
                Consume();
                sb.Append((char)ch);
                hasExponent = true;
                
                // Optional sign in exponent
                ch = Peek();
                if (ch == '+' || ch == '-')
                {
                    Consume();
                    sb.Append((char)ch);
                }
            }
            else
            {
                break;
            }
        }
        
        if (!hasDigits)
            throw ParserException("Invalid numeric literal");
        
        var value = sb.ToString();
        
        // Determine datatype
        if (hasExponent)
            return $"\"{value}\"^^<http://www.w3.org/2001/XMLSchema#double>";
        
        if (hasDecimal)
            return $"\"{value}\"^^<http://www.w3.org/2001/XMLSchema#decimal>";
        
        return $"\"{value}\"^^<http://www.w3.org/2001/XMLSchema#integer>";
    }
    
    /// <summary>
    /// [42] LANG_DIR ::= '@' [a-zA-Z]+ ('-' [a-zA-Z0-9]+)* ('--' [a-zA-Z]+)?
    /// </summary>
    private string ParseLangDir()
    {
        var sb = new StringBuilder(16);
        
        // Parse language tag
        while (true)
        {
            var ch = Peek();
            
            if (char.IsLetter((char)ch))
            {
                Consume();
                sb.Append(char.ToLowerInvariant((char)ch));
            }
            else if (ch == '-')
            {
                var next = PeekAhead(1);
                
                // Check for initial text direction: --
                if (next == '-')
                {
                    Consume(); Consume();
                    sb.Append("--");
                    
                    // Parse direction
                    while (char.IsLetter((char)Peek()))
                    {
                        ch = Peek();
                        Consume();
                        sb.Append(char.ToLowerInvariant((char)ch));
                    }
                    
                    break;
                }
                
                // Continue with subtag
                Consume();
                sb.Append('-');
            }
            else
            {
                break;
            }
        }
        
        return sb.ToString();
    }
    
    /// <summary>
    /// [27] BlankNode ::= BLANK_NODE_LABEL | ANON
    /// [41] BLANK_NODE_LABEL ::= '_:' (PN_CHARS_U | [0-9]) ((PN_CHARS | '.')* PN_CHARS)?
    /// </summary>
    private string ParseBlankNode()
    {
        if (!TryConsume('_') || !TryConsume(':'))
            throw ParserException("Invalid blank node");
        
        var sb = new StringBuilder(32);
        
        while (true)
        {
            var ch = Peek();
            
            if (IsPnCharsU(ch) || char.IsDigit((char)ch) || IsPnChars(ch))
            {
                Consume();
                sb.Append((char)ch);
            }
            else if (ch == '.')
            {
                var next = PeekAhead(1);
                if (IsPnChars(next))
                {
                    Consume();
                    sb.Append('.');
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
        
        var label = sb.ToString();
        
        // Allocate or reuse blank node identifier
        if (!_blankNodes.TryGetValue(label, out var blankNodeId))
        {
            blankNodeId = $"_:b{_blankNodeCounter++}";
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
}
