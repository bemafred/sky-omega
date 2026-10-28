using System;
using System.Collections.Generic;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Parsing;

/// <summary>
/// SPARQL Update parsing methods.
/// </summary>
internal ref partial struct SparqlParser
{
    /// <summary>
    /// Parse INSERT DATA { QuadData }
    /// [38] InsertData ::= 'INSERT DATA' QuadData
    /// </summary>
    private UpdateOperation ParseInsertData(Prologue prologue)
    {
        ConsumeKeyword("INSERT");
        SkipWhitespace();
        ConsumeKeyword("DATA");
        SkipWhitespace();

        // [49] QuadData ::= '{' Quads '}': a template without variables, read by the template grammar
        ExpectChar('{');
        var quads = ParseQuads();
        ExpectChar('}');
        ThrowIfVariables(quads, "INSERT DATA");

        return new UpdateOperation
        {
            Type = QueryType.InsertData,
            Prologue = prologue,
            InsertData = quads
        };
    }

    /// <summary>
    /// Parse DELETE DATA { QuadData }
    /// [39] DeleteData ::= 'DELETE DATA' QuadData
    /// </summary>
    private UpdateOperation ParseDeleteData(Prologue prologue)
    {
        ConsumeKeyword("DELETE");
        SkipWhitespace();
        ConsumeKeyword("DATA");
        SkipWhitespace();

        ExpectChar('{');
        var quads = ParseQuads();
        ExpectChar('}');
        ThrowIfVariables(quads, "DELETE DATA");
        ThrowIfBlankNodes(quads, "DELETE DATA");

        return new UpdateOperation
        {
            Type = QueryType.DeleteData,
            Prologue = prologue,
            DeleteData = quads
        };
    }

    /// <summary>
    /// Parse DELETE WHERE { QuadPattern }
    /// [40] DeleteWhere ::= 'DELETE WHERE' QuadPattern
    /// DELETE WHERE QuadPattern means DELETE QuadPattern WHERE { QuadPattern } (SPARQL 1.1 Update §3.1.3.3), so the
    /// QuadPattern is parsed as the delete template ([52] Quads: triples and GRAPH blocks only, so a FILTER or
    /// OPTIONAL is rejected as the spec requires) and recorded as the WHERE group (ADR-052).
    /// </summary>
    private UpdateOperation ParseDeleteWhere(Prologue prologue)
    {
        ConsumeKeyword("DELETE");
        SkipWhitespace();
        ConsumeKeyword("WHERE");
        SkipWhitespace();

        var op = new UpdateOperation
        {
            Type = QueryType.DeleteWhere,
            Prologue = prologue,
            WhereGroupStart = _position
        };

        ExpectChar('{');
        op.DeleteTemplate = ParseQuads();
        ExpectChar('}');
        ThrowIfBlankNodes(op.DeleteTemplate, "DELETE WHERE");

        // The same text is evaluated as a group, so the group grammar must read it to the same end.
        int templateEnd = _position;
        ParseWhereGroup(op.WhereGroupStart);
        if (_position != templateEnd)
            throw new SparqlParseException($"DELETE WHERE accepts only triples and GRAPH blocks (position {op.WhereGroupStart})");

        return op;
    }

    /// <summary>
    /// ADR-052: parse the Update WHERE group at <paramref name="groupStart"/> (its opening '{') with the pattern-tree
    /// grammar queries use, leaving the cursor after its closing '}'. Parsing here, during
    /// <see cref="ParseUpdateSequence"/>, keeps ADR-046's rule: a syntax error anywhere means nothing is executed.
    /// The tree itself is discarded; the executor parses it again from the same offset when the operation runs,
    /// as a query does.
    /// </summary>
    private void ParseWhereGroup(int groupStart)
    {
        const int treeBytes = PatternSlot.Size * 1024;
        var treeBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(treeBytes);
        try
        {
            var tree = new PatternArray(treeBuffer.AsSpan(0, treeBytes));
            ParsePatternTreeAt(groupStart, ref tree);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(treeBuffer);
        }
    }

    /// <summary>
    /// Parse DELETE/INSERT ... WHERE (Modify operation)
    /// [41] Modify ::= ( 'WITH' iri )? ( DeleteClause InsertClause? | InsertClause ) UsingClause* 'WHERE' GroupGraphPattern
    /// </summary>
    private UpdateOperation ParseModify(Prologue prologue)
    {
        var op = new UpdateOperation
        {
            Type = QueryType.Modify,
            Prologue = prologue
        };

        // Optional WITH clause
        if (TryConsumeKeyword("WITH"))
        {
            SkipWhitespace();
            // Parse graph IRI or prefixed name - store in dedicated WithGraph fields
            var iriStart = _position;
            ParseIriOrPrefixedName();
            var iriLen = _position - iriStart;
            op.WithGraphStart = iriStart;
            op.WithGraphLength = iriLen;
            SkipWhitespace();
        }

        // DELETE clause and/or INSERT clause
        bool hasDelete = false;
        bool hasInsert = false;

        // Parse DELETE/INSERT directly into op fields: the JIT allocates every local in the prologue, so
        // intermediate template locals would all sit in this frame.
        if (TryConsumeKeyword("DELETE"))
        {
            hasDelete = true;
            SkipWhitespace();
            if (Peek() != '{')
                throw new SparqlParseException($"Expected '{{' but found '{Peek()}'");
            Advance();
            op.DeleteTemplate = ParseQuads();
            if (Peek() != '}')
                throw new SparqlParseException($"Expected '}}' but found '{Peek()}'");
            Advance();
            SkipWhitespace();
            ThrowIfBlankNodes(op.DeleteTemplate, "a DELETE template");
        }

        if (TryConsumeKeyword("INSERT"))
        {
            hasInsert = true;
            SkipWhitespace();
            if (Peek() != '{')
                throw new SparqlParseException($"Expected '{{' but found '{Peek()}'");
            Advance();
            op.InsertTemplate = ParseQuads();
            if (Peek() != '}')
                throw new SparqlParseException($"Expected '}}' but found '{Peek()}'");
            Advance();
            SkipWhitespace();
        }

        if (!hasDelete && !hasInsert)
        {
            throw new SparqlParseException("Expected DELETE or INSERT clause");
        }

        // Optional USING clauses
        var usingClauses = new List<DatasetClause>();
        while (TryConsumeKeyword("USING"))
        {
            SkipWhitespace();
            var isNamed = false;
            if (TryConsumeKeyword("NAMED"))
            {
                SkipWhitespace();
                isNamed = true;
            }
            var iriStart = _position;
            ParseIriOrPrefixedName();
            var iriLen = _position - iriStart;
            var clause = isNamed ? DatasetClause.Named(iriStart, iriLen) : DatasetClause.Default(iriStart, iriLen);
            usingClauses.Add(clause);
            SkipWhitespace();
        }
        if (usingClauses.Count > 0)
            op.UsingClauses = usingClauses.ToArray();

        // [53] GroupGraphPattern ::= '{' ( SubSelect | GroupGraphPatternSub ) '}'
        // ADR-052: the same grammar a query's WHERE uses — the pattern tree. Record where the group opens.
        ConsumeKeyword("WHERE");
        SkipWhitespace();
        if (Peek() != '{')
            throw new SparqlParseException($"Expected '{{' after WHERE but found '{Peek()}'");
        op.WhereGroupStart = _position;
        ParseWhereGroup(op.WhereGroupStart);

        return op;
    }

    /// <summary>
    /// Parse LOAD (SILENT)? iri (INTO GRAPH iri)?
    /// [42] Load ::= 'LOAD' 'SILENT'? iri ( 'INTO' GraphRef )?
    /// </summary>
    private UpdateOperation ParseLoad(Prologue prologue)
    {
        ConsumeKeyword("LOAD");
        SkipWhitespace();

        var op = new UpdateOperation
        {
            Type = QueryType.Load,
            Prologue = prologue
        };

        if (TryConsumeKeyword("SILENT"))
        {
            op.Silent = true;
            SkipWhitespace();
        }

        var sourceStart = _position;
        ParseIriRef();
        var sourceLen = _position - sourceStart;
        op.SourceUriStart = sourceStart;
        op.SourceUriLength = sourceLen;
        SkipWhitespace();

        if (TryConsumeKeyword("INTO"))
        {
            SkipWhitespace();
            op.DestinationGraph = ParseGraphRef();
        }

        return op;
    }

    /// <summary>
    /// Parse CLEAR (SILENT)? GraphRefAll
    /// [43] Clear ::= 'CLEAR' 'SILENT'? GraphRefAll
    /// </summary>
    private UpdateOperation ParseClear(Prologue prologue)
    {
        ConsumeKeyword("CLEAR");
        SkipWhitespace();

        var op = new UpdateOperation
        {
            Type = QueryType.Clear,
            Prologue = prologue
        };

        if (TryConsumeKeyword("SILENT"))
        {
            op.Silent = true;
            SkipWhitespace();
        }

        op.DestinationGraph = ParseGraphRefAll();
        return op;
    }

    /// <summary>
    /// Parse CREATE (SILENT)? GraphRef
    /// [44] Create ::= 'CREATE' 'SILENT'? GraphRef
    /// </summary>
    private UpdateOperation ParseCreate(Prologue prologue)
    {
        ConsumeKeyword("CREATE");
        SkipWhitespace();

        var op = new UpdateOperation
        {
            Type = QueryType.Create,
            Prologue = prologue
        };

        if (TryConsumeKeyword("SILENT"))
        {
            op.Silent = true;
            SkipWhitespace();
        }

        op.DestinationGraph = ParseGraphRef();
        return op;
    }

    /// <summary>
    /// Parse DROP (SILENT)? GraphRefAll
    /// [45] Drop ::= 'DROP' 'SILENT'? GraphRefAll
    /// </summary>
    private UpdateOperation ParseDrop(Prologue prologue)
    {
        ConsumeKeyword("DROP");
        SkipWhitespace();

        var op = new UpdateOperation
        {
            Type = QueryType.Drop,
            Prologue = prologue
        };

        if (TryConsumeKeyword("SILENT"))
        {
            op.Silent = true;
            SkipWhitespace();
        }

        op.DestinationGraph = ParseGraphRefAll();
        return op;
    }

    /// <summary>
    /// Parse COPY (SILENT)? GraphOrDefault TO GraphOrDefault
    /// [46] Copy ::= 'COPY' 'SILENT'? GraphOrDefault 'TO' GraphOrDefault
    /// </summary>
    private UpdateOperation ParseCopy(Prologue prologue)
    {
        ConsumeKeyword("COPY");
        SkipWhitespace();

        var op = new UpdateOperation
        {
            Type = QueryType.Copy,
            Prologue = prologue
        };

        if (TryConsumeKeyword("SILENT"))
        {
            op.Silent = true;
            SkipWhitespace();
        }

        op.SourceGraph = ParseGraphOrDefault();
        SkipWhitespace();
        ConsumeKeyword("TO");
        SkipWhitespace();
        op.DestinationGraph = ParseGraphOrDefault();

        return op;
    }

    /// <summary>
    /// Parse MOVE (SILENT)? GraphOrDefault TO GraphOrDefault
    /// [47] Move ::= 'MOVE' 'SILENT'? GraphOrDefault 'TO' GraphOrDefault
    /// </summary>
    private UpdateOperation ParseMove(Prologue prologue)
    {
        ConsumeKeyword("MOVE");
        SkipWhitespace();

        var op = new UpdateOperation
        {
            Type = QueryType.Move,
            Prologue = prologue
        };

        if (TryConsumeKeyword("SILENT"))
        {
            op.Silent = true;
            SkipWhitespace();
        }

        op.SourceGraph = ParseGraphOrDefault();
        SkipWhitespace();
        ConsumeKeyword("TO");
        SkipWhitespace();
        op.DestinationGraph = ParseGraphOrDefault();

        return op;
    }

    /// <summary>
    /// Parse ADD (SILENT)? GraphOrDefault TO GraphOrDefault
    /// [48] Add ::= 'ADD' 'SILENT'? GraphOrDefault 'TO' GraphOrDefault
    /// </summary>
    private UpdateOperation ParseAdd(Prologue prologue)
    {
        ConsumeKeyword("ADD");
        SkipWhitespace();

        var op = new UpdateOperation
        {
            Type = QueryType.Add,
            Prologue = prologue
        };

        if (TryConsumeKeyword("SILENT"))
        {
            op.Silent = true;
            SkipWhitespace();
        }

        op.SourceGraph = ParseGraphOrDefault();
        SkipWhitespace();
        ConsumeKeyword("TO");
        SkipWhitespace();
        op.DestinationGraph = ParseGraphOrDefault();

        return op;
    }

    /// <summary>
    /// The body of a QuadPattern template (DELETE / INSERT / DELETE WHERE), from after its '{' to before its '}'.
    /// [50] Quads ::= TriplesTemplate? ( QuadsNotTriples '.'? TriplesTemplate? )*
    /// [51] QuadsNotTriples ::= 'GRAPH' VarOrIri '{' TriplesTemplate? '}'
    /// Every triple becomes one <see cref="TemplateQuad"/>; the template has as many as the text has.
    /// </summary>
    private TemplateQuad[] ParseQuads()
    {
        var quads = new List<TemplateQuad>();
        while (true)
        {
            SkipWhitespace();
            if (IsAtEnd() || Peek() == '}')
                return quads.ToArray();

            var span = PeekSpan(5);
            if (AtKeyword("GRAPH"))
            {
                ConsumeKeyword("GRAPH");
                SkipWhitespace();
                var graph = ParseTerm();
                SkipWhitespace();
                ExpectChar('{');
                ParseTriplesTemplate(graph, quads);
                SkipWhitespace();
                ExpectChar('}');
            }
            else
            {
                ParseTriplesTemplate(default, quads);
            }

            SkipWhitespace();
            if (Peek() == '.')
                Advance();
        }
    }

    /// <summary>
    /// [52] TriplesTemplate ::= TriplesSameSubject ( '.' TriplesTemplate? )?, adding each triple to
    /// <paramref name="quads"/> in <paramref name="graph"/>. Stops before a '}' or a GRAPH keyword.
    /// [75] TriplesSameSubject ::= VarOrTerm PropertyListNotEmpty | TriplesNode PropertyList
    /// [77] PropertyListNotEmpty ::= Verb ObjectList ( ';' ( Verb ObjectList )? )*
    /// [78] Verb ::= VarOrIri | 'a'        [79] ObjectList ::= Object ( ',' Object )*
    /// The one template grammar: Update's DATA and templates, and CONSTRUCT's template. A collection or a blank-node
    /// property list is a template node (<see cref="Execution.Operators.SyntheticTerms.ListNode"/>), a new blank node
    /// each time the template is instantiated, with its triples added here. A template takes a Verb, not a property
    /// path: a path has no single triple to instantiate.
    /// </summary>
    private void ParseTriplesTemplate(Term graph, List<TemplateQuad> quads)
    {
        while (true)
        {
            SkipWhitespace();
            if (IsAtEnd() || Peek() == '}')
                return;
            var span = PeekSpan(5);
            if (AtKeyword("GRAPH"))
                return;

            var written = ParseTerm();
            if (written.Length == 0 && written.Start >= 0)
                throw new SparqlParseException($"Expected a triple in the template at position {_position}");
            var subject = TemplateNode(written, graph, quads);

            // A TriplesNode may stand alone ([] PropertyList, with PropertyList empty)
            SkipWhitespace();
            if (subject.Start < 0 && (IsAtEnd() || Peek() is '.' or '}'))
            {
                if (Peek() == '.') Advance();
                continue;
            }

            ParseTemplatePropertyList(subject, graph, quads);

            SkipWhitespace();
            if (Peek() != '.')
                return;
            Advance();
        }
    }

    /// <summary>[77] PropertyListNotEmpty for <paramref name="subject"/>, up to a '.', '}' or ']'.</summary>
    private void ParseTemplatePropertyList(Term subject, Term graph, List<TemplateQuad> quads)
    {
        while (true)
        {
            SkipWhitespace();
            var (verb, path) = ParsePredicateOrPath();
            if (path.Type != PathType.None)
                throw new SparqlParseException($"A property path is not allowed in a template (position {_position})");
            if (verb.Length == 0 && verb.Start >= 0)
                throw new SparqlParseException($"Expected a predicate in the template at position {_position}");

            // ObjectList
            while (true)
            {
                SkipWhitespace();
                var written = ParseTerm();
                if (written.Length == 0 && written.Start >= 0)
                    throw new SparqlParseException($"Expected an object in the template at position {_position}");
                var obj = TemplateNode(written, graph, quads);
                quads.Add(new TemplateQuad { Subject = subject, Predicate = verb, Object = obj, Graph = graph });

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
            if (IsAtEnd() || Peek() is '.' or '}' or ']')
                return;
        }
    }

    /// <summary>
    /// A template term as instantiated: a collection ( … ) or a blank-node property list [ … ] becomes a new template
    /// node with its triples added to <paramref name="quads"/>; an empty collection is rdf:nil; any other term is
    /// itself.
    /// </summary>
    private Term TemplateNode(Term term, Term graph, List<TemplateQuad> quads)
    {
        bool collection = IsCollection(term);
        if (!collection && !IsBlankNodePropertyList(term))
            return term;

        int saved = _position;
        _position = term.Start + 1; // past '(' or '['
        SkipWhitespace();
        Term node;
        if (collection)
        {
            node = Peek() == ')' ? new Term { Type = TermType.Iri, Start = -8, Length = 0 } : NewTemplateNode();
            var current = node;
            while (!IsAtEnd() && Peek() != ')')
            {
                var item = TemplateNode(ParseTerm(), graph, quads);
                quads.Add(new TemplateQuad { Subject = current, Predicate = new Term { Type = TermType.Iri, Start = -6, Length = 0 }, Object = item, Graph = graph });
                SkipWhitespace();
                var rest = Peek() == ')' ? new Term { Type = TermType.Iri, Start = -8, Length = 0 } : NewTemplateNode();
                quads.Add(new TemplateQuad { Subject = current, Predicate = new Term { Type = TermType.Iri, Start = -7, Length = 0 }, Object = rest, Graph = graph });
                current = rest;
            }
        }
        else
        {
            node = NewTemplateNode();
            if (Peek() != ']')
                ParseTemplatePropertyList(node, graph, quads);
            SkipWhitespace();
            if (Peek() != ']')
                throw new SparqlParseException($"Expected ']' at position {_position}");
        }
        _position = saved;
        return node;
    }

    /// <summary>A new template node, numbered per request.</summary>
    private Term NewTemplateNode() =>
        new() { Type = TermType.BlankNode, Start = Execution.Operators.SyntheticTerms.ListNode(_constructListNodeCounter++), Length = 0 };

    /// <summary>[49] QuadData: a data block holds no variables (SPARQL 1.1 Update §19.6, note 8).</summary>
    private readonly void ThrowIfVariables(TemplateQuad[] quads, string where)
    {
        foreach (var q in quads)
        {
            if (q.Subject.IsVariable || q.Predicate.IsVariable || q.Object.IsVariable || (q.InGraphBlock && q.Graph.IsVariable))
                throw new SparqlParseException($"Variables are not allowed in {where}");
        }
    }

    /// <summary>DELETE DATA, a DELETE template and DELETE WHERE hold no blank nodes (§19.6, notes 7 and 9).</summary>
    private readonly void ThrowIfBlankNodes(TemplateQuad[]? quads, string where)
    {
        if (quads == null) return;
        foreach (var q in quads)
        {
            if (q.Subject.IsBlankNode || q.Object.IsBlankNode)
                throw new SparqlParseException($"Blank nodes are not allowed in {where}");
        }
    }

    /// <summary>
    /// Parse GraphRef - GRAPH iri
    /// Supports both full IRIs (&lt;http://...&gt;) and prefixed names (prefix:local, :local)
    /// </summary>
    private GraphTarget ParseGraphRef()
    {
        ConsumeKeyword("GRAPH");
        SkipWhitespace();
        var start = _position;
        ParseIriOrPrefixedName();
        var len = _position - start;
        return new GraphTarget
        {
            Type = GraphTargetType.Graph,
            IriStart = start,
            IriLength = len
        };
    }

    /// <summary>
    /// Parse GraphRefAll - DEFAULT | NAMED | ALL | GraphRef
    /// </summary>
    private GraphTarget ParseGraphRefAll()
    {
        if (TryConsumeKeyword("DEFAULT"))
            return new GraphTarget { Type = GraphTargetType.Default };
        if (TryConsumeKeyword("NAMED"))
            return new GraphTarget { Type = GraphTargetType.Named };
        if (TryConsumeKeyword("ALL"))
            return new GraphTarget { Type = GraphTargetType.All };

        return ParseGraphRef();
    }

    /// <summary>
    /// Parse GraphOrDefault - DEFAULT | GRAPH? iri
    /// Supports both full IRIs (&lt;http://...&gt;) and prefixed names (prefix:local, :local)
    /// </summary>
    private GraphTarget ParseGraphOrDefault()
    {
        if (TryConsumeKeyword("DEFAULT"))
            return new GraphTarget { Type = GraphTargetType.Default };

        // Optional GRAPH keyword
        TryConsumeKeyword("GRAPH");
        SkipWhitespace();

        var start = _position;
        ParseIriOrPrefixedName();
        var len = _position - start;
        return new GraphTarget
        {
            Type = GraphTargetType.Graph,
            IriStart = start,
            IriLength = len
        };
    }

    /// <summary>
    /// Parse an IRI reference or prefixed name.
    /// Handles: &lt;http://...&gt;, prefix:local, :local
    /// </summary>
    private void ParseIriOrPrefixedName()
    {
        var ch = Peek();

        if (ch == '<')
        {
            // Full IRI: <http://...>
            ParseIriRef();
        }
        else if (TryReadPnameNs())
        {
            ReadPnLocal(); // [141] PNAME_LN, or a PNAME_NS alone
        }
        else
        {
            throw new SparqlParseException($"Expected IRI or prefixed name at position {_position}");
        }
    }

    /// <summary>
    /// Expect a specific character, throw if not found.
    /// </summary>
    private void ExpectChar(char expected)
    {
        if (Peek() != expected)
            throw new SparqlParseException($"Expected '{expected}' but found '{Peek()}'");
        Advance();
    }

    /// <summary>
    /// Try to consume a keyword, returning true if successful.
    /// </summary>
    /// <summary>Consumes <paramref name="keyword"/> when the whole word is at the cursor.</summary>
    private bool TryConsumeKeyword(string keyword)
    {
        if (!AtKeyword(keyword))
            return false;
        _position += keyword.Length;
        return true;
    }
}
