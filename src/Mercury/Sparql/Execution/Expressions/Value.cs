using System;
using System.Buffers;
using System.Globalization;
using SkyOmega.Mercury.Rdf;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Execution.Expressions;

/// <summary>
/// The kind of RDF term a <see cref="Value"/> is (ADR-056 D1). The literal kinds are the classes the SPARQL operator
/// mapping tells apart (§17.3); <see cref="Unbound"/> is an error, which is also what an unbound variable evaluates to.
/// </summary>
internal enum ValueType
{
    Unbound,
    Uri,
    /// <summary>A blank node, written <c>_:label</c>: neither an IRI nor a literal (SPARQL 1.1 §17.4.2).</summary>
    BlankNode,
    /// <summary>A simple literal, or an <c>xsd:string</c> literal (RDF 1.1 Concepts §3.3: the same term).</summary>
    String,
    LangString,
    /// <summary>A literal of any other datatype: a dateTime, a date, an unknown type, or an ill-typed literal.</summary>
    Typed,
    /// <summary><c>xsd:integer</c> and every type derived from it.</summary>
    Integer,
    Decimal,
    Float,
    Double,
    Boolean,
}

/// <summary>The outcome of a SPARQL test: an error is its own outcome, not false (§17.2).</summary>
internal enum Truth : byte
{
    False,
    True,
    Error,
}

