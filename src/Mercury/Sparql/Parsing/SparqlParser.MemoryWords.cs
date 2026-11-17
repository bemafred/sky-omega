using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Parsing;

// ═══════════════════════════════════════════════════════════════════════════
// ADR-061 D6, D10 — the words of Cognitive SPARQL.
//
//   TriplePattern Words?            Words ::= Word Word?, the two of different kinds
//   Word  ::= ( 'MEMORIZED' | 'FORGOTTEN' ) Var? ( ( 'SINCE' | 'BEFORE' ) Bound )*, each bound at most once
//   Bound ::= an expression without variables, up to the next word or bound, or the end of the triple pattern
//
//   Query SolutionModifier 'INCLUDING' 'FORGOTTEN'?, on the outermost query only
//
// The words follow the object of a triple pattern, where SPARQL 1.1 admits no bare word, so no standard query
// reaches them (D9). A pattern with a property-path operator matches no single stored period, and SERVICE is
// answered by another store: the words are an error on either.
// ═══════════════════════════════════════════════════════════════════════════

internal ref partial struct SparqlParser
{
    /// <summary>True when a word of Cognitive SPARQL is at the cursor.</summary>
    private readonly bool AtMemoryWord() => AtKeyword("MEMORIZED") || AtKeyword("FORGOTTEN");

    /// <summary>
    /// The words after the object of the triple pattern just added to <paramref name="pa"/>, whose verb is
    /// <paramref name="path"/>. Their span, and whether FORGOTTEN is among them, go into the pattern's slot.
    /// </summary>
    private void AttachMemoryWords(scoped ref PatternArray pa, PropertyPath path)
    {
        if (path.Type != PathType.None)
            throw new SparqlParseException(
                $"MEMORIZED and FORGOTTEN follow a triple pattern that matches one stored period; a pattern with a property path matches none (ADR-061 D10), at position {_position}");
        if (_serviceDepth > 0)
            throw new SparqlParseException(
                $"MEMORIZED and FORGOTTEN are not allowed inside SERVICE, which another store answers (ADR-061 D10), at position {_position}");

        int start = _position;
        var words = ParseMemoryWords();
        var slot = pa[pa.Count - 1];
        slot.WordsStart = start;
        slot.WordsLength = _position - start;
        slot.CarriesForgotten = words.Forgotten.IsWritten;
    }

    /// <summary>The words at <paramref name="position"/>, read as the parser read them: for the executor.</summary>
    internal MemoryWords ParseMemoryWordsAt(int position)
    {
        _position = position;
        return ParseMemoryWords();
    }

    /// <summary>Reads the words at the cursor (ADR-061 D6, D10); the cursor ends after them.</summary>
    private MemoryWords ParseMemoryWords()
    {
        var words = new MemoryWords();
        while (true)
        {
            SkipWhitespace();
            bool memorized = AtKeyword("MEMORIZED");
            if (!memorized && !AtKeyword("FORGOTTEN"))
                return words;

            string name = memorized ? "MEMORIZED" : "FORGOTTEN";
            ref var word = ref memorized ? ref words.Memorized : ref words.Forgotten;
            if (word.IsWritten)
                throw new SparqlParseException($"A triple pattern takes {name} at most once (ADR-061 D10), at position {_position}");
            ConsumeKeyword(name);
            word.IsWritten = true;

            SkipWhitespace();
            if (!IsAtEnd() && VariableName.IsSigil(Peek()))
            {
                var variable = ParseVariable();
                word.VariableStart = variable.Start;
                word.VariableLength = variable.Length;
                SkipWhitespace();
            }

            while (true)
            {
                bool since = AtKeyword("SINCE");
                if (!since && !AtKeyword("BEFORE"))
                    break;
                string keyword = since ? "SINCE" : "BEFORE";
                if ((since ? word.SinceLength : word.BeforeLength) > 0)
                    throw new SparqlParseException($"{name} takes {keyword} at most once (ADR-061 D10), at position {_position}");
                ConsumeKeyword(keyword);
                var (start, length) = ParseMemoryBound(keyword);
                if (since)
                {
                    word.SinceStart = start;
                    word.SinceLength = length;
                }
                else
                {
                    word.BeforeStart = start;
                    word.BeforeLength = length;
                }
                SkipWhitespace();
            }
        }
    }

    /// <summary>
    /// A bound: an expression read with the expression token rule, up to the next word or bound, or the end of the
    /// triple pattern. It names no variable: a bound that depends on one is a FILTER on the bound time (ADR-061 D10).
    /// </summary>
    private (int Start, int Length) ParseMemoryBound(string keyword)
    {
        SkipWhitespace();
        int start = _position, end = _position;
        while (!IsAtEnd())
        {
            char ch = Peek();
            // A group may follow a triples statement with no '.' between them ([54] GroupGraphPatternSub)
            if (ch is '.' or ';' or ',' or '{' or '}' or ']' || AtKeyword("SINCE") || AtKeyword("BEFORE") || AtMemoryWord()
                || IsBodyKeywordAhead())
                break;
            SkipExpressionToken();
            end = _position;
            SkipWhitespace();
        }
        if (end == start)
            throw new SparqlParseException($"Expected a time after {keyword} (ADR-061 D10), at position {_position}");

        // A variable inside a group or a call is found too: every '?' or '$' outside a literal or an IRI is one
        int after = _position;
        _position = start;
        while (_position < end)
        {
            char c = Peek();
            if (c is '"' or '\'')
            {
                SkipStringLiteral();
                continue;
            }
            if (c == '<' && TrySkipIriRef())
                continue;
            if (VariableName.IsSigil(c))
                throw new SparqlParseException(
                    $"A bound of MEMORIZED or FORGOTTEN is an expression without variables (ADR-061 D10); a bound that depends on a variable is a FILTER on the bound time, at position {_position}");
            Advance();
        }
        _position = after;
        return (start, end - start);
    }

    /// <summary>
    /// <c>INCLUDING FORGOTTEN</c> after a query's solution modifiers (ADR-061 D6): every pattern of the query matches
    /// forgotten memories too.
    /// </summary>
    private bool ParseIncludingForgotten()
    {
        if (!AtKeyword("INCLUDING"))
            return false;
        ConsumeKeyword("INCLUDING");
        SkipWhitespace();
        if (!AtKeyword("FORGOTTEN"))
            throw new SparqlParseException($"Expected FORGOTTEN after INCLUDING (ADR-061 D6), at position {_position}");
        ConsumeKeyword("FORGOTTEN");
        return true;
    }
}
