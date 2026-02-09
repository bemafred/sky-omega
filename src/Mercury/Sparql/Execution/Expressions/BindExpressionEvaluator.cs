using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Execution.Expressions;

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
    private ReadOnlySpan<char> _baseIri;

    public BindExpressionEvaluator(ReadOnlySpan<char> expression,
        ReadOnlySpan<Binding> bindings, int bindingCount, ReadOnlySpan<char> stringBuffer)
    {
        _expression = expression;
        _position = 0;
        _bindingData = bindings;
        _bindingCount = bindingCount;
        _bindingStrings = stringBuffer;
        _baseIri = default;
    }

    public BindExpressionEvaluator(ReadOnlySpan<char> expression,
        ReadOnlySpan<Binding> bindings, int bindingCount, ReadOnlySpan<char> stringBuffer,
        ReadOnlySpan<char> baseIri)
    {
        _expression = expression;
        _position = 0;
        _bindingData = bindings;
        _bindingCount = bindingCount;
        _bindingStrings = stringBuffer;
        _baseIri = baseIri;
    }

    /// <summary>
    /// Evaluate the expression and return a Value.
    /// </summary>
    public Value Evaluate()
    {
        _position = 0;
        return ParseComparison();
    }

    /// <summary>
    /// Comparison := Additive (ComparisonOp Additive)?
    /// Handles =, ==, !=, <, >, <=, >=
    /// </summary>
    private Value ParseComparison()
    {
        var left = ParseAdditive();
        SkipWhitespace();

        if (IsAtEnd()) return left;

        // Check for comparison operators
        var op = PeekComparisonOperator();
        if (op == ComparisonOp.None)
            return left;

        ConsumeComparisonOperator(op);
        SkipWhitespace();

        var right = ParseAdditive();

        // Evaluate comparison and return boolean
        return EvaluateComparison(left, op, right);
    }

    private enum ComparisonOp { None, Equal, NotEqual, Less, Greater, LessOrEqual, GreaterOrEqual }

    private ComparisonOp PeekComparisonOperator()
    {
        if (_position >= _expression.Length) return ComparisonOp.None;

        var ch = _expression[_position];
        if (_position + 1 < _expression.Length)
        {
            var next = _expression[_position + 1];
            if (ch == '=' && next == '=') return ComparisonOp.Equal;
            if (ch == '!' && next == '=') return ComparisonOp.NotEqual;
            if (ch == '<' && next == '=') return ComparisonOp.LessOrEqual;
            if (ch == '>' && next == '=') return ComparisonOp.GreaterOrEqual;
        }
        if (ch == '=') return ComparisonOp.Equal;
        if (ch == '<') return ComparisonOp.Less;
        if (ch == '>') return ComparisonOp.Greater;
        return ComparisonOp.None;
    }

    private void ConsumeComparisonOperator(ComparisonOp op)
    {
        switch (op)
        {
            case ComparisonOp.Equal:
                if (_position + 1 < _expression.Length && _expression[_position + 1] == '=')
                    _position += 2;
                else
                    _position += 1;
                break;
            case ComparisonOp.NotEqual:
            case ComparisonOp.LessOrEqual:
            case ComparisonOp.GreaterOrEqual:
                _position += 2;
                break;
            case ComparisonOp.Less:
            case ComparisonOp.Greater:
                _position += 1;
                break;
        }
    }

    private Value EvaluateComparison(Value left, ComparisonOp op, Value right)
    {
        // Compare based on types
        bool result;

        if (left.Type == ValueType.Integer && right.Type == ValueType.Integer)
        {
            result = op switch
            {
                ComparisonOp.Equal => left.IntegerValue == right.IntegerValue,
                ComparisonOp.NotEqual => left.IntegerValue != right.IntegerValue,
                ComparisonOp.Less => left.IntegerValue < right.IntegerValue,
                ComparisonOp.Greater => left.IntegerValue > right.IntegerValue,
                ComparisonOp.LessOrEqual => left.IntegerValue <= right.IntegerValue,
                ComparisonOp.GreaterOrEqual => left.IntegerValue >= right.IntegerValue,
                _ => false
            };
        }
        else if ((left.Type == ValueType.Double || left.Type == ValueType.Integer) &&
                 (right.Type == ValueType.Double || right.Type == ValueType.Integer))
        {
            var leftVal = left.Type == ValueType.Double ? left.DoubleValue : left.IntegerValue;
            var rightVal = right.Type == ValueType.Double ? right.DoubleValue : right.IntegerValue;
            result = op switch
            {
                ComparisonOp.Equal => Math.Abs(leftVal - rightVal) < 1e-10,
                ComparisonOp.NotEqual => Math.Abs(leftVal - rightVal) >= 1e-10,
                ComparisonOp.Less => leftVal < rightVal,
                ComparisonOp.Greater => leftVal > rightVal,
                ComparisonOp.LessOrEqual => leftVal <= rightVal,
                ComparisonOp.GreaterOrEqual => leftVal >= rightVal,
                _ => false
            };
        }
        else if (left.Type == ValueType.String && right.Type == ValueType.String)
        {
            var cmp = left.StringValue.CompareTo(right.StringValue, StringComparison.Ordinal);
            result = op switch
            {
                ComparisonOp.Equal => cmp == 0,
                ComparisonOp.NotEqual => cmp != 0,
                ComparisonOp.Less => cmp < 0,
                ComparisonOp.Greater => cmp > 0,
                ComparisonOp.LessOrEqual => cmp <= 0,
                ComparisonOp.GreaterOrEqual => cmp >= 0,
                _ => false
            };
        }
        else if (left.Type == ValueType.Boolean && right.Type == ValueType.Boolean)
        {
            result = op switch
            {
                ComparisonOp.Equal => left.BooleanValue == right.BooleanValue,
                ComparisonOp.NotEqual => left.BooleanValue != right.BooleanValue,
                _ => false
            };
        }
        else
        {
            // Type mismatch - for = and != we can compare, otherwise unbound
            if (op == ComparisonOp.Equal)
                result = false;
            else if (op == ComparisonOp.NotEqual)
                result = true;
            else
                return new Value { Type = ValueType.Unbound };
        }

        return new Value { Type = ValueType.Boolean, BooleanValue = result };
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

        // Parenthesized expression - recurse to ParseComparison to handle comparisons inside parens
        if (ch == '(')
        {
            Advance();
            var result = ParseComparison();
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

        // IRI literal <...>
        if (ch == '<')
        {
            return ParseIriLiteral();
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

    private Value ParseIriLiteral()
    {
        var start = _position;
        Advance(); // Skip '<'

        while (!IsAtEnd() && Peek() != '>')
            Advance();

        if (Peek() == '>') Advance();

        // Return the IRI including angle brackets
        var iri = _expression.Slice(start, _position - start);
        return new Value { Type = ValueType.Uri, StringValue = iri };
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
        // Capture start position (including opening quote) for full literal preservation
        var literalStart = _position;

        Advance(); // Skip '"'
        var contentStart = _position;

        while (!IsAtEnd() && Peek() != '"')
        {
            if (Peek() == '\\') Advance();
            Advance();
        }

        // Capture string content before moving past closing quote
        var str = _expression.Slice(contentStart, _position - contentStart);

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

                // Capture full literal with language tag for preservation
                var fullLiteral = _expression.Slice(literalStart, _position - literalStart);
                return new Value { Type = ValueType.String, StringValue = fullLiteral };
            }
            else if (Peek() == '^' && _position + 1 < _expression.Length &&
                     _expression[_position + 1] == '^')
            {
                // Skip datatype: ^^<IRI> or ^^prefix:local
                Advance(); // Skip first '^'
                Advance(); // Skip second '^'

                if (!IsAtEnd() && Peek() == '<')
                {
                    // Full IRI datatype: <http://...>
                    Advance(); // Skip '<'
                    while (!IsAtEnd() && Peek() != '>')
                        Advance();
                    if (!IsAtEnd()) Advance(); // Skip '>'

                    // Capture full literal with datatype for preservation
                    var fullLiteral = _expression.Slice(literalStart, _position - literalStart);
                    return new Value { Type = ValueType.String, StringValue = fullLiteral };
                }
                else
                {
                    // Prefixed name datatype: prefix:local
                    // Expand common prefixes for output
                    var prefixStart = _position;
                    while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                        Advance();
                    if (!IsAtEnd() && Peek() == ':')
                    {
                        Advance();
                        while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_' || Peek() == '-'))
                            Advance();
                    }

                    // Try to expand known prefixes
                    var prefixedName = _expression.Slice(prefixStart, _position - prefixStart);
                    var expandedIri = ExpandCommonPrefix(prefixedName);

                    if (!expandedIri.IsEmpty)
                    {
                        // Return with expanded IRI
                        _stringResult = $"\"{str}\"^^<{expandedIri}>";
                        return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
                    }

                    // Unknown prefix - return full literal as-is
                    var fullLiteral = _expression.Slice(literalStart, _position - literalStart);
                    return new Value { Type = ValueType.String, StringValue = fullLiteral };
                }
            }
        }

        // Return the string content (without quotes) as the value for plain literals
        return new Value { Type = ValueType.String, StringValue = str };
    }

    /// <summary>
    /// Expand common prefixes used in SPARQL typed literals.
    /// </summary>
    private static ReadOnlySpan<char> ExpandCommonPrefix(ReadOnlySpan<char> prefixedName)
    {
        if (prefixedName.StartsWith("xsd:", StringComparison.OrdinalIgnoreCase))
        {
            var local = prefixedName.Slice(4);
            if (local.Equals("date", StringComparison.OrdinalIgnoreCase))
                return "http://www.w3.org/2001/XMLSchema#date".AsSpan();
            if (local.Equals("dateTime", StringComparison.OrdinalIgnoreCase))
                return "http://www.w3.org/2001/XMLSchema#dateTime".AsSpan();
            if (local.Equals("integer", StringComparison.OrdinalIgnoreCase))
                return "http://www.w3.org/2001/XMLSchema#integer".AsSpan();
            if (local.Equals("decimal", StringComparison.OrdinalIgnoreCase))
                return "http://www.w3.org/2001/XMLSchema#decimal".AsSpan();
            if (local.Equals("double", StringComparison.OrdinalIgnoreCase))
                return "http://www.w3.org/2001/XMLSchema#double".AsSpan();
            if (local.Equals("float", StringComparison.OrdinalIgnoreCase))
                return "http://www.w3.org/2001/XMLSchema#float".AsSpan();
            if (local.Equals("boolean", StringComparison.OrdinalIgnoreCase))
                return "http://www.w3.org/2001/XMLSchema#boolean".AsSpan();
            if (local.Equals("string", StringComparison.OrdinalIgnoreCase))
                return "http://www.w3.org/2001/XMLSchema#string".AsSpan();
            if (local.Equals("time", StringComparison.OrdinalIgnoreCase))
                return "http://www.w3.org/2001/XMLSchema#time".AsSpan();
            if (local.Equals("gYear", StringComparison.OrdinalIgnoreCase))
                return "http://www.w3.org/2001/XMLSchema#gYear".AsSpan();
            if (local.Equals("gYearMonth", StringComparison.OrdinalIgnoreCase))
                return "http://www.w3.org/2001/XMLSchema#gYearMonth".AsSpan();
        }
        return ReadOnlySpan<char>.Empty;
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

            // BNODE - construct blank node (0 or 1 argument)
            if (name.Equals("BNODE", StringComparison.OrdinalIgnoreCase))
            {
                if (Peek() == ')')
                {
                    // BNODE() - no argument, generate fresh blank node
                    Advance();
                    var counter = System.Threading.Interlocked.Increment(ref s_bnodeCounter);
                    _stringResult = $"_:b{counter}";
                    return new Value { Type = ValueType.Uri, StringValue = _stringResult.AsSpan() };
                }
                // BNODE(label) - with string argument
                // Same string in same row returns same bnode, but different rows get different bnodes
                var labelArg = ParseAdditive();
                SkipWhitespace();
                if (Peek() == ')') Advance();

                var label = labelArg.GetLexicalForm();
                if (label.IsEmpty)
                    return new Value { Type = ValueType.Unbound };

                // Include row seed so different rows get different bnodes for the same label
                var rowSeed = s_bnodeRowSeed;
                _stringResult = $"_:r{rowSeed}_{label.ToString()}";
                return new Value { Type = ValueType.Uri, StringValue = _stringResult.AsSpan() };
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

            // DATATYPE - returns the datatype IRI of a literal
            // Returns Unbound for non-literals (IRIs, blank nodes)
            if (name.Equals("DATATYPE", StringComparison.OrdinalIgnoreCase))
            {
                // IRIs and blank nodes have no datatype - return unbound
                if (arg.Type == ValueType.Uri)
                    return new Value { Type = ValueType.Unbound };

                // Check if StringValue contains an explicit datatype annotation first
                // This handles the case where a numeric value was parsed from a typed literal
                // (e.g., "1.0"^^<xsd:decimal> was parsed to Double but StringValue preserved)
                if (!arg.StringValue.IsEmpty)
                {
                    var str = arg.StringValue;
                    var caretIdx = str.LastIndexOf("^^".AsSpan());
                    if (caretIdx > 0)
                    {
                        var dtPart = str.Slice(caretIdx + 2);
                        // Extract IRI from <...>
                        if (dtPart.Length >= 2 && dtPart[0] == '<' && dtPart[^1] == '>')
                        {
                            _stringResult = dtPart.ToString();
                            return new Value { Type = ValueType.Uri, StringValue = _stringResult.AsSpan() };
                        }
                    }
                    // Check for language tag: "value"@lang
                    // Per RDF 1.1, language-tagged strings have datatype rdf:langString
                    var atIdx = str.LastIndexOf('@');
                    if (atIdx > 0 && str.Slice(0, atIdx).EndsWith("\""))
                    {
                        _stringResult = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#langString>";
                        return new Value { Type = ValueType.Uri, StringValue = _stringResult.AsSpan() };
                    }
                }

                // Fall back to XSD datatype based on value type
                if (arg.Type == ValueType.Integer)
                {
                    _stringResult = "<http://www.w3.org/2001/XMLSchema#integer>";
                    return new Value { Type = ValueType.Uri, StringValue = _stringResult.AsSpan() };
                }
                if (arg.Type == ValueType.Double)
                {
                    _stringResult = "<http://www.w3.org/2001/XMLSchema#double>";
                    return new Value { Type = ValueType.Uri, StringValue = _stringResult.AsSpan() };
                }
                if (arg.Type == ValueType.Boolean)
                {
                    _stringResult = "<http://www.w3.org/2001/XMLSchema#boolean>";
                    return new Value { Type = ValueType.Uri, StringValue = _stringResult.AsSpan() };
                }
                if (arg.Type == ValueType.String)
                {
                    // Plain literal - xsd:string
                    _stringResult = "<http://www.w3.org/2001/XMLSchema#string>";
                    return new Value { Type = ValueType.Uri, StringValue = _stringResult.AsSpan() };
                }
                return new Value { Type = ValueType.Unbound };
            }

            // IRI/URI - construct IRI from string
            // Resolves relative IRIs against base IRI if available
            if (name.Equals("IRI", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("URI", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Type == ValueType.Uri)
                {
                    // Check if it's a relative IRI that needs resolution
                    var uriVal = arg.StringValue;
                    // Remove angle brackets if present
                    if (uriVal.Length >= 2 && uriVal[0] == '<' && uriVal[^1] == '>')
                        uriVal = uriVal.Slice(1, uriVal.Length - 2);
                    // If it's an absolute IRI, return unchanged
                    if (uriVal.Contains("://".AsSpan(), StringComparison.Ordinal))
                        return arg;
                    // Relative IRI - resolve against base
                    if (_baseIri.Length > 0)
                    {
                        var baseStr = _baseIri;
                        if (baseStr.Length >= 2 && baseStr[0] == '<' && baseStr[^1] == '>')
                            baseStr = baseStr.Slice(1, baseStr.Length - 2);
                        _stringResult = $"<{baseStr}{uriVal}>";
                        return new Value { Type = ValueType.Uri, StringValue = _stringResult.AsSpan() };
                    }
                    return arg; // No base, return unchanged
                }
                if (arg.Type == ValueType.String)
                {
                    var lexical = arg.GetLexicalForm();
                    // Check if it's an absolute IRI (contains scheme)
                    if (lexical.Contains("://".AsSpan(), StringComparison.Ordinal))
                    {
                        _stringResult = $"<{lexical}>";
                        return new Value { Type = ValueType.Uri, StringValue = _stringResult.AsSpan() };
                    }
                    // Relative IRI - resolve against base if available
                    if (_baseIri.Length > 0)
                    {
                        // Remove angle brackets from base if present
                        var baseStr = _baseIri;
                        if (baseStr.Length >= 2 && baseStr[0] == '<' && baseStr[^1] == '>')
                            baseStr = baseStr.Slice(1, baseStr.Length - 2);
                        // Simple resolution: append to base (handles most common case)
                        _stringResult = $"<{baseStr}{lexical}>";
                        return new Value { Type = ValueType.Uri, StringValue = _stringResult.AsSpan() };
                    }
                    // No base - just wrap as IRI
                    _stringResult = $"<{lexical}>";
                    return new Value { Type = ValueType.Uri, StringValue = _stringResult.AsSpan() };
                }
                return new Value { Type = ValueType.Unbound };
            }

            // STRLEN function - length in Unicode code points (not UTF-16 code units)
            // Characters outside BMP (like emoji) count as 1, not 2
            if (name.Equals("STRLEN", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Type == ValueType.String)
                    return new Value { Type = ValueType.Integer, IntegerValue = CodePointOps.GetCodePointCount(arg.GetLexicalForm()) };
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

            // TZ - extract timezone string from xsd:dateTime
            // Returns "Z" for UTC, "+HH:MM" or "-HH:MM" for offsets, "" for no timezone
            if (name.Equals("TZ", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Type == ValueType.String)
                {
                    var tzStr = ExtractTimezone(arg.StringValue);
                    _stringResult = $"\"{tzStr}\"";
                    return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
                }
                return new Value { Type = ValueType.Unbound };
            }

            // TIMEZONE - extract timezone as xsd:dayTimeDuration
            // Returns "PT0S" for UTC, "PT5H30M" for +05:30, "-PT8H" for -08:00, unbound for no timezone
            if (name.Equals("TIMEZONE", StringComparison.OrdinalIgnoreCase))
            {
                if (arg.Type == ValueType.String)
                {
                    var tzStr = ExtractTimezone(arg.StringValue);
                    if (string.IsNullOrEmpty(tzStr))
                    {
                        // No timezone specified - return unbound per SPARQL spec
                        return new Value { Type = ValueType.Unbound };
                    }
                    var duration = ConvertToXsdDuration(tzStr);
                    _stringResult = $"\"{duration}\"^^<http://www.w3.org/2001/XMLSchema#dayTimeDuration>";
                    return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
                }
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
    /// Extract timezone string from xsd:dateTime
    /// Returns "Z" for UTC, "+HH:MM" or "-HH:MM" for offsets, "" for no timezone
    /// </summary>
    private static string ExtractTimezone(ReadOnlySpan<char> str)
    {
        if (str.IsEmpty)
            return "";

        // Handle RDF typed literals: extract lexical value from quotes
        var parseSpan = str;
        if (str.Length > 2 && str[0] == '"')
        {
            var endQuote = str.Slice(1).IndexOf('"');
            if (endQuote > 0)
            {
                parseSpan = str.Slice(1, endQuote);
            }
        }

        // Check for Z (UTC)
        if (parseSpan[^1] == 'Z')
            return "Z";

        // Look for +/- timezone offset at end
        // Format: +HH:MM or -HH:MM (6 chars)
        if (parseSpan.Length >= 6)
        {
            var potentialTz = parseSpan.Slice(parseSpan.Length - 6);
            if ((potentialTz[0] == '+' || potentialTz[0] == '-') && potentialTz[3] == ':')
            {
                return potentialTz.ToString();
            }
        }

        // No timezone specified
        return "";
    }

    /// <summary>
    /// Convert timezone string to xsd:dayTimeDuration format
    /// "Z" → "PT0S", "+05:30" → "PT5H30M", "-08:00" → "-PT8H"
    /// </summary>
    private static string ConvertToXsdDuration(string tz)
    {
        if (tz == "Z")
            return "PT0S";

        if (tz.Length != 6)
            return "PT0S";

        var sign = tz[0];
        if (sign != '+' && sign != '-')
            return "PT0S";

        // Parse hours and minutes from +HH:MM or -HH:MM
        if (!int.TryParse(tz.AsSpan(1, 2), out var hours))
            return "PT0S";
        if (!int.TryParse(tz.AsSpan(4, 2), out var minutes))
            return "PT0S";

        // Build duration string
        var result = new StringBuilder();
        if (sign == '-')
            result.Append('-');
        result.Append("PT");

        if (hours > 0)
            result.Append($"{hours}H");
        if (minutes > 0)
            result.Append($"{minutes}M");

        // If both are zero, return PT0S
        if (hours == 0 && minutes == 0)
            return "PT0S";

        return result.ToString();
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
    /// Only accepts string literals (not integers, URIs, etc.).
    /// If all arguments have the same language tag, result preserves that tag.
    /// If mixed language tags, result is a plain literal.
    /// Per SPARQL spec: if any argument is unbound, result is unbound
    /// </summary>
    private Value ParseConcatFunction()
    {
        // Collect all argument string values and track language tags
        var parts = new List<string>();
        string? commonLangTag = null;
        bool firstArg = true;
        bool mixedLangTags = false;

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

            // CONCAT only accepts string literals - integers, URIs, booleans etc. are errors
            if (value.Type != ValueType.String)
            {
                while (!IsAtEnd() && Peek() != ')')
                    Advance();
                if (Peek() == ')') Advance();
                return new Value { Type = ValueType.Unbound };
            }

            parts.Add(value.GetLexicalForm().ToString());

            // Track language tags for result
            var suffix = value.GetLangTagOrDatatype();
            var langTag = GetLangTag(suffix);
            var langStr = langTag.IsEmpty ? null : langTag.ToString();

            if (firstArg)
            {
                commonLangTag = langStr;
                firstArg = false;
            }
            else if (!mixedLangTags)
            {
                // Check if language tags differ
                if (commonLangTag != langStr &&
                    !(commonLangTag == null && langStr == null))
                {
                    if (commonLangTag == null || langStr == null ||
                        !commonLangTag.Equals(langStr, StringComparison.OrdinalIgnoreCase))
                    {
                        mixedLangTags = true;
                        commonLangTag = null;
                    }
                }
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
        var concatenated = string.Concat(parts);
        if (!mixedLangTags && commonLangTag != null)
        {
            _stringResult = $"\"{concatenated}\"@{commonLangTag}";
        }
        else
        {
            _stringResult = concatenated;
        }
        return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
    }

    /// <summary>
    /// Extract the language tag from a suffix like @en or @en-US.
    /// Returns empty span if suffix is a datatype (^^...) or empty.
    /// </summary>
    private static ReadOnlySpan<char> GetLangTag(ReadOnlySpan<char> suffix)
    {
        if (suffix.Length > 1 && suffix[0] == '@')
            return suffix.Slice(1);
        return ReadOnlySpan<char>.Empty;
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
        var codePointCount = CodePointOps.GetCodePointCount(str);

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
        var result = CodePointOps.SubstringByCodePoints(str, startCodePoint, lengthCodePoints);

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
    /// Returns empty simple literal if delimiter not found.
    /// Arguments must be compatible (same language tag or both plain/xsd:string).
    /// Preserves language tag from first arg (but not xsd:string datatype).
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
            return new Value { Type = ValueType.Unbound };
        }

        Advance(); // Skip ','
        SkipWhitespace();

        var delimiterArg = ParseAdditive();
        SkipWhitespace();
        if (Peek() == ')') Advance();

        if (stringArg.Type != ValueType.String || delimiterArg.Type != ValueType.String)
        {
            return new Value { Type = ValueType.Unbound };
        }

        var str = stringArg.GetLexicalForm();
        var delimiter = delimiterArg.GetLexicalForm();

        // Get language tags for compatibility check
        var arg1Suffix = stringArg.GetLangTagOrDatatype();
        var arg2Suffix = delimiterArg.GetLangTagOrDatatype();
        var arg1Lang = GetLangTag(arg1Suffix);
        var arg2Lang = GetLangTag(arg2Suffix);

        // Check argument compatibility: if arg2 has a language tag, arg1 must have the same tag
        if (!arg2Lang.IsEmpty && !arg1Lang.Equals(arg2Lang, StringComparison.OrdinalIgnoreCase))
        {
            return new Value { Type = ValueType.Unbound };
        }

        // Empty delimiter returns empty string with preserved language tag
        if (delimiter.IsEmpty)
        {
            if (!arg1Lang.IsEmpty)
            {
                _stringResult = $"\"\"@{arg1Lang.ToString()}";
                return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
            }
            return new Value { Type = ValueType.String, StringValue = ReadOnlySpan<char>.Empty };
        }

        var index = str.IndexOf(delimiter);
        if (index < 0)
        {
            // Delimiter not found returns empty simple literal (no language tag per SPARQL spec)
            return new Value { Type = ValueType.String, StringValue = ReadOnlySpan<char>.Empty };
        }

        // Preserve language tag from the first argument (but not xsd:string)
        var result = str.Slice(0, index).ToString();
        if (!arg1Lang.IsEmpty)
        {
            _stringResult = $"\"{result}\"@{arg1Lang.ToString()}";
            return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
        }
        _stringResult = result;
        return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
    }

    /// <summary>
    /// Parse STRAFTER(string, delimiter) - returns substring after first occurrence of delimiter
    /// Returns empty simple literal if delimiter not found.
    /// Arguments must be compatible (same language tag or both plain/xsd:string).
    /// Preserves language tag from first arg (but not xsd:string datatype).
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
            return new Value { Type = ValueType.Unbound };
        }

        Advance(); // Skip ','
        SkipWhitespace();

        var delimiterArg = ParseAdditive();
        SkipWhitespace();
        if (Peek() == ')') Advance();

        if (stringArg.Type != ValueType.String || delimiterArg.Type != ValueType.String)
        {
            return new Value { Type = ValueType.Unbound };
        }

        var str = stringArg.GetLexicalForm();
        var delimiter = delimiterArg.GetLexicalForm();

        // Get language tags for compatibility check
        var arg1Suffix = stringArg.GetLangTagOrDatatype();
        var arg2Suffix = delimiterArg.GetLangTagOrDatatype();
        var arg1Lang = GetLangTag(arg1Suffix);
        var arg2Lang = GetLangTag(arg2Suffix);

        // Check argument compatibility: if arg2 has a language tag, arg1 must have the same tag
        if (!arg2Lang.IsEmpty && !arg1Lang.Equals(arg2Lang, StringComparison.OrdinalIgnoreCase))
        {
            return new Value { Type = ValueType.Unbound };
        }

        // Empty delimiter returns full string with preserved language tag (per SPARQL spec)
        if (delimiter.IsEmpty)
        {
            if (!arg1Lang.IsEmpty)
            {
                _stringResult = $"\"{str.ToString()}\"@{arg1Lang.ToString()}";
                return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
            }
            _stringResult = str.ToString();
            return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
        }

        var index = str.IndexOf(delimiter);
        if (index < 0)
        {
            // Delimiter not found returns empty simple literal (no language tag per SPARQL spec)
            return new Value { Type = ValueType.String, StringValue = ReadOnlySpan<char>.Empty };
        }

        // Preserve language tag from the first argument (but not xsd:string)
        var result = str.Slice(index + delimiter.Length).ToString();
        if (!arg1Lang.IsEmpty)
        {
            _stringResult = $"\"{result}\"@{arg1Lang.ToString()}";
            return new Value { Type = ValueType.String, StringValue = _stringResult.AsSpan() };
        }
        _stringResult = result;
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

    // Static counter for generating unique blank node identifiers across all evaluations
    // Uses Interlocked for thread-safety
    private static int s_bnodeCounter = 0;

    // Per-row seed for BNODE(str) - ensures same string in same row gets same bnode,
    // but different rows get different bnodes for the same string
    // Uses Interlocked for thread-safety
    private static int s_bnodeRowSeed = 0;

    /// <summary>
    /// Increments the bnode row seed. Call this before processing each new row's SELECT expressions
    /// to ensure BNODE(str) produces different bnodes for different rows.
    /// </summary>
    public static void IncrementBnodeRowSeed()
    {
        System.Threading.Interlocked.Increment(ref s_bnodeRowSeed);
    }

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
