using System;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Parsing;

// ═══════════════════════════════════════════════════════════════════════════
// ADR-045 Step 2 — recursive parser producing the PatternArray tree directly.
//
// "A default graph is also a graph; one path, active-graph as a parameter."
//
// This is the future single parse path. It is built ALONGSIDE the shipping flat
// parser (SparqlParser.Clauses.cs / SparqlParser.cs) and is TEST/HARNESS-ONLY
// until the Step 4 cutover — the ~4,500 conformance tests keep running through the
// untouched shipping path. It reuses the shipping leaf-parsers verbatim (ParseTerm,
// ParsePredicateOrPath, ParseValuesValue) and the cursor primitives (Peek/Advance/
// SkipWhitespace/PeekSpan/ConsumeKeyword); ONLY the group-structure assembly is new.
//
// The win is structural, not behavioural: GRAPH recurses into the SAME group-body
// parser as everything else, so BIND / VALUES / FILTER / nested groups inside GRAPH
// are children of the GraphHeader BY CONSTRUCTION. The recurring GRAPH-divergence bug
// class (1.7.71 property-list, 1.8.3 aggregation, VALUES-in-GRAPH (D1), BIND-in-GRAPH
// (D2), PNAME-in-FILTER-in-GRAPH) becomes unrepresentable — there is no GRAPH-only
// body grammar to omit a feature from.
//
// HANDLED by this first increment (the spine):
//   GroupGraphPattern '{' … '}'        → GroupHeader
//   GRAPH (VarOrIri) '{' … '}'         → GraphHeader (active graph rebinds for the subtree)
//   { … } UNION { … } …                → UnionHeader { branch GroupHeaders }
//   OPTIONAL '{' … '}'                 → OptionalHeader
//   MINUS '{' … '}'                    → MinusHeader
//   TriplesBlock (subject verb object, ';' property-list shorthand)
//   FILTER (BrackettedExpression | BuiltInCall)   → Filter leaf (source span)
//   BIND '(' Expression 'AS' Var ')'              → Bind leaf (source spans)
//   VALUES Var '{' … '}' (single-variable form)   → ValuesHeader + ValuesEntry leaves
//
// DEFERRED to later Step 2 increments (before cutover) — kept explicit so nothing is
// silently mis-parsed: sub-SELECT, SERVICE, FILTER (NOT) EXISTS, multi-variable VALUES,
// RDF-star quoted triples, blank-node property lists, collections, and property-path
// sequence expansion. Each throws SparqlParseException on encounter rather than guessing.
// ═══════════════════════════════════════════════════════════════════════════

