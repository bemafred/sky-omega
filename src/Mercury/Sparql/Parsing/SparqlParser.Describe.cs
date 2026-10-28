using System;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Parsing;

internal ref partial struct SparqlParser
{
    /// <summary>
    /// Reads [11] DescribeQuery's <c>VarOrIri+</c> from the source, one target at a time. The parser reads the list
    /// with it to check it, and the executor to collect what to describe, so the two cannot disagree; the list has no
    /// size limit. The list ends at the first token that is not a variable or IRI: <c>{</c>, a clause keyword or the
    /// end of the query.
    /// </summary>
    internal ref struct DescribeTargetReader
    {
        private SparqlParser _parser;

        /// <param name="source">The query source.</param>
        /// <param name="targetsStart">Offset of the first target.</param>
        public DescribeTargetReader(ReadOnlySpan<char> source, int targetsStart)
        {
            _parser = new SparqlParser(source);
            _parser._position = targetsStart;
        }

        /// <summary>Offset just past the last target.</summary>
        public readonly int End => _parser._position;

        /// <summary>The next target, a variable or an IRI (written in full or prefixed); false after the last.</summary>
        public bool MoveNext(out Term target)
        {
            target = default;
            int end = _parser._position;
            _parser.SkipWhitespace();
            char ch = _parser.Peek();
            var ahead = _parser.PeekSpan(6);
            if (_parser.IsAtEnd() || ch == '{' || KeywordIs(ahead, "WHERE") || KeywordIs(ahead, "FROM") ||
                KeywordIs(ahead, "ORDER") || KeywordIs(ahead, "GROUP") || KeywordIs(ahead, "HAVING") ||
                KeywordIs(ahead, "LIMIT") || KeywordIs(ahead, "OFFSET") || KeywordIs(ahead, "VALUES"))
            {
                _parser._position = end;
                return false;
            }
            target = _parser.ParseTerm();
            if (target.Length == 0 || (target.Type != TermType.Variable && target.Type != TermType.Iri))
                throw new SparqlParseException("DESCRIBE expects variables, IRIs or '*'");
            return true;
        }
    }
}
