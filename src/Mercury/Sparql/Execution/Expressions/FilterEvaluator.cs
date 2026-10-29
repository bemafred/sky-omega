using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using SkyOmega.Mercury.Rdf;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Patterns;

namespace SkyOmega.Mercury.Sparql.Execution.Expressions;

/// <summary>
/// The SPARQL <c>[110] Expression</c> evaluator, for FILTER, BIND and every other expression (ADR-049). It reads the
/// expression text directly and evaluates to a <see cref="Value"/>, an RDF term or an error (ADR-056).
/// </summary>
internal ref partial struct FilterEvaluator
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
    // Base IRI for resolving a relative ref in IRI()/URI() (ADR-049 step 2; empty = no base).
    private ReadOnlySpan<char> _baseIri;
    private string? _literalScratch; // ADR-044: scratch owner for canonicalized filter literals

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
        _baseIri = ReadOnlySpan<char>.Empty;
        _literalScratch = null;
        _filterScopeDepth = -1; // Disabled by default
    }

    /// <summary>
    /// Evaluate FILTER expression against current bindings: true when its effective boolean value is true (an error
    /// keeps no row, §17.2).
    /// </summary>
    public bool Evaluate(ReadOnlySpan<Binding> bindings, int bindingCount, ReadOnlySpan<char> stringBuffer)
    {
        _position = 0;
        _bindingData = bindings;
        _bindingCount = bindingCount;
        _bindingStrings = stringBuffer;
        _prefixes = null;
        _source = ReadOnlySpan<char>.Empty;
        return Value.EffectiveBoolean(ValueOr()) == Truth.True;
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
        return Value.EffectiveBoolean(ValueOr()) == Truth.True;
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
        return Value.EffectiveBoolean(ValueOr()) == Truth.True;
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
        return Value.EffectiveBoolean(ValueOr()) == Truth.True;
    }

    /// <summary>
    /// <c>left [NOT] IN (e1, e2, …)</c> (§17.4.1.9): the <c>||</c> of the <c>=</c> tests, so it is an error when no
    /// test is true and one is an error. NOT IN is its negation.
    /// </summary>
    private Truth EvaluateInExpression(scoped Value left, bool negated)
    {
        if (Peek() != '(')
            return Truth.Error;
        Advance();
        SkipWhitespace();

        var result = Truth.False;
        while (!IsAtEnd() && Peek() != ')')
        {
            var startPos = _position;
            scoped var item = ValueOr();
            SkipWhitespace();
            if (_position == startPos)
            {
                // An item the grammar can't read: an error, and the list continues after it
                while (!IsAtEnd() && Peek() != ',' && Peek() != ')')
                    Advance();
                result = Value.Or(result, Truth.Error);
            }
            else
            {
                result = Value.Or(result, Value.Equal(left, item));
            }
            if (Peek() == ',')
            {
                Advance();
                SkipWhitespace();
            }
        }
        if (Peek() == ')')
            Advance();
        return negated ? Value.Not(result) : result;
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
            return ParseFullUri();

        // Function call or prefixed name (both start with letter); the empty prefix starts with ':'
        if (IsLetter(ch) || ch == ':')
            return ParseFunctionOrPrefixedName();

        if (VariableName.IsSigil(ch))
            return ParseVariable();

        // Literal value: [156]–[159], in any of the four quote forms
        if (ch == '"' || ch == '\'')
            return ParseStringLiteral();

        // Numeric literal: [146]–[155], a leading '.' ([147]) and a sign included
        if (IsDigit(ch) || ((ch is '-' or '+' or '.') && (IsDigit(PeekNext()) || (PeekNext() == '.' && ch != '.'))))
            return ParseNumericLiteral();

        return Value.Error;
    }

    private Value ParseVariable()
    {
        var start = _position;
        Advance(); // the sigil

        while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
            Advance();

        var varName = _expression.Slice(start, _position - start);

        var index = FindBinding(varName);
        if (index < 0)
            return Value.Error;

        ref readonly var binding = ref _bindingData[index];
        return binding.Type switch
        {
            BindingValueType.Integer => Value.FromInteger(binding.IntegerValue),
            BindingValueType.Double => Value.FromDouble(binding.DoubleValue),
            BindingValueType.Boolean => Value.FromBoolean(binding.BooleanValue),
            BindingValueType.String => Value.FromTerm(_bindingStrings.Slice(binding.StringOffset, binding.StringLength)),
            BindingValueType.Uri => NonLiteralTerm(_bindingStrings.Slice(binding.StringOffset, binding.StringLength)),
            _ => Value.Error
        };
    }

    /// <summary>A binding stored as a non-literal: an IRI, or a blank node when it is written _:label.</summary>
    private static Value NonLiteralTerm(ReadOnlySpan<char> term) =>
        term.Length > 2 && term[0] == '_' && term[1] == ':' ? Value.FromBlankNode(term) : Value.FromIri(term);

    private int FindBinding(ReadOnlySpan<char> variableName)
    {
        var hash = VariableName.Hash(variableName);
        for (int i = 0; i < _bindingCount; i++)
        {
            if (_bindingData[i].VariableNameHash == hash)
            {
                // A binding from a BIND at a shallower scope than the filter's is not visible to it
                var bindingScopeDepth = _bindingData[i].BindScopeDepth;
                if (_filterScopeDepth >= 0 && bindingScopeDepth >= 0 && bindingScopeDepth < _filterScopeDepth)
                    continue;
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// [129] RDFLiteral ::= String ( LANGTAG | ( '^^' iri ) )?, the string in any of the four quote forms. The term it
    /// evaluates to keeps its lexical form (decoded, ADR-044), its tag and its datatype; a prefixed datatype is
    /// expanded through the query's prefixes, and an undeclared prefix is an error (ADR-056 D2).
    /// </summary>
    private Value ParseStringLiteral()
    {
        char quote = Peek();
        bool isLong = _position + 2 < _expression.Length && _expression[_position + 1] == quote && _expression[_position + 2] == quote;
        int quoteLength = isLong ? 3 : 1;
        _position += quoteLength;
        var start = _position;

        while (!IsAtEnd() && !(Peek() == quote && (!isLong ||
                   (_position + 2 < _expression.Length && _expression[_position + 1] == quote && _expression[_position + 2] == quote))))
        {
            if (Peek() == '\\')
                Advance(); // Skip escape
            Advance();
        }

        var lexical = LiteralForm.CanonicalizeContent(_expression.Slice(start, _position - start), out _literalScratch);

        if (!IsAtEnd())
            _position += quoteLength; // the closing quote(s)

        // [145] LANGTAG ::= '@' [a-zA-Z]+ ('-' [a-zA-Z0-9]+)*
        if (!IsAtEnd() && Peek() == '@')
        {
            Advance();
            var langStart = _position;
            while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '-'))
                Advance();
            return Value.FromLangString(lexical, _expression.Slice(langStart, _position - langStart));
        }

        if (_position + 1 < _expression.Length && _expression[_position] == '^' && _expression[_position + 1] == '^')
        {
            _position += 2;
            if (!IsAtEnd() && Peek() == '<')
            {
                var iriStart = _position;
                while (!IsAtEnd() && Peek() != '>')
                    Advance();
                if (!IsAtEnd())
                    Advance();
                return Value.Literal(lexical, default, _expression.Slice(iriStart, _position - iriStart));
            }
            var nameStart = _position;
            while (!IsAtEnd() && CodePointOps.IsPrefixedNameLocalChar(_expression, _position))
                Advance();
            var datatype = ExpandPrefixedName(_expression.Slice(nameStart, _position - nameStart));
            return datatype == null ? Value.Error : Value.Literal(lexical, default, datatype);
        }

        return Value.FromString(lexical);
    }

    /// <summary>
    /// The IRI a prefixed name stands for, as <c>&lt;iri&gt;</c>, through the query's prefixes; null when its prefix
    /// isn't declared.
    /// </summary>
    private readonly string? ExpandPrefixedName(ReadOnlySpan<char> name)
    {
        var colon = name.IndexOf(':');
        if (colon < 0 || _prefixes == null || _source.IsEmpty)
            return null;
        var prefix = name[..(colon + 1)]; // stored with its colon: "ex:"
        foreach (var mapping in _prefixes)
        {
            if (!_source.Slice(mapping.PrefixStart, mapping.PrefixLength).SequenceEqual(prefix))
                continue;
            var namespaceIri = Value.IriText(_source.Slice(mapping.IriStart, mapping.IriLength));
            return string.Concat("<", namespaceIri, name[(colon + 1)..], ">");
        }
        return null;
    }

    /// <summary>
    /// [130] NumericLiteral, with its sign ([149]–[154]) and a leading '.' ([147]): the term it abbreviates, its
    /// lexical form exactly as written. A '.' belongs to the number only when a digit follows it, and an exponent only
    /// when digits follow it.
    /// </summary>
    private Value ParseNumericLiteral()
    {
        var start = _position;
        if (Peek() is '-' or '+')
            Advance();
        while (!IsAtEnd() && IsDigit(Peek()))
            Advance();
        if (Peek() == '.' && IsDigit(PeekNext()))
        {
            Advance();
            while (!IsAtEnd() && IsDigit(Peek()))
                Advance();
        }
        if (Peek() is 'e' or 'E')
        {
            int at = _position + 1;
            if (at < _expression.Length && _expression[at] is '+' or '-')
                at++;
            if (at < _expression.Length && IsDigit(_expression[at]))
            {
                _position = at;
                while (!IsAtEnd() && IsDigit(Peek()))
                    Advance();
            }
        }
        var token = _expression.Slice(start, _position - start);
        return Value.IsNumericToken(token, out _, out _) ? Value.FromNumericToken(token) : Value.Error;
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

        return Value.FromIri(_expression.Slice(start, _position - start));
    }

    /// <summary>
    /// Either a function call (followed by '(') or a prefixed name, expanded through the query's prefixes.
    /// </summary>
    private Value ParseFunctionOrPrefixedName()
    {
        var start = _position;

        // A prefixed name's local part is PN_LOCAL — letters, digits, '_', ':', '-', and internal '.' — so a
        // hyphenated name like ck:obs-graph-limit-pushdown is one token, not the arithmetic ck:obs - graph - …
        // (a function name stops at '(' either way, since '(' is not a local-name char).
        while (!IsAtEnd() && CodePointOps.IsPrefixedNameLocalChar(_expression, _position))
            Advance();

        var name = _expression.Slice(start, _position - start);

        SkipWhitespace();

        // [134] BooleanLiteral
        if (name.Equals("true", StringComparison.OrdinalIgnoreCase))
            return Value.FromBoolean(true);
        if (name.Equals("false", StringComparison.OrdinalIgnoreCase))
            return Value.FromBoolean(false);

        if (Peek() == '(')
        {
            _position = start;
            return ParseFunctionCall();
        }

        var expanded = ExpandPrefixedName(name);
        // An undeclared prefix: the name as written (an IRI that matches nothing)
        return Value.FromIri(expanded != null ? expanded.AsSpan() : name);
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
            return Value.Error;

        Advance(); // Skip '('
        SkipWhitespace();

        if (funcName.Equals("if", StringComparison.OrdinalIgnoreCase))
            return ParseIfFunction();
        if (funcName.Equals("coalesce", StringComparison.OrdinalIgnoreCase))
            return ParseCoalesceFunction();
        if (funcName.Equals("regex", StringComparison.OrdinalIgnoreCase))
            return ParseRegexFunction();
        if (funcName.Equals("replace", StringComparison.OrdinalIgnoreCase))
            return ParseReplaceFunction();
        if (funcName.Equals("contains", StringComparison.OrdinalIgnoreCase))
            return ParseContainsFunction();
        if (funcName.Equals("text:match", StringComparison.OrdinalIgnoreCase) ||
            funcName.Equals("match", StringComparison.OrdinalIgnoreCase))
            return ParseTextMatchFunction();
        if (funcName.Equals("strstarts", StringComparison.OrdinalIgnoreCase))
            return ParseStrStartsFunction();
        if (funcName.Equals("strends", StringComparison.OrdinalIgnoreCase))
            return ParseStrEndsFunction();
        if (funcName.Equals("substr", StringComparison.OrdinalIgnoreCase))
            return ParseSubstrFunction();
        if (funcName.Equals("concat", StringComparison.OrdinalIgnoreCase))
            return ParseConcatFunction();
        if (funcName.Equals("langmatches", StringComparison.OrdinalIgnoreCase))
            return ParseLangMatchesFunction();
        if (funcName.Equals("sameterm", StringComparison.OrdinalIgnoreCase))
            return ParseSameTermFunction();
        if (funcName.Equals("strbefore", StringComparison.OrdinalIgnoreCase))
            return ParseStrBeforeFunction();
        if (funcName.Equals("strafter", StringComparison.OrdinalIgnoreCase))
            return ParseStrAfterFunction();
        if (funcName.Equals("strdt", StringComparison.OrdinalIgnoreCase))
            return ParseStrDtFunction();
        if (funcName.Equals("strlang", StringComparison.OrdinalIgnoreCase))
            return ParseStrLangFunction();

        // BNODE - construct blank node (0 or 1 argument)
        if (funcName.Equals("bnode", StringComparison.OrdinalIgnoreCase))
        {
            SkipWhitespace();
            if (Peek() == ')')
            {
                Advance();
                var counter = System.Threading.Interlocked.Increment(ref s_bnodeCounter);
                return Value.FromBlankNode(Rdf.BlankNodeScope.Process.Label($"b{counter}"));
            }
            var labelArg = ValueOr();
            SkipWhitespace();
            if (Peek() == ')')
                Advance();
            if (labelArg.Type != ValueType.String) // a simple literal or xsd:string (§17.4.2.9)
                return Value.Error;

            // Per-row seed: same label -> same bnode within a row, distinct across rows (SPARQL §17.4.2.9).
            // The string's 64-bit FNV-1a hash names the node: any string is a valid label this way
            ulong hash = 14695981039346656037UL;
            foreach (var ch in labelArg.StringValue)
                hash = (hash ^ ch) * 1099511628211UL;
            return Value.FromBlankNode(Rdf.BlankNodeScope.Process.Label(
                $"r{s_bnodeRowSeed}h{hash.ToString("x16", CultureInfo.InvariantCulture)}"));
        }

        // UUID - a fresh IRI with a UUID v7
        if (funcName.Equals("uuid", StringComparison.OrdinalIgnoreCase))
        {
            SkipWhitespace();
            if (Peek() == ')')
                Advance();
            return Value.FromIri($"<urn:uuid:{Guid.CreateVersion7():D}>");
        }

        // STRUUID - a fresh UUID v7, as a simple literal
        if (funcName.Equals("struuid", StringComparison.OrdinalIgnoreCase))
        {
            SkipWhitespace();
            if (Peek() == ')')
                Advance();
            return Value.FromString(Guid.CreateVersion7().ToString("D"));
        }

        // NOW - the current dateTime
        if (funcName.Equals("now", StringComparison.OrdinalIgnoreCase))
        {
            SkipWhitespace();
            if (Peek() == ')')
                Advance();
            return Value.FromTyped(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), XsdDatatype.DateTime);
        }

        // RAND - a double in [0, 1)
        if (funcName.Equals("rand", StringComparison.OrdinalIgnoreCase))
        {
            SkipWhitespace();
            if (Peek() == ')')
                Advance();
            return Value.FromDouble(Random.Shared.NextDouble());
        }

        // Parse first argument for single-arg functions
        var arg1 = ValueOr();

        SkipWhitespace();
        if (Peek() == ')')
            Advance();

        if (funcName.Equals("bound", StringComparison.OrdinalIgnoreCase))
            return Value.FromBoolean(arg1.Type != ValueType.Unbound);

        if (funcName.Equals("isIRI", StringComparison.OrdinalIgnoreCase) ||
            funcName.Equals("isURI", StringComparison.OrdinalIgnoreCase))
            return arg1.Type == ValueType.Unbound ? Value.Error : Value.FromBoolean(arg1.Type == ValueType.Uri);

        if (funcName.Equals("isBlank", StringComparison.OrdinalIgnoreCase))
            return arg1.Type == ValueType.Unbound ? Value.Error : Value.FromBoolean(arg1.Type == ValueType.BlankNode);

        if (funcName.Equals("isLiteral", StringComparison.OrdinalIgnoreCase))
            return arg1.Type == ValueType.Unbound ? Value.Error : Value.FromBoolean(arg1.IsLiteral);

        if (funcName.Equals("isNumeric", StringComparison.OrdinalIgnoreCase))
            return arg1.Type == ValueType.Unbound ? Value.Error : Value.FromBoolean(arg1.IsNumeric);

        // STR (§17.4.2.5): a literal's lexical form, an IRI's text; a blank node is an error
        if (funcName.Equals("str", StringComparison.OrdinalIgnoreCase))
        {
            if (arg1.Type == ValueType.Uri)
                return Value.FromString(Value.IriText(arg1.StringValue));
            if (!arg1.IsLiteral)
                return Value.Error;
            if (!arg1.StringValue.IsEmpty || arg1.Type is ValueType.String or ValueType.LangString or ValueType.Typed)
                return Value.FromString(arg1.StringValue);
            Span<char> number = stackalloc char[64];
            return Value.FromString(arg1.Lexical(number).ToString());
        }

        // IRI/URI - an IRI from a string or an IRI; a relative reference resolves against the base
        if (funcName.Equals("iri", StringComparison.OrdinalIgnoreCase) ||
            funcName.Equals("uri", StringComparison.OrdinalIgnoreCase))
        {
            if (!arg1.HasText)
                return Value.Error;
            var rel = arg1.Type == ValueType.Uri ? Value.IriText(arg1.StringValue) : arg1.StringValue;
            var baseStr = Value.IriText(_baseIri);
            var absolute = rel.Contains("://".AsSpan(), StringComparison.Ordinal);
            return Value.FromIri(absolute || baseStr.IsEmpty
                ? string.Concat("<", rel, ">")
                : string.Concat("<", baseStr, rel, ">"));
        }

        // LANG (§17.4.2.6): a literal's tag, "" when it has none; an error for anything but a literal
        if (funcName.Equals("lang", StringComparison.OrdinalIgnoreCase))
            return arg1.IsLiteral ? Value.FromString(arg1.Language) : Value.Error;

        // DATATYPE (§17.4.2.7): a literal's datatype IRI; an error for anything but a literal
        if (funcName.Equals("datatype", StringComparison.OrdinalIgnoreCase))
        {
            if (!arg1.IsLiteral || arg1.DatatypeIri.IsEmpty)
                return Value.Error;
            return Value.FromIri(arg1.DatatypeIri);
        }

        // STRLEN - length in Unicode code points
        if (funcName.Equals("strlen", StringComparison.OrdinalIgnoreCase))
            return arg1.IsStringLiteral
                ? Value.FromInteger(CodePointOps.GetCodePointCount(TextOf(arg1)))
                : Value.Error;

        // UCASE / LCASE - the lexical form in upper / lower case, the tag and datatype kept
        if (funcName.Equals("ucase", StringComparison.OrdinalIgnoreCase))
            return arg1.IsStringLiteral ? WithText(arg1, TextOf(arg1).ToString().ToUpperInvariant()) : Value.Error;
        if (funcName.Equals("lcase", StringComparison.OrdinalIgnoreCase))
            return arg1.IsStringLiteral ? WithText(arg1, TextOf(arg1).ToString().ToLowerInvariant()) : Value.Error;

        if (funcName.Equals("encode_for_uri", StringComparison.OrdinalIgnoreCase))
            return arg1.IsStringLiteral ? Value.FromString(Uri.EscapeDataString(TextOf(arg1).ToString())) : Value.Error;

        // MD5 / SHA1 / SHA256 / SHA384 / SHA512 of a simple literal or xsd:string (§17.4.6), as lower-case hex
        if (funcName.Equals("md5", StringComparison.OrdinalIgnoreCase))
            return arg1.Type == ValueType.String ? Value.FromString(Hex(MD5.HashData(Encoding.UTF8.GetBytes(TextOf(arg1).ToString())))) : Value.Error;
        if (funcName.Equals("sha1", StringComparison.OrdinalIgnoreCase))
            return arg1.Type == ValueType.String ? Value.FromString(Hex(SHA1.HashData(Encoding.UTF8.GetBytes(TextOf(arg1).ToString())))) : Value.Error;
        if (funcName.Equals("sha256", StringComparison.OrdinalIgnoreCase))
            return arg1.Type == ValueType.String ? Value.FromString(Hex(SHA256.HashData(Encoding.UTF8.GetBytes(TextOf(arg1).ToString())))) : Value.Error;
        if (funcName.Equals("sha384", StringComparison.OrdinalIgnoreCase))
            return arg1.Type == ValueType.String ? Value.FromString(Hex(SHA384.HashData(Encoding.UTF8.GetBytes(TextOf(arg1).ToString())))) : Value.Error;
        if (funcName.Equals("sha512", StringComparison.OrdinalIgnoreCase))
            return arg1.Type == ValueType.String ? Value.FromString(Hex(SHA512.HashData(Encoding.UTF8.GetBytes(TextOf(arg1).ToString())))) : Value.Error;

        // YEAR/MONTH/DAY/HOURS/MINUTES/SECONDS — the components of the lexical form as written (§17.4.5: no timezone
        // conversion, which a DateTime parse would apply)
        if (funcName.Equals("year", StringComparison.OrdinalIgnoreCase))
            return arg1.HasText && TryParseDateTime(arg1.StringValue, out var year, out _, out _, out _, out _, out _)
                ? Value.FromInteger(year) : Value.Error;
        if (funcName.Equals("month", StringComparison.OrdinalIgnoreCase))
            return arg1.HasText && TryParseDateTime(arg1.StringValue, out _, out var month, out _, out _, out _, out _)
                ? Value.FromInteger(month) : Value.Error;
        if (funcName.Equals("day", StringComparison.OrdinalIgnoreCase))
            return arg1.HasText && TryParseDateTime(arg1.StringValue, out _, out _, out var day, out _, out _, out _)
                ? Value.FromInteger(day) : Value.Error;
        if (funcName.Equals("hours", StringComparison.OrdinalIgnoreCase))
            return arg1.HasText && TryParseDateTime(arg1.StringValue, out _, out _, out _, out var hour, out _, out _)
                ? Value.FromInteger(hour) : Value.Error;
        if (funcName.Equals("minutes", StringComparison.OrdinalIgnoreCase))
            return arg1.HasText && TryParseDateTime(arg1.StringValue, out _, out _, out _, out _, out var minute, out _)
                ? Value.FromInteger(minute) : Value.Error;
        // SECONDS is an xsd:decimal (§17.4.5.7)
        if (funcName.Equals("seconds", StringComparison.OrdinalIgnoreCase))
            return arg1.HasText && TryParseDateTime(arg1.StringValue, out _, out _, out _, out _, out _, out var second)
                ? Value.FromDecimal(second) : Value.Error;

        // TZ - the timezone as written, a simple literal
        if (funcName.Equals("tz", StringComparison.OrdinalIgnoreCase))
            return arg1.HasText ? Value.FromString(ExtractTimezone(arg1.StringValue)) : Value.Error;

        // TIMEZONE - the timezone as an xsd:dayTimeDuration; an error when there is none
        if (funcName.Equals("timezone", StringComparison.OrdinalIgnoreCase))
        {
            if (!arg1.HasText)
                return Value.Error;
            var tzStr = ExtractTimezone(arg1.StringValue);
            return string.IsNullOrEmpty(tzStr)
                ? Value.Error
                : Value.FromTyped(ConvertToXsdDuration(tzStr), XsdDatatype.DayTimeDuration);
        }

        // ABS / ROUND / CEIL / FLOOR, in the operand's type (XPath fn:abs, fn:round, fn:ceiling, fn:floor)
        if (funcName.Equals("abs", StringComparison.OrdinalIgnoreCase))
            return arg1.Type switch
            {
                ValueType.Integer when arg1.IntegerValue != long.MinValue => Value.FromInteger(Math.Abs(arg1.IntegerValue)),
                ValueType.Integer => Value.FromDecimal(-(decimal)arg1.IntegerValue),
                ValueType.Decimal => Value.FromDecimal(Math.Abs(arg1.DecimalValue)),
                ValueType.Float => Value.FromFloat(Math.Abs(arg1.DoubleValue)),
                ValueType.Double => Value.FromDouble(Math.Abs(arg1.DoubleValue)),
                _ => Value.Error,
            };
        // fn:round rounds a half towards positive infinity
        if (funcName.Equals("round", StringComparison.OrdinalIgnoreCase))
            return arg1.Type switch
            {
                ValueType.Integer => Value.FromInteger(arg1.IntegerValue),
                ValueType.Decimal => Value.FromDecimal(Math.Floor(arg1.DecimalValue + 0.5m)),
                ValueType.Float => Value.FromFloat(Math.Floor(arg1.DoubleValue + 0.5)),
                ValueType.Double => Value.FromDouble(Math.Floor(arg1.DoubleValue + 0.5)),
                _ => Value.Error,
            };
        if (funcName.Equals("ceil", StringComparison.OrdinalIgnoreCase))
            return arg1.Type switch
            {
                ValueType.Integer => Value.FromInteger(arg1.IntegerValue),
                ValueType.Decimal => Value.FromDecimal(Math.Ceiling(arg1.DecimalValue)),
                ValueType.Float => Value.FromFloat(Math.Ceiling(arg1.DoubleValue)),
                ValueType.Double => Value.FromDouble(Math.Ceiling(arg1.DoubleValue)),
                _ => Value.Error,
            };
        if (funcName.Equals("floor", StringComparison.OrdinalIgnoreCase))
            return arg1.Type switch
            {
                ValueType.Integer => Value.FromInteger(arg1.IntegerValue),
                ValueType.Decimal => Value.FromDecimal(Math.Floor(arg1.DecimalValue)),
                ValueType.Float => Value.FromFloat(Math.Floor(arg1.DoubleValue)),
                ValueType.Double => Value.FromDouble(Math.Floor(arg1.DoubleValue)),
                _ => Value.Error,
            };

        // XSD casts (§17.5)
        if (funcName.Equals("xsd:integer", StringComparison.OrdinalIgnoreCase))
            return CastToInteger(arg1);
        if (funcName.Equals("xsd:decimal", StringComparison.OrdinalIgnoreCase))
            return CastToDecimal(arg1);
        if (funcName.Equals("xsd:double", StringComparison.OrdinalIgnoreCase))
            return CastToDouble(arg1, isFloat: false);
        if (funcName.Equals("xsd:float", StringComparison.OrdinalIgnoreCase))
            return CastToDouble(arg1, isFloat: true);
        if (funcName.Equals("xsd:boolean", StringComparison.OrdinalIgnoreCase))
            return CastToBoolean(arg1);
        if (funcName.Equals("xsd:string", StringComparison.OrdinalIgnoreCase))
            return CastToString(arg1);
        if (funcName.Equals("xsd:dateTime", StringComparison.OrdinalIgnoreCase))
            return CastToDateTime(arg1);

        return Value.Error;
    }

    /// <summary>The text a string function reads: a literal's lexical form, an IRI's text.</summary>
    private static ReadOnlySpan<char> TextOf(scoped in Value v) =>
        v.Type == ValueType.Uri ? Value.IriText(v.StringValue) : v.StringValue;

    /// <summary>A string function's result: <paramref name="text"/>, with the argument's tag or datatype (an IRI's
    /// text gives a simple literal).</summary>
    private static Value WithText(scoped in Value arg, ReadOnlySpan<char> text) => arg.Type switch
    {
        ValueType.LangString => Value.FromLangString(text, arg.Language),
        ValueType.Typed => Value.FromTyped(text, arg.Datatype),
        ValueType.String => new Value { Type = ValueType.String, StringValue = text, Datatype = arg.Datatype },
        _ => Value.FromString(text),
    };

    private static string Hex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

    // Static counter for generating unique blank node identifiers across all evaluations
    private static int s_bnodeCounter = 0;

    // BNODE() mints in the process's blank-node scope (Rdf.BlankNodeScope.Process): the counters restart in every
    // process, and its token does not, so a stored label is never minted again

    // Per-row seed so BNODE(label) yields the same blank node within a row but a
    // distinct one across rows (SPARQL §17.4.2.9). Bumped per result row by the
    // BIND consumer via IncrementBnodeRowSeed().
    private static int s_bnodeRowSeed = 0;

    /// <summary>Advance the per-row BNODE seed (called once per result row by the BIND pipeline).</summary>
    public static void IncrementBnodeRowSeed() => System.Threading.Interlocked.Increment(ref s_bnodeRowSeed);

    /// <summary>
    /// The components of an xsd:dateTime lexical form, read directly (no timezone conversion) per SPARQL §17.4.5.
    /// </summary>
    private static bool TryParseDateTime(ReadOnlySpan<char> parseSpan, out int year, out int month, out int day,
        out int hour, out int minute, out decimal second)
    {
        year = month = day = hour = minute = 0;
        second = 0;

        // Format: yyyy-MM-ddTHH:mm:ss[.fff][Z|+HH:MM|-HH:MM] — minimum 19 chars.
        if (parseSpan.Length < 19)
            return false;
        if (parseSpan[4] != '-' || parseSpan[7] != '-' || parseSpan[10] != 'T' ||
            parseSpan[13] != ':' || parseSpan[16] != ':')
            return false;

        if (!int.TryParse(parseSpan.Slice(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out year)) return false;
        if (!int.TryParse(parseSpan.Slice(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out month)) return false;
        if (!int.TryParse(parseSpan.Slice(8, 2), NumberStyles.None, CultureInfo.InvariantCulture, out day)) return false;
        if (!int.TryParse(parseSpan.Slice(11, 2), NumberStyles.None, CultureInfo.InvariantCulture, out hour)) return false;
        if (!int.TryParse(parseSpan.Slice(14, 2), NumberStyles.None, CultureInfo.InvariantCulture, out minute)) return false;

        // Seconds, including any fractional part.
        var secondsEnd = 19;
        for (int i = 19; i < parseSpan.Length; i++)
        {
            var c = parseSpan[i];
            if (c == '.' || IsDigit(c)) secondsEnd = i + 1;
            else break;
        }
        return decimal.TryParse(parseSpan.Slice(17, secondsEnd - 17),
            NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out second);
    }

    /// <summary>
    /// The timezone of an xsd:dateTime as written: "Z", "+HH:MM" or "-HH:MM"; "" when there is none.
    /// </summary>
    private static string ExtractTimezone(ReadOnlySpan<char> str)
    {
        if (str.IsEmpty)
            return "";
        if (str[^1] == 'Z')
            return "Z";
        if (str.Length >= 6)
        {
            var potentialTz = str.Slice(str.Length - 6);
            if ((potentialTz[0] == '+' || potentialTz[0] == '-') && potentialTz[3] == ':')
                return potentialTz.ToString();
        }
        return "";
    }

    /// <summary>
    /// A timezone as an xsd:dayTimeDuration: "Z" → "PT0S", "+05:30" → "PT5H30M", "-08:00" → "-PT8H".
    /// </summary>
    private static string ConvertToXsdDuration(string tz)
    {
        if (tz == "Z" || tz.Length != 6)
            return "PT0S";
        var sign = tz[0];
        if (sign != '+' && sign != '-')
            return "PT0S";
        if (!int.TryParse(tz.AsSpan(1, 2), out var hours) || !int.TryParse(tz.AsSpan(4, 2), out var minutes))
            return "PT0S";
        if (hours == 0 && minutes == 0)
            return "PT0S";

        var result = new StringBuilder();
        if (sign == '-')
            result.Append('-');
        result.Append("PT");
        if (hours > 0)
            result.Append(hours.ToString(CultureInfo.InvariantCulture)).Append('H');
        if (minutes > 0)
            result.Append(minutes.ToString(CultureInfo.InvariantCulture)).Append('M');
        return result.ToString();
    }

    // ── XSD casts (SPARQL 1.1 §17.5, XPath casting). A string casts by the target type's lexical rule; a cast with no
    //    result is an error. ──

    private static Value CastToInteger(scoped in Value arg)
    {
        switch (arg.Type)
        {
            case ValueType.Boolean:
                return Value.FromInteger(arg.BooleanValue ? 1 : 0);
            case ValueType.Integer:
                return Value.FromInteger(arg.IntegerValue);
            case ValueType.Decimal:
                var truncated = decimal.Truncate(arg.DecimalValue);
                return truncated >= long.MinValue && truncated <= long.MaxValue
                    ? Value.FromInteger((long)truncated) : Value.Error;
            case ValueType.Float:
            case ValueType.Double:
                var d = Math.Truncate(arg.DoubleValue);
                return double.IsFinite(d) && d >= long.MinValue && d <= long.MaxValue
                    ? Value.FromInteger((long)d) : Value.Error;
            case ValueType.String:
                var parsed = Value.Literal(arg.StringValue.Trim(), default, XsdDatatype.Integer);
                return parsed.Type == ValueType.Integer ? Value.FromInteger(parsed.IntegerValue) : Value.Error;
            default:
                return Value.Error;
        }
    }

    private static Value CastToDecimal(scoped in Value arg)
    {
        switch (arg.Type)
        {
            case ValueType.Boolean:
                return Value.FromDecimal(arg.BooleanValue ? 1 : 0);
            case ValueType.Integer:
            case ValueType.Decimal:
                return Value.FromDecimal(arg.AsDecimal);
            case ValueType.Float:
            case ValueType.Double:
                if (!double.IsFinite(arg.DoubleValue))
                    return Value.Error;
                try { return Value.FromDecimal((decimal)arg.DoubleValue); }
                catch (OverflowException) { return Value.Error; }
            case ValueType.String:
                var parsed = Value.Literal(arg.StringValue.Trim(), default, XsdDatatype.Decimal);
                return parsed.Type == ValueType.Decimal ? Value.FromDecimal(parsed.DecimalValue) : Value.Error;
            default:
                return Value.Error;
        }
    }

    private static Value CastToDouble(scoped in Value arg, bool isFloat)
    {
        double result;
        switch (arg.Type)
        {
            case ValueType.Boolean:
                result = arg.BooleanValue ? 1 : 0;
                break;
            case ValueType.Integer:
            case ValueType.Decimal:
            case ValueType.Float:
            case ValueType.Double:
                result = arg.AsDouble;
                break;
            case ValueType.String:
                var parsed = Value.Literal(arg.StringValue.Trim(), default, XsdDatatype.Double);
                if (parsed.Type != ValueType.Double)
                    return Value.Error;
                result = parsed.DoubleValue;
                break;
            default:
                return Value.Error;
        }
        return isFloat ? Value.FromFloat(result) : Value.FromDouble(result);
    }

    private static Value CastToBoolean(scoped in Value arg)
    {
        switch (arg.Type)
        {
            case ValueType.Boolean:
                return Value.FromBoolean(arg.BooleanValue);
            case ValueType.Integer:
            case ValueType.Decimal:
            case ValueType.Float:
            case ValueType.Double:
                return Value.FromBoolean(Value.EffectiveBoolean(arg) == Truth.True);
            case ValueType.String:
                var parsed = Value.Literal(arg.StringValue.Trim(), default, XsdDatatype.Boolean);
                return parsed.Type == ValueType.Boolean ? Value.FromBoolean(parsed.BooleanValue) : Value.Error;
            default:
                return Value.Error;
        }
    }

    /// <summary>
    /// xsd:string(x): an IRI's text, or a literal's value as XPath casts it to a string (XPath F&amp;O §19.1.2): a number
    /// or boolean is written in its canonical form, so <c>"1.0"^^xsd:decimal</c> gives "1" and <c>"0"^^xsd:boolean</c>
    /// gives "false"; a double between 1E-6 and 1E6 is written as a decimal; any other literal gives its lexical form.
    /// </summary>
    private static Value CastToString(scoped in Value arg)
    {
        if (arg.Type == ValueType.Uri)
            return Value.FromString(Value.IriText(arg.StringValue));
        if (!arg.IsLiteral)
            return Value.Error;
        const string decimalDigits = "0.############################";
        switch (arg.Type)
        {
            case ValueType.Boolean:
                return Value.FromString(arg.BooleanValue ? "true" : "false");
            case ValueType.Integer:
                return Value.FromString(arg.IntegerValue.ToString(CultureInfo.InvariantCulture));
            case ValueType.Decimal:
                return Value.FromString(arg.DecimalValue.ToString(decimalDigits, CultureInfo.InvariantCulture));
            case ValueType.Float:
            case ValueType.Double:
                double d = arg.DoubleValue;
                if (double.IsNaN(d)) return Value.FromString("NaN");
                if (double.IsPositiveInfinity(d)) return Value.FromString("INF");
                if (double.IsNegativeInfinity(d)) return Value.FromString("-INF");
                if (d == 0) return Value.FromString(double.IsNegative(d) ? "-0" : "0");
                double magnitude = Math.Abs(d);
                if (magnitude >= 1e-6 && magnitude < 1e6)
                {
                    decimal exact = arg.Type == ValueType.Float ? (decimal)(float)d : (decimal)d;
                    return Value.FromString(exact.ToString(decimalDigits, CultureInfo.InvariantCulture));
                }
                return Value.FromString(d.ToString("0.0################E0", CultureInfo.InvariantCulture));
            default:
                return Value.FromString(arg.StringValue);
        }
    }

    private static Value CastToDateTime(scoped in Value arg)
    {
        bool isDateTime = arg.Type == ValueType.Typed && XsdDatatype.KindOf(arg.Datatype) == XsdKind.DateTime;
        if ((isDateTime || arg.Type == ValueType.String)
            && XsdDateTime.TryParse(arg.StringValue.Trim(), isDate: false, out _))
            return Value.FromTyped(arg.StringValue.Trim(), XsdDatatype.DateTime);
        return Value.Error;
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
            if (span[0] == '=' && span[1] == '=') { _position += 2; return ComparisonOperator.Equal; }
            if (span[0] == '!' && span[1] == '=') { _position += 2; return ComparisonOperator.NotEqual; }
            if (span[0] == '<' && span[1] == '=') { _position += 2; return ComparisonOperator.LessOrEqual; }
            if (span[0] == '>' && span[1] == '=') { _position += 2; return ComparisonOperator.GreaterOrEqual; }
        }

        if (span.Length >= 1)
        {
            if (span[0] == '<') { _position++; return ComparisonOperator.Less; }
            if (span[0] == '>') { _position++; return ComparisonOperator.Greater; }
            if (span[0] == '=') { _position++; return ComparisonOperator.Equal; }
        }

        return ComparisonOperator.Unknown;
    }

    /// <summary>A comparison by the operator mapping (ADR-056 D3): true, false or an error.</summary>
    private static Truth EvaluateComparison(scoped in Value left, ComparisonOperator op, scoped in Value right) => op switch
    {
        ComparisonOperator.Equal => Value.Equal(left, right),
        ComparisonOperator.NotEqual => Value.Not(Value.Equal(left, right)),
        ComparisonOperator.Unknown => Truth.Error,
        _ => Value.Order(left, op, right),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SkipWhitespace()
    {
        while (!IsAtEnd() && IsWhitespace(Peek()))
            _position++;
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

internal enum ComparisonOperator
{
    Unknown,
    Equal,
    NotEqual,
    Less,
    Greater,
    LessOrEqual,
    GreaterOrEqual
}
