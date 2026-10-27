using System;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Parsing;

internal ref partial struct SparqlParser
{
    /// <summary>
    /// [28] ValuesClause / [61] InlineData ::= 'VALUES' DataBlock: checks the clause at the cursor, moves past it and
    /// returns where it stands. Every VALUES is read this way: inside a WHERE, after a query and after a sub-SELECT.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)] // keeps the reader out of the recursive group parser's frame
    private ValuesClause ParseValuesClause()
    {
        int start = _position;
        var reader = new DataBlockReader(_source, start);
        while (reader.MoveNextVariable(out _, out _)) { }
        while (reader.MoveNextRow())
            while (reader.MoveNextValue(out _, out _)) { }
        _position = reader.End;
        return new ValuesClause { Start = start, Length = _position - start };
    }

    /// <summary>
    /// Reads <c>'VALUES' DataBlock</c> from the source, one variable and one value at a time. The parser reads a clause
    /// with it to check it, and the executor to bind its rows, so the two cannot disagree; reading the source as it
    /// goes, a data block has no size limit and reading it allocates nothing.
    /// <code>
    /// [62] DataBlock        ::= InlineDataOneVar | InlineDataFull
    /// [63] InlineDataOneVar ::= Var '{' DataBlockValue* '}'
    /// [64] InlineDataFull   ::= ( NIL | '(' Var* ')' ) '{' ( '(' DataBlockValue* ')' | NIL )* '}'
    /// [65] DataBlockValue   ::= iri | RDFLiteral | NumericLiteral | BooleanLiteral | 'UNDEF'
    /// </code>
    /// Read the variables with <see cref="MoveNextVariable"/> until it returns false, then each row with
    /// <see cref="MoveNextRow"/> and its values with <see cref="MoveNextValue"/>. A malformed block throws.
    /// </summary>
    internal ref struct DataBlockReader
    {
        private SparqlParser _parser;
        private readonly bool _full; // InlineDataFull: the variables and every row are parenthesized
        private int _variableCount;
        private int _column;
        private bool _inRows;
        private bool _done;

        /// <param name="source">The query source.</param>
        /// <param name="valuesStart">Offset of the <c>VALUES</c> keyword.</param>
        public DataBlockReader(ReadOnlySpan<char> source, int valuesStart)
        {
            _parser = new SparqlParser(source);
            _parser._position = valuesStart;
            _parser.ConsumeKeyword("VALUES");
            _parser.SkipWhitespace();
            char ch = _parser.Peek();
            if (ch == '(')
            {
                _parser.Advance();
                _full = true;
            }
            else if (ch != '?' && ch != '$')
            {
                throw new SparqlParseException("Expected a variable or '(' after VALUES");
            }
        }

        public readonly int VariableCount => _variableCount;

        /// <summary>Offset just past the closing <c>'}'</c>, once every row has been read.</summary>
        public readonly int End => _parser._position;

        /// <summary>The next variable, with its sigil; false after the last, with the cursor inside the data's '{'.</summary>
        public bool MoveNextVariable(out int start, out int length)
        {
            start = length = 0;
            if (_inRows) return false;
            _parser.SkipWhitespace();
            if (_full ? _parser.Peek() != ')' : _variableCount == 0)
            {
                char ch = _parser.Peek();
                if (ch != '?' && ch != '$')
                    throw new SparqlParseException(_full ? "Expected a variable or ')' in VALUES" : "Expected a variable after VALUES");
                var variable = _parser.ParseVariable();
                start = variable.Start;
                length = variable.Length;
                _variableCount++;
                return true;
            }
            if (_full) _parser.Advance(); // ')'
            _parser.SkipWhitespace();
            if (_parser.Peek() != '{')
                throw new SparqlParseException("Expected '{' to open the VALUES data");
            _parser.Advance();
            _inRows = true;
            return false;
        }

        /// <summary>Opens the next row; false after the last, with the cursor past the closing '}'.</summary>
        public bool MoveNextRow()
        {
            if (_done) return false;
            if (!_inRows)
                throw new InvalidOperationException("Read the VALUES variables before its rows");
            _parser.SkipWhitespace();
            if (_parser.IsAtEnd())
                throw new SparqlParseException("Expected '}' to close the VALUES data");
            if (_parser.Peek() == '}')
            {
                _parser.Advance();
                _done = true;
                return false;
            }
            _column = 0;
            if (_full)
            {
                if (_parser.Peek() != '(')
                    throw new SparqlParseException("Expected '(' to open a VALUES row");
                _parser.Advance();
            }
            return true;
        }

        /// <summary>The row's next value, in variable order; <paramref name="length"/> is -1 for UNDEF. False after the last.</summary>
        public bool MoveNextValue(out int start, out int length)
        {
            start = length = 0;
            _parser.SkipWhitespace();
            if (_full)
            {
                if (_parser.Peek() == ')')
                {
                    if (_column != _variableCount)
                        throw new SparqlParseException($"VALUES row has {_column} values but {_variableCount} variables declared");
                    _parser.Advance();
                    return false;
                }
                if (_column == _variableCount)
                    throw new SparqlParseException($"VALUES row has more values than its {_variableCount} variables");
            }
            else if (_column == 1)
            {
                return false; // InlineDataOneVar: a row is one value
            }

            _column++;
            var ahead = _parser.PeekSpan(6);
            if (KeywordIs(ahead, "UNDEF") && (ahead.Length == 5 || !IsLetterOrDigit(ahead[5])))
            {
                _parser.ConsumeKeyword("UNDEF");
                length = -1;
                return true;
            }
            if (_parser.IsAtEnd())
                throw new SparqlParseException("Expected '}' to close the VALUES data");
            var term = _parser.ParseTerm();
            if (term.Length == 0 || (term.Type != TermType.Iri && term.Type != TermType.Literal))
                throw new SparqlParseException("A VALUES value must be an IRI, a literal or UNDEF");
            start = term.Start;
            length = term.Length;
            return true;
        }
    }
}
