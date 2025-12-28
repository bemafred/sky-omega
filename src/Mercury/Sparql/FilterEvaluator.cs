using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace SkyOmega.Mercury.Sparql;

/// <summary>
/// Zero-allocation FILTER expression evaluator using stack-based evaluation
/// </summary>
public ref struct FilterEvaluator
{
    private ReadOnlySpan<char> _expression;
    private int _position;
    // Binding data stored as fields to avoid scope issues with spans
    private ReadOnlySpan<Binding> _bindingData;
    private ReadOnlySpan<char> _bindingStrings;
    private int _bindingCount;

    public FilterEvaluator(ReadOnlySpan<char> expression)
    {
        _expression = expression;
        _position = 0;
        _bindingData = ReadOnlySpan<Binding>.Empty;
        _bindingStrings = ReadOnlySpan<char>.Empty;
        _bindingCount = 0;
    }

    /// <summary>
    /// Evaluate FILTER expression against current bindings.
    /// </summary>
    public bool Evaluate(ReadOnlySpan<Binding> bindings, int bindingCount, ReadOnlySpan<char> stringBuffer)
    {
        _position = 0;
        _bindingData = bindings;
        _bindingCount = bindingCount;
        _bindingStrings = stringBuffer;
        return EvaluateOrExpression();
    }

    /// <summary>
    /// Evaluate FILTER expression with empty bindings.
    /// </summary>
    public bool Evaluate()
    {
        _position = 0;
        _bindingData = ReadOnlySpan<Binding>.Empty;
        _bindingStrings = ReadOnlySpan<char>.Empty;
        _bindingCount = 0;
        return EvaluateOrExpression();
    }

    /// <summary>
    /// OrExpr := AndExpr (('||' | 'OR') AndExpr)*
    /// </summary>
    private bool EvaluateOrExpression()
    {
        var result = EvaluateAndExpression();

        while (true)
        {
            SkipWhitespace();
            if (IsAtEnd())
                break;

            if (MatchOperator("||") || MatchKeyword("OR"))
            {
                var right = EvaluateAndExpression();
                result = result || right;
            }
            else
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// AndExpr := UnaryExpr (('&&' | 'AND') UnaryExpr)*
    /// </summary>
    private bool EvaluateAndExpression()
    {
        var result = EvaluateUnaryExpression();

        while (true)
        {
            SkipWhitespace();
            if (IsAtEnd())
                break;

            if (MatchOperator("&&") || MatchKeyword("AND"))
            {
                var right = EvaluateUnaryExpression();
                result = result && right;
            }
            else
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// UnaryExpr := ('!' | 'NOT')? PrimaryExpr
    /// </summary>
    private bool EvaluateUnaryExpression()
    {
        SkipWhitespace();

        if (MatchOperator("!") || MatchKeyword("NOT"))
        {
            return !EvaluateUnaryExpression();
        }

        return EvaluatePrimaryExpression();
    }

    /// <summary>
    /// PrimaryExpr := '(' Expression ')' | Comparison
    /// </summary>
    private bool EvaluatePrimaryExpression()
    {
        SkipWhitespace();

        // Parenthesized expression
        if (Peek() == '(')
        {
            Advance(); // Skip '('
            var result = EvaluateOrExpression();
            SkipWhitespace();
            if (Peek() == ')')
                Advance(); // Skip ')'
            return result;
        }

        // Comparison expression
        return EvaluateComparison();
    }

    /// <summary>
    /// Comparison := Term (ComparisonOp Term | [NOT] IN ValueList)?
    /// </summary>
    private bool EvaluateComparison()
    {
        SkipWhitespace();

        scoped var left = ParseTerm();
        SkipWhitespace();

        if (IsAtEnd() || IsLogicalOperator())
            return CoerceToBool(left);

        // Check for closing paren - means we're done with this comparison
        if (Peek() == ')')
            return CoerceToBool(left);

        // Check for NOT IN or IN
        bool negated = false;
        if (MatchKeyword("NOT"))
        {
            SkipWhitespace();
            negated = true;
        }

        if (MatchKeyword("IN"))
        {
            SkipWhitespace();
            return EvaluateInExpression(left, negated);
        }

        // If we matched NOT but not IN, this is an error - just return false
        if (negated)
            return false;

        var op = ParseComparisonOperator();
        if (op == ComparisonOperator.Unknown)
            return CoerceToBool(left);

        SkipWhitespace();

        scoped var right = ParseTerm();

        return EvaluateComparisonOp(left, op, right);
    }

    /// <summary>
    /// Evaluate IN or NOT IN expression: value [NOT] IN (v1, v2, ...)
    /// </summary>
    private bool EvaluateInExpression(scoped Value left, bool negated)
    {
        // Expect opening paren
        if (Peek() != '(')
            return false;

        Advance(); // Skip '('
        SkipWhitespace();

        bool found = false;

        // Parse value list
        while (!IsAtEnd() && Peek() != ')')
        {
            scoped var listValue = ParseTerm();
            SkipWhitespace();

            // Check if left matches this list value
            if (!found && CompareEqual(left, listValue))
            {
                found = true;
                // Continue parsing to consume the rest of the list
            }

            // Skip comma if present
            if (Peek() == ',')
            {
                Advance();
                SkipWhitespace();
            }
        }

        // Skip closing paren
        if (Peek() == ')')
            Advance();

        // IN returns true if found, NOT IN returns true if not found
        return negated ? !found : found;
    }

    /// <summary>
    /// Check if current position is at a logical operator
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsLogicalOperator()
    {
        if (IsAtEnd()) return false;

        var remaining = _expression.Length - _position;
        if (remaining >= 2)
        {
            var span = _expression.Slice(_position, 2);
            if (span[0] == '|' && span[1] == '|') return true;
            if (span[0] == '&' && span[1] == '&') return true;
        }

        if (remaining >= 3)
        {
            var span = _expression.Slice(_position, 3);
            if (span.Equals("AND", StringComparison.OrdinalIgnoreCase)) return true;
        }

        if (remaining >= 2)
        {
            var span = _expression.Slice(_position, 2);
            if (span.Equals("OR", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// Try to match a two-character operator
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MatchOperator(string op)
    {
        SkipWhitespace();
        var remaining = _expression.Length - _position;
        if (remaining < op.Length) return false;

        var span = _expression.Slice(_position, op.Length);
        if (span.SequenceEqual(op.AsSpan()))
        {
            _position += op.Length;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Try to match a keyword (case-insensitive, must be followed by non-letter)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MatchKeyword(string keyword)
    {
        SkipWhitespace();
        var remaining = _expression.Length - _position;
        if (remaining < keyword.Length) return false;

        var span = _expression.Slice(_position, keyword.Length);
        if (!span.Equals(keyword.AsSpan(), StringComparison.OrdinalIgnoreCase))
            return false;

        // Must be followed by non-letter (or end) to be a keyword
        if (_position + keyword.Length < _expression.Length)
        {
            var nextChar = _expression[_position + keyword.Length];
            if (IsLetterOrDigit(nextChar))
                return false;
        }

        _position += keyword.Length;
        return true;
    }

    private Value ParseTerm()
    {
        SkipWhitespace();

        var ch = Peek();

        // Function call
        if (IsLetter(ch))
        {
            return ParseFunctionCall();
        }

        // Variable reference
        if (ch == '?')
        {
            return ParseVariable();
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

    private Value ParseVariable()
    {
        var start = _position;
        Advance(); // Skip '?'

        while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
            Advance();

        var varName = _expression.Slice(start, _position - start);

        // Look up variable in bindings
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
        // FNV-1a hash (same as BindingTable)
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

    private Value ParseFunctionCall()
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
        var arg1 = ParseTerm();

        SkipWhitespace();
        if (Peek() == ',')
        {
            Advance();
            var arg2 = ParseTerm();
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

    private ComparisonOperator ParseComparisonOperator()
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
    private bool EvaluateComparisonOp(scoped Value left, ComparisonOperator op, scoped Value right)
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
        // Same type - direct comparison
        if (left.Type == right.Type)
        {
            return left.Type switch
            {
                ValueType.Integer => left.IntegerValue == right.IntegerValue,
                ValueType.Double => Math.Abs(left.DoubleValue - right.DoubleValue) < 1e-10,
                ValueType.Boolean => left.BooleanValue == right.BooleanValue,
                ValueType.String => left.StringValue.SequenceEqual(right.StringValue),
                _ => false
            };
        }

        // Type coercion for numeric comparisons
        // Try to coerce string to number for comparison with numeric types
        if ((left.Type == ValueType.String && (right.Type == ValueType.Integer || right.Type == ValueType.Double)) ||
            (right.Type == ValueType.String && (left.Type == ValueType.Integer || left.Type == ValueType.Double)))
        {
            var (leftNum, rightNum) = CoerceToNumbers(left, right);
            if (double.IsNaN(leftNum) || double.IsNaN(rightNum))
                return false;
            return Math.Abs(leftNum - rightNum) < 1e-10;
        }

        // Integer vs Double coercion
        if (left.Type == ValueType.Integer && right.Type == ValueType.Double)
            return Math.Abs(left.IntegerValue - right.DoubleValue) < 1e-10;
        if (left.Type == ValueType.Double && right.Type == ValueType.Integer)
            return Math.Abs(left.DoubleValue - right.IntegerValue) < 1e-10;

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CompareLess(in Value left, in Value right)
    {
        // Same type - direct comparison
        if (left.Type == right.Type)
        {
            return left.Type switch
            {
                ValueType.Integer => left.IntegerValue < right.IntegerValue,
                ValueType.Double => left.DoubleValue < right.DoubleValue,
                ValueType.String => left.StringValue.SequenceCompareTo(right.StringValue) < 0,
                _ => false
            };
        }

        // Type coercion for numeric comparisons
        // Try to coerce string to number for comparison with numeric types
        if ((left.Type == ValueType.String && (right.Type == ValueType.Integer || right.Type == ValueType.Double)) ||
            (right.Type == ValueType.String && (left.Type == ValueType.Integer || left.Type == ValueType.Double)))
        {
            var (leftNum, rightNum) = CoerceToNumbers(left, right);
            if (double.IsNaN(leftNum) || double.IsNaN(rightNum))
                return false;
            return leftNum < rightNum;
        }

        // Integer vs Double coercion
        if (left.Type == ValueType.Integer && right.Type == ValueType.Double)
            return left.IntegerValue < right.DoubleValue;
        if (left.Type == ValueType.Double && right.Type == ValueType.Integer)
            return left.DoubleValue < right.IntegerValue;

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (double Left, double Right) CoerceToNumbers(in Value left, in Value right)
    {
        double leftNum = left.Type switch
        {
            ValueType.Integer => left.IntegerValue,
            ValueType.Double => left.DoubleValue,
            ValueType.String => TryParseStringAsNumber(left.StringValue),
            _ => double.NaN
        };

        double rightNum = right.Type switch
        {
            ValueType.Integer => right.IntegerValue,
            ValueType.Double => right.DoubleValue,
            ValueType.String => TryParseStringAsNumber(right.StringValue),
            _ => double.NaN
        };

        return (leftNum, rightNum);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double TryParseStringAsNumber(ReadOnlySpan<char> str)
    {
        // Handle RDF string literals with quotes: "30" -> 30
        if (str.Length >= 2 && str[0] == '"' && str[^1] == '"')
        {
            str = str[1..^1];
        }

        if (double.TryParse(str, out var result))
            return result;

        return double.NaN;
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
