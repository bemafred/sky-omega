using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SkyOmega.Mercury.Sparql;

namespace SkyOmega.Mercury.Sparql.Execution;

public ref partial struct FilterEvaluator
{
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

        // Get string value (lexical form)
        ReadOnlySpan<char> stringValue;
        if (stringArg.Type == ValueType.String || stringArg.Type == ValueType.Uri)
            stringValue = stringArg.GetLexicalForm();
        else if (stringArg.Type == ValueType.Unbound)
            return new Value { Type = ValueType.Boolean, BooleanValue = false };
        else
            return new Value { Type = ValueType.Boolean, BooleanValue = false };

        // Get pattern value (lexical form)
        ReadOnlySpan<char> pattern;
        if (patternArg.Type == ValueType.String)
            pattern = patternArg.GetLexicalForm();
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

        var stringValue = stringArg.GetLexicalForm();
        var pattern = patternArg.GetLexicalForm();
        var replacement = replacementArg.GetLexicalForm();

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
            var result = regex.Replace(stringValue.ToString(), replacement.ToString());

            // Preserve language tag or datatype from the first argument
            var suffix = stringArg.GetLangTagOrDatatype();
            _replaceResult = suffix.IsEmpty ? result : $"\"{result}\"{suffix.ToString()}";
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
    /// Parse text:match(text, query) - full-text search with case-insensitive matching.
    /// Returns true if text contains query (case-insensitive).
    /// </summary>
    /// <remarks>
    /// This function is designed to work with the trigram index for pre-filtering,
    /// but the actual matching is done here with case-insensitive comparison.
    /// The trigram index provides candidate filtering at query planning time.
    /// </remarks>
    private Value ParseTextMatchFunction()
    {
        var textArg = ParseTerm();
        SkipWhitespace();

        if (Peek() != ',')
        {
            SkipToCloseParen();
            return new Value { Type = ValueType.Boolean, BooleanValue = false };
        }

        Advance(); // Skip ','
        SkipWhitespace();

        var queryArg = ParseTerm();
        SkipWhitespace();
        if (Peek() == ')')
            Advance();

        // Validate both arguments are strings
        if ((textArg.Type != ValueType.String && textArg.Type != ValueType.Uri) ||
            (queryArg.Type != ValueType.String && queryArg.Type != ValueType.Uri))
        {
            return new Value { Type = ValueType.Boolean, BooleanValue = false };
        }

        // Case-insensitive contains check (handles Unicode including Swedish å, ä, ö)
        // Use CurrentCultureIgnoreCase for proper Unicode case-folding
        // Use lexical form to strip quotes from RDF literals
        var matches = textArg.GetLexicalForm().Contains(queryArg.GetLexicalForm(), StringComparison.CurrentCultureIgnoreCase);
        return new Value { Type = ValueType.Boolean, BooleanValue = matches };
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

        var contains = arg1.GetLexicalForm().Contains(arg2.GetLexicalForm(), StringComparison.Ordinal);
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

        var startsWith = arg1.GetLexicalForm().StartsWith(arg2.GetLexicalForm(), StringComparison.Ordinal);
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

        var endsWith = arg1.GetLexicalForm().EndsWith(arg2.GetLexicalForm(), StringComparison.Ordinal);
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
    /// Returns empty string if delimiter not found. Preserves language tag/datatype from first arg.
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

        var str = stringArg.GetLexicalForm();
        var delimiter = delimiterArg.GetLexicalForm();

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

        // Preserve language tag/datatype from the first argument
        var result = str.Slice(0, index).ToString();
        var suffix = stringArg.GetLangTagOrDatatype();
        // Only add quotes if there's a suffix to preserve, otherwise return plain string
        _strbeforeResult = suffix.IsEmpty ? result : $"\"{result}\"{suffix.ToString()}";
        return new Value
        {
            Type = ValueType.String,
            StringValue = _strbeforeResult.AsSpan()
        };
    }

    /// <summary>
    /// Parse STRAFTER(string, delimiter) - returns substring after first occurrence of delimiter
    /// Returns empty string if delimiter not found. Preserves language tag/datatype from first arg.
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

        var str = stringArg.GetLexicalForm();
        var delimiter = delimiterArg.GetLexicalForm();
        var suffix = stringArg.GetLangTagOrDatatype();

        // Empty delimiter returns full string with preserved suffix (per SPARQL spec)
        if (delimiter.IsEmpty)
        {
            // Only add quotes if there's a suffix to preserve, otherwise return plain string
            _strafterResult = suffix.IsEmpty ? str.ToString() : $"\"{str.ToString()}\"{suffix.ToString()}";
            return new Value { Type = ValueType.String, StringValue = _strafterResult.AsSpan() };
        }

        var index = str.IndexOf(delimiter);
        if (index < 0)
        {
            return new Value { Type = ValueType.String, StringValue = ReadOnlySpan<char>.Empty };
        }

        // Preserve language tag/datatype from the first argument
        var result = str.Slice(index + delimiter.Length).ToString();
        // Only add quotes if there's a suffix to preserve, otherwise return plain string
        _strafterResult = suffix.IsEmpty ? result : $"\"{result}\"{suffix.ToString()}";
        return new Value
        {
            Type = ValueType.String,
            StringValue = _strafterResult.AsSpan()
        };
    }

    /// <summary>
    /// Parse STRDT(lexicalForm, datatypeIRI) - constructs a typed literal
    /// Returns a literal with the specified datatype
    /// </summary>
    private Value ParseStrDtFunction()
    {
        var lexicalArg = ParseTerm();
        SkipWhitespace();

        if (Peek() != ',')
        {
            SkipToCloseParen();
            return new Value { Type = ValueType.Unbound };
        }

        Advance(); // Skip ','
        SkipWhitespace();

        var datatypeArg = ParseTerm();
        SkipWhitespace();
        if (Peek() == ')')
            Advance();

        // Lexical form must be a simple literal (string without language tag)
        if (lexicalArg.Type != ValueType.String)
            return new Value { Type = ValueType.Unbound };

        // Datatype must be an IRI
        if (datatypeArg.Type != ValueType.Uri && datatypeArg.Type != ValueType.String)
            return new Value { Type = ValueType.Unbound };

        var lexical = lexicalArg.StringValue;
        var datatype = datatypeArg.StringValue;

        // Construct typed literal: "lexical"^^<datatype>
        _strdtResult = $"\"{lexical}\"^^<{datatype}>";
        return new Value
        {
            Type = ValueType.String,
            StringValue = _strdtResult.AsSpan()
        };
    }

    /// <summary>
    /// Parse STRLANG(lexicalForm, langTag) - constructs a language-tagged literal
    /// Returns a literal with the specified language tag
    /// </summary>
    private Value ParseStrLangFunction()
    {
        var lexicalArg = ParseTerm();
        SkipWhitespace();

        if (Peek() != ',')
        {
            SkipToCloseParen();
            return new Value { Type = ValueType.Unbound };
        }

        Advance(); // Skip ','
        SkipWhitespace();

        var langArg = ParseTerm();
        SkipWhitespace();
        if (Peek() == ')')
            Advance();

        // Lexical form must be a simple literal (string without language tag)
        if (lexicalArg.Type != ValueType.String)
            return new Value { Type = ValueType.Unbound };

        // Language tag must be a string
        if (langArg.Type != ValueType.String)
            return new Value { Type = ValueType.Unbound };

        var lexical = lexicalArg.StringValue;
        var lang = langArg.StringValue;

        // Language tag must not be empty
        if (lang.IsEmpty)
            return new Value { Type = ValueType.Unbound };

        // Construct language-tagged literal: "lexical"@lang
        _strlangResult = $"\"{lexical}\"@{lang}";
        return new Value
        {
            Type = ValueType.String,
            StringValue = _strlangResult.AsSpan()
        };
    }

    /// <summary>
    /// Parse SUBSTR(string, start [, length]) - returns substring
    /// Note: SPARQL uses 1-based indexing in Unicode code points (not UTF-16 units).
    /// Preserves language tag/datatype from first arg.
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

        var str = stringArg.GetLexicalForm();
        var startCodePoint = (int)startArg.IntegerValue; // SPARQL is 1-based, keep as-is for helper

        // Get code point count for bounds checking
        var codePointCount = UnicodeHelper.GetCodePointCount(str);

        if (startCodePoint < 1) startCodePoint = 1;
        if (startCodePoint > codePointCount)
            return new Value { Type = ValueType.String, StringValue = ReadOnlySpan<char>.Empty };

        int lengthCodePoints;
        if (lengthArg.Type == ValueType.Integer)
        {
            lengthCodePoints = (int)lengthArg.IntegerValue;
            if (lengthCodePoints < 0) lengthCodePoints = 0;
        }
        else
        {
            lengthCodePoints = -1; // Take remainder
        }

        // Use code point-based substring
        var result = UnicodeHelper.SubstringByCodePoints(str, startCodePoint, lengthCodePoints);

        // Preserve language tag/datatype from the first argument
        var suffix = stringArg.GetLangTagOrDatatype();
        // Only add quotes if there's a suffix to preserve, otherwise return plain string
        _substrResult = suffix.IsEmpty ? result : $"\"{result}\"{suffix.ToString()}";
        return new Value
        {
            Type = ValueType.String,
            StringValue = _substrResult.AsSpan()
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
                parts.Add(arg.GetLexicalForm().ToString());
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
}
