using System;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Parsing;

internal ref partial struct SparqlParser
{
    /// <summary>
    /// Reads [11] DescribeQuery's <c>VarOrIri+</c> from the source, one target at a time. The parser reads the list
    /// with it to check it, and the executor to collect what to describe, so the two cannot disagree; the list has no
    /// size limit. A target is a variable (<c>?x</c>, <c>$x</c>), an IRI (<c>&lt;…&gt;</c>) or a prefixed name (a token
    /// with a <c>:</c>); the list ends at the first token that is none of these: <c>{</c>, a keyword (WHERE, FROM, a
    /// solution modifier, VALUES, Mercury's AS OF, DURING and ALL VERSIONS) or the end of the query. A keyword is
    /// told from a prefixed name by its colon, so <c>orders:x</c> is a target.
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
            if (_parser.IsAtEnd() || !StartsTarget(_parser._source[_parser._position..]))
            {
                _parser._position = end;
                return false;
            }
            target = _parser.ParseTerm();
            if (target.Length == 0 || (target.Type != TermType.Variable && target.Type != TermType.Iri))
                throw new SparqlParseException("DESCRIBE expects variables, IRIs or '*'");
            return true;
        }

        /// <summary>True when <paramref name="rest"/> starts with a variable, an IRI or a prefixed name.</summary>
        private static bool StartsTarget(ReadOnlySpan<char> rest)
        {
            if (VariableName.IsSigil(rest[0]) || rest[0] == '<')
                return true;
            for (int i = 0; i < rest.Length && !char.IsWhiteSpace(rest[i]) && rest[i] is not ('{' or '}' or '"' or '\'' or '('); i++)
            {
                if (rest[i] == ':')
                    return true;
            }
            return false;
        }
    }
}