internal ref partial struct SparqlParser
{
    /// <summary>
    /// Parse one <c>GroupGraphPattern</c> at the cursor into the recursive <see cref="PatternArray"/> tree.
    /// The cursor must be positioned at the opening <c>'{'</c>. Returns the root header index (a GroupHeader).
    /// </summary>
    internal int ParsePatternTree(scoped ref PatternArray pa)
    {
        SkipWhitespace();
        return ParseGroupTree(ref pa, depth: 0);
    }

    /// <summary>
    /// [53] GroupGraphPattern ::= '{' ( SubSelect | GroupGraphPatternSub ) '}'
    /// Emits a GroupHeader whose direct children are the body's patterns. Returns the header index.
    /// </summary>
    private int ParseGroupTree(scoped ref PatternArray pa, int depth)
    {
        if (depth > _maxDepth)
            throw new SparqlParseException($"Group pattern nesting exceeds maximum depth of {_maxDepth}");

        SkipWhitespace();
        if (Peek() != '{')
            throw new SparqlParseException("Expected '{' to open a group graph pattern");
        Advance(); // consume '{'

        int header = pa.BeginGroupHeader(PatternKind.GroupHeader);
        ParseGroupBodyTree(ref pa, depth);
        pa.EndGroupHeader(header);

        SkipWhitespace();
        if (Peek() == '}')
            Advance(); // consume '}'
        return header;
    }

    /// <summary>
    /// [54] GroupGraphPatternSub ::= TriplesBlock? ( GraphPatternNotTriples '.'? TriplesBlock? )*
    /// The single body loop every group-like context recurses into — the heart of "one pattern path".
    /// Children are appended to whichever header is currently open (Group / Graph / Optional / Minus / Union branch).
    /// </summary>
    private void ParseGroupBodyTree(scoped ref PatternArray pa, int depth)
    {
        while (true)
        {
            SkipWhitespace();
            if (IsAtEnd() || Peek() == '}')
                break;

            // A '.' separates a TriplesBlock from a GraphPatternNotTriples in either order
            // ([54] GroupGraphPatternSub). Consume it here so it is handled uniformly after a triple
            // OR after a clause (GRAPH / OPTIONAL / FILTER / BIND / VALUES …), not only after a triple.
            if (Peek() == '.')
            {
                Advance();
                continue;
            }

            // Peek enough for the longest keyword (OPTIONAL = 8).
            var span = PeekSpan(8);

            if (Peek() == '{')
            {
                ParseGroupOrUnionTree(ref pa, depth);
                continue;
            }

            if (KeywordIs(span, "FILTER"))
            {
                EmitFilterTree(ref pa, depth);
                continue;
            }

            if (KeywordIs(span, "OPTIONAL"))
            {
                ParseWrappedGroupTree(ref pa, depth, PatternKind.OptionalHeader, "OPTIONAL");
                continue;
            }

            if (KeywordIs(span, "MINUS"))
            {
                ParseWrappedGroupTree(ref pa, depth, PatternKind.MinusHeader, "MINUS");
                continue;
            }

            if (KeywordIs(span, "BIND"))
            {
                EmitBindTree(ref pa, depth);
                continue;
            }

            if (KeywordIs(span, "VALUES"))
            {
                EmitValuesTree(ref pa, depth);
                continue;
            }

            if (KeywordIs(span, "GRAPH"))
            {
                ParseGraphTree(ref pa, depth);
                continue;
            }

            if (KeywordIs(span, "SERVICE"))
                throw new SparqlParseException("SERVICE is not yet handled by the recursive pattern parser (ADR-045 Step 2 increment)");

            if (KeywordIs(span, "SELECT"))
                throw new SparqlParseException("Sub-SELECT is not yet handled by the recursive pattern parser (ADR-045 Step 2 increment)");

            if (KeywordIs(span, "UNION"))
                throw new SparqlParseException("UNION requires graph patterns enclosed in braces: { pattern } UNION { pattern }");

            // Otherwise it is a triples block. Its trailing '.' is consumed at the loop top.
            if (!EmitTripleBlockTree(ref pa))
                break;
        }
    }

    /// <summary>
    /// [57] GroupOrUnionGraphPattern ::= GroupGraphPattern ( 'UNION' GroupGraphPattern )*
    /// Parses the first branch group, then — only if 'UNION' actually follows — wraps it (and the further
    /// branches) in a UnionHeader. A lone group stays a plain GroupHeader child: minimal tree, no degenerate
    /// single-branch union. The header kind is decided after the first branch, hence <see cref="PatternArray.WrapSubtree"/>.
    /// </summary>
    private void ParseGroupOrUnionTree(scoped ref PatternArray pa, int depth)
    {
        int firstBranch = ParseGroupTree(ref pa, depth + 1);

        SkipWhitespace();
        if (!KeywordIs(PeekSpan(5), "UNION"))
            return; // a single nested group, not a union

        int union = pa.WrapSubtree(firstBranch, PatternKind.UnionHeader);
        while (KeywordIs(PeekSpan(5), "UNION"))
        {
            ConsumeKeyword("UNION");
            SkipWhitespace();
            ParseGroupTree(ref pa, depth + 1); // each further branch is a GroupHeader child of the union
            SkipWhitespace();
        }
        pa.EndGroupHeader(union);
    }

    /// <summary>
    /// [58] GraphGraphPattern ::= 'GRAPH' VarOrIri GroupGraphPattern
    /// The construction that dissolves the GRAPH-divergence class: parse the graph term, then recurse into the
    /// SAME group-body parser. The GraphHeader rebinds the active graph for its subtree at evaluation time
    /// (default = the unnamed graph); BIND / VALUES / FILTER inside work because the body parser handles them.
    /// </summary>
    private void ParseGraphTree(scoped ref PatternArray pa, int depth)
    {
        if (depth + 1 > _maxDepth)
            throw new SparqlParseException($"Group pattern nesting exceeds maximum depth of {_maxDepth}");

        ConsumeKeyword("GRAPH");
        SkipWhitespace();

        var graphTerm = ParseTerm(); // VarOrIri
        if (graphTerm.Length == 0)
            throw new SparqlParseException("Expected a variable or IRI after GRAPH");

        SkipWhitespace();
        if (Peek() != '{')
            throw new SparqlParseException("Expected '{' after the GRAPH term");
        Advance(); // consume '{'

        int header = pa.BeginGraph(graphTerm.Type, graphTerm.Start, graphTerm.Length);
        ParseGroupBodyTree(ref pa, depth + 1);
        pa.EndGraph(header);

        SkipWhitespace();
        if (Peek() == '}')
            Advance(); // consume '}'
    }

    /// <summary>
    /// OPTIONAL / MINUS block form: a keyword followed by a GroupGraphPattern. The keyword's header directly
    /// owns the body (no redundant inner GroupHeader). Active graph is unchanged — it threads through unaltered.
    /// </summary>
    private void ParseWrappedGroupTree(scoped ref PatternArray pa, int depth, PatternKind headerKind, ReadOnlySpan<char> keyword)
    {
        if (depth + 1 > _maxDepth)
            throw new SparqlParseException($"Group pattern nesting exceeds maximum depth of {_maxDepth}");

        ConsumeKeyword(keyword);
        SkipWhitespace();
        if (Peek() != '{')
            throw new SparqlParseException($"Expected '{{' after {keyword.ToString()}");
        Advance(); // consume '{'

        int header = pa.BeginGroupHeader(headerKind);
        ParseGroupBodyTree(ref pa, depth + 1);
        pa.EndGroupHeader(header);

        SkipWhitespace();
        if (Peek() == '}')
            Advance(); // consume '}'
    }

    /// <summary>
    /// TriplesBlock: one subject followed by a predicate-object list, with ';' property-list shorthand
    /// (same subject, new predicate-object). Emits one Triple leaf per predicate-object pair. Returns false at a
    /// non-triple boundary so the body loop can stop. A property path of ANY form (^ / * + ? / | ! grouping) is
    /// carried as its full source span on the Triple slot, for the evaluator's path algebra — no path form is
    /// deferred (a deferral on the new path is exactly the divergence ADR-045 deletes). (RDF-star quoted triples
    /// and blank-node property lists remain deferred.)
    /// </summary>
    private bool EmitTripleBlockTree(scoped ref PatternArray pa)
    {
        SkipWhitespace();
        if (IsAtEnd() || Peek() == '}')
            return false;

        var subject = ParseTerm();
        if (subject.Type == TermType.Variable && subject.Length == 0)
            return false;
        if (subject.IsQuotedTriple)
            throw new SparqlParseException("RDF-star quoted triples are not yet handled by the recursive pattern parser (ADR-045 Step 2 increment)");

        SkipWhitespace();
        int pathStart = _position;
        var (predicate, path) = ParsePredicateOrPath();
        int pathLength = _position - pathStart;
        if (predicate.Length == 0 && path.Type == PathType.None)
            throw new SparqlParseException("Incomplete triple pattern - expected predicate");

        SkipWhitespace();
        var obj = ParseTerm();
        if (obj.Length == 0)
            throw new SparqlParseException("Incomplete triple pattern - expected object");
        if (obj.IsQuotedTriple)
            throw new SparqlParseException("RDF-star quoted triples are not yet handled by the recursive pattern parser (ADR-045 Step 2 increment)");

        EmitTriple(ref pa, subject, predicate, obj, path, pathStart, pathLength);

        // PropertyListNotEmpty ::= Verb ObjectList ( ';' ( Verb ObjectList )? )*
        SkipWhitespace();
        while (Peek() == ';')
        {
            Advance(); // consume ';'
            SkipWhitespace();
            if (IsAtEnd() || Peek() == '}' || Peek() == '.')
                break;
            if (IsBodyKeywordAhead())
                break;

            int nextPathStart = _position;
            var (nextPredicate, nextPath) = ParsePredicateOrPath();
            int nextPathLength = _position - nextPathStart;
            SkipWhitespace();
            var nextObj = ParseTerm();
            if (nextObj.Length == 0)
                throw new SparqlParseException("Incomplete triple pattern - expected object after ';'");

            EmitTriple(ref pa, subject, nextPredicate, nextObj, nextPath, nextPathStart, nextPathLength);
            SkipWhitespace();
        }

        return true;
    }

    /// <summary>
    /// Emit a Triple slot. For a property path (<paramref name="path"/>.Type != None) the slot carries the FULL
    /// path-expression source span (<paramref name="pathStart"/>..<paramref name="pathLength"/>) — not just the
    /// base IRI — so the evaluator re-parses and evaluates the complete path algebra; for a plain predicate the
    /// path fields are empty and PathKind is None.
    /// </summary>
    private void EmitTriple(scoped ref PatternArray pa, Term subject, Term predicate, Term obj, PropertyPath path,
        int pathStart, int pathLength)
    {
        int pathIriStart = path.Type == PathType.None ? path.Iri.Start : pathStart;
        int pathIriLength = path.Type == PathType.None ? path.Iri.Length : pathLength;
        pa.AddTriple(
            subject.Type, subject.Start, subject.Length,
            predicate.Type, predicate.Start, predicate.Length,
            obj.Type, obj.Start, obj.Length,
            path.Type, pathIriStart, pathIriLength);
    }

    /// <summary>
    /// FILTER constraint. <c>FILTER [NOT] EXISTS { … }</c> (bare or parenthesized) becomes an
    /// <c>ExistsHeader</c>/<c>NotExistsHeader</c> whose body is parsed by the same group-body loop — so it is a
    /// nested group, evaluated per solution with the current bindings in scope. Otherwise the constraint is
    /// captured as a source span (the leaf-level reuse the shipping parser performs) and emitted as a Filter leaf.
    /// </summary>
    private void EmitFilterTree(scoped ref PatternArray pa, int depth)
    {
        ConsumeKeyword("FILTER");
        SkipWhitespace();

        // FILTER [NOT] EXISTS { … }, optionally wrapped in one level of parentheses.
        int savedPos = _position;
        bool paren = false;
        if (Peek() == '(') { Advance(); SkipWhitespace(); paren = true; }

        var look = PeekSpan(10);
        bool negated = false;
        if (look.Length >= 3 && look[..3].Equals("NOT", StringComparison.OrdinalIgnoreCase) &&
            (look.Length < 4 || (!IsLetterOrDigit(look[3]) && look[3] != '_')))
        {
            ConsumeKeyword("NOT");
            SkipWhitespace();
            look = PeekSpan(6);
            negated = true;
        }
        if (KeywordIs(look, "EXISTS"))
        {
            ConsumeKeyword("EXISTS");
            SkipWhitespace();
            if (Peek() != '{')
                throw new SparqlParseException("Expected '{' after EXISTS");
            Advance(); // consume '{'
            int header = pa.BeginExists(negated);
            ParseGroupBodyTree(ref pa, depth + 1);
            pa.EndExists(header);
            SkipWhitespace();
            if (Peek() == '}') Advance();
            if (paren) { SkipWhitespace(); if (Peek() == ')') Advance(); }
            return;
        }

        // Not EXISTS — rewind any consumed '(' / NOT and parse a normal constraint.
        _position = savedPos;

        if (Peek() == '(')
        {
            // BrackettedExpression: FILTER(expr) — capture the interior, excluding the outer parentheses.
            Advance(); // consume '('
            int start = _position;
            int parenDepth = 1;
            while (!IsAtEnd() && parenDepth > 0)
            {
                char ch = Advance();
                if (ch == '(') parenDepth++;
                else if (ch == ')') parenDepth--;
            }
            int length = _position - start - 1; // exclude the closing ')'
            pa.AddFilter(start, length, depth);
        }
        else if (IsLetter(Peek()))
        {
            // BuiltInCall / FunctionCall: FILTER CONTAINS(...) — capture name + argument list.
            int start = _position;
            while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_' || Peek() == ':'))
                Advance();
            SkipWhitespace();
            if (Peek() == '(')
            {
                int parenDepth = 1;
                Advance(); // consume '('
                while (!IsAtEnd() && parenDepth > 0)
                {
                    char ch = Advance();
                    if (ch == '(') parenDepth++;
                    else if (ch == ')') parenDepth--;
                }
            }
            int length = _position - start;
            pa.AddFilter(start, length, depth);
        }
        else
        {
            throw new SparqlParseException("Expected '(' or a built-in call after FILTER");
        }
    }

    /// <summary>
    /// [60] Bind ::= 'BIND' '(' Expression 'AS' Var ')'. Captures the expression span (everything up to 'AS' at
    /// paren-depth 0) and the target variable span, emitting a Bind leaf at the current depth. AfterPatternIndex
    /// is -1: in the tree the executor evaluates children in document order, so position is structural.
    /// </summary>
    private void EmitBindTree(scoped ref PatternArray pa, int depth)
    {
        ConsumeKeyword("BIND");
        SkipWhitespace();
        if (Peek() != '(')
            throw new SparqlParseException("Expected '(' after BIND");
        Advance(); // consume '('
        SkipWhitespace();

        int exprStart = _position;
        int parenDepth = 0;
        while (!IsAtEnd())
        {
            char ch = Peek();
            if (ch == '(')
            {
                parenDepth++;
                Advance();
            }
            else if (ch == ')')
            {
                if (parenDepth == 0)
                    break;
                parenDepth--;
                Advance();
            }
            else
            {
                if (parenDepth == 0)
                {
                    var span = PeekSpan(3);
                    if (span.Length >= 2 && span[..2].Equals("AS", StringComparison.OrdinalIgnoreCase) &&
                        (span.Length < 3 || !IsLetterOrDigit(span[2])))
                        break;
                }
                Advance();
            }
        }
        int exprLength = _position - exprStart;
        while (exprLength > 0 && char.IsWhiteSpace(_source[exprStart + exprLength - 1]))
            exprLength--;

        SkipWhitespace();
        ConsumeKeyword("AS");
        SkipWhitespace();

        int varStart = _position;
        if (Peek() == '?')
        {
            Advance(); // consume '?'
            while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                Advance();
        }
        int varLength = _position - varStart;
        if (varLength == 0)
            throw new SparqlParseException("Expected a target variable after AS in BIND");

        SkipWhitespace();
        if (Peek() == ')')
            Advance(); // consume ')'

        pa.AddBind(exprStart, exprLength, varStart, varLength, afterPatternIndex: -1, scopeDepth: depth);
    }

    /// <summary>
    /// [61] InlineData ::= 'VALUES' DataBlock. Single-variable form: <c>VALUES ?v { val … }</c>. Emits a
    /// ValuesHeader followed by one ValuesEntry per value (UNDEF carried as length -1, matching the shipping
    /// parser). Multi-variable VALUES <c>( ?a ?b )</c> is a deferred increment (the slot stores one variable).
    /// </summary>
    private void EmitValuesTree(scoped ref PatternArray pa, int depth)
    {
        ConsumeKeyword("VALUES");
        SkipWhitespace();

        if (Peek() == '(')
            throw new SparqlParseException("Multi-variable VALUES is not yet handled by the recursive pattern parser (ADR-045 Step 2 increment)");
        if (Peek() != '?')
            throw new SparqlParseException("Expected a variable after VALUES");

        int varStart = _position;
        Advance(); // consume '?'
        while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
            Advance();
        int varLength = _position - varStart;

        SkipWhitespace();
        if (Peek() != '{')
            throw new SparqlParseException("Expected '{' after the VALUES variable");
        Advance(); // consume '{'

        int header = pa.AddValuesHeader(varStart, varLength);
        SkipWhitespace();
        while (!IsAtEnd() && Peek() != '}')
        {
            var span = PeekSpan(5);
            if (KeywordIs(span, "UNDEF"))
            {
                ConsumeKeyword("UNDEF");
                pa.AddValuesEntry(0, -1, header); // UNDEF marker
            }
            else
            {
                int valueStart = _position;
                int valueLen = ParseValuesValue(); // shipping leaf-parser: advances the cursor, returns length
                if (valueLen <= 0)
                    break;
                pa.AddValuesEntry(valueStart, valueLen, header);
            }
            SkipWhitespace();
        }

        if (Peek() == '}')
            Advance(); // consume '}'
    }

    /// <summary>True if a body keyword (one that ends a property list) begins <paramref name="span"/>.</summary>
    private static bool KeywordIs(ReadOnlySpan<char> span, ReadOnlySpan<char> keyword)
        => span.Length >= keyword.Length && span[..keyword.Length].Equals(keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>Lookahead used inside a property list to stop before a keyword that begins a new clause.</summary>
    private bool IsBodyKeywordAhead()
    {
        var span = PeekSpan(8);
        return KeywordIs(span, "FILTER") || KeywordIs(span, "OPTIONAL") || KeywordIs(span, "MINUS")
            || KeywordIs(span, "BIND") || KeywordIs(span, "VALUES") || KeywordIs(span, "SERVICE")
            || KeywordIs(span, "GRAPH");
    }
}
