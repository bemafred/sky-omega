using System;
using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Patterns;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// Zero-allocation FILTER expression evaluator using stack-based evaluation
/// </summary>
public ref partial struct FilterEvaluator
{
    private ReadOnlySpan<char> _expression;
    private int _position;
    // Binding data stored as fields to avoid scope issues with spans
    private ReadOnlySpan<Binding> _bindingData;
    private ReadOnlySpan<char> _bindingStrings;
    private int _bindingCount;

    // Prefix expansion support (optional)
    private PrefixMapping[]? _prefixes;
    private ReadOnlySpan<char> _source;
    private string? _expandedPrefixBuffer;

    // BIND scope depth filtering (for nested group scoping)
    // When >= 0, exclude bindings from BINDs with BindScopeDepth < this value
    private int _filterScopeDepth;

    public FilterEvaluator(ReadOnlySpan<char> expression)
    {
        _expression = expression;
        _position = 0;
        _bindingData = ReadOnlySpan<Binding>.Empty;
        _bindingStrings = ReadOnlySpan<char>.Empty;
        _bindingCount = 0;
        _prefixes = null;
        _source = ReadOnlySpan<char>.Empty;
        _expandedPrefixBuffer = null;
        _filterScopeDepth = -1; // Disabled by default
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
        _prefixes = null;
        _source = ReadOnlySpan<char>.Empty;
        return EvaluateOrExpression();
    }

    /// <summary>
    /// Evaluate FILTER expression against current bindings with prefix expansion support.
    /// </summary>
    internal bool Evaluate(ReadOnlySpan<Binding> bindings, int bindingCount, ReadOnlySpan<char> stringBuffer,
        PrefixMapping[]? prefixes, ReadOnlySpan<char> source)
    {
        _position = 0;
        _bindingData = bindings;
        _bindingCount = bindingCount;
        _bindingStrings = stringBuffer;
        _prefixes = prefixes;
        _source = source;
        _filterScopeDepth = -1; // No scope filtering
        return EvaluateOrExpression();
    }

    /// <summary>
    /// Evaluate FILTER expression against current bindings with prefix expansion and scope depth support.
    /// Bindings from BINDs with scope depth less than filterScopeDepth are excluded from lookup.
    /// This implements SPARQL semantics where variables from outer BIND don't affect nested group filters.
    /// </summary>
    internal bool Evaluate(ReadOnlySpan<Binding> bindings, int bindingCount, ReadOnlySpan<char> stringBuffer,
        PrefixMapping[]? prefixes, ReadOnlySpan<char> source, int filterScopeDepth)
    {
        _position = 0;
        _bindingData = bindings;
        _bindingCount = bindingCount;
        _bindingStrings = stringBuffer;
        _prefixes = prefixes;
        _source = source;
        _filterScopeDepth = filterScopeDepth;
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
            var startPos = _position;
            scoped var listValue = ParseTerm();
            SkipWhitespace();

            // Check if we made progress - if not, skip to next comma or closing paren
            // This handles expressions like 1/0 that ParseTerm can't parse
            if (_position == startPos || listValue.Type == ValueType.Unbound)
            {
                // Skip to next comma or closing paren (treats unparseable as error/non-match)
                while (!IsAtEnd() && Peek() != ',' && Peek() != ')')
                    Advance();
            }
            else
            {
                // Check if left matches this list value
                if (!found && CompareEqual(left, listValue))
                {
                    found = true;
                    // Continue parsing to consume the rest of the list
                }
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

        // SPARQL semantics for IN/NOT IN with errors:
        // - IN: if any value matches, return true; if error and no match, return error (treat as false)
        // - NOT IN: if any value matches, return false; if all non-error values don't match, return true
        // For simplicity, we treat errors as non-matching values
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

        // Full URI: <...>
        if (ch == '<')
        {
            return ParseFullUri();
        }

        // Function call or prefixed name (both start with letter)
        // Also handle empty prefix case (:localName) which starts with colon
        if (IsLetter(ch) || ch == ':')
        {
            return ParseFunctionOrPrefixedName();
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
            {
                // Check BIND scope depth compatibility
                // If _filterScopeDepth is set (>= 0), exclude bindings from BINDs at shallower scopes
                // A binding is from a BIND if BindScopeDepth >= 0, otherwise it's from a triple pattern (-1)
                var bindingScopeDepth = _bindingData[i].BindScopeDepth;
                if (_filterScopeDepth >= 0 && bindingScopeDepth >= 0 && bindingScopeDepth < _filterScopeDepth)
                {
                    // This binding is from a BIND at a shallower scope - skip it
                    continue;
                }
                return i;
            }
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

        // Check for typed literal suffix: ^^<datatype>
        if (_position + 1 < _expression.Length && _expression[_position] == '^' && _expression[_position + 1] == '^')
        {
            Advance(); // Skip first '^'
            Advance(); // Skip second '^'

            // Consume the datatype IRI <...>
            if (!IsAtEnd() && Peek() == '<')
            {
                var dtStart = _position;
                Advance(); // Skip '<'
                while (!IsAtEnd() && Peek() != '>')
                    Advance();
                if (!IsAtEnd())
                    Advance(); // Skip '>'

                var datatype = _expression.Slice(dtStart, _position - dtStart);

                // Parse numeric datatypes
                if (datatype.Contains("integer", StringComparison.OrdinalIgnoreCase) ||
                    datatype.Contains("int", StringComparison.OrdinalIgnoreCase) ||
                    datatype.Contains("short", StringComparison.OrdinalIgnoreCase) ||
                    datatype.Contains("byte", StringComparison.OrdinalIgnoreCase) ||
                    datatype.Contains("long", StringComparison.OrdinalIgnoreCase))
                {
                    if (long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intVal))
                    {
                        return new Value { Type = ValueType.Integer, IntegerValue = intVal };
                    }
                }

                if (datatype.Contains("decimal", StringComparison.OrdinalIgnoreCase) ||
                    datatype.Contains("double", StringComparison.OrdinalIgnoreCase) ||
                    datatype.Contains("float", StringComparison.OrdinalIgnoreCase))
                {
                    if (double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleVal))
                    {
                        return new Value { Type = ValueType.Double, DoubleValue = doubleVal };
                    }
                }

                if (datatype.Contains("boolean", StringComparison.OrdinalIgnoreCase))
                {
                    var boolVal = str.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                  str.Equals("1", StringComparison.Ordinal);
                    return new Value { Type = ValueType.Boolean, BooleanValue = boolVal };
                }
            }
        }

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
            if (double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                return new Value { Type = ValueType.Double, DoubleValue = d };
            }
        }
        else
        {
            var str = _expression.Slice(start, _position - start);
            if (long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            {
                return new Value { Type = ValueType.Integer, IntegerValue = i };
            }
        }
        
        return new Value { Type = ValueType.Unbound };
    }

    /// <summary>
    /// Parse a full URI enclosed in angle brackets: &lt;http://example.org/foo&gt;
    /// </summary>
    private Value ParseFullUri()
    {
        var start = _position;
        Advance(); // Skip '<'

        while (!IsAtEnd() && Peek() != '>')
            Advance();

        if (!IsAtEnd())
            Advance(); // Skip '>'

        var uri = _expression.Slice(start, _position - start);
        return new Value { Type = ValueType.Uri, StringValue = uri };
    }

    /// <summary>
    /// Parse either a function call (followed by '(') or a prefixed name (prefix:localName).
    /// For prefixed names, expands using the _prefixes array if available.
    /// </summary>
    private Value ParseFunctionOrPrefixedName()
    {
        var start = _position;

        // Allow letters, digits, underscore, and colon
        while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_' || Peek() == ':'))
            Advance();

        var name = _expression.Slice(start, _position - start);

        SkipWhitespace();

        // Check for boolean literals first
        if (name.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return new Value { Type = ValueType.Boolean, BooleanValue = true };
        }
        if (name.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return new Value { Type = ValueType.Boolean, BooleanValue = false };
        }

        // If followed by '(', it's a function call - use existing ParseFunctionCall logic
        if (Peek() == '(')
        {
            // Reset position and let ParseFunctionCall handle it
            _position = start;
            return ParseFunctionCall();
        }

        // Otherwise, it's a prefixed name - try to expand it
        var colonIndex = name.IndexOf(':');
        // colonIndex >= 0 handles empty prefix (e.g., :s1 where colon is at index 0)
        if (colonIndex >= 0 && _prefixes != null && !_source.IsEmpty)
        {
            // Include the colon in the prefix to match the stored format "ex:"
            var prefix = name.Slice(0, colonIndex + 1);
            var localName = name.Slice(colonIndex + 1);

            // Find matching prefix mapping
            foreach (var mapping in _prefixes)
            {
                var mappedPrefix = _source.Slice(mapping.PrefixStart, mapping.PrefixLength);
                if (mappedPrefix.SequenceEqual(prefix))
                {
                    // Found a match - construct full URI
                    var baseIri = _source.Slice(mapping.IriStart, mapping.IriLength);

                    // baseIri is like "<http://example.org/>" - strip angle brackets
                    if (baseIri.Length >= 2 && baseIri[0] == '<' && baseIri[^1] == '>')
                    {
                        baseIri = baseIri.Slice(1, baseIri.Length - 2);
                    }

                    // Build expanded URI: <baseIri + localName>
                    _expandedPrefixBuffer = $"<{baseIri}{localName}>";
                    return new Value { Type = ValueType.Uri, StringValue = _expandedPrefixBuffer.AsSpan() };
                }
            }
        }

        // No prefix match or not a prefixed name - return as-is (URI without brackets)
        return new Value { Type = ValueType.Uri, StringValue = name };
    }

    private Value ParseFunctionCall()
    {
        var start = _position;

        // Allow letters, digits, underscore, and colon (for prefixed functions like text:match)
        while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_' || Peek() == ':'))
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

        // Handle text:match - full-text search (case-insensitive contains)
        if (funcName.Equals("text:match", StringComparison.OrdinalIgnoreCase) ||
            funcName.Equals("match", StringComparison.OrdinalIgnoreCase))
        {
            return ParseTextMatchFunction();
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

        // Handle STRDT - construct typed literal
        if (funcName.Equals("strdt", StringComparison.OrdinalIgnoreCase))
        {
            return ParseStrDtFunction();
        }

        // Handle STRLANG - construct language-tagged literal
        if (funcName.Equals("strlang", StringComparison.OrdinalIgnoreCase))
        {
            return ParseStrLangFunction();
        }

        // BNODE - construct blank node (0 or 1 argument)
        if (funcName.Equals("bnode", StringComparison.OrdinalIgnoreCase))
        {
            SkipWhitespace();
            if (Peek() == ')')
            {
                // BNODE() - no argument, generate fresh blank node
                Advance();
                var counter = System.Threading.Interlocked.Increment(ref s_bnodeCounter);
                _bnodeResult = $"_:b{counter}";
                return new Value
                {
                    Type = ValueType.Uri,
                    StringValue = _bnodeResult.AsSpan()
                };
            }
            // BNODE(label) - with string argument
            var labelArg = ParseTerm();
            SkipWhitespace();
            if (Peek() == ')')
                Advance();

            if (labelArg.Type != ValueType.String)
                return new Value { Type = ValueType.Unbound };

            var label = labelArg.StringValue;
            if (label.IsEmpty)
                return new Value { Type = ValueType.Unbound };

            _bnodeResult = $"_:{label}";
            return new Value
            {
                Type = ValueType.Uri,
                StringValue = _bnodeResult.AsSpan()
            };
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
            _datetimeResult = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
            return new Value
            {
                Type = ValueType.String,
                StringValue = _datetimeResult.AsSpan()
            };
        }

        // RAND - return random double between 0.0 (inclusive) and 1.0 (exclusive)
        if (funcName.Equals("rand", StringComparison.OrdinalIgnoreCase))
        {
            SkipWhitespace();
            if (Peek() == ')')
                Advance();
            return new Value
            {
                Type = ValueType.Double,
                DoubleValue = Random.Shared.NextDouble()
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
            // Handle numeric types by converting to string representation
            if (arg1.Type == ValueType.Integer)
            {
                _strResult = arg1.IntegerValue.ToString(CultureInfo.InvariantCulture);
                return new Value { Type = ValueType.String, StringValue = _strResult.AsSpan() };
            }
            if (arg1.Type == ValueType.Double)
            {
                _strResult = arg1.DoubleValue.ToString("G", CultureInfo.InvariantCulture);
                return new Value { Type = ValueType.String, StringValue = _strResult.AsSpan() };
            }
            if (arg1.Type == ValueType.Boolean)
            {
                _strResult = arg1.BooleanValue ? "true" : "false";
                return new Value { Type = ValueType.String, StringValue = _strResult.AsSpan() };
            }

            // For URIs, strip angle brackets: <http://...> -> http://...
            if (arg1.Type == ValueType.Uri)
            {
                var strVal = arg1.StringValue;
                if (strVal.Length >= 2 && strVal[0] == '<' && strVal[^1] == '>')
                {
                    strVal = strVal.Slice(1, strVal.Length - 2);
                }
                return new Value { Type = ValueType.String, StringValue = strVal };
            }

            // For strings, extract lexical form (strip quotes and language tag/datatype)
            // "foo"@en -> foo, "bar"^^<xsd:string> -> bar
            return new Value { Type = ValueType.String, StringValue = arg1.GetLexicalForm() };
        }

        // IRI/URI - construct IRI from string
        if (funcName.Equals("iri", StringComparison.OrdinalIgnoreCase) ||
            funcName.Equals("uri", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String || arg1.Type == ValueType.Uri)
            {
                return new Value
                {
                    Type = ValueType.Uri,
                    StringValue = arg1.StringValue
                };
            }
            return new Value { Type = ValueType.Unbound };
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
                ValueType.Integer => "<http://www.w3.org/2001/XMLSchema#integer>".AsSpan(),
                ValueType.Double => "<http://www.w3.org/2001/XMLSchema#double>".AsSpan(),
                ValueType.Boolean => "<http://www.w3.org/2001/XMLSchema#boolean>".AsSpan(),
                ValueType.String => GetStringDatatypeWithBrackets(arg1.StringValue),
                _ => ReadOnlySpan<char>.Empty
            };
            if (datatypeIri.IsEmpty)
                return new Value { Type = ValueType.Unbound };
            return new Value { Type = ValueType.Uri, StringValue = datatypeIri };
        }

        // STRLEN - returns string length in Unicode code points (not UTF-16 code units)
        // Characters outside BMP (like emoji) count as 1, not 2
        if (funcName.Equals("strlen", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String || arg1.Type == ValueType.Uri)
            {
                return new Value
                {
                    Type = ValueType.Integer,
                    IntegerValue = UnicodeHelper.GetCodePointCount(arg1.GetLexicalForm())
                };
            }
            return new Value { Type = ValueType.Unbound };
        }

        // UCASE - convert string to uppercase (of lexical form), preserving language tag/datatype
        if (funcName.Equals("ucase", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String || arg1.Type == ValueType.Uri)
            {
                var lexical = arg1.GetLexicalForm().ToString().ToUpperInvariant();
                var suffix = arg1.GetLangTagOrDatatype();
                // Only add quotes if there's a suffix to preserve, otherwise return plain string
                _caseResult = suffix.IsEmpty ? lexical : $"\"{lexical}\"{suffix.ToString()}";
                return new Value
                {
                    Type = ValueType.String,
                    StringValue = _caseResult.AsSpan()
                };
            }
            return new Value { Type = ValueType.Unbound };
        }

        // LCASE - convert string to lowercase (of lexical form), preserving language tag/datatype
        if (funcName.Equals("lcase", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String || arg1.Type == ValueType.Uri)
            {
                var lexical = arg1.GetLexicalForm().ToString().ToLowerInvariant();
                var suffix = arg1.GetLangTagOrDatatype();
                // Only add quotes if there's a suffix to preserve, otherwise return plain string
                _caseResult = suffix.IsEmpty ? lexical : $"\"{lexical}\"{suffix.ToString()}";
                return new Value
                {
                    Type = ValueType.String,
                    StringValue = _caseResult.AsSpan()
                };
            }
            return new Value { Type = ValueType.Unbound };
        }

        // ENCODE_FOR_URI - percent-encode string for use in URI (of lexical form)
        if (funcName.Equals("encode_for_uri", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String || arg1.Type == ValueType.Uri)
            {
                _encodeResult = Uri.EscapeDataString(arg1.GetLexicalForm().ToString());
                return new Value
                {
                    Type = ValueType.String,
                    StringValue = _encodeResult.AsSpan()
                };
            }
            return new Value { Type = ValueType.Unbound };
        }

        // MD5 - compute MD5 hash of lexical form
        if (funcName.Equals("md5", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String || arg1.Type == ValueType.Uri)
            {
                var bytes = Encoding.UTF8.GetBytes(arg1.GetLexicalForm().ToString());
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

        // SHA1 - compute SHA-1 hash of lexical form
        if (funcName.Equals("sha1", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String || arg1.Type == ValueType.Uri)
            {
                var bytes = Encoding.UTF8.GetBytes(arg1.GetLexicalForm().ToString());
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

        // SHA256 - compute SHA-256 hash of lexical form
        if (funcName.Equals("sha256", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String || arg1.Type == ValueType.Uri)
            {
                var bytes = Encoding.UTF8.GetBytes(arg1.GetLexicalForm().ToString());
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

        // SHA384 - compute SHA-384 hash of lexical form
        if (funcName.Equals("sha384", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String || arg1.Type == ValueType.Uri)
            {
                var bytes = Encoding.UTF8.GetBytes(arg1.GetLexicalForm().ToString());
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

        // SHA512 - compute SHA-512 hash of lexical form
        if (funcName.Equals("sha512", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String || arg1.Type == ValueType.Uri)
            {
                var bytes = Encoding.UTF8.GetBytes(arg1.GetLexicalForm().ToString());
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

        // TIMEZONE - extract timezone as xsd:dayTimeDuration
        if (funcName.Equals("timezone", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.String)
            {
                var tzStr = ExtractTimezone(arg1.StringValue);
                if (string.IsNullOrEmpty(tzStr))
                {
                    // No timezone specified - return unbound per SPARQL spec
                    return new Value { Type = ValueType.Unbound };
                }
                _timezoneResult = ConvertToXsdDuration(tzStr);
                return new Value { Type = ValueType.String, StringValue = _timezoneResult.AsSpan() };
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

        // XSD type cast functions
        // xsd:integer - cast to integer
        if (funcName.Equals("xsd:integer", StringComparison.OrdinalIgnoreCase))
        {
            return CastToInteger(arg1);
        }

        // xsd:decimal - cast to decimal (represented as double internally)
        if (funcName.Equals("xsd:decimal", StringComparison.OrdinalIgnoreCase))
        {
            return CastToDecimal(arg1);
        }

        // xsd:double - cast to double
        if (funcName.Equals("xsd:double", StringComparison.OrdinalIgnoreCase))
        {
            return CastToDouble(arg1);
        }

        // xsd:float - cast to float (same as double in SPARQL)
        if (funcName.Equals("xsd:float", StringComparison.OrdinalIgnoreCase))
        {
            return CastToFloat(arg1);
        }

        // xsd:boolean - cast to boolean
        if (funcName.Equals("xsd:boolean", StringComparison.OrdinalIgnoreCase))
        {
            return CastToBoolean(arg1);
        }

        // xsd:string - cast to string
        if (funcName.Equals("xsd:string", StringComparison.OrdinalIgnoreCase))
        {
            return CastToString(arg1);
        }

        return new Value { Type = ValueType.Unbound };
    }


    // Storage for CONCAT result to keep span valid
    private string _concatResult = string.Empty;

    // Storage for UCASE/LCASE result to keep span valid
    private string _caseResult = string.Empty;

    // Storage for REPLACE result to keep span valid
    private string _replaceResult = string.Empty;

    // Storage for ENCODE_FOR_URI result to keep span valid
    private string _encodeResult = string.Empty;

    // Storage for STR() numeric conversion result to keep span valid
    private string _strResult = string.Empty;

    // Storage for hash function results to keep span valid
    private string _hashResult = string.Empty;

    // Storage for UUID/STRUUID results to keep span valid
    private string _uuidResult = string.Empty;

    // Storage for datetime results to keep span valid
    private string _datetimeResult = string.Empty;

    // Storage for STRDT result to keep span valid
    private string _strdtResult = string.Empty;

    // Storage for STRLANG result to keep span valid
    private string _strlangResult = string.Empty;
    private string _strbeforeResult = string.Empty;
    private string _strafterResult = string.Empty;
    private string _substrResult = string.Empty;

    // Storage for BNODE result to keep span valid
    private string _bnodeResult = string.Empty;

    // Static counter for generating unique blank node identifiers across all evaluations
    private static int s_bnodeCounter = 0;

    // Storage for TIMEZONE result to keep span valid
    private string _timezoneResult = string.Empty;

    // XSD namespace for datatype URIs (with angle brackets for DATATYPE function)
    private const string XsdStringBracketed = "<http://www.w3.org/2001/XMLSchema#string>";
    private const string RdfLangStringBracketed = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#langString>";

    /// <summary>
    /// Get the datatype IRI for a string literal with angle brackets.
    /// Used by DATATYPE function to match prefix-expanded URIs.
    /// </summary>
    private static ReadOnlySpan<char> GetStringDatatypeWithBrackets(ReadOnlySpan<char> str)
    {
        // Check for explicit datatype: "value"^^<datatype>
        // LastIndexOf finds position of "^^" pattern
        var caretIndex = str.LastIndexOf("^^".AsSpan());
        if (caretIndex > 0 && caretIndex < str.Length - 3)
        {
            var datatypePart = str.Slice(caretIndex + 2);
            // If already has angle brackets, return as-is
            if (datatypePart.Length >= 2 && datatypePart[0] == '<' && datatypePart[^1] == '>')
                return datatypePart;
            // Otherwise return as-is (shouldn't normally happen)
            return datatypePart;
        }

        // Check for language tag: "value"@lang -> rdf:langString
        var atIndex = str.LastIndexOf('@');
        if (atIndex > 0 && atIndex < str.Length - 1)
        {
            var beforeAt = str.Slice(0, atIndex);
            if (beforeAt.Length >= 2 && beforeAt[^1] == '"')
                return RdfLangStringBracketed.AsSpan();
        }

        // Plain literal defaults to xsd:string
        return XsdStringBracketed.AsSpan();
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

    // Storage for XSD cast results to keep span valid
    private string _castResult = string.Empty;

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
            var str = ExtractLiteralValue(arg.StringValue);

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
    private Value CastToDecimal(Value arg)
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
            var str = ExtractLiteralValue(arg.StringValue);
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
    private Value CastToDouble(Value arg)
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
            var str = ExtractLiteralValue(arg.StringValue);
            // Handle special values
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
    /// Cast a value to xsd:float.
    /// </summary>
    private Value CastToFloat(Value arg)
    {
        // Float is same as double in SPARQL (both use ValueType.Double)
        // Internal representation is identical; difference only matters for serialization
        return CastToDouble(arg);
    }

    /// <summary>
    /// Cast a value to xsd:boolean.
    /// </summary>
    private Value CastToBoolean(Value arg)
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
            var str = ExtractLiteralValue(arg.StringValue);
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
                _castResult = arg.IntegerValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return new Value { Type = ValueType.String, StringValue = _castResult.AsSpan() };

            case ValueType.Double:
                if (double.IsNaN(arg.DoubleValue))
                    return new Value { Type = ValueType.String, StringValue = "NaN".AsSpan() };
                if (double.IsPositiveInfinity(arg.DoubleValue))
                    return new Value { Type = ValueType.String, StringValue = "INF".AsSpan() };
                if (double.IsNegativeInfinity(arg.DoubleValue))
                    return new Value { Type = ValueType.String, StringValue = "-INF".AsSpan() };
                _castResult = arg.DoubleValue.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
                return new Value { Type = ValueType.String, StringValue = _castResult.AsSpan() };

            case ValueType.String:
                // Extract just the value part (strip datatype/language tag)
                var val = ExtractLiteralValue(arg.StringValue);
                _castResult = val.ToString();
                return new Value { Type = ValueType.String, StringValue = _castResult.AsSpan() };

            case ValueType.Uri:
                // Strip angle brackets if present to get just the URI string
                var uriStr = arg.StringValue;
                if (uriStr.Length >= 2 && uriStr[0] == '<' && uriStr[^1] == '>')
                    uriStr = uriStr[1..^1];
                _castResult = uriStr.ToString();
                return new Value { Type = ValueType.String, StringValue = _castResult.AsSpan() };

            default:
                return new Value { Type = ValueType.Unbound };
        }
    }

    /// <summary>
    /// Extract the value part from an RDF literal string.
    /// Handles formats: "value", "value"^^type, "value"@lang
    /// </summary>
    private static ReadOnlySpan<char> ExtractLiteralValue(ReadOnlySpan<char> str)
    {
        if (str.IsEmpty)
            return str;

        // Check if it's a quoted literal
        if (str[0] == '"')
        {
            // Find the closing quote (might have datatype or language tag after)
            for (int i = 1; i < str.Length; i++)
            {
                if (str[i] == '"')
                {
                    // Check if escaped
                    if (i > 0 && str[i - 1] == '\\')
                        continue;
                    // Return content between quotes
                    return str.Slice(1, i - 1);
                }
            }
        }

        // Not a quoted literal, return as-is
        return str;
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
                ValueType.Uri => left.StringValue.SequenceEqual(right.StringValue),
                _ => false
            };
        }

        // URI and String are comparable
        if ((left.Type == ValueType.Uri && right.Type == ValueType.String) ||
            (left.Type == ValueType.String && right.Type == ValueType.Uri))
        {
            return left.StringValue.SequenceEqual(right.StringValue);
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
                ValueType.Uri => left.StringValue.SequenceCompareTo(right.StringValue) < 0,
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

        if (double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
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

    /// <summary>
    /// Parse a string binding that may be a URI, typed RDF literal, or plain string.
    /// Detects URIs (&lt;...&gt;), typed literals ("value"^^&lt;datatype&gt;), and extracts
    /// numeric values for integer/decimal/double/float/boolean datatypes.
    /// </summary>
    public static Value ParseFromBinding(ReadOnlySpan<char> str)
    {
        // Check for URI format: <uri>
        if (str.Length >= 2 && str[0] == '<' && str[^1] == '>')
        {
            return new Value { Type = ValueType.Uri, StringValue = str };
        }

        // Check for typed literal format: "value"^^<datatype>
        if (str.Length > 2 && str[0] == '"')
        {
            var closeQuote = str.Slice(1).IndexOf('"');
            if (closeQuote > 0)
            {
                var value = str.Slice(1, closeQuote);
                var suffix = str.Slice(closeQuote + 2);

                // Check for datatype annotation
                if (suffix.StartsWith("^^"))
                {
                    var datatype = suffix.Slice(2);

                    // Check if it's a numeric datatype
                    // Also preserve the original string in StringValue for DATATYPE function
                    if (datatype.Contains("integer", StringComparison.OrdinalIgnoreCase) ||
                        datatype.Contains("int", StringComparison.OrdinalIgnoreCase) ||
                        datatype.Contains("short", StringComparison.OrdinalIgnoreCase) ||
                        datatype.Contains("byte", StringComparison.OrdinalIgnoreCase) ||
                        datatype.Contains("long", StringComparison.OrdinalIgnoreCase))
                    {
                        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intVal))
                        {
                            return new Value { Type = ValueType.Integer, IntegerValue = intVal, StringValue = str };
                        }
                    }

                    if (datatype.Contains("decimal", StringComparison.OrdinalIgnoreCase) ||
                        datatype.Contains("double", StringComparison.OrdinalIgnoreCase) ||
                        datatype.Contains("float", StringComparison.OrdinalIgnoreCase))
                    {
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleVal))
                        {
                            return new Value { Type = ValueType.Double, DoubleValue = doubleVal, StringValue = str };
                        }
                    }

                    if (datatype.Contains("boolean", StringComparison.OrdinalIgnoreCase))
                    {
                        var boolVal = value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                      value.Equals("1", StringComparison.Ordinal);
                        return new Value { Type = ValueType.Boolean, BooleanValue = boolVal, StringValue = str };
                    }

                    // For other typed literals (like xsd:dateTime, xsd:string), keep full form
                    // so DATATYPE function can extract the datatype annotation
                    return new Value { Type = ValueType.String, StringValue = str };
                }

                // Plain literal with quotes but no datatype - keep full form for LANG/DATATYPE functions
                // Hash functions will extract lexical form themselves
                return new Value { Type = ValueType.String, StringValue = str };
            }
        }

        // Not a typed literal - return as-is
        return new Value { Type = ValueType.String, StringValue = str };
    }

    /// <summary>
    /// Get the lexical form of a string literal (without enclosing quotes and without language tag).
    /// For URIs or non-literals, returns the StringValue as-is.
    /// For quoted literals like "hello" or "hello"@en, returns just hello.
    /// </summary>
    public readonly ReadOnlySpan<char> GetLexicalForm()
    {
        if (Type != ValueType.String || StringValue.Length < 2)
            return StringValue;

        // Check for quoted literal: "value" or "value"@lang or "value"^^<type>
        if (StringValue[0] == '"')
        {
            var closeQuote = StringValue.Slice(1).IndexOf('"');
            if (closeQuote >= 0)
                return StringValue.Slice(1, closeQuote);
        }

        return StringValue;
    }

    /// <summary>
    /// Get the language tag or datatype suffix from a string literal.
    /// For "hello"@en returns @en, for "hello"^^&lt;type&gt; returns ^^&lt;type&gt;.
    /// Returns empty span if no suffix.
    /// </summary>
    public readonly ReadOnlySpan<char> GetLangTagOrDatatype()
    {
        if (Type != ValueType.String || StringValue.Length < 2)
            return ReadOnlySpan<char>.Empty;

        // Check for quoted literal: "value" or "value"@lang or "value"^^<type>
        if (StringValue[0] == '"')
        {
            var closeQuote = StringValue.Slice(1).IndexOf('"');
            if (closeQuote > 0)
            {
                var afterQuote = 1 + closeQuote + 1; // position after closing quote
                if (afterQuote < StringValue.Length)
                    return StringValue.Slice(afterQuote);
            }
        }

        return ReadOnlySpan<char>.Empty;
    }
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