/// <summary>
/// An RDF term as the expression evaluator holds it (ADR-056). A literal's fields always mean one thing:
/// <see cref="StringValue"/> is its lexical form (decoded, ADR-044), <see cref="Language"/> its tag without the '@', and
/// <see cref="Datatype"/> its datatype IRI in angle brackets. An IRI's <see cref="StringValue"/> is <c>&lt;iri&gt;</c>,
/// a blank node's <c>_:label</c>.
/// A number or boolean read from a term keeps its lexical form and datatype next to its value, so <c>"01"^^xsd:short</c>
/// stays that term; a computed one has neither, and its term is the canonical form of its type.
/// </summary>
internal ref struct Value
{
    public ValueType Type;
    public long IntegerValue;
    public decimal DecimalValue;
    /// <summary>A double, or a float rounded to float precision.</summary>
    public double DoubleValue;
    public bool BooleanValue;
    public ReadOnlySpan<char> StringValue;
    public ReadOnlySpan<char> Language;
    public ReadOnlySpan<char> Datatype;

    /// <summary>An error, which is also the value of an unbound variable.</summary>
    public static Value Error => default;

    public static Value FromIri(ReadOnlySpan<char> iri) => new() { Type = ValueType.Uri, StringValue = iri };
    public static Value FromBlankNode(ReadOnlySpan<char> label) => new() { Type = ValueType.BlankNode, StringValue = label };
    public static Value FromString(ReadOnlySpan<char> lexical) => new() { Type = ValueType.String, StringValue = lexical };
    public static Value FromLangString(ReadOnlySpan<char> lexical, ReadOnlySpan<char> language) =>
        new() { Type = ValueType.LangString, StringValue = lexical, Language = language };
    public static Value FromTyped(ReadOnlySpan<char> lexical, ReadOnlySpan<char> datatype) =>
        new() { Type = ValueType.Typed, StringValue = lexical, Datatype = datatype };
    public static Value FromInteger(long value) => new() { Type = ValueType.Integer, IntegerValue = value };
    public static Value FromDecimal(decimal value) => new() { Type = ValueType.Decimal, DecimalValue = value };
    public static Value FromFloat(double value) => new() { Type = ValueType.Float, DoubleValue = (float)value };
    public static Value FromDouble(double value) => new() { Type = ValueType.Double, DoubleValue = value };
    public static Value FromBoolean(bool value) => new() { Type = ValueType.Boolean, BooleanValue = value };
    public static Value FromTruth(Truth truth) => truth == Truth.Error ? Error : FromBoolean(truth == Truth.True);

    public readonly bool IsLiteral => Type >= ValueType.String;
    /// <summary>
    /// True when a string function reads this term's text: a literal that is not a number or a boolean, or an IRI.
    /// Reading an IRI's text is Mercury's (text:match over IRIs, ADR-051); SPARQL's string functions take literals.
    /// </summary>
    public readonly bool HasText => Type is ValueType.String or ValueType.LangString or ValueType.Typed or ValueType.Uri;

    public readonly bool IsNumeric => Type is ValueType.Integer or ValueType.Decimal or ValueType.Float or ValueType.Double;

    // ── Reading a term ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A literal with this lexical form, language tag and datatype (in angle brackets; empty for none). The one rule
    /// for both a stored term and a constant: the datatype is matched by its whole IRI, and a number or boolean whose
    /// lexical form is not valid for its type is ill-typed, <see cref="ValueType.Typed"/>.
    /// </summary>
    public static Value Literal(ReadOnlySpan<char> lexical, ReadOnlySpan<char> language, ReadOnlySpan<char> datatype)
    {
        if (!language.IsEmpty)
            return FromLangString(lexical, language);
        if (datatype.IsEmpty)
            return FromString(lexical);

        var value = FromTyped(lexical, datatype);
        switch (XsdDatatype.KindOf(datatype))
        {
            case XsdKind.String:
                value.Type = ValueType.String;
                break;
            case XsdKind.Boolean:
                if (lexical is "true" or "1") { value.Type = ValueType.Boolean; value.BooleanValue = true; }
                else if (lexical is "false" or "0") { value.Type = ValueType.Boolean; value.BooleanValue = false; }
                break;
            case XsdKind.Integer:
                if (IsNumericToken(lexical, out bool dot, out bool exponent) && !dot && !exponent)
                {
                    if (long.TryParse(lexical, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
                    {
                        value.Type = ValueType.Integer;
                        value.IntegerValue = l;
                    }
                    else if (decimal.TryParse(lexical, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var big))
                    {
                        value.Type = ValueType.Decimal; // past a long: the value as a decimal, the datatype kept
                        value.DecimalValue = big;
                    }
                }
                break;
            case XsdKind.Decimal:
                if (IsNumericToken(lexical, out _, out bool decimalExponent) && !decimalExponent
                    && decimal.TryParse(lexical, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var d))
                {
                    value.Type = ValueType.Decimal;
                    value.DecimalValue = d;
                }
                break;
            case XsdKind.Float:
            case XsdKind.Double:
                if (TryParseDouble(lexical, out var f))
                {
                    bool isFloat = XsdDatatype.KindOf(datatype) == XsdKind.Float;
                    value.Type = isFloat ? ValueType.Float : ValueType.Double;
                    value.DoubleValue = isFloat ? (float)f : f;
                }
                break;
        }
        return value;
    }

    /// <summary>
    /// A term as a binding or the store holds it: <c>&lt;iri&gt;</c>, <c>_:label</c>, or a literal <c>"lex"</c>,
    /// <c>"lex"@tag</c>, <c>"lex"^^&lt;datatype&gt;</c> (the lexical form between the first and the last quote). A
    /// datatype stored as <c>xsd:T</c> and a bare number or boolean are the forms of earlier writers (ADR-056 D7); text
    /// that is none of these reads as a simple literal.
    /// </summary>
    public static Value FromTerm(ReadOnlySpan<char> term)
    {
        if (term.Length >= 2 && term[0] == '<' && term[^1] == '>')
            return FromIri(term);
        if (term.Length > 2 && term[0] == '_' && term[1] == ':')
            return FromBlankNode(term);
        if (term.Length >= 2 && term[0] == '"')
        {
            int close = term.LastIndexOf('"');
            if (close > 0)
            {
                var lexical = term[1..close];
                var suffix = term[(close + 1)..];
                if (suffix.IsEmpty)
                    return FromString(lexical);
                if (suffix[0] == '@')
                    return FromLangString(lexical, suffix[1..]);
                if (suffix.Length >= 2 && suffix[0] == '^' && suffix[1] == '^')
                {
                    var datatype = suffix[2..];
                    if (!datatype.IsEmpty && datatype[0] == '<')
                        return Literal(lexical, default, datatype);
                    var stored = XsdDatatype.FromStoredPrefixedName(datatype);
                    return stored != null ? Literal(lexical, default, stored) : FromTyped(lexical, datatype);
                }
            }
        }
        if (term is "true" or "false")
            return FromBoolean(term[0] == 't');
        if (IsNumericToken(term, out _, out _))
            return FromNumericToken(term);
        return FromString(term);
    }

    /// <summary>
    /// A numeric literal token ([146]–[155]) as the term it abbreviates: an exponent makes an <c>xsd:double</c>, a '.'
    /// an <c>xsd:decimal</c>, otherwise an <c>xsd:integer</c>, with the lexical form exactly as written.
    /// </summary>
    public static Value FromNumericToken(ReadOnlySpan<char> token)
    {
        IsNumericToken(token, out bool dot, out bool exponent);
        string datatype = exponent ? XsdDatatype.Double : dot ? XsdDatatype.Decimal : XsdDatatype.Integer;
        return Literal(token, default, datatype);
    }

    /// <summary>
    /// True when <paramref name="s"/> is a number in the lexical space XSD and the SPARQL grammar share: a sign, then
    /// digits, a '.' and digits (either side may be empty, not both), then an optional exponent.
    /// </summary>
    public static bool IsNumericToken(ReadOnlySpan<char> s, out bool hasDot, out bool hasExponent)
    {
        hasDot = hasExponent = false;
        int i = 0;
        if (i < s.Length && s[i] is '+' or '-') i++;
        int digits = 0, fraction = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; digits++; }
        if (i < s.Length && s[i] == '.')
        {
            hasDot = true;
            i++;
            while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; fraction++; }
        }
        if (digits + fraction == 0) // "1." is a number (XSD's lexical space allows it), "." is not
            return false;
        if (i < s.Length && s[i] is 'e' or 'E')
        {
            hasExponent = true;
            i++;
            if (i < s.Length && s[i] is '+' or '-') i++;
            int exponentDigits = 0;
            while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; exponentDigits++; }
            if (exponentDigits == 0)
                return false;
        }
        return i == s.Length;
    }

    private static bool TryParseDouble(ReadOnlySpan<char> lexical, out double value)
    {
        switch (lexical)
        {
            case "INF": case "+INF": value = double.PositiveInfinity; return true;
            case "-INF": value = double.NegativeInfinity; return true;
            case "NaN": value = double.NaN; return true;
        }
        value = 0;
        return IsNumericToken(lexical, out _, out _)
               && double.TryParse(lexical, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    // ── The term's parts ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The lexical form: as read, or the canonical form of a computed number or boolean, written into
    /// <paramref name="buffer"/> (64 characters hold every number).
    /// </summary>
    public readonly ReadOnlySpan<char> Lexical(Span<char> buffer)
    {
        if (!StringValue.IsEmpty || !(IsNumeric || Type == ValueType.Boolean))
            return Type == ValueType.Uri ? IriText(StringValue) : StringValue;
        int written;
        switch (Type)
        {
            case ValueType.Integer:
                IntegerValue.TryFormat(buffer, out written, default, CultureInfo.InvariantCulture);
                return buffer[..written];
            case ValueType.Decimal:
                DecimalValue.TryFormat(buffer, out written, "0.0###########################", CultureInfo.InvariantCulture);
                return buffer[..written];
            case ValueType.Float:
            case ValueType.Double:
                if (double.IsNaN(DoubleValue)) return "NaN";
                if (double.IsPositiveInfinity(DoubleValue)) return "INF";
                if (double.IsNegativeInfinity(DoubleValue)) return "-INF";
                if (Type == ValueType.Float)
                    ((float)DoubleValue).TryFormat(buffer, out written, "R", CultureInfo.InvariantCulture);
                else
                    DoubleValue.TryFormat(buffer, out written, "R", CultureInfo.InvariantCulture);
                return buffer[..written];
            default:
                return BooleanValue ? "true" : "false";
        }
    }

    /// <summary>An IRI without its angle brackets.</summary>
    public static ReadOnlySpan<char> IriText(ReadOnlySpan<char> iri) =>
        iri.Length >= 2 && iri[0] == '<' && iri[^1] == '>' ? iri[1..^1] : iri;

    /// <summary>The datatype IRI in angle brackets: as read, or the one the kind implies; empty for a non-literal.</summary>
    public readonly ReadOnlySpan<char> DatatypeIri => !Datatype.IsEmpty ? Datatype : Type switch
    {
        ValueType.String => XsdDatatype.String,
        ValueType.LangString => XsdDatatype.LangString,
        ValueType.Integer => XsdDatatype.Integer,
        ValueType.Decimal => XsdDatatype.Decimal,
        ValueType.Float => XsdDatatype.Float,
        ValueType.Double => XsdDatatype.Double,
        ValueType.Boolean => XsdDatatype.Boolean,
        _ => default,
    };

    // ── Writing the term ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The term as the store and a binding hold it (ADR-056 D6), into <paramref name="destination"/>; its length,
    /// or -1 when it doesn't fit.</summary>
    public readonly int WriteTerm(Span<char> destination)
    {
        if (!IsLiteral)
        {
            if (StringValue.Length > destination.Length) return -1;
            StringValue.CopyTo(destination);
            return StringValue.Length;
        }
        Span<char> number = stackalloc char[64];
        var lexical = Lexical(number);
        ReadOnlySpan<char> datatype = Type switch
        {
            ValueType.String or ValueType.LangString => Datatype,
            ValueType.Typed => Datatype,
            _ => DatatypeIri,
        };
        bool typed = Type == ValueType.Typed || !datatype.IsEmpty;
        int length = lexical.Length + 2 + (Type == ValueType.LangString ? 1 + Language.Length : typed ? 2 + datatype.Length : 0);
        if (length > destination.Length) return -1;
        int at = 0;
        destination[at++] = '"';
        lexical.CopyTo(destination[at..]);
        at += lexical.Length;
        destination[at++] = '"';
        if (Type == ValueType.LangString)
        {
            destination[at++] = '@';
            Language.CopyTo(destination[at..]);
            at += Language.Length;
        }
        else if (typed)
        {
            destination[at++] = '^';
            destination[at++] = '^';
            datatype.CopyTo(destination[at..]);
            at += datatype.Length;
        }
        return at;
    }

    /// <summary>The term, as a string.</summary>
    public readonly string Term()
    {
        Span<char> buffer = stackalloc char[256];
        int length = WriteTerm(buffer);
        if (length >= 0)
            return new string(buffer[..length]);
        var large = new char[StringValue.Length + Language.Length + Datatype.Length + 72];
        return new string(large, 0, WriteTerm(large));
    }

    /// <summary>
    /// Binds the term to <paramref name="variable"/> (ADR-056 D6): the one way an evaluated value becomes a binding, for
    /// BIND, SELECT expressions, aggregate expressions and sub-SELECT projection. An error binds nothing (§18.6).
    /// </summary>
    public readonly void BindTo(ref BindingTable table, ReadOnlySpan<char> variable)
    {
        if (Type == ValueType.Unbound)
            return;
        Span<char> buffer = stackalloc char[256];
        int length = WriteTerm(buffer);
        if (length >= 0)
        {
            table.Bind(variable, buffer[..length]);
            return;
        }
        var rented = ArrayPool<char>.Shared.Rent(StringValue.Length + Language.Length + Datatype.Length + 72);
        try
        {
            table.Bind(variable, rented.AsSpan(0, WriteTerm(rented)));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    // ── The operator mapping (SPARQL 1.1 §17.3) ────────────────────────────────────────────────────────────────────

    // The value spaces the operator mapping compares within; None is any other literal, compared by RDFterm-equal.
    private enum ValueSpace : byte { None, Numeric, String, Boolean, DateTime, Date }

    private readonly ValueSpace Space => Type switch
    {
        ValueType.Integer or ValueType.Decimal or ValueType.Float or ValueType.Double => ValueSpace.Numeric,
        ValueType.String => ValueSpace.String,
        ValueType.Boolean => ValueSpace.Boolean,
        ValueType.Typed => XsdDatatype.KindOf(Datatype) switch
        {
            XsdKind.DateTime when XsdDateTime.TryParse(StringValue, isDate: false, out _) => ValueSpace.DateTime,
            XsdKind.Date when XsdDateTime.TryParse(StringValue, isDate: true, out _) => ValueSpace.Date,
            _ => ValueSpace.None,
        },
        _ => ValueSpace.None,
    };

    /// <summary>
    /// <c>=</c> (ADR-056 D3): by value within a value space of the operator mapping; otherwise RDFterm-equal. An IRI or
    /// a blank node equals only itself; a tagged literal never equals an untagged one, and two tagged literals compare
    /// their tags ignoring case; literals of two known value spaces are not equal; any other pair of literals is equal
    /// when it is the same term and an error otherwise (§17.4.1.7).
    /// </summary>
    public static Truth Equal(scoped in Value a, scoped in Value b)
    {
        if (a.Type == ValueType.Unbound || b.Type == ValueType.Unbound)
            return Truth.Error;
        if (!a.IsLiteral || !b.IsLiteral)
            return a.Type == b.Type && a.StringValue.SequenceEqual(b.StringValue) ? Truth.True : Truth.False;
        if (a.Type == ValueType.LangString || b.Type == ValueType.LangString)
            return a.Type == b.Type && a.StringValue.SequenceEqual(b.StringValue)
                   && a.Language.Equals(b.Language, StringComparison.OrdinalIgnoreCase) ? Truth.True : Truth.False;
        var space = a.Space;
        var other = b.Space;
        if (space != ValueSpace.None && other != ValueSpace.None)
        {
            if (space != other)
                return Truth.False;
            return Compare(a, b, space, out int order) switch
            {
                Truth.True => order == 0 ? Truth.True : Truth.False,
                Truth.False => Truth.False, // NaN equals nothing
                _ => Truth.Error,
            };
        }
        return SameTerm(a, b) ? Truth.True : Truth.Error;
    }

    /// <summary>
    /// <c>&lt;</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&gt;=</c>: defined within a value space of the operator mapping and an
    /// error otherwise. A comparison with NaN is false.
    /// </summary>
    public static Truth Order(scoped in Value a, ComparisonOperator op, scoped in Value b)
    {
        var space = a.Space;
        if (space == ValueSpace.None || space != b.Space)
            return Truth.Error;
        var compared = Compare(a, b, space, out int order);
        if (compared != Truth.True)
            return compared;
        bool holds = op switch
        {
            ComparisonOperator.Less => order < 0,
            ComparisonOperator.Greater => order > 0,
            ComparisonOperator.LessOrEqual => order <= 0,
            _ => order >= 0,
        };
        return holds ? Truth.True : Truth.False;
    }

    /// <summary>
    /// The order of two values in one value space: <see cref="Truth.True"/> with <paramref name="order"/> set;
    /// <see cref="Truth.False"/> when a NaN makes them unordered (every comparison is false); <see cref="Truth.Error"/>
    /// when XSD's partial order leaves a date or dateTime with and one without a timezone incomparable.
    /// </summary>
    private static Truth Compare(scoped in Value a, scoped in Value b, ValueSpace space, out int order)
    {
        order = 0;
        switch (space)
        {
            case ValueSpace.Numeric:
                if (a.Type is ValueType.Float or ValueType.Double || b.Type is ValueType.Float or ValueType.Double)
                {
                    double x = a.AsDouble, y = b.AsDouble;
                    if (double.IsNaN(x) || double.IsNaN(y))
                        return Truth.False;
                    order = x.CompareTo(y);
                }
                else if (a.Type == ValueType.Decimal || b.Type == ValueType.Decimal)
                    order = a.AsDecimal.CompareTo(b.AsDecimal);
                else
                    order = a.IntegerValue.CompareTo(b.IntegerValue);
                return Truth.True;
            case ValueSpace.String:
                order = a.StringValue.SequenceCompareTo(b.StringValue);
                return Truth.True;
            case ValueSpace.Boolean:
                order = a.BooleanValue.CompareTo(b.BooleanValue);
                return Truth.True;
            default:
                return XsdDateTime.TryCompare(a.StringValue, b.StringValue, space == ValueSpace.Date, out order)
                    ? Truth.True : Truth.Error;
        }
    }

    /// <summary>A number's value as a double.</summary>
    public readonly double AsDouble => Type switch
    {
        ValueType.Integer => IntegerValue,
        ValueType.Decimal => (double)DecimalValue,
        _ => DoubleValue,
    };

    /// <summary>An integer's or a decimal's value as a decimal.</summary>
    public readonly decimal AsDecimal => Type == ValueType.Integer ? IntegerValue : DecimalValue;

    /// <summary>
    /// sameTerm (§17.4.2.8, RDF 1.1 Concepts §3.3): the same kind of term, and for a literal the same lexical form,
    /// datatype and language tag, character by character. A computed value is compared through its canonical term.
    /// </summary>
    public static bool SameTerm(scoped in Value a, scoped in Value b)
    {
        if (a.Type == ValueType.Unbound || b.Type == ValueType.Unbound)
            return false;
        if (!a.IsLiteral || !b.IsLiteral)
            return a.Type == b.Type && a.StringValue.SequenceEqual(b.StringValue);
        Span<char> left = stackalloc char[64];
        Span<char> right = stackalloc char[64];
        return a.Lexical(left).SequenceEqual(b.Lexical(right))
               && a.Language.SequenceEqual(b.Language)
               && a.DatatypeIri.SequenceEqual(b.DatatypeIri);
    }

    /// <summary>
    /// The effective boolean value (§17.2.2, ADR-056 D4): a boolean is its value; a number is false when it is 0 or
    /// NaN; a string is false when empty; an ill-typed number or boolean is false; anything else is an error.
    /// </summary>
    public static Truth EffectiveBoolean(scoped in Value v)
    {
        switch (v.Type)
        {
            case ValueType.Boolean: return v.BooleanValue ? Truth.True : Truth.False;
            case ValueType.Integer: return v.IntegerValue != 0 ? Truth.True : Truth.False;
            case ValueType.Decimal: return v.DecimalValue != 0 ? Truth.True : Truth.False;
            case ValueType.Float:
            case ValueType.Double:
                return v.DoubleValue != 0 && !double.IsNaN(v.DoubleValue) ? Truth.True : Truth.False;
            case ValueType.String:
            case ValueType.LangString:
                return v.StringValue.IsEmpty ? Truth.False : Truth.True;
            case ValueType.Typed:
                return XsdDatatype.KindOf(v.Datatype) is XsdKind.Boolean or XsdKind.Integer or XsdKind.Decimal
                    or XsdKind.Float or XsdKind.Double ? Truth.False : Truth.Error;
            default:
                return Truth.Error;
        }
    }

    public static Truth Not(Truth t) => t switch { Truth.True => Truth.False, Truth.False => Truth.True, _ => Truth.Error };

    /// <summary>§17.2 logical-or: true if either is true, else an error if either is, else false.</summary>
    public static Truth Or(Truth a, Truth b) =>
        a == Truth.True || b == Truth.True ? Truth.True : a == Truth.Error || b == Truth.Error ? Truth.Error : Truth.False;

    /// <summary>§17.2 logical-and: false if either is false, else an error if either is, else true.</summary>
    public static Truth And(Truth a, Truth b) =>
        a == Truth.False || b == Truth.False ? Truth.False : a == Truth.Error || b == Truth.Error ? Truth.Error : Truth.True;

    /// <summary>
    /// <c>+ - * /</c> on two numbers (XPath op:numeric-*): the operands are promoted to the wider of integer, decimal,
    /// float and double. Integer division gives a decimal, and a decimal division by zero is an error. Anything that is
    /// not a number is an error.
    /// </summary>
    public static Value Arithmetic(char op, scoped in Value a, scoped in Value b)
    {
        if (!a.IsNumeric || !b.IsNumeric)
            return Error;
        var wider = a.Type > b.Type ? a.Type : b.Type;
        if (wider == ValueType.Integer && op != '/')
        {
            try
            {
                return op switch
                {
                    '+' => FromInteger(checked(a.IntegerValue + b.IntegerValue)),
                    '-' => FromInteger(checked(a.IntegerValue - b.IntegerValue)),
                    _ => FromInteger(checked(a.IntegerValue * b.IntegerValue)),
                };
            }
            catch (OverflowException)
            {
                wider = ValueType.Decimal; // past a long: computed as a decimal
            }
        }
        if (wider is ValueType.Integer or ValueType.Decimal)
        {
            decimal x = a.AsDecimal, y = b.AsDecimal;
            if (op == '/' && y == 0)
                return Error;
            try
            {
                return FromDecimal(op switch { '+' => x + y, '-' => x - y, '*' => x * y, _ => x / y });
            }
            catch (OverflowException)
            {
                return Error;
            }
        }
        double p = a.AsDouble, q = b.AsDouble;
        double result = op switch { '+' => p + q, '-' => p - q, '*' => p * q, _ => p / q };
        return wider == ValueType.Float ? FromFloat(result) : FromDouble(result);
    }

    /// <summary>Unary minus (op:numeric-unary-minus), in the operand's type; an error for anything else.</summary>
    public static Value Negate(scoped in Value v) => v.Type switch
    {
        ValueType.Integer when v.IntegerValue != long.MinValue => FromInteger(-v.IntegerValue),
        ValueType.Integer => FromDecimal(-(decimal)v.IntegerValue),
        ValueType.Decimal => FromDecimal(-v.DecimalValue),
        ValueType.Float => FromFloat(-v.DoubleValue),
        ValueType.Double => FromDouble(-v.DoubleValue),
        _ => Error,
    };

    /// <summary>Unary plus (op:numeric-unary-plus): the number's value; an error for anything else.</summary>
    public static Value Plus(scoped in Value v) => v.Type switch
    {
        ValueType.Integer => FromInteger(v.IntegerValue),
        ValueType.Decimal => FromDecimal(v.DecimalValue),
        ValueType.Float => FromFloat(v.DoubleValue),
        ValueType.Double => FromDouble(v.DoubleValue),
        _ => Error,
    };
}
