using System;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Parsing;

// ═══════════════════════════════════════════════════════════════════════════
// ADR-045 Step 2 — recursive parser producing the PatternArray tree directly.
//
// "A default graph is also a graph; one path, active-graph as a parameter."
//
// This is the group grammar every WHERE is evaluated with: a query's (since the ADR-045/047
// cutover, TreeJoinExecutor runs every query WHERE from this tree) and an Update's (ADR-052:
// Modify's WHERE and DELETE WHERE's QuadPattern are parsed only here). It reuses the shared
// leaf-parsers verbatim (ParseTerm, ParsePredicateOrPath, ParseValues) and the cursor
// primitives (Peek/Advance/SkipWhitespace/PeekSpan/ConsumeKeyword).
//
// The win is structural, not behavioural: GRAPH recurses into the SAME group-body
// parser as everything else, so BIND / VALUES / FILTER / nested groups inside GRAPH
// are children of the GraphHeader BY CONSTRUCTION. The recurring GRAPH-divergence bug
// class (1.7.71 property-list, 1.8.3 aggregation, VALUES-in-GRAPH (D1), BIND-in-GRAPH
// (D2), PNAME-in-FILTER-in-GRAPH) becomes unrepresentable — there is no GRAPH-only
// body grammar to omit a feature from.
//
// HANDLED (the full common pattern grammar):
//   GroupGraphPattern '{' … '}'        → GroupHeader        |  GRAPH (VarOrIri) '{' … '}'  → GraphHeader
//   { … } UNION { … } …                → UnionHeader        |  OPTIONAL '{' … '}'           → OptionalHeader
//   MINUS '{' … '}'                    → MinusHeader        |  { SELECT … }                 → SubSelectHeader over its WHERE
//   SERVICE [SILENT] ep '{' … '}'      → ServiceHeader      |  FILTER [NOT] EXISTS { … }    → (Not)ExistsHeader
//   TriplesBlock (one property-list reader, EmitPropertyListTree, for a subject and for a blank-node property list;
//     ALL property-path forms ^ / | * + ? ( ) ! carried as the full path span; RDF-star quoted triples expand to
//     their reifier)
//   FILTER (BrackettedExpression | BuiltInCall) → Filter    |  BIND ( Expr AS Var )         → Bind
//   VALUES DataBlock (single- and multi-variable forms) → ValuesHeader carrying the clause span
//   RDF collections ( '(' a b c ')' ) expand to rdf:first / rdf:rest triples (EmitNodeTree)
//
// Every group, GRAPH, OPTIONAL, MINUS and EXISTS body must be closed by its '}'; an unclosed one is a syntax
// error, never a guess (this parser is the only one an Update WHERE passes through).
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
    /// ADR-045 cutover: parse the WHERE group at <paramref name="position"/> (the opening <c>'{'</c>) over THIS
    /// parser's source — the FULL query — so the tree's slot offsets index the same source as the prologue
    /// prefixes. Used to wire the executor against a whole query (the prefixes live in the prologue, not the group).
    /// </summary>
    internal int ParsePatternTreeAt(int position, scoped ref PatternArray pa)
    {
        _position = position;
        _blankNodeLabels = null;
        _basicPatternCount = 0;
        return ParsePatternTree(ref pa);
    }

    /// <summary>
    /// [53] GroupGraphPattern ::= '{' ( SubSelect | GroupGraphPatternSub ) '}'
    /// A <c>{ SELECT … }</c> body is a sub-SELECT, a SubSelectHeader whose child is its WHERE group (ADR-055 D4).
    /// Otherwise emits a GroupHeader over the body. Returns the index.
    /// </summary>
    private int ParseGroupTree(scoped ref PatternArray pa, int depth)
    {
        if (depth > _maxDepth)
            throw new SparqlParseException($"Group pattern nesting exceeds maximum depth of {_maxDepth}");

        SkipWhitespace();
        if (Peek() != '{')
            throw new SparqlParseException("Expected '{' to open a group graph pattern");
        Advance(); // consume '{'
        SkipWhitespace();

        int header;
        if (AtKeyword("SELECT"))
        {
            // SubSelect: its WHERE is a child subtree, evaluated with the active graph threaded in
            header = pa.Count;
            ParseSubSelectTree(ref pa, depth);
        }
        else
        {
            header = pa.BeginGroupHeader(PatternKind.GroupHeader);
            ParseGroupBodyTree(ref pa, depth);
            pa.EndGroupHeader(header);
        }

        SkipWhitespace();
        ExpectChar('}'); // an unclosed group is a syntax error, not content to guess at
        return header;
    }

    /// <summary>
    /// [54] GroupGraphPatternSub ::= TriplesBlock? ( GraphPatternNotTriples '.'? TriplesBlock? )*
    /// [55] TriplesBlock ::= TriplesSameSubjectPath ( '.' TriplesBlock? )?
    /// The single body loop every group-like context recurses into — the heart of "one pattern path".
    /// Children are appended to whichever header is currently open (Group / Graph / Optional / Minus / Union branch).
    /// </summary>
    private void ParseGroupBodyTree(scoped ref PatternArray pa, int depth)
    {
        int groupFirstSlot = pa.Count; // this group's children (and their subtrees) are appended from here
        // The '.' rules of [54] and [55]: one '.' may follow a triples statement or a clause (GRAPH, OPTIONAL, FILTER,
        // BIND, VALUES, a group …); two triples statements need a '.' between them; a group never starts with one.
        bool dotAllowed = false;  // the element before may be followed by a '.'
        bool triplesOpen = false; // the element before is a triples statement with no '.' after it
        // A basic graph pattern is a run of triples statements; a FILTER doesn't end it, any other clause does
        // (§18.2.2.1 removes the FILTERs before adjacent triples are collected)
        int basicPattern = 0;
        bool basicPatternOpen = false;
        while (true)
        {
            SkipWhitespace();
            if (IsAtEnd() || Peek() == '}')
                break;

            if (Peek() == '.')
            {
                if (!dotAllowed)
                    throw new SparqlParseException($"Unexpected '.' at position {_position}");
                Advance();
                dotAllowed = false;
                triplesOpen = false;
                continue;
            }
            bool needsDot = triplesOpen;
            dotAllowed = true;
            triplesOpen = false;
            bool continuesBasicPattern = basicPatternOpen;
            basicPatternOpen = basicPatternOpen && AtKeyword("FILTER");

            // Peek enough for the longest keyword (OPTIONAL = 8).
            var span = PeekSpan(8);

            if (Peek() == '{')
            {
                ParseGroupOrUnionTree(ref pa, depth);
                continue;
            }

            if (AtKeyword("FILTER"))
            {
                EmitFilterTree(ref pa, depth);
                continue;
            }

            if (AtKeyword("OPTIONAL"))
            {
                ParseWrappedGroupTree(ref pa, depth, PatternKind.OptionalHeader, "OPTIONAL");
                continue;
            }

            if (AtKeyword("MINUS"))
            {
                ParseWrappedGroupTree(ref pa, depth, PatternKind.MinusHeader, "MINUS");
                continue;
            }

            if (AtKeyword("BIND"))
            {
                EmitBindTree(ref pa, depth, groupFirstSlot);
                continue;
            }

            if (AtKeyword("VALUES"))
            {
                EmitValuesTree(ref pa, depth);
                continue;
            }

            if (AtKeyword("GRAPH"))
            {
                ParseGraphTree(ref pa, depth);
                continue;
            }

            if (AtKeyword("SERVICE"))
            {
                ParseServiceTree(ref pa, depth);
                continue;
            }

            // A sub-SELECT is `{ SELECT … }`, caught at the group level (the '{' branch above → ParseGroupTree).
            // A bare SELECT here is an unwrapped subquery — a syntax error, not a missing feature.
            if (AtKeyword("SELECT"))
                throw new SparqlParseException("SELECT (subquery) must be wrapped in braces: { SELECT ... }");

            if (AtKeyword("UNION"))
                throw new SparqlParseException("UNION requires graph patterns enclosed in braces: { pattern } UNION { pattern }");

            // Otherwise it is a triples statement. Its trailing '.' is consumed at the loop top.
            if (needsDot)
                throw new SparqlParseException($"Expected '.' between two triple patterns at position {_position}");
            if (!continuesBasicPattern)
                basicPattern = ++_basicPatternCount;
            basicPatternOpen = true;
            _basicPattern = basicPattern;
            if (!EmitTripleBlockTree(ref pa))
                break;
            triplesOpen = true;
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
        if (!AtKeyword("UNION"))
            return; // a single nested group, not a union

        int union = pa.WrapSubtree(firstBranch, PatternKind.UnionHeader);
        while (AtKeyword("UNION"))
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

        var graphTerm = ParseTerm(); // VarOrIri: not a blank node (W3C syn-bad-31) or a literal
        if (graphTerm.Length == 0 || (graphTerm.Type != TermType.Variable && graphTerm.Type != TermType.Iri))
            throw new SparqlParseException("Expected a variable or IRI after GRAPH");

        SkipWhitespace();
        if (Peek() != '{')
            throw new SparqlParseException("Expected '{' after the GRAPH term");
        Advance(); // consume '{'

        int header = pa.BeginGraph(graphTerm.Type, graphTerm.Start, graphTerm.Length);
        SkipWhitespace();
        if (AtKeyword("SELECT"))
        {
            // GRAPH g { SELECT … } — the GroupGraphPattern is a SubSelect ([53] GroupGraphPattern ::= '{' SubSelect '}'),
            // e.g. W3C agg-empty-group-count-graph: GRAPH ?g { SELECT (count(*) AS ?c) WHERE { … } }. Carry the
            // sub-SELECT span as a SubSelectHeader child of the GraphHeader; the evaluator threads the active graph into
            // it, so an aggregating sub-SELECT is computed PER graph (one row per named graph, count 0 for empty ones).
            // (The double-braced form GRAPH g { { SELECT … } } already reaches this via ParseGroupOrUnionTree below.)
            ParseSubSelectTree(ref pa, depth + 1);
        }
        else
        {
            ParseGroupBodyTree(ref pa, depth + 1);
        }
        pa.EndGraph(header);

        SkipWhitespace();
        ExpectChar('}'); // an unclosed group is a syntax error, not content to guess at
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

        int header = pa.BeginGroupHeader(headerKind);
        int open = _position;
        Advance(); // consume '{'
        SkipWhitespace();
        if (AtKeyword("SELECT"))
        {
            // [53] GroupGraphPattern ::= '{' SubSelect '}': the block is a sub-SELECT, its header's one child
            _position = open;
            ParseGroupTree(ref pa, depth + 1);
            pa.EndGroupHeader(header);
            return;
        }
        ParseGroupBodyTree(ref pa, depth + 1);
        pa.EndGroupHeader(header);

        SkipWhitespace();
        ExpectChar('}'); // an unclosed group is a syntax error, not content to guess at
    }

    /// <summary>
    /// [75] TriplesSameSubjectPath ::= VarOrTerm PropertyListPathNotEmpty | TriplesNodePath PropertyListPath: a subject and
    /// its property list. A blank-node property list or a collection may stand alone (its PropertyListPath empty); ANON,
    /// <c>[]</c>, may not (W3C syn-bad-bnode-dot). Returns false at a non-triple boundary so the body loop can stop.
    /// </summary>
    private bool EmitTripleBlockTree(scoped ref PatternArray pa)
    {
        SkipWhitespace();
        if (IsAtEnd() || Peek() == '}')
            return false;

        var written = ParseTerm();
        if (written.Type == TermType.Variable && written.Length == 0)
            return false;

        bool triplesNode = IsCollection(written) || (IsBlankNodePropertyList(written) && !IsAnon(written));
        // A subject that is a node of its own ([ … ], a collection, a quoted triple) is one node for its whole property
        // list: its node is the subject of every triple below
        var subject = EmitNodeTree(ref pa, written);
        SkipWhitespace();
        if (triplesNode && (IsAtEnd() || Peek() is '.' or '}' or '{' || IsBodyKeywordAhead()))
            return true;
        EmitPropertyListTree(ref pa, subject);
        return true;
    }

    /// <summary>
    /// [83] PropertyListPathNotEmpty ::= ( VerbPath | VerbSimple ) ObjectListPath ( ';' ( ( VerbPath | VerbSimple )
    /// ObjectList )? )* for <paramref name="subject"/>, up to a '.', '}', ']' or a clause. The one property-list reader
    /// of a pattern: a subject's, and a blank-node property list's (<see cref="EmitNodeTree"/>). A property path of any
    /// form is carried as its whole source span on the Triple slot, for the evaluator's path algebra.
    /// </summary>
    private void EmitPropertyListTree(scoped ref PatternArray pa, Term subject)
    {
        while (true)
        {
            SkipWhitespace();
            int pathStart = _position;
            var (predicate, path) = ParsePredicateOrPath();
            int pathLength = _position - pathStart;
            if (predicate.Length == 0 && path.Type == PathType.None)
                throw new SparqlParseException($"Incomplete triple pattern - expected predicate at position {pathStart}");

            // [86] ObjectListPath ::= ObjectPath ( ',' ObjectPath )*
            while (true)
            {
                SkipWhitespace();
                var written = ParseTerm();
                // A synthetic term (negative offset: an empty collection () is rdf:nil at -8) is valid at Length 0
                if (written.Length == 0 && written.Start >= 0)
                    throw new SparqlParseException($"Incomplete triple pattern - expected object at position {_position}");
                var obj = EmitNodeTree(ref pa, written);
                AddPathTriple(ref pa, subject, predicate, obj, path, pathStart, pathLength);
                SkipWhitespace();
                if (Peek() != ',')
                    break;
                Advance();
            }

            if (Peek() != ';')
                return;
            // ';' may repeat and may end the list: "?s :p ?o ; ; ." and "?s :p ?o ;" are both valid
            while (Peek() == ';')
            {
                Advance();
                SkipWhitespace();
            }
            if (IsAtEnd() || Peek() is '.' or '}' or ']' || IsBodyKeywordAhead())
                return;
        }
    }

    /// <summary>
    /// The node a written term stands for in the tree, with the triples that describe it:
    /// <list type="bullet">
    /// <item>a [77] blank-node property list is a new node, its property list read by <see cref="EmitPropertyListTree"/>;</item>
    /// <item>ANON, <c>[]</c>, is a new node;</item>
    /// <item>a [102] collection is the head of its list, with an rdf:first and an rdf:rest triple for each item;</item>
    /// <item>a quoted triple is its reifier.</item>
    /// </list>
    /// Any other term is itself. The nodes are join variables: a pattern's blank node matches any term.
    /// </summary>
    private Term EmitNodeTree(scoped ref PatternArray pa, Term term)
    {
        if (term.IsQuotedTriple)
            return EmitTriplesNode(ref pa, term);
        bool propertyList = IsBlankNodePropertyList(term);
        if (!propertyList && !IsCollection(term))
            return term;

        IncrementDepth(propertyList ? "Blank node property list" : "Collection");
        int saved = _position;
        try
        {
            _position = term.Start + 1; // past '[' or '('
            SkipWhitespace();
            var node = NewJoinVariable();
            if (propertyList)
            {
                if (Peek() != ']')
                    EmitPropertyListTree(ref pa, node);
                SkipWhitespace();
                ExpectChar(']');
            }
            else
            {
                var current = node;
                while (true)
                {
                    var written = ParseTerm();
                    if (written.Length == 0 && written.Start >= 0)
                        throw new SparqlParseException($"Expected a collection item or ')' at position {_position}");
                    var item = EmitNodeTree(ref pa, written);
                    SkipWhitespace();
                    bool last = Peek() == ')';
                    // node rdf:first item ; rdf:rest (next | rdf:nil)  (rdf:first, rdf:rest, rdf:nil = synthetic -6, -7, -8)
                    var rest = last ? new Term { Type = TermType.Iri, Start = -8, Length = 0 } : NewJoinVariable();
                    AddTreeTriple(ref pa, current, new Term { Type = TermType.Iri, Start = -6, Length = 0 }, item,
                        PathType.None, 0, 0);
                    AddTreeTriple(ref pa, current, new Term { Type = TermType.Iri, Start = -7, Length = 0 }, rest,
                        PathType.None, 0, 0);
                    if (last)
                        break;
                    current = rest;
                }
                ExpectChar(')');
            }
            if (_position != term.Start + term.Length)
                throw new SparqlParseException($"Unexpected text before the closing bracket at position {_position}");
            return node;
        }
        finally
        {
            _position = saved;
            DecrementDepth();
        }
    }

    /// <summary>
    /// A triple whose verb is <paramref name="path"/>, written at <c>[spanStart, spanStart + spanLength)</c>, translated
    /// as SPARQL 1.1 §18.2.2.4 and §18.6 define the path forms that are not a closure:
    /// <list type="bullet">
    /// <item>[88] PathSequence: one triple per step, the steps joined by join variables of this parse, so they are
    /// distinct from every other node of the query. Through 1.8.30 the executor expanded a sequence when it ran,
    /// numbering its join variables from 0 again: two sequences in one pattern, or a sequence beside a <c>[ … ]</c>,
    /// shared a node and matched nothing.</item>
    /// <item>[89] PathAlternative: a UNION with a branch per alternative, eval(Path(X, alt(P,Q), Y)) =
    /// Union(eval(Path(X,P,Y)), eval(Path(X,Q,Y))). Through 1.8.30 the scan evaluated an alternative itself: with both
    /// ends unbound it split the path's text on '|' and '/' and read a grouped branch as a name.</item>
    /// <item>^( P ): P with its ends swapped, X ^P Y being Y P X. Through 1.8.30 the scan needed a bound end to walk
    /// it from.</item>
    /// </list>
    /// Each part is translated again, so a branch or a step can be any path. A closure (*, +, ?), an inverse IRI and a
    /// negated set are one triple carrying its span, for the scan's path algebra.
    /// </summary>
    private void AddPathTriple(scoped ref PatternArray pa, Term subject, Term predicate, Term obj, PropertyPath path,
        int spanStart, int spanLength)
    {
        if (path.Type is not (PathType.Sequence or PathType.Alternative or PathType.InverseGroup))
        {
            AddTreeTriple(ref pa, subject, predicate, obj, path.Type,
                path.Type == PathType.None ? path.Iri.Start : spanStart,
                path.Type == PathType.None ? path.Iri.Length : spanLength);
            return;
        }

        // Only a path inside a path, (p1/p2)/p3 or (p|q)/r, recurses; it counts toward the depth limit
        IncrementDepth("Property path");
        try
        {
            if (path.Type == PathType.InverseGroup)
            {
                var (innerPredicate, innerPath) = ParsePathSegment(path.LeftStart, path.LeftLength);
                AddPathTriple(ref pa, obj, innerPredicate, subject, innerPath, path.LeftStart, path.LeftLength);
                return;
            }

            if (path.Type == PathType.Alternative)
            {
                // p|q|r is read as p | (q|r): the right part is the rest, so one UNION takes every branch
                int union = pa.BeginGroupHeader(PatternKind.UnionHeader);
                while (true)
                {
                    int rightStart = path.RightStart, rightLength = path.RightLength;
                    var (leftPredicate, leftPath) = ParsePathSegment(path.LeftStart, path.LeftLength);
                    int branch = pa.BeginGroupHeader(PatternKind.GroupHeader);
                    AddPathTriple(ref pa, subject, leftPredicate, obj, leftPath, path.LeftStart, path.LeftLength);
                    pa.EndGroupHeader(branch);

                    (predicate, path) = ParsePathSegment(rightStart, rightLength);
                    if (path.Type != PathType.Alternative)
                    {
                        branch = pa.BeginGroupHeader(PatternKind.GroupHeader);
                        AddPathTriple(ref pa, subject, predicate, obj, path, rightStart, rightLength);
                        pa.EndGroupHeader(branch);
                        break;
                    }
                }
                pa.EndGroupHeader(union);
                return;
            }

            while (path.Type == PathType.Sequence)
            {
                var middle = NewJoinVariable(); // the node between two steps
                int leftStart = path.LeftStart, leftLength = path.LeftLength;
                int rightStart = path.RightStart, rightLength = path.RightLength;
                var (leftPredicate, leftPath) = ParsePathSegment(leftStart, leftLength);
                AddPathTriple(ref pa, subject, leftPredicate, middle, leftPath, leftStart, leftLength);
                // on along the sequence from the middle node: its right part is a step, or the rest of the sequence
                (predicate, path) = ParsePathSegment(rightStart, rightLength);
                subject = middle;
                spanStart = rightStart;
                spanLength = rightLength;
            }
            AddPathTriple(ref pa, subject, predicate, obj, path, spanStart, spanLength);
        }
        finally
        {
            DecrementDepth();
        }
    }

    /// <summary>
    /// ADR-045 cutover: re-parse the path a tree slot carries, written at <c>[position, position + length)</c>, over THIS
    /// parser's source, so the resulting <see cref="PropertyPath"/>'s offsets index the same source the zero-GC
    /// executor hands <c>TriplePatternScan</c>. The read stops at the span's end: a sequence's step, <c>p*</c> in
    /// <c>p*/q</c>, is that step alone.
    /// </summary>
    internal (Term predicate, PropertyPath path) ParsePredicateOrPathAt(int position, int length)
        => ParsePathSegment(position, length);

    /// <summary>
    /// Adds a triple pattern to the tree. Every triple the tree holds is added here. A labeled blank node
    /// (<c>_:b</c>) in a pattern acts as a variable (SPARQL 1.1 §4.1.4), so it is added as the variable named
    /// <c>_:b</c>: it matches any term and joins the other patterns that use the label. Having no sigil, it is not a
    /// query variable: it hashes apart from <c>?b</c>, is never projected and is not in scope.
    /// </summary>
    private void AddTreeTriple(scoped ref PatternArray pa, Term subject, Term predicate, Term obj,
        PathType pathType, int pathStart, int pathLength)
    {
        ThrowIfLabelInAnotherPattern(subject);
        ThrowIfLabelInAnotherPattern(obj);
        pa.AddTriple(
            PatternTermType(subject), subject.Start, subject.Length,
            predicate.Type, predicate.Start, predicate.Length,
            PatternTermType(obj), obj.Start, obj.Length,
            pathType, pathStart, pathLength);
    }

    /// <summary>
    /// §19.6: the same blank-node label cannot be used in two basic graph patterns of a query (W3C syntax-sparql3
    /// blabel-cross-*, syntax-sparql4 syn-bad-34 to -38 and *-breaks-BGP). Records a labeled blank node with the
    /// basic graph pattern it is in, and throws when the label is already in another one.
    /// </summary>
    private void ThrowIfLabelInAnotherPattern(Term term)
    {
        if (!term.IsBlankNode || term.Start < 0 || term.Length <= 2 || _source[term.Start] != '_')
            return;
        var label = _source.Slice(term.Start, term.Length);
        _blankNodeLabels ??= new List<(int Start, int Length, int Pattern)>();
        foreach (var (start, length, pattern) in _blankNodeLabels)
        {
            if (!_source.Slice(start, length).SequenceEqual(label))
                continue;
            if (pattern != _basicPattern)
                throw new SparqlParseException(
                    $"Blank node label {label.ToString()} is used in two basic graph patterns (position {term.Start})");
            return;
        }
        _blankNodeLabels.Add((term.Start, term.Length, _basicPattern));
    }

    private readonly TermType PatternTermType(Term term)
        => term.IsBlankNode && term.Start >= 0 && term.Length > 2 && _source[term.Start] == '_'
            ? TermType.Variable
            : term.Type;

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

        // FILTER [NOT] EXISTS { … }, optionally wrapped in one level of parentheses. With parentheses it is that form
        // only when ')' follows the group; FILTER(EXISTS { … } && …) is an expression with an EXISTS inside it.
        int savedPos = _position;
        bool paren = false;
        if (Peek() == '(') { Advance(); SkipWhitespace(); paren = true; }

        bool negated = false;
        if (AtKeyword("NOT"))
        {
            ConsumeKeyword("NOT");
            SkipWhitespace();
            negated = true;
        }
        if (AtKeyword("EXISTS"))
        {
            ConsumeKeyword("EXISTS");
            SkipWhitespace();
            if (Peek() != '{')
                throw new SparqlParseException("Expected '{' after EXISTS");
            int groupStart = _position;
            bool wholeConstraint = true;
            if (paren)
            {
                SkipBracketedGroup();
                SkipWhitespace();
                wholeConstraint = Peek() == ')';
                _position = groupStart;
            }
            if (wholeConstraint)
            {
                Advance(); // consume '{'
                int header = pa.BeginExists(negated);
                ParseGroupBodyTree(ref pa, depth + 1);
                pa.EndExists(header);
                SkipWhitespace();
                ExpectChar('}');
                if (paren) { SkipWhitespace(); ExpectChar(')'); }
                return;
            }
        }

        // Not a whole [NOT] EXISTS: rewind any consumed '(' / NOT and capture the constraint
        _position = savedPos;

        if (Peek() == '(')
        {
            // BrackettedExpression: FILTER(expr), the interior without the outer parentheses
            int open = _position;
            SkipBracketedGroup();
            pa.AddFilter(open + 1, _position - open - 2, depth);
        }
        else if (IsPnCharsBase(Peek()) || Peek() is '<' or ':')
        {
            // BuiltInCall / FunctionCall: FILTER CONTAINS(...), FILTER ex:fn(...), FILTER :fn(...), FILTER <iri>(...)
            int start = _position;
            SkipExpressionToken(); // the name or IRI
            SkipWhitespace();
            if (Peek() != '(')
                throw new SparqlParseException("Expected '(' after the function in FILTER");
            SkipBracketedGroup();
            pa.AddFilter(start, _position - start, depth);
        }
        else
        {
            throw new SparqlParseException("Expected '(' or a built-in call after FILTER");
        }
    }

    /// <summary>
    /// [60] Bind ::= 'BIND' '(' Expression 'AS' Var ')'. Captures the expression span (its tokens up to the AS
    /// keyword) and the target variable span, emitting a Bind leaf at the current depth. The tree executor
    /// evaluates children in document order, so a BIND's position is structural — no per-slot evaluation index.
    /// </summary>
    private void EmitBindTree(scoped ref PatternArray pa, int depth, int groupFirstSlot)
    {
        ConsumeKeyword("BIND");
        SkipWhitespace();
        if (Peek() != '(')
            throw new SparqlParseException("Expected '(' after BIND");
        Advance(); // consume '('
        SkipWhitespace();

        int exprStart = _position;
        while (!IsAtEnd() && Peek() != ')' && !AtKeyword("AS"))
            SkipExpressionToken();
        int exprLength = _position - exprStart;
        while (exprLength > 0 && char.IsWhiteSpace(_source[exprStart + exprLength - 1]))
            exprLength--;

        SkipWhitespace();
        ConsumeKeyword("AS");
        SkipWhitespace();

        int varStart = _position;
        if (VariableName.IsSigil(Peek()))
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

        ThrowIfInScope(ref pa, groupFirstSlot, varStart, varLength);
        pa.AddBind(exprStart, exprLength, varStart, varLength, scopeDepth: depth);
    }

    /// <summary>
    /// [61] InlineData ::= 'VALUES' DataBlock, checked by <see cref="ParseValuesClause"/>; a ValuesHeader leaf carries
    /// its source span, which the evaluator reads with the same <see cref="DataBlockReader"/>.
    /// </summary>
    private void EmitValuesTree(scoped ref PatternArray pa, int depth)
    {
        var clause = ParseValuesClause();
        pa.AddValuesBlock(clause.Start, clause.Length);
    }

    // The expansions below live in their own frames, entered only when the construct occurs (ADR-055 Q3).

    /// <summary>
    /// [59] ServiceGraphPattern ::= 'SERVICE' 'SILENT'? VarOrIri GroupGraphPattern. The group is the ServiceHeader's
    /// child, parsed by the same group grammar as every other; the evaluator sends its text to the endpoint.
    /// </summary>
    private void ParseServiceTree(scoped ref PatternArray pa, int depth)
    {
        int header = pa.BeginService(_position);
        ParseServiceHead();
        int groupStart = _position;
        ParseGroupTree(ref pa, depth + 1);
        pa.EndService(header, groupStart, _position);
    }

    /// <summary>A SERVICE's <c>'SERVICE' 'SILENT'? VarOrIri</c> at <paramref name="serviceStart"/>; the evaluator reads
    /// its endpoint with the reader the parser checked it with.</summary>
    internal (bool Silent, Term Endpoint) ParseServiceHeadAt(int serviceStart)
    {
        _position = serviceStart;
        return ParseServiceHead();
    }

    /// <summary>Reads <c>'SERVICE' 'SILENT'? VarOrIri</c>, leaving the cursor at the group's '{'.</summary>
    private (bool Silent, Term Endpoint) ParseServiceHead()
    {
        ConsumeKeyword("SERVICE");
        SkipWhitespace();
        bool silent = false;
        if (AtKeyword("SILENT"))
        {
            ConsumeKeyword("SILENT");
            SkipWhitespace();
            silent = true;
        }
        var endpoint = ParseTerm();
        if (endpoint.Length == 0 || (endpoint.Type != TermType.Variable && endpoint.Type != TermType.Iri))
            throw new SparqlParseException("SERVICE expects a variable or an IRI");
        SkipWhitespace();
        return (silent, endpoint);
    }

    /// <summary>
    /// [60] Bind: the variable a BIND assigns must not be in scope already (SPARQL 1.1 §18.2.1; syntax-BINDscope6-8):
    /// in scope of the group's earlier patterns, <c>[groupFirstSlot, Count)</c>, read by <see cref="AddInScopeVariables(ref PatternArray, int, int, ref InScopeVariables)"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)] // the walk reads sub-SELECT clauses; keep them out of the recursive parse
    private void ThrowIfInScope(scoped ref PatternArray pa, int groupFirstSlot, int varStart, int varLength)
    {
        var inScope = new InScopeVariables(_source);
        try
        {
            AddInScopeVariables(ref pa, groupFirstSlot, pa.Count, ref inScope);
            if (inScope.Contains(_source.Slice(varStart, varLength)))
                throw new SparqlParseException($"BIND target variable {_source.Slice(varStart, varLength).ToString()} is already in scope - cannot rebind");
        }
        finally
        {
            inScope.Dispose();
        }
    }

    /// <summary>
    /// A quoted triple ([… RDF-star]): adds its reification triples and returns its reifier, for the triples that use it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Term EmitTriplesNode(scoped ref PatternArray pa, Term quoted)
    {
        var expanded = new TriplePatternList();
        try
        {
            var reifier = ExpandQuotedTriple(ref expanded, quoted);
            for (int i = 0; i < expanded.Count; i++)
            {
                var tp = expanded[i];
                AddTreeTriple(ref pa, tp.Subject, tp.Predicate, tp.Object, tp.Path.Type, tp.Path.Iri.Start, tp.Path.Iri.Length);
            }
            return reifier;
        }
        finally
        {
            expanded.Dispose();
        }
    }

    /// <summary>Lookahead used inside a property list to stop before a keyword that begins a new clause.</summary>
    private bool IsBodyKeywordAhead()
    {
        return AtKeyword("FILTER") || AtKeyword("OPTIONAL") || AtKeyword("MINUS")
            || AtKeyword("BIND") || AtKeyword("VALUES") || AtKeyword("SERVICE")
            || AtKeyword("GRAPH");
    }
}
