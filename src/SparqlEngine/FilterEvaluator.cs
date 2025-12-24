using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SparqlEngine;

/// <summary>
/// Zero-allocation FILTER expression evaluator using stack-based evaluation
/// </summary>
public ref struct FilterEvaluator
{
    private ReadOnlySpan<char> _expression;
    private int _position;

    public FilterEvaluator(ReadOnlySpan<char> expression)
    {
        _expression = expression;
        _position = 0;
    }

    /// <summary>
    /// Evaluate FILTER expression against current bindings
    /// </summary>
    public bool Evaluate(in BindingTable bindings)
    {
        _position = 0;
        return EvaluateExpression(bindings);
    }

    private bool EvaluateExpression(in BindingTable bindings)
    {
        SkipWhitespace();
        
        // Parse relational expression
        var left = ParseTerm(bindings);
        SkipWhitespace();
        
        if (IsAtEnd())
            return CoerceToBool(left);
        
        var op = ParseOperator();
        SkipWhitespace();
        
        var right = ParseTerm(bindings);
        
        return EvaluateComparison(left, op, right);
    }

    private Value ParseTerm(in BindingTable bindings)
    {
        SkipWhitespace();
        
        var ch = Peek();
        
        // Function call
        if (IsLetter(ch))
        {
            return ParseFunctionCall(bindings);
        }
        
        // Variable reference
        if (ch == '?')
        {
            return ParseVariable(bindings);
        }
        
        // Literal value
        if (ch == '"')
        {
            return ParseStringLiteral();
        }
        
        // Numeric literal
        if (IsDigit(ch) || ch == '-')
        {
            return ParseNumericLiteral();
        }
        
        return new Value { Type = ValueType.Unbound };
    }

    private Value ParseVariable(in BindingTable bindings)
    {
        Advance(); // Skip '?'
        var start = _position;
        
        while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
            Advance();
        
        var varName = _expression.Slice(start - 1, _position - start + 1);
        
        // For simplicity, return unbound - full implementation would lookup in bindings
        return new Value { Type = ValueType.Unbound };
    }

    private Value ParseStringLiteral()
    {
        Advance(); // Skip '"'
        var start = _position;
        
        while (!IsAtEnd() && Peek() != '"')
        {
            if (Peek() == '\\')
                Advance(); // Skip escape
            Advance();
        }
        
        var str = _expression.Slice(start, _position - start);
        
        if (!IsAtEnd())
            Advance(); // Skip closing '"'
        
        return new Value
        {
            Type = ValueType.String,
            StringValue = str
        };
    }

    private Value ParseNumericLiteral()
    {
        var start = _position;
        
        if (Peek() == '-')
            Advance();
        
        while (!IsAtEnd() && IsDigit(Peek()))
            Advance();
        
        if (!IsAtEnd() && Peek() == '.')
        {
            Advance();
            while (!IsAtEnd() && IsDigit(Peek()))
                Advance();
            
            var str = _expression.Slice(start, _position - start);
            if (double.TryParse(str, out var d))
            {
                return new Value { Type = ValueType.Double, DoubleValue = d };
            }
        }
        else
        {
            var str = _expression.Slice(start, _position - start);
            if (long.TryParse(str, out var i))
            {
                return new Value { Type = ValueType.Integer, IntegerValue = i };
            }
        }
        
        return new Value { Type = ValueType.Unbound };
    }

    private Value ParseFunctionCall(in BindingTable bindings)
    {
        var start = _position;
        
        while (!IsAtEnd() && IsLetterOrDigit(Peek()))
            Advance();
        
        var funcName = _expression.Slice(start, _position - start);
        
        SkipWhitespace();
        if (Peek() != '(')
            return new Value { Type = ValueType.Unbound };
        
        Advance(); // Skip '('
        
        // Parse arguments
        var arg1 = ParseTerm(bindings);
        
        SkipWhitespace();
        if (Peek() == ',')
        {
            Advance();
            var arg2 = ParseTerm(bindings);
        }
        
        SkipWhitespace();
        if (Peek() == ')')
            Advance();
        
        // Evaluate built-in functions
        if (funcName.Equals("bound", StringComparison.OrdinalIgnoreCase))
        {
            return new Value
            {
                Type = ValueType.Boolean,
                BooleanValue = arg1.Type != ValueType.Unbound
            };
        }
        
        if (funcName.Equals("isIRI", StringComparison.OrdinalIgnoreCase))
        {
            return new Value
            {
                Type = ValueType.Boolean,
                BooleanValue = arg1.Type == ValueType.Uri
            };
        }
        
        if (funcName.Equals("str", StringComparison.OrdinalIgnoreCase))
        {
            return new Value
            {
                Type = ValueType.String,
                StringValue = arg1.StringValue
            };
        }
        
        return new Value { Type = ValueType.Unbound };
    }

    private ComparisonOperator ParseOperator()
    {
        var span = _expression.Slice(_position, Math.Min(2, _expression.Length - _position));
        
        if (span.Length >= 2)
        {
            if (span[0] == '=' && span[1] == '=')
            {
                _position += 2;
                return ComparisonOperator.Equal;
            }
            if (span[0] == '!' && span[1] == '=')
            {
                _position += 2;
                return ComparisonOperator.NotEqual;
            }
            if (span[0] == '<' && span[1] == '=')
            {
                _position += 2;
                return ComparisonOperator.LessOrEqual;
            }
            if (span[0] == '>' && span[1] == '=')
            {
                _position += 2;
                return ComparisonOperator.GreaterOrEqual;
            }
        }
        
        if (span.Length >= 1)
        {
            if (span[0] == '<')
            {
                _position++;
                return ComparisonOperator.Less;
            }
            if (span[0] == '>')
            {
                _position++;
                return ComparisonOperator.Greater;
            }
            if (span[0] == '=')
            {
                _position++;
                return ComparisonOperator.Equal;
            }
        }
        
        return ComparisonOperator.Unknown;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool EvaluateComparison(in Value left, ComparisonOperator op, in Value right)
    {
        // Type coercion for comparisons
        if (left.Type == ValueType.Unbound || right.Type == ValueType.Unbound)
            return false;
        
        return op switch
        {
            ComparisonOperator.Equal => CompareEqual(left, right),
            ComparisonOperator.NotEqual => !CompareEqual(left, right),
            ComparisonOperator.Less => CompareLess(left, right),
            ComparisonOperator.Greater => CompareLess(right, left),
            ComparisonOperator.LessOrEqual => CompareEqual(left, right) || CompareLess(left, right),
            ComparisonOperator.GreaterOrEqual => CompareEqual(left, right) || CompareLess(right, left),
            _ => false
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CompareEqual(in Value left, in Value right)
    {
        if (left.Type != right.Type)
            return false;
        
        return left.Type switch
        {
            ValueType.Integer => left.IntegerValue == right.IntegerValue,
            ValueType.Double => Math.Abs(left.DoubleValue - right.DoubleValue) < 1e-10,
            ValueType.Boolean => left.BooleanValue == right.BooleanValue,
            ValueType.String => left.StringValue.SequenceEqual(right.StringValue),
            _ => false
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CompareLess(in Value left, in Value right)
    {
        if (left.Type != right.Type)
            return false;
        
        return left.Type switch
        {
            ValueType.Integer => left.IntegerValue < right.IntegerValue,
            ValueType.Double => left.DoubleValue < right.DoubleValue,
            ValueType.String => left.StringValue.SequenceCompareTo(right.StringValue) < 0,
            _ => false
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CoerceToBool(in Value value)
    {
        return value.Type switch
        {
            ValueType.Boolean => value.BooleanValue,
            ValueType.Integer => value.IntegerValue != 0,
            ValueType.Double => Math.Abs(value.DoubleValue) > 1e-10,
            ValueType.String => value.StringValue.Length > 0,
            _ => false
        };
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

/// <summary>
/// Stack-allocated value for FILTER evaluation
/// </summary>
public ref struct Value
{
    public ValueType Type;
    public long IntegerValue;
    public double DoubleValue;
    public bool BooleanValue;
    public ReadOnlySpan<char> StringValue;
}

public enum ValueType
{
    Unbound,
    Uri,
    String,
    Integer,
    Double,
    Boolean
}

public enum ComparisonOperator
{
    Unknown,
    Equal,
    NotEqual,
    Less,
    Greater,
    LessOrEqual,
    GreaterOrEqual
}
