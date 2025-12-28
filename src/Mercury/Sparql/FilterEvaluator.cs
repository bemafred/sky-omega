using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

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

        while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
            Advance();

        var funcName = _expression.Slice(start, _position - start);

        SkipWhitespace();
        if (Peek() != '(')
            return new Value { Type = ValueType.Unbound };

        Advance(); // Skip '('
        SkipWhitespace();

        // Handle IF separately - needs special parsing for condition
        if (funcName.Equals("if", StringComparison.OrdinalIgnoreCase))
        {
            return ParseIfFunction();
        }

        // Handle COALESCE - variable number of arguments
        if (funcName.Equals("coalesce", StringComparison.OrdinalIgnoreCase))
        {
            return ParseCoalesceFunction();
        }

        // Handle REGEX - needs special parsing for 2-3 arguments
        if (funcName.Equals("regex", StringComparison.OrdinalIgnoreCase))
        {
            return ParseRegexFunction();
        }

        // Handle REPLACE - regex-based string replacement
        if (funcName.Equals("replace", StringComparison.OrdinalIgnoreCase))
        {
            return ParseReplaceFunction();
        }

        // Handle CONTAINS - two string arguments
        if (funcName.Equals("contains", StringComparison.OrdinalIgnoreCase))
        {
            return ParseContainsFunction();
        }

        // Handle STRSTARTS - two string arguments
        if (funcName.Equals("strstarts", StringComparison.OrdinalIgnoreCase))
        {
            return ParseStrStartsFunction();
        }

        // Handle STRENDS - two string arguments
        if (funcName.Equals("strends", StringComparison.OrdinalIgnoreCase))
        {
            return ParseStrEndsFunction();
        }

        // Handle SUBSTR - 2-3 arguments
        if (funcName.Equals("substr", StringComparison.OrdinalIgnoreCase))
        {
            return ParseSubstrFunction();
        }

        // Handle CONCAT - variable arguments
        if (funcName.Equals("concat", StringComparison.OrdinalIgnoreCase))
        {
            return ParseConcatFunction();
        }

        // Handle LANGMATCHES - two string arguments
        if (funcName.Equals("langmatches", StringComparison.OrdinalIgnoreCase))
        {
            return ParseLangMatchesFunction();
        }

        // Handle sameTerm - strict RDF term equality
        if (funcName.Equals("sameterm", StringComparison.OrdinalIgnoreCase))
        {
            return ParseSameTermFunction();
        }

        // Handle STRBEFORE - substring before delimiter
        if (funcName.Equals("strbefore", StringComparison.OrdinalIgnoreCase))
        {
            return ParseStrBeforeFunction();
        }

        // Handle STRAFTER - substring after delimiter
        if (funcName.Equals("strafter", StringComparison.OrdinalIgnoreCase))
        {
            return ParseStrAfterFunction();
        }

        // UUID - generate a fresh IRI with UUID v7
        if (funcName.Equals("uuid", StringComparison.OrdinalIgnoreCase))
        {
            SkipWhitespace();
            if (Peek() == ')')
                Advance();
            _uuidResult = $"urn:uuid:{Guid.CreateVersion7():D}";
            return new Value
            {
                Type = ValueType.Uri,
                StringValue = _uuidResult.AsSpan()
            };
        }

        // STRUUID - generate a fresh UUID v7 string
        if (funcName.Equals("struuid", StringComparison.OrdinalIgnoreCase))
        {
            SkipWhitespace();
            if (Peek() == ')')
                Advance();
            _uuidResult = Guid.CreateVersion7().ToString("D");
            return new Value
            {
                Type = ValueType.String,
                StringValue = _uuidResult.AsSpan()
            };
        }

        // NOW - return current datetime as xsd:dateTime
        if (funcName.Equals("now", StringComparison.OrdinalIgnoreCase))
        {
            SkipWhitespace();
            if (Peek() == ')')
                Advance();
            _datetimeResult = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            return new Value
            {
                Type = ValueType.String,
                StringValue = _datetimeResult.AsSpan()
            };
        }

        // Parse first argument for single-arg functions
        var arg1 = ParseTerm();

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

        if (funcName.Equals("isIRI", StringComparison.OrdinalIgnoreCase) ||
            funcName.Equals("isURI", StringComparison.OrdinalIgnoreCase))
        {
            return new Value
            {
                Type = ValueType.Boolean,
                BooleanValue = arg1.Type == ValueType.Uri
            };
        }

        if (funcName.Equals("isBlank", StringComparison.OrdinalIgnoreCase))
        {
            // Check if value is a blank node (starts with _:)
            var isBlank = arg1.Type == ValueType.Uri &&
                          arg1.StringValue.Length >= 2 &&
                          arg1.StringValue[0] == '_' &&
                          arg1.StringValue[1] == ':';
            return new Value
            {
                Type = ValueType.Boolean,
                BooleanValue = isBlank
            };
        }

        if (funcName.Equals("isLiteral", StringComparison.OrdinalIgnoreCase))
        {
            // Literals are strings, integers, doubles, booleans
            var isLiteral = arg1.Type == ValueType.String ||
                            arg1.Type == ValueType.Integer ||
                            arg1.Type == ValueType.Double ||
                            arg1.Type == ValueType.Boolean;
            return new Value
            {
                Type = ValueType.Boolean,
                BooleanValue = isLiteral
            };
        }

        if (funcName.Equals("isNumeric", StringComparison.OrdinalIgnoreCase))
        {
            var isNumeric = arg1.Type == ValueType.Integer ||
                            arg1.Type == ValueType.Double;
            return new Value
            {
                Type = ValueType.Boolean,
                BooleanValue = isNumeric
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

        // LANG - returns language tag of a literal
        if (funcName.Equals("lang", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String)
            {
                // Look for language tag pattern: "value"@lang
                var str = arg1.StringValue;
                var atIndex = str.LastIndexOf('@');
                if (atIndex > 0 && atIndex < str.Length - 1)
                {
                    // Check if it's a quoted string followed by @lang
                    // e.g., "hello"@en -> lang is "en"
                    var beforeAt = str.Slice(0, atIndex);
                    if (beforeAt.Length >= 2 && beforeAt[^1] == '"')
                    {
                        return new Value
                        {
                            Type = ValueType.String,
                            StringValue = str.Slice(atIndex + 1)
                        };
                    }
                }
                // No language tag - return empty string
                return new Value { Type = ValueType.String, StringValue = ReadOnlySpan<char>.Empty };
            }
            // Non-literals return empty string
            return new Value { Type = ValueType.String, StringValue = ReadOnlySpan<char>.Empty };
        }

        // DATATYPE - returns datatype IRI of a literal
        if (funcName.Equals("datatype", StringComparison.OrdinalIgnoreCase))
        {
            // Return XSD datatype based on value type
            ReadOnlySpan<char> datatypeIri = arg1.Type switch
            {
                ValueType.Integer => "http://www.w3.org/2001/XMLSchema#integer".AsSpan(),
                ValueType.Double => "http://www.w3.org/2001/XMLSchema#double".AsSpan(),
                ValueType.Boolean => "http://www.w3.org/2001/XMLSchema#boolean".AsSpan(),
                ValueType.String => GetStringDatatype(arg1.StringValue),
                _ => ReadOnlySpan<char>.Empty
            };
            if (datatypeIri.IsEmpty)
                return new Value { Type = ValueType.Unbound };
            return new Value { Type = ValueType.Uri, StringValue = datatypeIri };
        }

        // STRLEN - returns string length
        if (funcName.Equals("strlen", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String || arg1.Type == ValueType.Uri)
            {
                return new Value
                {
                    Type = ValueType.Integer,
                    IntegerValue = arg1.StringValue.Length
                };
            }
            return new Value { Type = ValueType.Unbound };
        }

        // UCASE - convert string to uppercase
        if (funcName.Equals("ucase", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String || arg1.Type == ValueType.Uri)
            {
                _caseResult = arg1.StringValue.ToString().ToUpperInvariant();
                return new Value
                {
                    Type = ValueType.String,
                    StringValue = _caseResult.AsSpan()
                };
            }
            return new Value { Type = ValueType.Unbound };
        }

        // LCASE - convert string to lowercase
        if (funcName.Equals("lcase", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String || arg1.Type == ValueType.Uri)
            {
                _caseResult = arg1.StringValue.ToString().ToLowerInvariant();
                return new Value
                {
                    Type = ValueType.String,
                    StringValue = _caseResult.AsSpan()
                };
            }
            return new Value { Type = ValueType.Unbound };
        }

        // ENCODE_FOR_URI - percent-encode string for use in URI
        if (funcName.Equals("encode_for_uri", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String || arg1.Type == ValueType.Uri)
            {
                _encodeResult = Uri.EscapeDataString(arg1.StringValue.ToString());
                return new Value
                {
                    Type = ValueType.String,
                    StringValue = _encodeResult.AsSpan()
                };
            }
            return new Value { Type = ValueType.Unbound };
        }

        // MD5 - compute MD5 hash
        if (funcName.Equals("md5", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String || arg1.Type == ValueType.Uri)
            {
                var bytes = Encoding.UTF8.GetBytes(arg1.StringValue.ToString());
                var hash = MD5.HashData(bytes);
                _hashResult = Convert.ToHexString(hash).ToLowerInvariant();
                return new Value
                {
                    Type = ValueType.String,
                    StringValue = _hashResult.AsSpan()
                };
            }
            return new Value { Type = ValueType.Unbound };
        }

        // SHA1 - compute SHA-1 hash
        if (funcName.Equals("sha1", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String || arg1.Type == ValueType.Uri)
            {
                var bytes = Encoding.UTF8.GetBytes(arg1.StringValue.ToString());
                var hash = SHA1.HashData(bytes);
                _hashResult = Convert.ToHexString(hash).ToLowerInvariant();
                return new Value
                {
                    Type = ValueType.String,
                    StringValue = _hashResult.AsSpan()
                };
            }
            return new Value { Type = ValueType.Unbound };
        }

        // SHA256 - compute SHA-256 hash
        if (funcName.Equals("sha256", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String || arg1.Type == ValueType.Uri)
            {
                var bytes = Encoding.UTF8.GetBytes(arg1.StringValue.ToString());
                var hash = SHA256.HashData(bytes);
                _hashResult = Convert.ToHexString(hash).ToLowerInvariant();
                return new Value
                {
                    Type = ValueType.String,
                    StringValue = _hashResult.AsSpan()
                };
            }
            return new Value { Type = ValueType.Unbound };
        }

        // SHA384 - compute SHA-384 hash
        if (funcName.Equals("sha384", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String || arg1.Type == ValueType.Uri)
            {
                var bytes = Encoding.UTF8.GetBytes(arg1.StringValue.ToString());
                var hash = SHA384.HashData(bytes);
                _hashResult = Convert.ToHexString(hash).ToLowerInvariant();
                return new Value
                {
                    Type = ValueType.String,
                    StringValue = _hashResult.AsSpan()
                };
            }
            return new Value { Type = ValueType.Unbound };
        }

        // SHA512 - compute SHA-512 hash
        if (funcName.Equals("sha512", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String || arg1.Type == ValueType.Uri)
            {
                var bytes = Encoding.UTF8.GetBytes(arg1.StringValue.ToString());
                var hash = SHA512.HashData(bytes);
                _hashResult = Convert.ToHexString(hash).ToLowerInvariant();
                return new Value
                {
                    Type = ValueType.String,
                    StringValue = _hashResult.AsSpan()
                };
            }
            return new Value { Type = ValueType.Unbound };
        }

        // YEAR - extract year from xsd:dateTime
        if (funcName.Equals("year", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String && TryParseDateTime(arg1.StringValue, out var dt))
            {
                return new Value { Type = ValueType.Integer, IntegerValue = dt.Year };
            }
            return new Value { Type = ValueType.Unbound };
        }

        // MONTH - extract month from xsd:dateTime (1-12)
        if (funcName.Equals("month", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String && TryParseDateTime(arg1.StringValue, out var dt))
            {
                return new Value { Type = ValueType.Integer, IntegerValue = dt.Month };
            }
            return new Value { Type = ValueType.Unbound };
        }

        // DAY - extract day from xsd:dateTime (1-31)
        if (funcName.Equals("day", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String && TryParseDateTime(arg1.StringValue, out var dt))
            {
                return new Value { Type = ValueType.Integer, IntegerValue = dt.Day };
            }
            return new Value { Type = ValueType.Unbound };
        }

        // HOURS - extract hours from xsd:dateTime (0-23)
        if (funcName.Equals("hours", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String && TryParseDateTime(arg1.StringValue, out var dt))
            {
                return new Value { Type = ValueType.Integer, IntegerValue = dt.Hour };
            }
            return new Value { Type = ValueType.Unbound };
        }

        // MINUTES - extract minutes from xsd:dateTime (0-59)
        if (funcName.Equals("minutes", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String && TryParseDateTime(arg1.StringValue, out var dt))
            {
                return new Value { Type = ValueType.Integer, IntegerValue = dt.Minute };
            }
            return new Value { Type = ValueType.Unbound };
        }

        // SECONDS - extract seconds from xsd:dateTime (0-59, with fractional part)
        if (funcName.Equals("seconds", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String && TryParseDateTime(arg1.StringValue, out var dt))
            {
                var seconds = dt.Second + dt.Millisecond / 1000.0;
                return new Value { Type = ValueType.Double, DoubleValue = seconds };
            }
            return new Value { Type = ValueType.Unbound };
        }

        // TZ - extract timezone string from xsd:dateTime
        if (funcName.Equals("tz", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String)
            {
                var tzStr = ExtractTimezone(arg1.StringValue);
                _datetimeResult = tzStr;
                return new Value { Type = ValueType.String, StringValue = _datetimeResult.AsSpan() };
            }
            return new Value { Type = ValueType.Unbound };
        }

        // ABS - absolute value
        if (funcName.Equals("abs", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.Integer)
            {
                return new Value
                {
                    Type = ValueType.Integer,
                    IntegerValue = Math.Abs(arg1.IntegerValue)
                };
            }
            if (arg1.Type == ValueType.Double)
            {
                return new Value
                {
                    Type = ValueType.Double,
                    DoubleValue = Math.Abs(arg1.DoubleValue)
                };
            }
            return new Value { Type = ValueType.Unbound };
        }

        // ROUND - round to nearest integer
        if (funcName.Equals("round", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.Integer)
            {
                return arg1; // Already an integer
            }
            if (arg1.Type == ValueType.Double)
            {
                return new Value
                {
                    Type = ValueType.Integer,
                    IntegerValue = (long)Math.Round(arg1.DoubleValue)
                };
            }
            return new Value { Type = ValueType.Unbound };
        }

        // CEIL - round up to nearest integer
        if (funcName.Equals("ceil", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.Integer)
            {
                return arg1; // Already an integer
            }
            if (arg1.Type == ValueType.Double)
            {
                return new Value
                {
                    Type = ValueType.Integer,
                    IntegerValue = (long)Math.Ceiling(arg1.DoubleValue)
                };
            }
            return new Value { Type = ValueType.Unbound };
        }

        // FLOOR - round down to nearest integer
        if (funcName.Equals("floor", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.Integer)
            {
                return arg1; // Already an integer
            }
            if (arg1.Type == ValueType.Double)
            {
                return new Value
                {
                    Type = ValueType.Integer,
                    IntegerValue = (long)Math.Floor(arg1.DoubleValue)
                };
            }
            return new Value { Type = ValueType.Unbound };
        }

        return new Value { Type = ValueType.Unbound };
    }

    /// <summary>
    /// Parse IF(condition, thenExpr, elseExpr)
    /// </summary>
    private Value ParseIfFunction()
    {
        // Parse condition - this is a full expression that needs boolean evaluation
        var conditionStart = _position;

        // Find the first comma by tracking parentheses depth
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

        // Evaluate the condition
        var condEvaluator = new FilterEvaluator(conditionExpr);
        var conditionResult = condEvaluator.Evaluate(_bindingData, _bindingCount, _bindingStrings);

        SkipWhitespace();
        if (Peek() == ',')
            Advance();
        SkipWhitespace();

        // Parse then expression
        var thenValue = ParseTerm();

        SkipWhitespace();
        if (Peek() == ',')
            Advance();
        SkipWhitespace();

        // Parse else expression
        var elseValue = ParseTerm();

        SkipWhitespace();
        if (Peek() == ')')
            Advance();

        // Return based on condition
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
            var value = ParseTerm();

            // If we haven't found a bound value yet and this one is bound, use it
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

        if (Peek() == ')')
            Advance();

        return result;
    }

    /// <summary>
    /// Parse REGEX(string, pattern [, flags])
    /// </summary>
    private Value ParseRegexFunction()
    {
        // Parse string argument
        var stringArg = ParseTerm();
        SkipWhitespace();

        if (Peek() != ',')
        {
            // Skip to closing paren
            while (!IsAtEnd() && Peek() != ')')
                Advance();
            if (Peek() == ')')
                Advance();
            return new Value { Type = ValueType.Boolean, BooleanValue = false };
        }

        Advance(); // Skip ','
        SkipWhitespace();

        // Parse pattern argument
        var patternArg = ParseTerm();
        SkipWhitespace();

        // Check for optional flags argument
        ReadOnlySpan<char> flags = ReadOnlySpan<char>.Empty;
        if (Peek() == ',')
        {
            Advance(); // Skip ','
            SkipWhitespace();
            var flagsArg = ParseTerm();
            if (flagsArg.Type == ValueType.String)
                flags = flagsArg.StringValue;
        }

        SkipWhitespace();
        if (Peek() == ')')
            Advance();

        // Get string value
        ReadOnlySpan<char> stringValue;
        if (stringArg.Type == ValueType.String || stringArg.Type == ValueType.Uri)
            stringValue = stringArg.StringValue;
        else if (stringArg.Type == ValueType.Unbound)
            return new Value { Type = ValueType.Boolean, BooleanValue = false };
        else
            return new Value { Type = ValueType.Boolean, BooleanValue = false };

        // Get pattern value
        ReadOnlySpan<char> pattern;
        if (patternArg.Type == ValueType.String)
            pattern = patternArg.StringValue;
        else
            return new Value { Type = ValueType.Boolean, BooleanValue = false };

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

        // Perform regex match
        try
        {
            var regex = new Regex(pattern.ToString(), options, TimeSpan.FromMilliseconds(100));
            var isMatch = regex.IsMatch(stringValue.ToString());
            return new Value { Type = ValueType.Boolean, BooleanValue = isMatch };
        }
        catch
        {
            // Invalid regex pattern
            return new Value { Type = ValueType.Boolean, BooleanValue = false };
        }
    }

    /// <summary>
    /// Parse REPLACE(string, pattern, replacement [, flags])
    /// Replaces all occurrences of regex pattern with replacement string
    /// </summary>
    private Value ParseReplaceFunction()
    {
        // Parse string argument
        var stringArg = ParseTerm();
        SkipWhitespace();

        if (Peek() != ',')
        {
            SkipToCloseParen();
            return new Value { Type = ValueType.Unbound };
        }

        Advance(); // Skip ','
        SkipWhitespace();

        // Parse pattern argument
        var patternArg = ParseTerm();
        SkipWhitespace();

        if (Peek() != ',')
        {
            SkipToCloseParen();
            return new Value { Type = ValueType.Unbound };
        }

        Advance(); // Skip ','
        SkipWhitespace();

        // Parse replacement argument
        var replacementArg = ParseTerm();
        SkipWhitespace();

        // Check for optional flags argument
        ReadOnlySpan<char> flags = ReadOnlySpan<char>.Empty;
        if (Peek() == ',')
        {
            Advance(); // Skip ','
            SkipWhitespace();
            var flagsArg = ParseTerm();
            if (flagsArg.Type == ValueType.String)
                flags = flagsArg.StringValue;
        }

        SkipWhitespace();
        if (Peek() == ')')
            Advance();

        // Validate string argument
        if (stringArg.Type != ValueType.String && stringArg.Type != ValueType.Uri)
            return new Value { Type = ValueType.Unbound };

        // Validate pattern argument
        if (patternArg.Type != ValueType.String)
            return new Value { Type = ValueType.Unbound };

        // Validate replacement argument
        if (replacementArg.Type != ValueType.String)
            return new Value { Type = ValueType.Unbound };

        var stringValue = stringArg.StringValue;
        var pattern = patternArg.StringValue;
        var replacement = replacementArg.StringValue;

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
            _replaceResult = regex.Replace(stringValue.ToString(), replacement.ToString());
            return new Value
            {
                Type = ValueType.String,
                StringValue = _replaceResult.AsSpan()
            };
        }
        catch
        {
            // Invalid regex pattern
            return new Value { Type = ValueType.Unbound };
        }
    }

    /// <summary>
    /// Parse CONTAINS(string, substring) - returns true if string contains substring
    /// </summary>
    private Value ParseContainsFunction()
    {
        var arg1 = ParseTerm();
        SkipWhitespace();

        if (Peek() != ',')
        {
            SkipToCloseParen();
            return new Value { Type = ValueType.Boolean, BooleanValue = false };
        }

        Advance(); // Skip ','
        SkipWhitespace();

        var arg2 = ParseTerm();
        SkipWhitespace();
        if (Peek() == ')')
            Advance();

        if ((arg1.Type != ValueType.String && arg1.Type != ValueType.Uri) ||
            (arg2.Type != ValueType.String && arg2.Type != ValueType.Uri))
        {
            return new Value { Type = ValueType.Boolean, BooleanValue = false };
        }

        var contains = arg1.StringValue.Contains(arg2.StringValue, StringComparison.Ordinal);
        return new Value { Type = ValueType.Boolean, BooleanValue = contains };
    }

    /// <summary>
    /// Parse STRSTARTS(string, prefix) - returns true if string starts with prefix
    /// </summary>
    private Value ParseStrStartsFunction()
    {
        var arg1 = ParseTerm();
        SkipWhitespace();

        if (Peek() != ',')
        {
            SkipToCloseParen();
            return new Value { Type = ValueType.Boolean, BooleanValue = false };
        }

        Advance(); // Skip ','
        SkipWhitespace();

        var arg2 = ParseTerm();
        SkipWhitespace();
        if (Peek() == ')')
            Advance();

        if ((arg1.Type != ValueType.String && arg1.Type != ValueType.Uri) ||
            (arg2.Type != ValueType.String && arg2.Type != ValueType.Uri))
        {
            return new Value { Type = ValueType.Boolean, BooleanValue = false };
        }

        var startsWith = arg1.StringValue.StartsWith(arg2.StringValue, StringComparison.Ordinal);
        return new Value { Type = ValueType.Boolean, BooleanValue = startsWith };
    }

    /// <summary>
    /// Parse STRENDS(string, suffix) - returns true if string ends with suffix
    /// </summary>
    private Value ParseStrEndsFunction()
    {
        var arg1 = ParseTerm();
        SkipWhitespace();

        if (Peek() != ',')
        {
            SkipToCloseParen();
            return new Value { Type = ValueType.Boolean, BooleanValue = false };
        }

        Advance(); // Skip ','
        SkipWhitespace();

        var arg2 = ParseTerm();
        SkipWhitespace();
        if (Peek() == ')')
            Advance();

        if ((arg1.Type != ValueType.String && arg1.Type != ValueType.Uri) ||
            (arg2.Type != ValueType.String && arg2.Type != ValueType.Uri))
        {
            return new Value { Type = ValueType.Boolean, BooleanValue = false };
        }

        var endsWith = arg1.StringValue.EndsWith(arg2.StringValue, StringComparison.Ordinal);
        return new Value { Type = ValueType.Boolean, BooleanValue = endsWith };
    }

    /// <summary>
    /// Parse LANGMATCHES(langTag, langRange) - returns true if language tag matches range
    /// </summary>
    private Value ParseLangMatchesFunction()
    {
        var langTagArg = ParseTerm();
        SkipWhitespace();

        if (Peek() != ',')
        {
            SkipToCloseParen();
            return new Value { Type = ValueType.Boolean, BooleanValue = false };
        }

        Advance(); // Skip ','
        SkipWhitespace();

        var langRangeArg = ParseTerm();
        SkipWhitespace();
        if (Peek() == ')')
            Advance();

        if (langTagArg.Type != ValueType.String || langRangeArg.Type != ValueType.String)
        {
            return new Value { Type = ValueType.Boolean, BooleanValue = false };
        }

        var langTag = langTagArg.StringValue;
        var langRange = langRangeArg.StringValue;

        // "*" matches any non-empty language tag
        if (langRange.Length == 1 && langRange[0] == '*')
        {
            return new Value { Type = ValueType.Boolean, BooleanValue = langTag.Length > 0 };
        }

        // Empty language tag never matches (except with *)
        if (langTag.IsEmpty)
        {
            return new Value { Type = ValueType.Boolean, BooleanValue = false };
        }

        // Case-insensitive prefix match with optional hyphen boundary
        // e.g., "en-US" matches "en", "en" matches "en", "de" doesn't match "en"
        if (langTag.Length < langRange.Length)
        {
            return new Value { Type = ValueType.Boolean, BooleanValue = false };
        }

        var tagPrefix = langTag.Slice(0, langRange.Length);
        if (!tagPrefix.Equals(langRange, StringComparison.OrdinalIgnoreCase))
        {
            return new Value { Type = ValueType.Boolean, BooleanValue = false };
        }

        // If exact length match, it's a match
        if (langTag.Length == langRange.Length)
        {
            return new Value { Type = ValueType.Boolean, BooleanValue = true };
        }

        // Otherwise, next char must be hyphen for subtag
        var matches = langTag[langRange.Length] == '-';
        return new Value { Type = ValueType.Boolean, BooleanValue = matches };
    }

    /// <summary>
    /// Parse sameTerm(term1, term2) - strict RDF term equality
    /// Unlike =, sameTerm requires exact type and value match with no coercion
    /// </summary>
    private Value ParseSameTermFunction()
    {
        var arg1 = ParseTerm();
        SkipWhitespace();

        if (Peek() != ',')
        {
            SkipToCloseParen();
            return new Value { Type = ValueType.Boolean, BooleanValue = false };
        }

        Advance(); // Skip ','
        SkipWhitespace();

        var arg2 = ParseTerm();
        SkipWhitespace();
        if (Peek() == ')')
            Advance();

        // Both must be bound
        if (arg1.Type == ValueType.Unbound || arg2.Type == ValueType.Unbound)
        {
            return new Value { Type = ValueType.Boolean, BooleanValue = false };
        }

        // Types must match exactly
        if (arg1.Type != arg2.Type)
        {
            return new Value { Type = ValueType.Boolean, BooleanValue = false };
        }

        // Values must match exactly based on type
        bool same = arg1.Type switch
        {
            ValueType.Integer => arg1.IntegerValue == arg2.IntegerValue,
            ValueType.Double => arg1.DoubleValue == arg2.DoubleValue, // Exact equality, not epsilon
            ValueType.Boolean => arg1.BooleanValue == arg2.BooleanValue,
            ValueType.String => arg1.StringValue.SequenceEqual(arg2.StringValue),
            ValueType.Uri => arg1.StringValue.SequenceEqual(arg2.StringValue),
            _ => false
        };

        return new Value { Type = ValueType.Boolean, BooleanValue = same };
    }

    /// <summary>
    /// Parse STRBEFORE(string, delimiter) - returns substring before first occurrence of delimiter
    /// Returns empty string if delimiter not found
    /// </summary>
    private Value ParseStrBeforeFunction()
    {
        var stringArg = ParseTerm();
        SkipWhitespace();

        if (Peek() != ',')
        {
            SkipToCloseParen();
            return new Value { Type = ValueType.String, StringValue = ReadOnlySpan<char>.Empty };
        }

        Advance(); // Skip ','
        SkipWhitespace();

        var delimiterArg = ParseTerm();
        SkipWhitespace();
        if (Peek() == ')')
            Advance();

        if ((stringArg.Type != ValueType.String && stringArg.Type != ValueType.Uri) ||
            (delimiterArg.Type != ValueType.String && delimiterArg.Type != ValueType.Uri))
        {
            return new Value { Type = ValueType.String, StringValue = ReadOnlySpan<char>.Empty };
        }

        var str = stringArg.StringValue;
        var delimiter = delimiterArg.StringValue;

        // Empty delimiter returns empty string
        if (delimiter.IsEmpty)
        {
            return new Value { Type = ValueType.String, StringValue = ReadOnlySpan<char>.Empty };
        }

        var index = str.IndexOf(delimiter);
        if (index < 0)
        {
            return new Value { Type = ValueType.String, StringValue = ReadOnlySpan<char>.Empty };
        }

        return new Value
        {
            Type = ValueType.String,
            StringValue = str.Slice(0, index)
        };
    }

    /// <summary>
    /// Parse STRAFTER(string, delimiter) - returns substring after first occurrence of delimiter
    /// Returns empty string if delimiter not found
    /// </summary>
    private Value ParseStrAfterFunction()
    {
        var stringArg = ParseTerm();
        SkipWhitespace();

        if (Peek() != ',')
        {
            SkipToCloseParen();
            return new Value { Type = ValueType.String, StringValue = ReadOnlySpan<char>.Empty };
        }

        Advance(); // Skip ','
        SkipWhitespace();

        var delimiterArg = ParseTerm();
        SkipWhitespace();
        if (Peek() == ')')
            Advance();

        if ((stringArg.Type != ValueType.String && stringArg.Type != ValueType.Uri) ||
            (delimiterArg.Type != ValueType.String && delimiterArg.Type != ValueType.Uri))
        {
            return new Value { Type = ValueType.String, StringValue = ReadOnlySpan<char>.Empty };
        }

        var str = stringArg.StringValue;
        var delimiter = delimiterArg.StringValue;

        // Empty delimiter returns full string (per SPARQL spec)
        if (delimiter.IsEmpty)
        {
            return new Value { Type = ValueType.String, StringValue = str };
        }

        var index = str.IndexOf(delimiter);
        if (index < 0)
        {
            return new Value { Type = ValueType.String, StringValue = ReadOnlySpan<char>.Empty };
        }

        return new Value
        {
            Type = ValueType.String,
            StringValue = str.Slice(index + delimiter.Length)
        };
    }

    /// <summary>
    /// Parse SUBSTR(string, start [, length]) - returns substring
    /// Note: SPARQL uses 1-based indexing
    /// </summary>
    private Value ParseSubstrFunction()
    {
        var stringArg = ParseTerm();
        SkipWhitespace();

        if (Peek() != ',')
        {
            SkipToCloseParen();
            return new Value { Type = ValueType.Unbound };
        }

        Advance(); // Skip ','
        SkipWhitespace();

        var startArg = ParseTerm();
        SkipWhitespace();

        // Optional length argument
        Value lengthArg = new Value { Type = ValueType.Unbound };
        if (Peek() == ',')
        {
            Advance();
            SkipWhitespace();
            lengthArg = ParseTerm();
            SkipWhitespace();
        }

        if (Peek() == ')')
            Advance();

        if (stringArg.Type != ValueType.String && stringArg.Type != ValueType.Uri)
            return new Value { Type = ValueType.Unbound };

        if (startArg.Type != ValueType.Integer)
            return new Value { Type = ValueType.Unbound };

        var str = stringArg.StringValue;
        var start = (int)startArg.IntegerValue - 1; // SPARQL is 1-based

        if (start < 0) start = 0;
        if (start >= str.Length)
            return new Value { Type = ValueType.String, StringValue = ReadOnlySpan<char>.Empty };

        int length;
        if (lengthArg.Type == ValueType.Integer)
        {
            length = (int)lengthArg.IntegerValue;
            if (length < 0) length = 0;
            if (start + length > str.Length)
                length = str.Length - start;
        }
        else
        {
            length = str.Length - start;
        }

        return new Value
        {
            Type = ValueType.String,
            StringValue = str.Slice(start, length)
        };
    }

    /// <summary>
    /// Parse CONCAT(string1, string2, ...) - concatenates strings
    /// Note: Allocates a string (exception to zero-GC for this function)
    /// </summary>
    private Value ParseConcatFunction()
    {
        // Collect string parts - this function allocates
        var parts = new System.Collections.Generic.List<string>();

        while (!IsAtEnd() && Peek() != ')')
        {
            var arg = ParseTerm();

            if (arg.Type == ValueType.Unbound)
            {
                SkipToCloseParen();
                return new Value { Type = ValueType.Unbound };
            }

            if (arg.Type == ValueType.String || arg.Type == ValueType.Uri)
                parts.Add(arg.StringValue.ToString());
            else if (arg.Type == ValueType.Integer)
                parts.Add(arg.IntegerValue.ToString());
            else if (arg.Type == ValueType.Double)
                parts.Add(arg.DoubleValue.ToString());

            SkipWhitespace();
            if (Peek() == ',')
            {
                Advance();
                SkipWhitespace();
            }
        }

        if (Peek() == ')')
            Advance();

        // Store concatenated result - allocates but keeps span valid
        _concatResult = string.Concat(parts);
        return new Value
        {
            Type = ValueType.String,
            StringValue = _concatResult.AsSpan()
        };
    }

    // Storage for CONCAT result to keep span valid
    private string _concatResult = string.Empty;

    // Storage for UCASE/LCASE result to keep span valid
    private string _caseResult = string.Empty;

    // Storage for REPLACE result to keep span valid
    private string _replaceResult = string.Empty;

    // Storage for ENCODE_FOR_URI result to keep span valid
    private string _encodeResult = string.Empty;

    // Storage for hash function results to keep span valid
    private string _hashResult = string.Empty;

    // Storage for UUID/STRUUID results to keep span valid
    private string _uuidResult = string.Empty;

    // Storage for datetime results to keep span valid
    private string _datetimeResult = string.Empty;

    // XSD namespace for datatype URIs
    private const string XsdString = "http://www.w3.org/2001/XMLSchema#string";
    private const string RdfLangString = "http://www.w3.org/1999/02/22-rdf-syntax-ns#langString";

    /// <summary>
    /// Get the datatype IRI for a string literal
    /// </summary>
    private static ReadOnlySpan<char> GetStringDatatype(ReadOnlySpan<char> str)
    {
        // Check for explicit datatype: "value"^^<datatype>
        var caretIndex = str.LastIndexOf('^');
        if (caretIndex > 0 && caretIndex < str.Length - 2 && str[caretIndex + 1] == '^')
        {
            var datatypePart = str.Slice(caretIndex + 2);
            // Remove angle brackets if present
            if (datatypePart.Length >= 2 && datatypePart[0] == '<' && datatypePart[^1] == '>')
                return datatypePart.Slice(1, datatypePart.Length - 2);
            return datatypePart;
        }

        // Check for language tag: "value"@lang -> rdf:langString
        var atIndex = str.LastIndexOf('@');
        if (atIndex > 0 && atIndex < str.Length - 1)
        {
            var beforeAt = str.Slice(0, atIndex);
            if (beforeAt.Length >= 2 && beforeAt[^1] == '"')
                return RdfLangString.AsSpan();
        }

        // Plain literal defaults to xsd:string
        return XsdString.AsSpan();
    }

    /// <summary>
    /// Try to parse an xsd:dateTime string
    /// Supports formats: yyyy-MM-ddTHH:mm:ss, yyyy-MM-ddTHH:mm:ss.fff, with optional timezone
    /// </summary>
    private static bool TryParseDateTime(ReadOnlySpan<char> str, out DateTime result)
    {
        result = default;
        if (str.IsEmpty)
            return false;

        // Try parsing with various formats
        var strValue = str.ToString();
        if (DateTime.TryParse(strValue, null, System.Globalization.DateTimeStyles.RoundtripKind, out result))
            return true;

        return false;
    }

    /// <summary>
    /// Extract timezone string from xsd:dateTime
    /// Returns "Z" for UTC, "+HH:MM" or "-HH:MM" for offsets, "" for no timezone
    /// </summary>
    private static string ExtractTimezone(ReadOnlySpan<char> str)
    {
        if (str.IsEmpty)
            return "";

        // Check for Z (UTC)
        if (str[^1] == 'Z')
            return "Z";

        // Look for +/- timezone offset at end
        // Format: +HH:MM or -HH:MM (6 chars)
        if (str.Length >= 6)
        {
            var potentialTz = str.Slice(str.Length - 6);
            if ((potentialTz[0] == '+' || potentialTz[0] == '-') && potentialTz[3] == ':')
            {
                return potentialTz.ToString();
            }
        }

        // No timezone specified
        return "";
    }

    /// <summary>
    /// Skip to closing parenthesis
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SkipToCloseParen()
    {
        int depth = 1;
        while (!IsAtEnd() && depth > 0)
        {
            var ch = Peek();
            if (ch == '(') depth++;
            else if (ch == ')') depth--;
            Advance();
        }
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
