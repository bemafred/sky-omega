using System;
using System.Globalization;
using System.Runtime.CompilerServices;
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

        var str = _expression.Slice(start, _position - start);

        if (!IsAtEnd()) Advance(); // Skip closing '"'

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
        }

        return new Value { Type = ValueType.Unbound };
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
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            return result;
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
