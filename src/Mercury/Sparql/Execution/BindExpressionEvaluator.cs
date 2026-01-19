using System;
using System.Globalization;
using System.Runtime.CompilerServices;
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
            BindingValueType.String => new Value { Type = ValueType.String, StringValue = _bindingStrings.Slice(binding.StringOffset, binding.StringLength) },
            BindingValueType.Uri => new Value { Type = ValueType.Uri, StringValue = _bindingStrings.Slice(binding.StringOffset, binding.StringLength) },
            _ => new Value { Type = ValueType.Unbound }
        };
    }

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

        while (!IsAtEnd() && IsLetterOrDigit(Peek()))
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

            // STR function
            if (name.Equals("STR", StringComparison.OrdinalIgnoreCase))
                return arg;

            // STRLEN function
            if (name.Equals("STRLEN", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Type == ValueType.String)
                    return new Value { Type = ValueType.Integer, IntegerValue = arg.StringValue.Length };
                return new Value { Type = ValueType.Unbound };
            }

            // UCASE function
            if (name.Equals("UCASE", StringComparison.OrdinalIgnoreCase))
                return arg; // Would need buffer for actual uppercase

            // LCASE function
            if (name.Equals("LCASE", StringComparison.OrdinalIgnoreCase))
                return arg; // Would need buffer for actual lowercase

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
        }

        return new Value { Type = ValueType.Unbound };
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
    /// Parse STRBEFORE(string, delimiter) - returns substring before first occurrence of delimiter
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
            return new Value { Type = ValueType.String, StringValue = ReadOnlySpan<char>.Empty };
        }

        Advance(); // Skip ','
        SkipWhitespace();

        var delimiterArg = ParseAdditive();
        SkipWhitespace();
        if (Peek() == ')') Advance();

        if (stringArg.Type != ValueType.String || delimiterArg.Type != ValueType.String)
            return new Value { Type = ValueType.String, StringValue = ReadOnlySpan<char>.Empty };

        var str = ExtractLexicalValue(stringArg.StringValue);
        var delimiter = ExtractLexicalValue(delimiterArg.StringValue);

        // Empty delimiter returns empty string
        if (delimiter.IsEmpty)
            return new Value { Type = ValueType.String, StringValue = ReadOnlySpan<char>.Empty };

        var index = str.IndexOf(delimiter);
        if (index < 0)
            return new Value { Type = ValueType.String, StringValue = ReadOnlySpan<char>.Empty };

        // Return quoted result
        _stringResult = $"\"{str.Slice(0, index).ToString()}\"";
        return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
    }

    /// <summary>
    /// Parse STRAFTER(string, delimiter) - returns substring after first occurrence of delimiter
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
            return new Value { Type = ValueType.String, StringValue = ReadOnlySpan<char>.Empty };
        }

        Advance(); // Skip ','
        SkipWhitespace();

        var delimiterArg = ParseAdditive();
        SkipWhitespace();
        if (Peek() == ')') Advance();

        if (stringArg.Type != ValueType.String || delimiterArg.Type != ValueType.String)
            return new Value { Type = ValueType.String, StringValue = ReadOnlySpan<char>.Empty };

        var str = ExtractLexicalValue(stringArg.StringValue);
        var delimiter = ExtractLexicalValue(delimiterArg.StringValue);

        // Empty delimiter returns full string (per SPARQL spec)
        if (delimiter.IsEmpty)
        {
            _stringResult = $"\"{str.ToString()}\"";
            return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
        }

        var index = str.IndexOf(delimiter);
        if (index < 0)
            return new Value { Type = ValueType.String, StringValue = ReadOnlySpan<char>.Empty };

        // Return quoted result
        _stringResult = $"\"{str.Slice(index + delimiter.Length).ToString()}\"";
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
            _stringResult = $"\"{regex.Replace(str.ToString(), replacement.ToString())}\"";
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
