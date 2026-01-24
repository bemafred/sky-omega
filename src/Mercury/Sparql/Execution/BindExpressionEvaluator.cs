using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using SkyOmega.Mercury.Sparql;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <remarks>
/// <para><strong>INTERNAL USE ONLY:</strong> This struct is internal because it is an
/// implementation detail of BIND expression evaluation in query execution.</para>
/// </remarks>
internal ref struct BindExpressionEvaluator
{
    private ReadOnlySpan<char> _expression;
    private int _position;
    private ReadOnlySpan<Binding> _bindingData;
    private ReadOnlySpan<char> _bindingStrings;
    private int _bindingCount;

    public BindExpressionEvaluator(ReadOnlySpan<char> expression,
        ReadOnlySpan<Binding> bindings, int bindingCount, ReadOnlySpan<char> stringBuffer)
    {
        _expression = expression;
        _position = 0;
        _bindingData = bindings;
        _bindingCount = bindingCount;
        _bindingStrings = stringBuffer;
    }

    /// <summary>
    /// Evaluate the expression and return a Value.
    /// </summary>
    public Value Evaluate()
    {
        _position = 0;
        return ParseAdditive();
    }

    /// <summary>
    /// Additive := Multiplicative (('+' | '-') Multiplicative)*
    /// </summary>
    private Value ParseAdditive()
    {
        var left = ParseMultiplicative();

        while (true)
        {
            SkipWhitespace();
            if (IsAtEnd()) break;

            var ch = Peek();
            if (ch == '+')
            {
                Advance();
                var right = ParseMultiplicative();
                left = Add(left, right);
            }
            else if (ch == '-')
            {
                Advance();
                var right = ParseMultiplicative();
                left = Subtract(left, right);
            }
            else
            {
                break;
            }
        }

        return left;
    }

    /// <summary>
    /// Multiplicative := Unary (('*' | '/') Unary)*
    /// </summary>
    private Value ParseMultiplicative()
    {
        var left = ParseUnary();

        while (true)
        {
            SkipWhitespace();
            if (IsAtEnd()) break;

            var ch = Peek();
            if (ch == '*')
            {
                Advance();
                var right = ParseUnary();
                left = Multiply(left, right);
            }
            else if (ch == '/')
            {
                Advance();
                var right = ParseUnary();
                left = Divide(left, right);
            }
            else
            {
                break;
            }
        }

        return left;
    }

    /// <summary>
    /// Unary := '-'? Primary
    /// </summary>
    private Value ParseUnary()
    {
        SkipWhitespace();

        if (Peek() == '-')
        {
            Advance();
            var val = ParsePrimary();
            return Negate(val);
        }

        return ParsePrimary();
    }

    /// <summary>
    /// Primary := '(' Additive ')' | Variable | Literal | FunctionCall
    /// </summary>
    private Value ParsePrimary()
    {
        SkipWhitespace();

        var ch = Peek();

        // Parenthesized expression
        if (ch == '(')
        {
            Advance();
            var result = ParseAdditive();
            SkipWhitespace();
            if (Peek() == ')') Advance();
            return result;
        }

        // Variable
        if (ch == '?')
        {
            return ParseVariable();
        }

        // String literal
        if (ch == '"')
        {
            return ParseStringLiteral();
        }

        // Numeric literal
        if (IsDigit(ch))
        {
            return ParseNumericLiteral();
        }

        // Function call or boolean literal
        if (IsLetter(ch))
        {
            return ParseFunctionOrLiteral();
        }

        return new Value { Type = ValueType.Unbound };
    }

    private Value ParseVariable()
    {
        var start = _position;
        Advance(); // Skip '?'

        while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
            Advance();

        var varName = _expression.Slice(start, _position - start);
        var index = FindBinding(varName);

        if (index < 0)
            return new Value { Type = ValueType.Unbound };

        ref readonly var binding = ref _bindingData[index];
        return binding.Type switch
        {
            BindingValueType.Integer => new Value { Type = ValueType.Integer, IntegerValue = binding.IntegerValue },
            BindingValueType.Double => new Value { Type = ValueType.Double, DoubleValue = binding.DoubleValue },
            BindingValueType.Boolean => new Value { Type = ValueType.Boolean, BooleanValue = binding.BooleanValue },
            BindingValueType.String => ParseTypedLiteralString(_bindingStrings.Slice(binding.StringOffset, binding.StringLength)),
            BindingValueType.Uri => new Value { Type = ValueType.Uri, StringValue = _bindingStrings.Slice(binding.StringOffset, binding.StringLength) },
            _ => new Value { Type = ValueType.Unbound }
        };
    }

    /// <summary>
    /// Parse a string binding that may be a typed RDF literal.
    /// Delegates to Value.ParseFromBinding for shared implementation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Value ParseTypedLiteralString(ReadOnlySpan<char> str) => Value.ParseFromBinding(str);

    private int FindBinding(ReadOnlySpan<char> variableName)
    {
        var hash = ComputeVariableHash(variableName);
        for (int i = 0; i < _bindingCount; i++)
        {
            if (_bindingData[i].VariableNameHash == hash)
                return i;
        }
        return -1;
    }

    private static int ComputeVariableHash(ReadOnlySpan<char> value)
    {
        uint hash = 2166136261;
        foreach (var ch in value)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return (int)hash;
    }

    private Value ParseStringLiteral()
    {
        Advance(); // Skip '"'
        var start = _position;

        while (!IsAtEnd() && Peek() != '"')
        {
            if (Peek() == '\\') Advance();
            Advance();
        }

        // Capture string content before moving past closing quote
        var str = _expression.Slice(start, _position - start);

        if (!IsAtEnd()) Advance(); // Skip closing '"'

        // Handle optional language tag @lang or datatype ^^type
        // We must skip these to avoid hanging in the parent parsing loop
        if (!IsAtEnd())
        {
            if (Peek() == '@')
            {
                // Skip language tag: @en, @en-US, etc.
                Advance(); // Skip '@'
                while (!IsAtEnd() && (IsLetter(Peek()) || Peek() == '-'))
                    Advance();
            }
            else if (Peek() == '^' && _position + 1 < _expression.Length &&
                     _expression[_position + 1] == '^')
            {
                // Skip datatype: ^^<IRI> or ^^prefix:local
                Advance(); // Skip first '^'
                Advance(); // Skip second '^'

                if (!IsAtEnd() && Peek() == '<')
                {
                    // Full IRI: <http://...>
                    Advance(); // Skip '<'
                    while (!IsAtEnd() && Peek() != '>')
                        Advance();
                    if (!IsAtEnd()) Advance(); // Skip '>'
                }
                else
                {
                    // Prefixed name: prefix:local
                    while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                        Advance();
                    if (!IsAtEnd() && Peek() == ':')
                    {
                        Advance();
                        while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_' || Peek() == '-'))
                            Advance();
                    }
                }
            }
        }

        // Return the string content (without quotes) as the value
        return new Value { Type = ValueType.String, StringValue = str };
    }

    private Value ParseNumericLiteral()
    {
        var start = _position;

        while (!IsAtEnd() && IsDigit(Peek()))
            Advance();

        if (!IsAtEnd() && Peek() == '.')
        {
            Advance();
            while (!IsAtEnd() && IsDigit(Peek()))
                Advance();

            var str = _expression.Slice(start, _position - start);
            if (double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return new Value { Type = ValueType.Double, DoubleValue = d };
        }
        else
        {
            var str = _expression.Slice(start, _position - start);
            if (long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                return new Value { Type = ValueType.Integer, IntegerValue = i };
        }

        return new Value { Type = ValueType.Unbound };
    }

    private Value ParseFunctionOrLiteral()
    {
        var start = _position;

        // Allow letters, digits, underscore, and colon (for prefixed functions like xsd:integer)
        while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_' || Peek() == ':'))
            Advance();

        var name = _expression.Slice(start, _position - start);

        // Boolean literals
        if (name.Equals("true", StringComparison.OrdinalIgnoreCase))
            return new Value { Type = ValueType.Boolean, BooleanValue = true };
        if (name.Equals("false", StringComparison.OrdinalIgnoreCase))
            return new Value { Type = ValueType.Boolean, BooleanValue = false };

        // Function call
        SkipWhitespace();
        if (Peek() == '(')
        {
            Advance();
            SkipWhitespace();

            // IF function - needs special handling for condition evaluation
            if (name.Equals("IF", StringComparison.OrdinalIgnoreCase))
            {
                return ParseIfFunction();
            }

            // COALESCE function - variable arguments
            if (name.Equals("COALESCE", StringComparison.OrdinalIgnoreCase))
            {
                return ParseCoalesceFunction();
            }

            // STRBEFORE(string, delimiter) - returns substring before first occurrence
            if (name.Equals("STRBEFORE", StringComparison.OrdinalIgnoreCase))
            {
                return ParseStrBeforeFunction();
            }

            // STRAFTER(string, delimiter) - returns substring after first occurrence
            if (name.Equals("STRAFTER", StringComparison.OrdinalIgnoreCase))
            {
                return ParseStrAfterFunction();
            }

            // REPLACE(string, pattern, replacement [, flags]) - regex replacement
            if (name.Equals("REPLACE", StringComparison.OrdinalIgnoreCase))
            {
                return ParseReplaceFunction();
            }

            // CONCAT(string1, string2, ...) - string concatenation
            if (name.Equals("CONCAT", StringComparison.OrdinalIgnoreCase))
            {
                return ParseConcatFunction();
            }

            // SUBSTR(string, start [, length]) - substring extraction
            if (name.Equals("SUBSTR", StringComparison.OrdinalIgnoreCase))
            {
                return ParseSubstrFunction();
            }

            // STRDT(lexicalForm, datatypeIRI) - construct typed literal
            if (name.Equals("STRDT", StringComparison.OrdinalIgnoreCase))
            {
                return ParseStrDtFunction();
            }

            // STRLANG(lexicalForm, langTag) - construct language-tagged literal
            if (name.Equals("STRLANG", StringComparison.OrdinalIgnoreCase))
            {
                return ParseStrLangFunction();
            }

            // UUID() - generate a fresh IRI with UUID v7
            if (name.Equals("UUID", StringComparison.OrdinalIgnoreCase))
            {
                if (Peek() == ')') Advance();
                _stringResult = $"<urn:uuid:{Guid.CreateVersion7():D}>";
                return new Value { Type = ValueType.Uri, StringValue = _stringResult.AsSpan() };
            }

            // STRUUID() - generate a fresh UUID v7 string (without urn:uuid: prefix)
            if (name.Equals("STRUUID", StringComparison.OrdinalIgnoreCase))
            {
                if (Peek() == ')') Advance();
                _stringResult = $"\"{Guid.CreateVersion7():D}\"";
                return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
            }

            // NOW() - current datetime
            if (name.Equals("NOW", StringComparison.OrdinalIgnoreCase))
            {
                if (Peek() == ')') Advance();
                _stringResult = $"\"{DateTime.UtcNow:O}\"^^<http://www.w3.org/2001/XMLSchema#dateTime>";
                return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
            }

            // RAND() - random number between 0 and 1
            if (name.Equals("RAND", StringComparison.OrdinalIgnoreCase))
            {
                if (Peek() == ')') Advance();
                return new Value { Type = ValueType.Double, DoubleValue = Random.Shared.NextDouble() };
            }

            var arg = ParseAdditive();
            SkipWhitespace();

            // Check for more arguments (for multi-arg functions)
            if (Peek() == ',')
            {
                Advance();
                SkipWhitespace();
                var arg2 = ParseAdditive();
                SkipWhitespace();
            }

            if (Peek() == ')') Advance();

            // BOUND function
            if (name.Equals("BOUND", StringComparison.OrdinalIgnoreCase))
                return new Value { Type = ValueType.Boolean, BooleanValue = arg.Type != ValueType.Unbound };

            // STR function - converts value to string representation (lexical form)
            if (name.Equals("STR", StringComparison.OrdinalIgnoreCase))
            {
                // Handle numeric types by converting to string representation
                if (arg.Type == ValueType.Integer)
                {
                    _stringResult = arg.IntegerValue.ToString(CultureInfo.InvariantCulture);
                    return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
                }
                if (arg.Type == ValueType.Double)
                {
                    _stringResult = arg.DoubleValue.ToString("G", CultureInfo.InvariantCulture);
                    return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
                }
                if (arg.Type == ValueType.Boolean)
                {
                    _stringResult = arg.BooleanValue ? "true" : "false";
                    return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
                }

                // For URIs, strip angle brackets: <http://...> -> http://...
                if (arg.Type == ValueType.Uri)
                {
                    var uriVal = arg.StringValue;
                    if (uriVal.Length >= 2 && uriVal[0] == '<' && uriVal[^1] == '>')
                    {
                        _stringResult = uriVal.Slice(1, uriVal.Length - 2).ToString();
                        return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
                    }
                    return new Value { Type = ValueType.String, StringValue = uriVal };
                }

                // For strings, extract lexical form (strip quotes and language tag/datatype)
                _stringResult = arg.GetLexicalForm().ToString();
                return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
            }

            // STRLEN function - length in Unicode code points (not UTF-16 code units)
            // Characters outside BMP (like emoji) count as 1, not 2
            if (name.Equals("STRLEN", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Type == ValueType.String)
                    return new Value { Type = ValueType.Integer, IntegerValue = UnicodeHelper.GetCodePointCount(arg.GetLexicalForm()) };
                return new Value { Type = ValueType.Unbound };
            }

            // ABS - absolute value
            if (name.Equals("ABS", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Type == ValueType.Integer)
                    return new Value { Type = ValueType.Integer, IntegerValue = Math.Abs(arg.IntegerValue) };
                if (arg.Type == ValueType.Double)
                    return new Value { Type = ValueType.Double, DoubleValue = Math.Abs(arg.DoubleValue) };
                return new Value { Type = ValueType.Unbound };
            }

            // CEIL - round up to nearest integer, preserving datatype
            if (name.Equals("CEIL", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Type == ValueType.Integer)
                    return arg; // Already an integer
                if (arg.Type == ValueType.Double)
                    return new Value { Type = ValueType.Double, DoubleValue = Math.Ceiling(arg.DoubleValue) };
                return new Value { Type = ValueType.Unbound };
            }

            // FLOOR - round down to nearest integer, preserving datatype
            if (name.Equals("FLOOR", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Type == ValueType.Integer)
                    return arg; // Already an integer
                if (arg.Type == ValueType.Double)
                    return new Value { Type = ValueType.Double, DoubleValue = Math.Floor(arg.DoubleValue) };
                return new Value { Type = ValueType.Unbound };
            }

            // ROUND - round to nearest integer, preserving datatype
            // SPARQL uses round-half-to-even (banker's rounding) for .5 cases
            if (name.Equals("ROUND", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Type == ValueType.Integer)
                    return arg; // Already an integer
                if (arg.Type == ValueType.Double)
                    return new Value { Type = ValueType.Double, DoubleValue = Math.Round(arg.DoubleValue, MidpointRounding.AwayFromZero) };
                return new Value { Type = ValueType.Unbound };
            }

            // UCASE function - convert to uppercase, preserving language tag/datatype
            if (name.Equals("UCASE", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Type == ValueType.String)
                {
                    var lexical = arg.GetLexicalForm().ToString().ToUpperInvariant();
                    var suffix = arg.GetLangTagOrDatatype();
                    // Only add quotes if there's a suffix to preserve, otherwise return plain string
                    var result = suffix.IsEmpty ? lexical : $"\"{lexical}\"{suffix.ToString()}";
                    return new Value { Type = ValueType.String, StringValue = result.AsSpan() };
                }
                return arg;
            }

            // LCASE function - convert to lowercase, preserving language tag/datatype
            if (name.Equals("LCASE", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Type == ValueType.String)
                {
                    var lexical = arg.GetLexicalForm().ToString().ToLowerInvariant();
                    var suffix = arg.GetLangTagOrDatatype();
                    // Only add quotes if there's a suffix to preserve, otherwise return plain string
                    var result = suffix.IsEmpty ? lexical : $"\"{lexical}\"{suffix.ToString()}";
                    return new Value { Type = ValueType.String, StringValue = result.AsSpan() };
                }
                return arg;
            }

            // ENCODE_FOR_URI - percent-encode string for use in URI (of lexical form)
            if (name.Equals("ENCODE_FOR_URI", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Type == ValueType.String || arg.Type == ValueType.Uri)
                {
                    var encoded = Uri.EscapeDataString(arg.GetLexicalForm().ToString());
                    return new Value { Type = ValueType.String, StringValue = encoded.AsSpan() };
                }
                return new Value { Type = ValueType.Unbound };
            }

            // Hash functions - compute hash of lexical form
            if (name.Equals("MD5", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Type == ValueType.String || arg.Type == ValueType.Uri)
                {
                    var bytes = Encoding.UTF8.GetBytes(arg.GetLexicalForm().ToString());
                    var hash = System.Security.Cryptography.MD5.HashData(bytes);
                    var result = Convert.ToHexString(hash).ToLowerInvariant();
                    return new Value { Type = ValueType.String, StringValue = result.AsSpan() };
                }
                return new Value { Type = ValueType.Unbound };
            }

            if (name.Equals("SHA1", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Type == ValueType.String || arg.Type == ValueType.Uri)
                {
                    var bytes = Encoding.UTF8.GetBytes(arg.GetLexicalForm().ToString());
                    var hash = System.Security.Cryptography.SHA1.HashData(bytes);
                    var result = Convert.ToHexString(hash).ToLowerInvariant();
                    return new Value { Type = ValueType.String, StringValue = result.AsSpan() };
                }
                return new Value { Type = ValueType.Unbound };
            }

            if (name.Equals("SHA256", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Type == ValueType.String || arg.Type == ValueType.Uri)
                {
                    var bytes = Encoding.UTF8.GetBytes(arg.GetLexicalForm().ToString());
                    var hash = System.Security.Cryptography.SHA256.HashData(bytes);
                    var result = Convert.ToHexString(hash).ToLowerInvariant();
                    return new Value { Type = ValueType.String, StringValue = result.AsSpan() };
                }
                return new Value { Type = ValueType.Unbound };
            }

            if (name.Equals("SHA384", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Type == ValueType.String || arg.Type == ValueType.Uri)
                {
                    var bytes = Encoding.UTF8.GetBytes(arg.GetLexicalForm().ToString());
                    var hash = System.Security.Cryptography.SHA384.HashData(bytes);
                    var result = Convert.ToHexString(hash).ToLowerInvariant();
                    return new Value { Type = ValueType.String, StringValue = result.AsSpan() };
                }
                return new Value { Type = ValueType.Unbound };
            }

            if (name.Equals("SHA512", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Type == ValueType.String || arg.Type == ValueType.Uri)
                {
                    var bytes = Encoding.UTF8.GetBytes(arg.GetLexicalForm().ToString());
                    var hash = System.Security.Cryptography.SHA512.HashData(bytes);
                    var result = Convert.ToHexString(hash).ToLowerInvariant();
                    return new Value { Type = ValueType.String, StringValue = result.AsSpan() };
                }
                return new Value { Type = ValueType.Unbound };
            }

            // DateTime extraction functions - extract components directly from lexical value per SPARQL spec
            // YEAR - extract year from xsd:dateTime
            if (name.Equals("YEAR", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Type == ValueType.String && TryParseDateTime(arg.StringValue, out var year, out _, out _, out _, out _, out _))
                    return new Value { Type = ValueType.Integer, IntegerValue = year };
                return new Value { Type = ValueType.Unbound };
            }

            // MONTH - extract month from xsd:dateTime (1-12)
            if (name.Equals("MONTH", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Type == ValueType.String && TryParseDateTime(arg.StringValue, out _, out var month, out _, out _, out _, out _))
                    return new Value { Type = ValueType.Integer, IntegerValue = month };
                return new Value { Type = ValueType.Unbound };
            }

            // DAY - extract day from xsd:dateTime (1-31)
            if (name.Equals("DAY", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Type == ValueType.String && TryParseDateTime(arg.StringValue, out _, out _, out var day, out _, out _, out _))
                    return new Value { Type = ValueType.Integer, IntegerValue = day };
                return new Value { Type = ValueType.Unbound };
            }

            // HOURS - extract hours from xsd:dateTime (0-23)
            if (name.Equals("HOURS", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Type == ValueType.String && TryParseDateTime(arg.StringValue, out _, out _, out _, out var hour, out _, out _))
                    return new Value { Type = ValueType.Integer, IntegerValue = hour };
                return new Value { Type = ValueType.Unbound };
            }

            // MINUTES - extract minutes from xsd:dateTime (0-59)
            if (name.Equals("MINUTES", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Type == ValueType.String && TryParseDateTime(arg.StringValue, out _, out _, out _, out _, out var minute, out _))
                    return new Value { Type = ValueType.Integer, IntegerValue = minute };
                return new Value { Type = ValueType.Unbound };
            }

            // SECONDS - extract seconds from xsd:dateTime (0-59, with fractional part)
            if (name.Equals("SECONDS", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Type == ValueType.String && TryParseDateTime(arg.StringValue, out _, out _, out _, out _, out _, out var seconds))
                    return new Value { Type = ValueType.Double, DoubleValue = seconds };
                return new Value { Type = ValueType.Unbound };
            }

            // XSD type cast functions
            // xsd:integer - cast to integer
            if (name.Equals("xsd:integer", StringComparison.OrdinalIgnoreCase))
            {
                return CastToInteger(arg);
            }

            // xsd:decimal - cast to decimal (represented as double internally)
            if (name.Equals("xsd:decimal", StringComparison.OrdinalIgnoreCase))
            {
                return CastToDecimal(arg);
            }

            // xsd:double - cast to double
            if (name.Equals("xsd:double", StringComparison.OrdinalIgnoreCase))
            {
                return CastToDouble(arg);
            }

            // xsd:float - cast to float (same as double in SPARQL)
            if (name.Equals("xsd:float", StringComparison.OrdinalIgnoreCase))
            {
                return CastToDouble(arg); // Float is same as double internally
            }

            // xsd:boolean - cast to boolean
            if (name.Equals("xsd:boolean", StringComparison.OrdinalIgnoreCase))
            {
                return CastToBoolean(arg);
            }

            // xsd:string - cast to string
            if (name.Equals("xsd:string", StringComparison.OrdinalIgnoreCase))
            {
                return CastToString(arg);
            }
        }

        // Handle prefixed names as IRIs when not used as function calls
        // xsd: prefix -> expand to full XSD namespace
        if (name.StartsWith("xsd:", StringComparison.OrdinalIgnoreCase))
        {
            var localName = name.Slice(4);
            _stringResult = $"http://www.w3.org/2001/XMLSchema#{localName.ToString()}";
            return new Value { Type = ValueType.Uri, StringValue = _stringResult.AsSpan() };
        }

        // rdf: prefix -> expand to full RDF namespace
        if (name.StartsWith("rdf:", StringComparison.OrdinalIgnoreCase))
        {
            var localName = name.Slice(4);
            _stringResult = $"http://www.w3.org/1999/02/22-rdf-syntax-ns#{localName.ToString()}";
            return new Value { Type = ValueType.Uri, StringValue = _stringResult.AsSpan() };
        }

        // rdfs: prefix -> expand to full RDFS namespace
        if (name.StartsWith("rdfs:", StringComparison.OrdinalIgnoreCase))
        {
            var localName = name.Slice(5);
            _stringResult = $"http://www.w3.org/2000/01/rdf-schema#{localName.ToString()}";
            return new Value { Type = ValueType.Uri, StringValue = _stringResult.AsSpan() };
        }

        return new Value { Type = ValueType.Unbound };
    }

    /// <summary>
    /// Cast a value to xsd:integer.
    /// Supports: booleans, typed numerics (integer/decimal/double/float), and strings that are valid integer literals.
    /// Plain strings can only cast if they match integer lexical form (digits with optional sign).
    /// </summary>
    private static Value CastToInteger(Value arg)
    {
        // Boolean to integer: true=1, false=0
        if (arg.Type == ValueType.Boolean)
        {
            return new Value { Type = ValueType.Integer, IntegerValue = arg.BooleanValue ? 1 : 0 };
        }

        // Already integer
        if (arg.Type == ValueType.Integer)
        {
            return arg;
        }

        // Double to integer (truncate) - this handles typed decimals/doubles/floats
        if (arg.Type == ValueType.Double)
        {
            if (double.IsNaN(arg.DoubleValue) || double.IsInfinity(arg.DoubleValue))
                return new Value { Type = ValueType.Unbound };
            return new Value { Type = ValueType.Integer, IntegerValue = (long)arg.DoubleValue };
        }

        // String to integer - ONLY valid integer lexical representations (no decimal point or scientific notation)
        if (arg.Type == ValueType.String)
        {
            var str = ExtractLexicalValue(arg.StringValue);

            // For plain strings, only accept valid integer lexical form (digits with optional leading +/-)
            // Must NOT contain decimal point (.), 'E', 'e' (scientific notation)
            if (str.IndexOfAny(['.', 'E', 'e']) >= 0)
            {
                // Contains decimal point or scientific notation - not a valid integer string
                return new Value { Type = ValueType.Unbound };
            }

            if (long.TryParse(str, System.Globalization.NumberStyles.Integer | System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var intVal))
            {
                return new Value { Type = ValueType.Integer, IntegerValue = intVal };
            }
        }

        return new Value { Type = ValueType.Unbound };
    }

    /// <summary>
    /// Cast a value to xsd:decimal.
    /// </summary>
    private static Value CastToDecimal(Value arg)
    {
        // Boolean to decimal: true=1.0, false=0.0
        if (arg.Type == ValueType.Boolean)
        {
            return new Value { Type = ValueType.Double, DoubleValue = arg.BooleanValue ? 1.0 : 0.0 };
        }

        // Integer to decimal
        if (arg.Type == ValueType.Integer)
        {
            return new Value { Type = ValueType.Double, DoubleValue = arg.IntegerValue };
        }

        // Already double (treat as decimal)
        if (arg.Type == ValueType.Double)
        {
            if (double.IsNaN(arg.DoubleValue) || double.IsInfinity(arg.DoubleValue))
                return new Value { Type = ValueType.Unbound };
            return arg;
        }

        // String to decimal
        if (arg.Type == ValueType.String)
        {
            var str = ExtractLexicalValue(arg.StringValue);
            if (double.TryParse(str, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dblVal))
            {
                if (!double.IsNaN(dblVal) && !double.IsInfinity(dblVal))
                    return new Value { Type = ValueType.Double, DoubleValue = dblVal };
            }
        }

        return new Value { Type = ValueType.Unbound };
    }

    /// <summary>
    /// Cast a value to xsd:double.
    /// </summary>
    private static Value CastToDouble(Value arg)
    {
        // Boolean to double: true=1.0E0, false=0.0E0
        if (arg.Type == ValueType.Boolean)
        {
            return new Value { Type = ValueType.Double, DoubleValue = arg.BooleanValue ? 1.0 : 0.0 };
        }

        // Integer to double
        if (arg.Type == ValueType.Integer)
        {
            return new Value { Type = ValueType.Double, DoubleValue = arg.IntegerValue };
        }

        // Already double
        if (arg.Type == ValueType.Double)
        {
            return arg;
        }

        // String to double
        if (arg.Type == ValueType.String)
        {
            var str = ExtractLexicalValue(arg.StringValue);
            // Handle special cases: INF, -INF, NaN
            if (str.Equals("INF", StringComparison.OrdinalIgnoreCase))
                return new Value { Type = ValueType.Double, DoubleValue = double.PositiveInfinity };
            if (str.Equals("-INF", StringComparison.OrdinalIgnoreCase))
                return new Value { Type = ValueType.Double, DoubleValue = double.NegativeInfinity };
            if (str.Equals("NaN", StringComparison.OrdinalIgnoreCase))
                return new Value { Type = ValueType.Double, DoubleValue = double.NaN };

            if (double.TryParse(str, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dblVal))
            {
                return new Value { Type = ValueType.Double, DoubleValue = dblVal };
            }
        }

        return new Value { Type = ValueType.Unbound };
    }

    /// <summary>
    /// Cast a value to xsd:boolean.
    /// </summary>
    private static Value CastToBoolean(Value arg)
    {
        // Already boolean
        if (arg.Type == ValueType.Boolean)
        {
            return arg;
        }

        // Integer to boolean: 0=false, non-zero=true
        if (arg.Type == ValueType.Integer)
        {
            return new Value { Type = ValueType.Boolean, BooleanValue = arg.IntegerValue != 0 };
        }

        // Double to boolean: 0.0=false, NaN=false, non-zero=true
        if (arg.Type == ValueType.Double)
        {
            return new Value { Type = ValueType.Boolean, BooleanValue = arg.DoubleValue != 0.0 && !double.IsNaN(arg.DoubleValue) };
        }

        // String to boolean: "true"/"1" = true, "false"/"0" = false
        if (arg.Type == ValueType.String)
        {
            var str = ExtractLexicalValue(arg.StringValue);
            if (str.Equals("true", StringComparison.OrdinalIgnoreCase) || str.Equals("1", StringComparison.Ordinal))
                return new Value { Type = ValueType.Boolean, BooleanValue = true };
            if (str.Equals("false", StringComparison.OrdinalIgnoreCase) || str.Equals("0", StringComparison.Ordinal))
                return new Value { Type = ValueType.Boolean, BooleanValue = false };
        }

        return new Value { Type = ValueType.Unbound };
    }

    /// <summary>
    /// Cast a value to xsd:string.
    /// </summary>
    private Value CastToString(Value arg)
    {
        switch (arg.Type)
        {
            case ValueType.Boolean:
                return new Value { Type = ValueType.String, StringValue = (arg.BooleanValue ? "true" : "false").AsSpan() };

            case ValueType.Integer:
                _stringResult = arg.IntegerValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };

            case ValueType.Double:
                if (double.IsNaN(arg.DoubleValue))
                    return new Value { Type = ValueType.String, StringValue = "NaN".AsSpan() };
                if (double.IsPositiveInfinity(arg.DoubleValue))
                    return new Value { Type = ValueType.String, StringValue = "INF".AsSpan() };
                if (double.IsNegativeInfinity(arg.DoubleValue))
                    return new Value { Type = ValueType.String, StringValue = "-INF".AsSpan() };
                _stringResult = arg.DoubleValue.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
                return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };

            case ValueType.String:
                // Extract just the value part (strip datatype/language tag)
                var val = ExtractLexicalValue(arg.StringValue);
                _stringResult = val.ToString();
                return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };

            case ValueType.Uri:
                // Strip angle brackets if present to get just the URI string
                var uriStr = arg.StringValue;
                if (uriStr.Length >= 2 && uriStr[0] == '<' && uriStr[^1] == '>')
                    uriStr = uriStr[1..^1];
                _stringResult = uriStr.ToString();
                return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };

            default:
                return new Value { Type = ValueType.Unbound };
        }
    }

    /// <summary>
    /// Try to parse an xsd:dateTime string and extract time components.
    /// Supports formats: yyyy-MM-ddTHH:mm:ss, yyyy-MM-ddTHH:mm:ss.fff, with optional timezone.
    /// Also handles RDF typed literals like "2010-06-21T11:28:01Z"^^xsd:dateTime.
    /// Extracts components directly from lexical value per SPARQL spec (no timezone conversion).
    /// </summary>
    private static bool TryParseDateTime(ReadOnlySpan<char> str, out int year, out int month, out int day,
        out int hour, out int minute, out double second)
    {
        year = month = day = hour = minute = 0;
        second = 0.0;
        if (str.IsEmpty)
            return false;

        // Handle RDF typed literals: extract lexical value from quotes
        var parseSpan = str;
        if (str.Length > 2 && str[0] == '"')
        {
            // Find the closing quote
            var endQuote = str.Slice(1).IndexOf('"');
            if (endQuote > 0)
            {
                parseSpan = str.Slice(1, endQuote);
            }
        }

        // Parse ISO 8601 format: yyyy-MM-ddTHH:mm:ss[.fff][Z|+HH:MM|-HH:MM]
        // Minimum valid: yyyy-MM-ddTHH:mm:ss (19 chars)
        if (parseSpan.Length < 19)
            return false;

        // Extract components directly from string
        // Format: 0123456789012345678
        //         yyyy-MM-ddTHH:mm:ss
        if (parseSpan[4] != '-' || parseSpan[7] != '-' || parseSpan[10] != 'T' ||
            parseSpan[13] != ':' || parseSpan[16] != ':')
            return false;

        if (!int.TryParse(parseSpan.Slice(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out year))
            return false;
        if (!int.TryParse(parseSpan.Slice(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out month))
            return false;
        if (!int.TryParse(parseSpan.Slice(8, 2), NumberStyles.None, CultureInfo.InvariantCulture, out day))
            return false;
        if (!int.TryParse(parseSpan.Slice(11, 2), NumberStyles.None, CultureInfo.InvariantCulture, out hour))
            return false;
        if (!int.TryParse(parseSpan.Slice(14, 2), NumberStyles.None, CultureInfo.InvariantCulture, out minute))
            return false;

        // Parse seconds (may include fractional part)
        var secondsStart = 17;
        var secondsEnd = 19;
        for (int i = 19; i < parseSpan.Length; i++)
        {
            var c = parseSpan[i];
            if (c == '.' || IsDigit(c))
                secondsEnd = i + 1;
            else
                break;
        }
        if (!double.TryParse(parseSpan.Slice(secondsStart, secondsEnd - secondsStart),
            NumberStyles.Float, CultureInfo.InvariantCulture, out second))
            return false;

        return true;
    }

    /// <summary>
    /// Parse IF(condition, thenExpr, elseExpr)
    /// </summary>
    private Value ParseIfFunction()
    {
        // Parse condition - need to find comma while respecting parentheses
        var conditionStart = _position;
        int depth = 0;

        while (!IsAtEnd())
        {
            var ch = Peek();
            if (ch == '(') depth++;
            else if (ch == ')') depth--;
            else if (ch == ',' && depth == 0) break;
            Advance();
        }

        var conditionExpr = _expression.Slice(conditionStart, _position - conditionStart);

        // Evaluate condition using FilterEvaluator
        var condEvaluator = new FilterEvaluator(conditionExpr);
        var conditionResult = condEvaluator.Evaluate(_bindingData, _bindingCount, _bindingStrings);

        SkipWhitespace();
        if (Peek() == ',') Advance();
        SkipWhitespace();

        // Parse then expression
        var thenValue = ParseAdditive();

        SkipWhitespace();
        if (Peek() == ',') Advance();
        SkipWhitespace();

        // Parse else expression
        var elseValue = ParseAdditive();

        SkipWhitespace();
        if (Peek() == ')') Advance();

        return conditionResult ? thenValue : elseValue;
    }

    /// <summary>
    /// Parse COALESCE(expr1, expr2, ...) - returns first bound value
    /// </summary>
    private Value ParseCoalesceFunction()
    {
        Value result = new Value { Type = ValueType.Unbound };

        while (!IsAtEnd() && Peek() != ')')
        {
            var value = ParseAdditive();

            if (result.Type == ValueType.Unbound && value.Type != ValueType.Unbound)
            {
                result = value;
            }

            SkipWhitespace();
            if (Peek() == ',')
            {
                Advance();
                SkipWhitespace();
            }
        }

        if (Peek() == ')') Advance();

        return result;
    }

    /// <summary>
    /// Parse CONCAT(string1, string2, ...) - concatenates all string arguments
    /// Per SPARQL spec: if any argument is unbound, result is unbound
    /// </summary>
    private Value ParseConcatFunction()
    {
        // Collect all argument string values
        var parts = new List<string>();

        while (!IsAtEnd() && Peek() != ')')
        {
            var value = ParseAdditive();

            if (value.Type == ValueType.Unbound)
            {
                // Per SPARQL spec, unbound argument results in unbound
                // Skip to closing paren
                while (!IsAtEnd() && Peek() != ')')
                    Advance();
                if (Peek() == ')') Advance();
                return new Value { Type = ValueType.Unbound };
            }

            // Convert value to string (using lexical form for string literals)
            if (value.Type == ValueType.String)
            {
                parts.Add(value.GetLexicalForm().ToString());
            }
            else if (value.Type == ValueType.Integer)
            {
                parts.Add(value.IntegerValue.ToString(CultureInfo.InvariantCulture));
            }
            else if (value.Type == ValueType.Double)
            {
                parts.Add(value.DoubleValue.ToString(CultureInfo.InvariantCulture));
            }
            else if (value.Type == ValueType.Boolean)
            {
                parts.Add(value.BooleanValue ? "true" : "false");
            }
            else if (value.Type == ValueType.Uri)
            {
                parts.Add(value.GetLexicalForm().ToString());
            }

            SkipWhitespace();
            if (Peek() == ',')
            {
                Advance();
                SkipWhitespace();
            }
        }

        if (Peek() == ')') Advance();

        // Concatenate all parts - store in _stringResult to keep span valid
        _stringResult = string.Concat(parts);
        return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
    }

    /// <summary>
    /// Parse SUBSTR(string, start [, length]) - returns substring
    /// Note: SPARQL uses 1-based indexing in Unicode code points (not UTF-16 units).
    /// </summary>
    private Value ParseSubstrFunction()
    {
        var stringArg = ParseAdditive();
        SkipWhitespace();

        if (Peek() != ',')
        {
            // Skip to closing paren
            while (!IsAtEnd() && Peek() != ')')
                Advance();
            if (Peek() == ')') Advance();
            return new Value { Type = ValueType.Unbound };
        }

        Advance(); // Skip ','
        SkipWhitespace();

        var startArg = ParseAdditive();
        SkipWhitespace();

        // Optional length argument
        Value lengthArg = new Value { Type = ValueType.Unbound };
        if (Peek() == ',')
        {
            Advance();
            SkipWhitespace();
            lengthArg = ParseAdditive();
            SkipWhitespace();
        }

        if (Peek() == ')') Advance();

        if (stringArg.Type != ValueType.String && stringArg.Type != ValueType.Uri)
            return new Value { Type = ValueType.Unbound };

        if (startArg.Type != ValueType.Integer && startArg.Type != ValueType.Double)
            return new Value { Type = ValueType.Unbound };

        var str = stringArg.GetLexicalForm();
        var startVal = startArg.Type == ValueType.Integer ? startArg.IntegerValue : (long)startArg.DoubleValue;
        var startCodePoint = (int)startVal; // SPARQL is 1-based, keep as-is for helper

        // Get code point count for bounds checking
        var codePointCount = UnicodeHelper.GetCodePointCount(str);

        if (startCodePoint < 1) startCodePoint = 1;
        if (startCodePoint > codePointCount)
            return new Value { Type = ValueType.String, StringValue = ReadOnlySpan<char>.Empty };

        int lengthCodePoints;
        if (lengthArg.Type == ValueType.Integer)
        {
            lengthCodePoints = (int)lengthArg.IntegerValue;
            if (lengthCodePoints < 0) lengthCodePoints = 0;
        }
        else if (lengthArg.Type == ValueType.Double)
        {
            lengthCodePoints = (int)lengthArg.DoubleValue;
            if (lengthCodePoints < 0) lengthCodePoints = 0;
        }
        else
        {
            lengthCodePoints = -1; // Take remainder
        }

        // Use code point-based substring
        var result = UnicodeHelper.SubstringByCodePoints(str, startCodePoint, lengthCodePoints);

        // Preserve language tag/datatype from the first argument
        var suffix = stringArg.GetLangTagOrDatatype();
        // Only add quotes if there's a suffix to preserve, otherwise return plain string
        _stringResult = suffix.IsEmpty ? result : $"\"{result}\"{suffix.ToString()}";
        return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
    }

    /// <summary>
    /// Parse STRDT(lexicalForm, datatypeIRI) - constructs a typed literal
    /// Returns a literal with the specified datatype
    /// Per SPARQL spec: first argument must be a simple literal (no language tag, no datatype except xsd:string)
    /// Per RDF 1.1: xsd:string typed literals are equivalent to simple literals
    /// </summary>
    private Value ParseStrDtFunction()
    {
        var lexicalArg = ParseAdditive();
        SkipWhitespace();

        if (Peek() != ',')
        {
            while (!IsAtEnd() && Peek() != ')')
                Advance();
            if (Peek() == ')') Advance();
            return new Value { Type = ValueType.Unbound };
        }

        Advance(); // Skip ','
        SkipWhitespace();

        var datatypeArg = ParseAdditive();
        SkipWhitespace();
        if (Peek() == ')') Advance();

        // Lexical form must be a simple literal (string without language tag)
        if (lexicalArg.Type != ValueType.String)
            return new Value { Type = ValueType.Unbound };

        // Check that the first argument is a simple literal (no language tag)
        var langTag = lexicalArg.GetLangTagOrDatatype();
        if (!langTag.IsEmpty && langTag[0] == '@')
            return new Value { Type = ValueType.Unbound }; // Language-tagged literal not allowed

        // Datatype must be an IRI
        if (datatypeArg.Type != ValueType.Uri && datatypeArg.Type != ValueType.String)
            return new Value { Type = ValueType.Unbound };

        var lexical = lexicalArg.GetLexicalForm();
        var datatype = datatypeArg.GetLexicalForm();

        // Per RDF 1.1: xsd:string typed literals are simple literals (no datatype suffix)
        const string xsdString = "http://www.w3.org/2001/XMLSchema#string";
        if (datatype.Equals(xsdString.AsSpan(), StringComparison.Ordinal))
        {
            _stringResult = lexical.ToString();
            return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
        }

        // Construct typed literal: "lexical"^^<datatype>
        _stringResult = $"\"{lexical}\"^^<{datatype}>";
        return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
    }

    /// <summary>
    /// Parse STRLANG(lexicalForm, langTag) - constructs a language-tagged literal
    /// Returns a literal with the specified language tag
    /// Per SPARQL spec: first argument must be a simple literal (no language tag, no datatype except xsd:string)
    /// </summary>
    private Value ParseStrLangFunction()
    {
        var lexicalArg = ParseAdditive();
        SkipWhitespace();

        if (Peek() != ',')
        {
            while (!IsAtEnd() && Peek() != ')')
                Advance();
            if (Peek() == ')') Advance();
            return new Value { Type = ValueType.Unbound };
        }

        Advance(); // Skip ','
        SkipWhitespace();

        var langArg = ParseAdditive();
        SkipWhitespace();
        if (Peek() == ')') Advance();

        // Lexical form must be a simple literal (string without language tag)
        if (lexicalArg.Type != ValueType.String)
            return new Value { Type = ValueType.Unbound };

        // Check that the first argument is a simple literal (no language tag)
        var existingLangTag = lexicalArg.GetLangTagOrDatatype();
        if (!existingLangTag.IsEmpty && existingLangTag[0] == '@')
            return new Value { Type = ValueType.Unbound }; // Language-tagged literal not allowed

        // Language tag must be a string
        if (langArg.Type != ValueType.String)
            return new Value { Type = ValueType.Unbound };

        var lexical = lexicalArg.GetLexicalForm();
        var lang = langArg.GetLexicalForm();

        // Language tag must not be empty
        if (lang.IsEmpty)
            return new Value { Type = ValueType.Unbound };

        // Construct language-tagged literal: "lexical"@lang
        _stringResult = $"\"{lexical}\"@{lang}";
        return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
    }

    /// <summary>
    /// Parse STRBEFORE(string, delimiter) - returns substring before first occurrence of delimiter
    /// Preserves language tag/datatype from first arg.
    /// </summary>
    private Value ParseStrBeforeFunction()
    {
        var stringArg = ParseAdditive();
        SkipWhitespace();

        if (Peek() != ',')
        {
            // Skip to closing paren
            while (!IsAtEnd() && Peek() != ')')
                Advance();
            if (Peek() == ')') Advance();
            _stringResult = "\"\"";  // Quoted empty string literal
            return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
        }

        Advance(); // Skip ','
        SkipWhitespace();

        var delimiterArg = ParseAdditive();
        SkipWhitespace();
        if (Peek() == ')') Advance();

        if (stringArg.Type != ValueType.String || delimiterArg.Type != ValueType.String)
        {
            _stringResult = "\"\"";  // Quoted empty string literal
            return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
        }

        var str = stringArg.GetLexicalForm();
        var delimiter = delimiterArg.GetLexicalForm();

        // Empty delimiter returns empty string literal
        if (delimiter.IsEmpty)
        {
            _stringResult = "\"\"";  // Quoted empty string literal
            return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
        }

        var index = str.IndexOf(delimiter);
        if (index < 0)
        {
            _stringResult = "\"\"";  // Quoted empty string literal
            return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
        }

        // Preserve language tag/datatype from the first argument
        var result = str.Slice(0, index).ToString();
        var suffix = stringArg.GetLangTagOrDatatype();
        // Only add quotes if there's a suffix to preserve, otherwise return plain string
        _stringResult = suffix.IsEmpty ? result : $"\"{result}\"{suffix.ToString()}";
        return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
    }

    /// <summary>
    /// Parse STRAFTER(string, delimiter) - returns substring after first occurrence of delimiter
    /// Preserves language tag/datatype from first arg.
    /// </summary>
    private Value ParseStrAfterFunction()
    {
        var stringArg = ParseAdditive();
        SkipWhitespace();

        if (Peek() != ',')
        {
            while (!IsAtEnd() && Peek() != ')')
                Advance();
            if (Peek() == ')') Advance();
            _stringResult = "\"\"";  // Quoted empty string literal
            return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
        }

        Advance(); // Skip ','
        SkipWhitespace();

        var delimiterArg = ParseAdditive();
        SkipWhitespace();
        if (Peek() == ')') Advance();

        if (stringArg.Type != ValueType.String || delimiterArg.Type != ValueType.String)
        {
            _stringResult = "\"\"";  // Quoted empty string literal
            return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
        }

        var str = stringArg.GetLexicalForm();
        var delimiter = delimiterArg.GetLexicalForm();
        var suffix = stringArg.GetLangTagOrDatatype();

        // Empty delimiter returns full string with preserved suffix (per SPARQL spec)
        if (delimiter.IsEmpty)
        {
            // Only add quotes if there's a suffix to preserve, otherwise return plain string
            _stringResult = suffix.IsEmpty ? str.ToString() : $"\"{str.ToString()}\"{suffix.ToString()}";
            return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
        }

        var index = str.IndexOf(delimiter);
        if (index < 0)
        {
            _stringResult = "\"\"";  // Quoted empty string literal
            return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
        }

        // Preserve language tag/datatype from the first argument
        var result = str.Slice(index + delimiter.Length).ToString();
        // Only add quotes if there's a suffix to preserve, otherwise return plain string
        _stringResult = suffix.IsEmpty ? result : $"\"{result}\"{suffix.ToString()}";
        return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
    }

    /// <summary>
    /// Parse REPLACE(string, pattern, replacement [, flags]) - regex replacement
    /// </summary>
    private Value ParseReplaceFunction()
    {
        var stringArg = ParseAdditive();
        SkipWhitespace();

        if (Peek() != ',')
        {
            while (!IsAtEnd() && Peek() != ')')
                Advance();
            if (Peek() == ')') Advance();
            return new Value { Type = ValueType.Unbound };
        }

        Advance(); // Skip ','
        SkipWhitespace();

        var patternArg = ParseAdditive();
        SkipWhitespace();

        if (Peek() != ',')
        {
            while (!IsAtEnd() && Peek() != ')')
                Advance();
            if (Peek() == ')') Advance();
            return new Value { Type = ValueType.Unbound };
        }

        Advance(); // Skip ','
        SkipWhitespace();

        var replacementArg = ParseAdditive();
        SkipWhitespace();

        // Optional flags argument
        ReadOnlySpan<char> flags = ReadOnlySpan<char>.Empty;
        if (Peek() == ',')
        {
            Advance(); // Skip ','
            SkipWhitespace();
            var flagsArg = ParseAdditive();
            if (flagsArg.Type == ValueType.String)
                flags = ExtractLexicalValue(flagsArg.StringValue);
        }

        SkipWhitespace();
        if (Peek() == ')') Advance();

        if (stringArg.Type != ValueType.String ||
            patternArg.Type != ValueType.String ||
            replacementArg.Type != ValueType.String)
            return new Value { Type = ValueType.Unbound };

        var str = ExtractLexicalValue(stringArg.StringValue);
        var pattern = ExtractLexicalValue(patternArg.StringValue);
        var replacement = ExtractLexicalValue(replacementArg.StringValue);

        // Build regex options from flags
        var options = RegexOptions.None;
        foreach (var flag in flags)
        {
            switch (flag)
            {
                case 'i': options |= RegexOptions.IgnoreCase; break;
                case 's': options |= RegexOptions.Singleline; break;
                case 'm': options |= RegexOptions.Multiline; break;
                case 'x': options |= RegexOptions.IgnorePatternWhitespace; break;
            }
        }

        // Perform regex replacement
        try
        {
            var regex = new Regex(pattern.ToString(), options, TimeSpan.FromMilliseconds(100));
            var result = regex.Replace(str.ToString(), replacement.ToString());

            // Preserve language tag or datatype from input string
            var suffix = stringArg.GetLangTagOrDatatype();
            _stringResult = suffix.IsEmpty ? $"\"{result}\"" : $"\"{result}\"{suffix.ToString()}";
            return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
        }
        catch
        {
            // Invalid regex pattern
            return new Value { Type = ValueType.Unbound };
        }
    }

    /// <summary>
    /// Extract the lexical value from an RDF literal.
    /// Handles both quoted literals ("value") and unquoted values.
    /// </summary>
    private static ReadOnlySpan<char> ExtractLexicalValue(ReadOnlySpan<char> str)
    {
        if (str.IsEmpty)
            return str;

        // Handle RDF typed literals: extract lexical value from quotes
        if (str.Length > 2 && str[0] == '"')
        {
            var endQuote = str.Slice(1).IndexOf('"');
            if (endQuote > 0)
                return str.Slice(1, endQuote);
        }

        return str;
    }

    // Field to hold string results to prevent GC of span backing memory
    private string _stringResult = "";

    /// <summary>
    /// Coerce a Value to a number. Strings are parsed as numbers.
    /// Returns NaN if coercion fails.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double CoerceToNumber(Value val)
    {
        return val.Type switch
        {
            ValueType.Integer => val.IntegerValue,
            ValueType.Double => val.DoubleValue,
            ValueType.String => TryParseNumber(val.StringValue),
            _ => double.NaN
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double TryParseNumber(ReadOnlySpan<char> s)
    {
        // Try parsing directly first (for plain numbers)
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            return result;

        // Handle typed literals like "0"^^<http://www.w3.org/2001/XMLSchema#integer>
        // Extract the lexical value between quotes
        if (s.Length > 2 && s[0] == '"')
        {
            var endQuote = s.Slice(1).IndexOf('"');
            if (endQuote > 0)
            {
                var lexicalValue = s.Slice(1, endQuote);
                if (double.TryParse(lexicalValue, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
                    return result;
            }
        }

        return double.NaN;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Value Add(Value left, Value right)
    {
        // Both integers - return integer
        if (left.Type == ValueType.Integer && right.Type == ValueType.Integer)
            return new Value { Type = ValueType.Integer, IntegerValue = left.IntegerValue + right.IntegerValue };

        // Coerce to numbers (handles strings)
        var l = CoerceToNumber(left);
        var r = CoerceToNumber(right);
        if (double.IsNaN(l) || double.IsNaN(r))
            return new Value { Type = ValueType.Unbound };

        // Check if result is integer
        var result = l + r;
        if (result == Math.Floor(result) && result >= long.MinValue && result <= long.MaxValue)
            return new Value { Type = ValueType.Integer, IntegerValue = (long)result };
        return new Value { Type = ValueType.Double, DoubleValue = result };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Value Subtract(Value left, Value right)
    {
        if (left.Type == ValueType.Integer && right.Type == ValueType.Integer)
            return new Value { Type = ValueType.Integer, IntegerValue = left.IntegerValue - right.IntegerValue };

        var l = CoerceToNumber(left);
        var r = CoerceToNumber(right);
        if (double.IsNaN(l) || double.IsNaN(r))
            return new Value { Type = ValueType.Unbound };

        var result = l - r;
        if (result == Math.Floor(result) && result >= long.MinValue && result <= long.MaxValue)
            return new Value { Type = ValueType.Integer, IntegerValue = (long)result };
        return new Value { Type = ValueType.Double, DoubleValue = result };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Value Multiply(Value left, Value right)
    {
        if (left.Type == ValueType.Integer && right.Type == ValueType.Integer)
            return new Value { Type = ValueType.Integer, IntegerValue = left.IntegerValue * right.IntegerValue };

        var l = CoerceToNumber(left);
        var r = CoerceToNumber(right);
        if (double.IsNaN(l) || double.IsNaN(r))
            return new Value { Type = ValueType.Unbound };

        var result = l * r;
        if (result == Math.Floor(result) && result >= long.MinValue && result <= long.MaxValue)
            return new Value { Type = ValueType.Integer, IntegerValue = (long)result };
        return new Value { Type = ValueType.Double, DoubleValue = result };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Value Divide(Value left, Value right)
    {
        var l = CoerceToNumber(left);
        var r = CoerceToNumber(right);
        if (double.IsNaN(l) || double.IsNaN(r) || r == 0)
            return new Value { Type = ValueType.Unbound };
        return new Value { Type = ValueType.Double, DoubleValue = l / r };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Value Negate(Value val)
    {
        if (val.Type == ValueType.Integer)
            return new Value { Type = ValueType.Integer, IntegerValue = -val.IntegerValue };

        var n = CoerceToNumber(val);
        if (double.IsNaN(n))
            return new Value { Type = ValueType.Unbound };

        if (n == Math.Floor(n) && n >= long.MinValue && n <= long.MaxValue)
            return new Value { Type = ValueType.Integer, IntegerValue = -(long)n };
        return new Value { Type = ValueType.Double, DoubleValue = -n };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SkipWhitespace()
    {
        while (!IsAtEnd() && IsWhitespace(Peek()))
            Advance();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsAtEnd() => _position >= _expression.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char Peek() => IsAtEnd() ? '\0' : _expression[_position];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char Advance() => IsAtEnd() ? '\0' : _expression[_position++];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsWhitespace(char ch) => ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDigit(char ch) => ch >= '0' && ch <= '9';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsLetter(char ch) => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsLetterOrDigit(char ch) => IsLetter(ch) || IsDigit(ch);
}
