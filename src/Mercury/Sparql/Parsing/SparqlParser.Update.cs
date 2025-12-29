using System;
using System.Collections.Generic;

namespace SkyOmega.Mercury.Sparql.Parsing;

/// <summary>
/// SPARQL Update parsing methods.
/// </summary>
public ref partial struct SparqlParser
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

        var quads = ParseQuadData();

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

        var quads = ParseQuadData();

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
    /// </summary>
    private UpdateOperation ParseDeleteWhere(Prologue prologue)
    {
        ConsumeKeyword("DELETE");
        SkipWhitespace();
        ConsumeKeyword("WHERE");
        SkipWhitespace();

        // Parse the pattern as both delete template and where clause
        var pattern = new GraphPattern();
        ExpectChar('{');
        SkipWhitespace();
        ParseGroupGraphPatternSub(ref pattern);
        SkipWhitespace();
        ExpectChar('}');

        return new UpdateOperation
        {
            Type = QueryType.DeleteWhere,
            Prologue = prologue,
            DeleteTemplate = pattern,
            WhereClause = new WhereClause { Pattern = pattern }
        };
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
            // Parse graph IRI - for now store in SourceGraph
            var iriStart = _position;
            ParseIriRef();
            var iriLen = _position - iriStart;
            op.SourceGraph = new GraphTarget
            {
                Type = GraphTargetType.Graph,
                IriStart = iriStart,
                IriLength = iriLen
            };
            SkipWhitespace();
        }

        // DELETE clause and/or INSERT clause
        bool hasDelete = false;
        bool hasInsert = false;

        if (TryConsumeKeyword("DELETE"))
        {
            hasDelete = true;
            SkipWhitespace();
            op.DeleteTemplate = ParseQuadPattern();
            SkipWhitespace();
        }

        if (TryConsumeKeyword("INSERT"))
        {
            hasInsert = true;
            SkipWhitespace();
            op.InsertTemplate = ParseQuadPattern();
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
            ParseIriRef();
            var iriLen = _position - iriStart;
            var clause = isNamed ? DatasetClause.Named(iriStart, iriLen) : DatasetClause.Default(iriStart, iriLen);
            usingClauses.Add(clause);
            SkipWhitespace();
        }
        if (usingClauses.Count > 0)
            op.UsingClauses = usingClauses.ToArray();

        // WHERE clause
        ConsumeKeyword("WHERE");
        SkipWhitespace();
        var wherePattern = new GraphPattern();
        ExpectChar('{');
        SkipWhitespace();
        ParseGroupGraphPatternSub(ref wherePattern);
        SkipWhitespace();
        ExpectChar('}');
        op.WhereClause = new WhereClause { Pattern = wherePattern };

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
    /// Parse QuadData - triples within { } possibly with GRAPH blocks
    /// </summary>
    private QuadData[] ParseQuadData()
    {
        var quads = new List<QuadData>();

        ExpectChar('{');
        SkipWhitespace();

        while (Peek() != '}' && !IsAtEnd())
        {
            if (TryConsumeKeyword("GRAPH"))
            {
                // GRAPH <iri> { triples }
                SkipWhitespace();
                var graphStart = _position;
                ParseIriRef();
                var graphLen = _position - graphStart;
                SkipWhitespace();
                ExpectChar('{');
                SkipWhitespace();

                ParseTriplesIntoQuads(quads, graphStart, graphLen);

                SkipWhitespace();
                ExpectChar('}');
            }
            else
            {
                // Default graph triples
                ParseTriplesIntoQuads(quads, 0, 0);
            }

            SkipWhitespace();
        }

        ExpectChar('}');
        return quads.ToArray();
    }

    /// <summary>
    /// Parse triples and add them to quads list with the specified graph.
    /// </summary>
    private void ParseTriplesIntoQuads(List<QuadData> quads, int graphStart, int graphLen)
    {
        while (Peek() != '}' && Peek() != '\0')
        {
            // Check for GRAPH keyword (nested or next block)
            var span = PeekSpan(5);
            if (span.Length >= 5 && span[..5].Equals("GRAPH", StringComparison.OrdinalIgnoreCase))
                break;

            // Parse subject
            var (subStart, subLen, subType) = ParseTermForUpdate();
            if (subLen == 0) break;

            SkipWhitespace();

            // Parse predicate-object list
            while (true)
            {
                var (predStart, predLen, predType) = ParseTermForUpdate();
                if (predLen == 0) break;

                SkipWhitespace();

                // Parse object list
                while (true)
                {
                    var (objStart, objLen, objType) = ParseTermForUpdate();
                    if (objLen == 0)
                        throw new SparqlParseException("Expected object in triple");

                    quads.Add(new QuadData
                    {
                        SubjectStart = subStart,
                        SubjectLength = subLen,
                        SubjectType = subType,
                        PredicateStart = predStart,
                        PredicateLength = predLen,
                        PredicateType = predType,
                        ObjectStart = objStart,
                        ObjectLength = objLen,
                        ObjectType = objType,
                        GraphStart = graphStart,
                        GraphLength = graphLen
                    });

                    SkipWhitespace();
                    if (Peek() == ',')
                    {
                        Advance(); // Skip ','
                        SkipWhitespace();
                        continue;
                    }
                    break;
                }

                SkipWhitespace();
                if (Peek() == ';')
                {
                    Advance(); // Skip ';'
                    SkipWhitespace();
                    // Check if there's another predicate or if we're done
                    if (Peek() == '.' || Peek() == '}')
                        break;
                    continue;
                }
                break;
            }

            SkipWhitespace();
            if (Peek() == '.')
            {
                Advance(); // Skip '.'
                SkipWhitespace();
            }
        }
    }

    /// <summary>
    /// Parse a term (IRI, literal, blank node) for Update operations.
    /// Returns (start, length, type).
    /// </summary>
    private (int start, int length, TermType type) ParseTermForUpdate()
    {
        SkipWhitespace();
        var start = _position;

        if (Peek() == '<')
        {
            // IRI
            Advance(); // <
            while (!IsAtEnd() && Peek() != '>')
                Advance();
            if (Peek() == '>') Advance();
            return (start, _position - start, TermType.Iri);
        }

        if (Peek() == '"' || Peek() == '\'')
        {
            // Literal
            var quote = Advance();
            // Check for long literal (""" or ''')
            if (Peek() == quote)
            {
                Advance();
                if (Peek() == quote)
                {
                    // Long literal
                    Advance();
                    while (!IsAtEnd())
                    {
                        if (Peek() == quote)
                        {
                            Advance();
                            if (Peek() == quote)
                            {
                                Advance();
                                if (Peek() == quote)
                                {
                                    Advance();
                                    break;
                                }
                            }
                        }
                        else
                        {
                            Advance();
                        }
                    }
                }
                // else empty string ""
            }
            else
            {
                // Short literal
                while (!IsAtEnd() && Peek() != quote && Peek() != '\n')
                {
                    if (Peek() == '\\') Advance(); // Skip escape
                    Advance();
                }
                if (Peek() == quote) Advance();
            }
            // Handle language tag or datatype
            if (Peek() == '@')
            {
                Advance();
                while (!IsAtEnd() && (char.IsLetterOrDigit(Peek()) || Peek() == '-'))
                    Advance();
            }
            else if (Peek() == '^' && _position + 1 < _source.Length && _source[_position + 1] == '^')
            {
                Advance(); Advance(); // ^^
                // Parse datatype IRI
                if (Peek() == '<')
                {
                    Advance();
                    while (!IsAtEnd() && Peek() != '>')
                        Advance();
                    if (Peek() == '>') Advance();
                }
            }
            return (start, _position - start, TermType.Literal);
        }

        if (Peek() == '_' && _position + 1 < _source.Length && _source[_position + 1] == ':')
        {
            // Blank node _:label
            Advance(); Advance(); // _:
            while (!IsAtEnd() && (char.IsLetterOrDigit(Peek()) || Peek() == '_' || Peek() == '-'))
                Advance();
            return (start, _position - start, TermType.BlankNode);
        }

        if (Peek() == '?')
        {
            // Variable
            Advance();
            while (!IsAtEnd() && (char.IsLetterOrDigit(Peek()) || Peek() == '_'))
                Advance();
            return (start, _position - start, TermType.Variable);
        }

        if (char.IsLetter(Peek()) || Peek() == ':')
        {
            // Prefixed name or keyword
            while (!IsAtEnd() && (char.IsLetterOrDigit(Peek()) || Peek() == '_' || Peek() == '-' || Peek() == ':'))
                Advance();
            return (start, _position - start, TermType.Iri);  // Prefixed names resolve to IRIs
        }

        if (char.IsDigit(Peek()) || Peek() == '+' || Peek() == '-')
        {
            // Numeric literal
            if (Peek() == '+' || Peek() == '-') Advance();
            while (!IsAtEnd() && char.IsDigit(Peek()))
                Advance();
            if (Peek() == '.')
            {
                Advance();
                while (!IsAtEnd() && char.IsDigit(Peek()))
                    Advance();
            }
            if (Peek() == 'e' || Peek() == 'E')
            {
                Advance();
                if (Peek() == '+' || Peek() == '-') Advance();
                while (!IsAtEnd() && char.IsDigit(Peek()))
                    Advance();
            }
            return (start, _position - start, TermType.Literal);
        }

        return (0, 0, TermType.Iri); // No term found
    }

    /// <summary>
    /// Parse QuadPattern - pattern within { } that may contain variables
    /// </summary>
    private GraphPattern ParseQuadPattern()
    {
        var pattern = new GraphPattern();
        ExpectChar('{');
        SkipWhitespace();
        ParseGroupGraphPatternSub(ref pattern);
        SkipWhitespace();
        ExpectChar('}');
        return pattern;
    }

    /// <summary>
    /// Parse GraphRef - GRAPH iri
    /// </summary>
    private GraphTarget ParseGraphRef()
    {
        ConsumeKeyword("GRAPH");
        SkipWhitespace();
        var start = _position;
        ParseIriRef();
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
    /// </summary>
    private GraphTarget ParseGraphOrDefault()
    {
        if (TryConsumeKeyword("DEFAULT"))
            return new GraphTarget { Type = GraphTargetType.Default };

        // Optional GRAPH keyword
        TryConsumeKeyword("GRAPH");
        SkipWhitespace();

        var start = _position;
        ParseIriRef();
        var len = _position - start;
        return new GraphTarget
        {
            Type = GraphTargetType.Graph,
            IriStart = start,
            IriLength = len
        };
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
    private bool TryConsumeKeyword(string keyword)
    {
        var span = PeekSpan(keyword.Length);
        if (span.Length >= keyword.Length &&
            span[..keyword.Length].Equals(keyword, StringComparison.OrdinalIgnoreCase))
        {
            _position += keyword.Length;
            return true;
        }
        return false;
    }
}
