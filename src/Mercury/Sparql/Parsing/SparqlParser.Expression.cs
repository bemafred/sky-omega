using System;
using System.Collections.Generic;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Parsing;

internal ref partial struct SparqlParser
{
    /// <summary>
    /// Steps over one token of an expression ([110] Expression, read lexically): a string literal in any of its four
    /// quote forms with its escapes, an IRIREF, a variable or name (with PN_LOCAL escapes), a comment, a bracketed group
    /// to its matching closer, or one character. A bracketed group is <c>( … )</c>, or <c>{ … }</c> for an EXISTS group;
    /// brackets inside literals and IRIs are not counted. Every place that finds where an expression ends (FILTER, BIND,
    /// SELECT and GROUP BY expressions, aggregate arguments, HAVING, ORDER BY, an embedded EXISTS) steps with this.
    /// </summary>
    private void SkipExpressionToken()
    {
        char ch = Peek();
        if (ch is '"' or '\'')
        {
            SkipStringLiteral();
        }
        else if (ch == '<' && TrySkipIriRef())
        {
        }
        else if (ch == '#')
        {
            while (!IsAtEnd() && Peek() != '\n')
                Advance();
        }
        else if (ch is '(' or '{')
        {
            SkipBracketedGroup();
        }
        else if (ch is ')' or '}')
        {
            throw new SparqlParseException($"Unbalanced '{ch}' in expression at position {_position}");
        }
        else if (IsExpressionNameChar(ch) || VariableName.IsSigil(ch))
        {
            Advance();
            while (!IsAtEnd())
            {
                char c = Peek();
                if (c == '\\' && _position + 1 < _source.Length) { Advance(); Advance(); } // PN_LOCAL_ESC
                else if (IsExpressionNameChar(c)) Advance();
                else break;
            }
        }
        else
        {
            Advance();
        }
    }

    /// <summary>True when an expression's name or number continues with <paramref name="ch"/>.</summary>
    private static bool IsExpressionNameChar(char ch) =>
        IsLetterOrDigit(ch) || ch is '_' or ':' or '-' or '.' or '%' || ch > 0x7F;

    /// <summary>
    /// Steps over <c>( … )</c>, <c>{ … }</c> or <c>[ … ]</c> at the cursor, through its matching closer, with brackets
    /// inside literals, IRIs and comments not counted: an expression's group, a pattern's collection or blank-node
    /// property list.
    /// </summary>
    private void SkipBracketedGroup()
    {
        int start = _position;
        int depth = 0;
        do
        {
            if (IsAtEnd())
                throw new SparqlParseException($"Unclosed '{_source[start]}' in expression at position {start}");
            char c = Peek();
            if (c is '"' or '\'') { SkipStringLiteral(); continue; }
            if (c == '<' && TrySkipIriRef()) continue;
            if (c == '#') { while (!IsAtEnd() && Peek() != '\n') Advance(); continue; }
            if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']') depth--;
            Advance();
        } while (depth > 0);
    }

    /// <summary>
    /// [156]–[159] STRING_LITERAL1, STRING_LITERAL2, STRING_LITERAL_LONG1, STRING_LITERAL_LONG2 at the cursor, with
    /// ECHAR escapes. A language tag or datatype after it is read as the following tokens. The one string reader: of
    /// expressions, and of literals in patterns, templates and data (<see cref="ParseLiteral"/>).
    /// </summary>
    private void SkipStringLiteral()
    {
        int start = _position;
        char quote = Peek();
        bool isLong = _position + 2 < _source.Length && _source[_position + 1] == quote && _source[_position + 2] == quote;
        int open = isLong ? 3 : 1;
        _position += open;
        while (true)
        {
            if (IsAtEnd())
                throw new SparqlParseException($"Unclosed string literal at position {start}");
            char c = Peek();
            if (c == '\\')
            {
                // A \u or \U escape may not name a surrogate code point
                if (PeekAt(1) == 'u') ValidateUnicodeEscape4(start);
                else if (PeekAt(1) == 'U') ValidateUnicodeEscape8(start);
                Advance();
                Advance();
                continue;
            }
            if (c == quote && (!isLong || (_position + 2 < _source.Length && _source[_position + 1] == quote && _source[_position + 2] == quote)))
            {
                _position += open;
                return;
            }
            if (!isLong && c is '\n' or '\r')
                throw new SparqlParseException($"Unclosed string literal at position {start}");
            Advance();
        }
    }

    /// <summary>
    /// [139] IRIREF ::= '&lt;' ([^&lt;&gt;"{}|^`\]-[#x00-#x20])* '&gt;' at the cursor. False, with the cursor unmoved,
    /// when the '&lt;' is the less-than operator (<c>?a &lt; ?b</c>), whose text never forms an IRIREF.
    /// </summary>
    private bool TrySkipIriRef()
    {
        for (int i = _position + 1; i < _source.Length; i++)
        {
            char c = _source[i];
            if (c == '>')
            {
                _position = i + 1;
                return true;
            }
            if (c <= ' ' || c is '<' or '"' or '{' or '}' or '|' or '^' or '`' or '\\')
                return false;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="keyword"/> is at the cursor as a whole word (case-insensitive): not followed by a name
    /// character, so <c>graph:a</c> or <c>?gas</c> is not GRAPH or AS. Every keyword test in the parser uses this.
    /// </summary>
    private readonly bool AtKeyword(ReadOnlySpan<char> keyword)
    {
        if (_position + keyword.Length > _source.Length)
            return false;
        if (!_source.Slice(_position, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase))
            return false;
        int after = _position + keyword.Length;
        return after >= _source.Length || !IsExpressionNameChar(_source[after]);
    }

    /// <summary>
    /// The end of the bracketed group at <paramref name="groupStart"/> (its '(' or '{'), just past its closer; for an
    /// evaluator that reads a group out of a captured expression with the parser's token rule.
    /// </summary>
    internal int BracketedGroupEndAt(int groupStart)
    {
        _position = groupStart;
        SkipBracketedGroup();
        return _position;
    }

    /// <summary>
    /// Each <c>EXISTS { … }</c> inside the expression at <c>[start, start + length)</c>, with a NOT before it left to the
    /// expression: the keyword's offset, the group's '{' and the offset just past its '}'. Read with the expression
    /// token rule, so an "EXISTS" or a brace inside a literal is not one; the scan goes into sub-expressions.
    /// </summary>
    internal List<(int KeywordStart, int GroupStart, int End)> EmbeddedExistsIn(int start, int length)
    {
        var found = new List<(int, int, int)>();
        _position = start;
        int end = start + length;
        while (_position < end)
        {
            if (AtKeyword("EXISTS"))
            {
                int keywordStart = _position;
                _position += "EXISTS".Length;
                SkipWhitespace();
                if (Peek() != '{')
                    throw new SparqlParseException("Expected '{' after EXISTS");
                int groupStart = _position;
                SkipBracketedGroup();
                found.Add((keywordStart, groupStart, _position));
                continue;
            }
            if (Peek() is '(' or ')') { Advance(); continue; } // into and out of sub-expressions
            SkipExpressionToken();
        }
        return found;
    }

    /// <summary>
    /// Each aggregate call (COUNT, SUM, AVG, MIN, MAX, SAMPLE, GROUP_CONCAT followed by '(') inside the expression at
    /// <c>[start, start + length)</c>: its function, where its name starts, its '(' and the offset just past its ')'.
    /// Read with the expression token rule, so a name or bracket inside a literal is not one. The scan goes into
    /// sub-expressions; an aggregate's own argument is stepped over whole (aggregates do not nest).
    /// </summary>
    internal List<(AggregateFunction Function, int NameStart, int ParenStart, int End)> AggregateCallsIn(int start, int length)
    {
        var found = new List<(AggregateFunction, int, int, int)>();
        _position = start;
        int end = start + length;
        while (_position < end)
        {
            var function = AggregateFunction.None;
            int nameLength = 0;
            foreach (var (name, aggregate) in AggregateNames)
            {
                if (AtKeyword(name))
                {
                    function = aggregate;
                    nameLength = name.Length;
                    break;
                }
            }
            if (function == AggregateFunction.None)
            {
                if (Peek() is '(' or ')') Advance(); // into and out of sub-expressions
                else SkipExpressionToken();
                continue;
            }
            int nameStart = _position;
            _position += nameLength;
            SkipWhitespace();
            if (Peek() != '(')
                continue; // a name that is not a call
            int parenStart = _position;
            SkipBracketedGroup();
            found.Add((function, nameStart, parenStart, _position));
        }
        return found;
    }

    /// <summary>
    /// True when <c>[start, start + length)</c> is one variable and nothing else, as the expression token rule reads
    /// it: <c>?o</c> is, <c>?o + 1</c> is not.
    /// </summary>
    internal bool IsOneVariable(int start, int length)
    {
        _position = start;
        int end = start + length;
        SkipWhitespace();
        if (_position >= end || !VariableName.IsSigil(Peek()))
            return false;
        SkipExpressionToken();
        while (_position < end && char.IsWhiteSpace(_source[_position]))
            _position++;
        return _position == end;
    }

    /// <summary>
    /// True when the expression at <c>[start, start + length)</c> reads the variable <paramref name="variable"/>
    /// (with or without its sigil). Read with the expression token rule, so a "?x" inside a literal or IRI is not one;
    /// the scan goes into sub-expressions.
    /// </summary>
    internal bool ExpressionReads(int start, int length, ReadOnlySpan<char> variable)
    {
        _position = start;
        int end = start + length;
        while (_position < end)
        {
            char ch = Peek();
            if (ch is '(' or ')' or '{' or '}') { Advance(); continue; } // into and out of sub-expressions
            int tokenStart = _position;
            SkipExpressionToken();
            if (VariableName.IsSigil(ch) && VariableName.Same(_source[tokenStart.._position], variable))
                return true;
        }
        return false;
    }

    private static readonly (string Name, AggregateFunction Function)[] AggregateNames =
    [
        ("COUNT", AggregateFunction.Count), ("SUM", AggregateFunction.Sum), ("AVG", AggregateFunction.Avg),
        ("MIN", AggregateFunction.Min), ("MAX", AggregateFunction.Max), ("SAMPLE", AggregateFunction.Sample),
        ("GROUP_CONCAT", AggregateFunction.GroupConcat),
    ];
}
