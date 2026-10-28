using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Execution.Expressions;

internal ref partial struct FilterEvaluator
{
#if DEBUG
    private static long _textMatchEvaluationCount;
    internal static long TextMatchEvaluationCount => _textMatchEvaluationCount;
    internal static void ResetTextMatchEvaluationCount() => Interlocked.Exchange(ref _textMatchEvaluationCount, 0);
#endif

    /// <summary>
    /// IF(condition, then, else) (§17.4.1.2): the condition's effective boolean value picks a branch, in the same
    /// context as the rest of the expression; an error in it is an error.
    /// </summary>
    private Value ParseIfFunction()
    {
        var condition = Value.EffectiveBoolean(ValueOr());
        SkipWhitespace();
        if (Peek() == ',')
            Advance();
        var thenValue = ValueOr();
        SkipWhitespace();
        if (Peek() == ',')
            Advance();
        var elseValue = ValueOr();
        SkipWhitespace();
        if (Peek() == ')')
            Advance();
        return condition switch
        {
            Truth.True => thenValue,
            Truth.False => elseValue,
            _ => Value.Error,
        };
    }

    /// <summary>
    /// COALESCE(expr1, expr2, …) (§17.4.1.4): the first argument that is not an error.
    /// </summary>
    private Value ParseCoalesceFunction()
    {
        Value result = Value.Error;

        while (!IsAtEnd() && Peek() != ')')
        {
            var value = ValueOr();
            if (result.Type == ValueType.Unbound && value.Type != ValueType.Unbound)
                result = value;

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
    /// The arguments of a two-argument function, read in order; false (an error) when the second is missing.
    /// </summary>
    private bool ParseSecondArgument()
    {
        SkipWhitespace();
        if (Peek() != ',')
        {
            SkipToCloseParen();
            return false;
        }
        Advance();
        SkipWhitespace();
        return true;
    }

    private void CloseArguments()
    {
        SkipWhitespace();
        if (Peek() == ')')
            Advance();
    }

    /// <summary>REGEX flags as .NET options: i, s, m and x (XPath fn:matches).</summary>
    private static RegexOptions RegexFlags(ReadOnlySpan<char> flags)
    {
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
        return options;
    }

    /// <summary>
    /// REGEX(text, pattern [, flags]) (§17.4.3.14); an error when an argument has no text or the pattern is invalid.
    /// </summary>
    private Value ParseRegexFunction()
    {
        var stringArg = ValueOr();
        if (!ParseSecondArgument())
            return Value.Error;
        var patternArg = ValueOr();
        SkipWhitespace();

        ReadOnlySpan<char> flags = default;
        bool flagsValid = true;
        if (Peek() == ',')
        {
            Advance();
            SkipWhitespace();
            var flagsArg = ValueOr();
            flagsValid = flagsArg.HasText;
            flags = flagsArg.StringValue;
        }
        CloseArguments();

        if (!stringArg.HasText || !patternArg.HasText || patternArg.Type == ValueType.Uri || !flagsValid)
            return Value.Error;

        try
        {
            var regex = new Regex(patternArg.StringValue.ToString(), RegexFlags(flags), TimeSpan.FromMilliseconds(100));
            return Value.FromBoolean(regex.IsMatch(TextOf(stringArg).ToString()));
        }
        catch (ArgumentException)
        {
            return Value.Error;
        }
    }

    /// <summary>
    /// REPLACE(text, pattern, replacement [, flags]) (§17.4.3.15): every match replaced, the text's tag or datatype kept.
    /// </summary>
    private Value ParseReplaceFunction()
    {
        var stringArg = ValueOr();
        if (!ParseSecondArgument())
            return Value.Error;
        var patternArg = ValueOr();
        if (!ParseSecondArgument())
            return Value.Error;
        var replacementArg = ValueOr();
        SkipWhitespace();

        ReadOnlySpan<char> flags = default;
        bool flagsValid = true;
        if (Peek() == ',')
        {
            Advance();
            SkipWhitespace();
            var flagsArg = ValueOr();
            flagsValid = flagsArg.HasText;
            flags = flagsArg.StringValue;
        }
        CloseArguments();

        if (!stringArg.HasText || !patternArg.HasText || !replacementArg.HasText || !flagsValid)
            return Value.Error;

        try
        {
            var regex = new Regex(patternArg.StringValue.ToString(), RegexFlags(flags), TimeSpan.FromMilliseconds(100));
            return WithText(stringArg, regex.Replace(TextOf(stringArg).ToString(), replacementArg.StringValue.ToString()));
        }
        catch (ArgumentException)
        {
            return Value.Error;
        }
    }

    /// <summary>
    /// text:match(text, query) - full-text search with case-insensitive matching.
    /// Returns true if text contains query (case-insensitive).
    /// </summary>
    /// <remarks>
    /// ADR-024: The trigram index provides set-based pre-filtering at scan level —
    /// the scan restricts the enumerator to candidate object atoms before bindings
    /// reach this evaluator. This Contains check is the verification step that
    /// eliminates trigram false positives. Both paths are required for correctness.
    /// </remarks>
    private Value ParseTextMatchFunction()
    {
        var textArg = ValueOr();
        if (!ParseSecondArgument())
            return Value.FromBoolean(false);
        var queryArg = ValueOr();
        CloseArguments();

        if (!textArg.HasText || !queryArg.HasText)
            return Value.FromBoolean(false);

        // Case-insensitive contains check using OrdinalIgnoreCase for locale-independent
        // Unicode case-folding (handles Swedish å, ä, ö). Consistent with TrigramIndex
        // which uses ToLowerInvariant() — both use Unicode simple case folding tables.
#if DEBUG
        Interlocked.Increment(ref _textMatchEvaluationCount);
#endif
        return Value.FromBoolean(TextOf(textArg).Contains(TextOf(queryArg), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>CONTAINS(text, substring) (§17.4.3.11)</summary>
    private Value ParseContainsFunction()
    {
        var arg1 = ValueOr();
        if (!ParseSecondArgument())
            return Value.Error;
        var arg2 = ValueOr();
        CloseArguments();
        if (!arg1.HasText || !arg2.HasText)
            return Value.Error;
        return Value.FromBoolean(TextOf(arg1).Contains(TextOf(arg2), StringComparison.Ordinal));
    }

    /// <summary>STRSTARTS(text, prefix) (§17.4.3.9)</summary>
    private Value ParseStrStartsFunction()
    {
        var arg1 = ValueOr();
        if (!ParseSecondArgument())
            return Value.Error;
        var arg2 = ValueOr();
        CloseArguments();
        if (!arg1.HasText || !arg2.HasText)
            return Value.Error;
        return Value.FromBoolean(TextOf(arg1).StartsWith(TextOf(arg2), StringComparison.Ordinal));
    }

    /// <summary>STRENDS(text, suffix) (§17.4.3.10)</summary>
    private Value ParseStrEndsFunction()
    {
        var arg1 = ValueOr();
        if (!ParseSecondArgument())
            return Value.Error;
        var arg2 = ValueOr();
        CloseArguments();
        if (!arg1.HasText || !arg2.HasText)
            return Value.Error;
        return Value.FromBoolean(TextOf(arg1).EndsWith(TextOf(arg2), StringComparison.Ordinal));
    }

    /// <summary>
    /// LANGMATCHES(tag, range) (§17.4.3.13, RFC 4647 basic filtering): "*" matches any non-empty tag; otherwise the
    /// range matches the tag or a prefix of it ending at a '-', ignoring case. Both arguments are simple literals.
    /// </summary>
    private Value ParseLangMatchesFunction()
    {
        var langTagArg = ValueOr();
        if (!ParseSecondArgument())
            return Value.Error;
        var langRangeArg = ValueOr();
        CloseArguments();

        if (langTagArg.Type != ValueType.String || langRangeArg.Type != ValueType.String)
            return Value.Error;

        var langTag = langTagArg.StringValue;
        var langRange = langRangeArg.StringValue;

        if (langRange is "*")
            return Value.FromBoolean(langTag.Length > 0);
        if (langTag.IsEmpty || langTag.Length < langRange.Length
            || !langTag[..langRange.Length].Equals(langRange, StringComparison.OrdinalIgnoreCase))
            return Value.FromBoolean(false);
        return Value.FromBoolean(langTag.Length == langRange.Length || langTag[langRange.Length] == '-');
    }

    /// <summary>sameTerm(term1, term2) (§17.4.2.8): the same RDF term (ADR-056 D3).</summary>
    private Value ParseSameTermFunction()
    {
        var arg1 = ValueOr();
        if (!ParseSecondArgument())
            return Value.Error;
        var arg2 = ValueOr();
        CloseArguments();
        if (arg1.Type == ValueType.Unbound || arg2.Type == ValueType.Unbound)
            return Value.Error;
        return Value.FromBoolean(Value.SameTerm(arg1, arg2));
    }

    /// <summary>
    /// The arguments of STRBEFORE / STRAFTER are compatible (§17.4.3.1.3) when the second has no language tag or the
    /// same one as the first.
    /// </summary>
    private static bool ArgumentsCompatible(scoped in Value text, scoped in Value delimiter) =>
        delimiter.Type != ValueType.LangString
        || (text.Type == ValueType.LangString && text.Language.Equals(delimiter.Language, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// STRBEFORE(text, delimiter) (§17.4.3.7): the text before the first delimiter, with the text's tag; the empty
    /// simple literal when the delimiter isn't found.
    /// </summary>
    private Value ParseStrBeforeFunction()
    {
        var stringArg = ValueOr();
        if (!ParseSecondArgument())
            return Value.Error;
        var delimiterArg = ValueOr();
        CloseArguments();

        if (!stringArg.HasText || !delimiterArg.HasText || !ArgumentsCompatible(stringArg, delimiterArg))
            return Value.Error;

        var text = TextOf(stringArg);
        var index = text.IndexOf(TextOf(delimiterArg));
        if (index < 0)
            return Value.FromString(default);
        return WithText(stringArg, text[..index]);
    }

    /// <summary>
    /// STRAFTER(text, delimiter) (§17.4.3.8): the text after the first delimiter, with the text's tag; the empty
    /// simple literal when the delimiter isn't found.
    /// </summary>
    private Value ParseStrAfterFunction()
    {
        var stringArg = ValueOr();
        if (!ParseSecondArgument())
            return Value.Error;
        var delimiterArg = ValueOr();
        CloseArguments();

        if (!stringArg.HasText || !delimiterArg.HasText || !ArgumentsCompatible(stringArg, delimiterArg))
            return Value.Error;

        var text = TextOf(stringArg);
        var delimiter = TextOf(delimiterArg);
        var index = text.IndexOf(delimiter);
        if (index < 0)
            return Value.FromString(default);
        return WithText(stringArg, text[(index + delimiter.Length)..]);
    }

    /// <summary>
    /// STRDT(lexical, datatype) (§17.4.3.19): the literal with that lexical form and datatype, read by the same rule
    /// as any literal (so <c>STRDT("1", xsd:integer)</c> is the integer 1).
    /// </summary>
    private Value ParseStrDtFunction()
    {
        var lexicalArg = ValueOr();
        if (!ParseSecondArgument())
            return Value.Error;
        var datatypeArg = ValueOr();
        CloseArguments();

        if (lexicalArg.Type != ValueType.String || datatypeArg.Type != ValueType.Uri)
            return Value.Error;
        var datatype = datatypeArg.StringValue;
        return Value.Literal(lexicalArg.StringValue, default,
            datatype.Length > 0 && datatype[0] == '<' ? datatype : string.Concat("<", datatype, ">"));
    }

    /// <summary>STRLANG(lexical, tag) (§17.4.3.20): the literal with that lexical form and language tag.</summary>
    private Value ParseStrLangFunction()
    {
        var lexicalArg = ValueOr();
        if (!ParseSecondArgument())
            return Value.Error;
        var langArg = ValueOr();
        CloseArguments();

        if (lexicalArg.Type != ValueType.String || langArg.Type != ValueType.String || langArg.StringValue.IsEmpty)
            return Value.Error;
        return Value.FromLangString(lexicalArg.StringValue, langArg.StringValue);
    }

    /// <summary>
    /// SUBSTR(text, start [, length]) (§17.4.3.3, XPath fn:substring): 1-based, in Unicode code points, start and
    /// length rounded; the text's tag or datatype kept.
    /// </summary>
    private Value ParseSubstrFunction()
    {
        var stringArg = ValueOr();
        if (!ParseSecondArgument())
            return Value.Error;
        var startArg = ValueOr();
        SkipWhitespace();

        Value lengthArg = Value.Error;
        bool hasLength = false;
        if (Peek() == ',')
        {
            Advance();
            SkipWhitespace();
            lengthArg = ValueOr();
            hasLength = true;
        }
        CloseArguments();

        if (!stringArg.HasText || !startArg.IsNumeric || (hasLength && !lengthArg.IsNumeric))
            return Value.Error;

        var text = TextOf(stringArg);
        var start = Math.Floor(startArg.AsDouble + 0.5);
        int codePointCount = CodePointOps.GetCodePointCount(text);
        // fn:substring: the code points at positions p with start <= p < start + length
        double end = hasLength ? start + Math.Floor(lengthArg.AsDouble + 0.5) : double.PositiveInfinity;
        if (double.IsNaN(start) || double.IsNaN(end))
            return WithText(stringArg, default);
        int first = (int)Math.Max(1, start);
        double last = Math.Min(end, codePointCount + 1);
        if (first > codePointCount || last <= first)
            return WithText(stringArg, default);
        return WithText(stringArg, CodePointOps.SubstringByCodePoints(text, first, (int)(last - first)));
    }

    /// <summary>
    /// CONCAT(text, …) (§17.4.3.12): the texts joined; tagged when every argument has the same tag. A number or
    /// boolean contributes its lexical form (Mercury's; SPARQL takes string literals). A blank node or an IRI is an
    /// error.
    /// </summary>
    private Value ParseConcatFunction()
    {
        var builder = new StringBuilder();
        string? commonLanguage = null;
        bool first = true;
        bool sameLanguage = true;
        Span<char> number = stackalloc char[64];

        while (!IsAtEnd() && Peek() != ')')
        {
            var arg = ValueOr();
            if (!arg.IsLiteral)
            {
                SkipToCloseParen();
                return Value.Error;
            }
            builder.Append(arg.Lexical(number));

            var language = arg.Type == ValueType.LangString ? arg.Language.ToString() : null;
            if (first)
                commonLanguage = language;
            else if (sameLanguage && !string.Equals(commonLanguage, language, StringComparison.OrdinalIgnoreCase))
                sameLanguage = false;
            first = false;

            SkipWhitespace();
            if (Peek() == ',')
            {
                Advance();
                SkipWhitespace();
            }
        }
        if (Peek() == ')')
            Advance();

        var concatenated = builder.ToString();
        return sameLanguage && commonLanguage != null
            ? Value.FromLangString(concatenated, commonLanguage)
            : Value.FromString(concatenated);
    }
}
