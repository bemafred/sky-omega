using System;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Execution.Operators;
using SkyOmega.Mercury.Sparql.Patterns;

namespace SkyOmega.Mercury.Sparql.Parsing;

internal ref partial struct SparqlParser
{
    /// <summary>
    /// [7] SelectQuery ::= SelectClause DatasetClause* WhereClause SolutionModifier. Its scope rules are checked by
    /// <see cref="ValidateSelectScope"/> once the query's trailing VALUES is read, since its variables are in scope.
    /// </summary>
    private Query ParseSelectQuery(Prologue prologue)
    {
        var selectClause = ParseSelectClause();
        var datasets = ParseDatasetClauses();
        var whereClause = ParseWhereClause();
        var modifier = ParseQuerySolutionModifier();

        return new Query
        {
            Type = QueryType.Select,
            Prologue = prologue,
            SelectClause = selectClause,
            Datasets = datasets,
            WhereClause = whereClause,
            SolutionModifier = modifier
        };
    }

    /// <summary>
    /// A SELECT query's scope rules: what a grouped query projects (<see cref="ValidateGroupBySemantics"/>) and where
    /// a SELECT expression assigns (<see cref="ThrowIfAssignedInScope"/>), checked against the WHERE group at
    /// <paramref name="whereGroupStart"/> and the trailing VALUES.
    /// </summary>
    private void ValidateSelectScope(SelectClause selectClause, GroupByClause groupBy, ValuesClause values, int whereGroupStart)
    {
        ValidateGroupBySemantics(selectClause, groupBy, values);
        if (selectClause.AggregateCount == 0)
            return;

        var visible = new InScopeVariables(_source);
        try
        {
            if (whereGroupStart >= 0)
                AddInScopeVariables(whereGroupStart, ref visible);
            ThrowIfAssignedInScope(selectClause, groupBy, values, ref visible);
        }
        finally
        {
            visible.Dispose();
        }
    }

    /// <summary>
    /// SPARQL 1.1 §18.2.4.4: the variable of a SELECT expression <c>(expr AS v)</c> must not be visible where it is
    /// assigned. (Nor projected earlier in the SELECT clause: <see cref="ParseSelectClause"/> rejects a name used
    /// twice.) Visible are the WHERE's in-scope variables (a
    /// sub-SELECT's projection among them), filled into <paramref name="visible"/> by the caller, and the variables
    /// of GROUP BY and of the trailing VALUES, which is joined before the SELECT expressions (§18.2.4.3).
    /// </summary>
    private void ThrowIfAssignedInScope(SelectClause selectClause, GroupByClause groupBy, ValuesClause values,
        scoped ref InScopeVariables visible)
    {
        for (int i = 0; i < groupBy.Count; i++)
        {
            var (start, length) = groupBy.GetVariable(i);
            if (length > 0)
                visible.Add(start, length);
        }
        if (values.IsPresent)
            AddDataBlockVariables(values.Start, ref visible);

        for (int a = 0; a < selectClause.AggregateCount; a++)
        {
            var assigned = selectClause.GetAggregate(a);
            if (assigned.AliasLength == 0)
                continue;
            var name = _source.Slice(assigned.AliasStart, assigned.AliasLength);
            if (visible.Contains(name))
                throw new SparqlParseException($"Variable {name.ToString()} is already in scope where a SELECT expression assigns it");
        }
    }

    /// <summary>
    /// Validates GROUP BY semantic constraints according to SPARQL 1.1 spec. A grouped query projects its GROUP BY
    /// variables and its trailing VALUES' (<paramref name="values"/>), which is joined after grouping (§18.2.4.3).
    /// </summary>
    private void ValidateGroupBySemantics(SelectClause selectClause, GroupByClause groupBy, ValuesClause values)
    {
        // Rule 1: SELECT * is not allowed with GROUP BY (syn-bad-01)
        if (selectClause.SelectAll && groupBy.HasGroupBy)
        {
            throw new SparqlParseException("SELECT * is not allowed with GROUP BY");
        }

        // Check if there are actual aggregate functions (not just expressions like (expr AS ?var))
        bool hasActualAggregates = HasActualAggregateFunctions(selectClause);

        // Rule 2: If SELECT has aggregates without GROUP BY, only aggregates/constants allowed (agg10)
        // Rule 3: With GROUP BY, non-aggregate SELECT items must appear in GROUP BY (agg09, syn-bad-02, group06)
        // These require tracking SELECT variables, which is done by ValidateSelectVariables
        if (selectClause.HasProjectedVariables && (groupBy.HasGroupBy || hasActualAggregates))
        {
            ValidateSelectVariables(selectClause, groupBy, values, hasActualAggregates);
        }
    }

    /// <summary>
    /// Checks if the SELECT clause contains actual aggregate functions (COUNT, SUM, etc.)
    /// as opposed to just expressions with aliases like (expr AS ?var).
    /// </summary>
    private static bool HasActualAggregateFunctions(SelectClause selectClause)
    {
        for (int i = 0; i < selectClause.AggregateCount; i++)
        {
            var agg = selectClause.GetAggregate(i);
            if (agg.Function != AggregateFunction.None)
                return true;
        }
        return false;
    }

    /// <summary>
    /// CONSTRUCT WHERE { TriplesTemplate } ([10] ConstructQuery, second form): the WHERE group is also the template, so
    /// it may hold only triples, and no property paths. Returns those triples as the template.
    /// </summary>
    private readonly Term TemplateTerm(Term term)
        => term.IsVariable && term.Start >= 0 && term.Length > 0 && !VariableName.IsSigil(_source[term.Start])
            ? Term.BlankNode(term.Start, term.Length)
            : term;

    private ConstructTemplate ConstructTemplateFromWhere(int whereGroupStart)
    {
        var template = new ConstructTemplate();
        const int treeBytes = PatternSlot.Size * 1024;
        var treeBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(treeBytes);
        try
        {
            var tree = new PatternArray(treeBuffer.AsSpan(0, treeBytes));
            int root = new SparqlParser(_source).ParsePatternTreeAt(whereGroupStart, ref tree);
            if (tree[root].Kind != PatternKind.GroupHeader)
                throw new SparqlParseException("CONSTRUCT WHERE shorthand does not allow subqueries");
            var e = tree.EnumerateDirectChildren(root);
            while (e.MoveNext())
            {
                var kind = e.Current.Kind;
                if (kind == PatternKind.Triple && e.Current.PathKind == PathType.None)
                {
                    // The tree holds a labeled blank node as a variable without a sigil (it matches as one); in the
                    // template it is a blank node again, a new one per solution
                    var triple = e.Current.ToTriplePattern();
                    triple.Subject = TemplateTerm(triple.Subject);
                    triple.Object = TemplateTerm(triple.Object);
                    template.AddPattern(triple);
                    continue;
                }
                throw new SparqlParseException("CONSTRUCT WHERE shorthand does not allow " + kind switch
                {
                    PatternKind.Triple => "property paths",
                    PatternKind.Filter => "FILTER",
                    PatternKind.GraphHeader => "GRAPH",
                    PatternKind.OptionalHeader => "OPTIONAL",
                    PatternKind.Bind => "BIND",
                    PatternKind.MinusHeader or PatternKind.MinusTriple => "MINUS",
                    PatternKind.UnionHeader => "UNION",
                    PatternKind.ServiceHeader => "SERVICE",
                    PatternKind.SubSelectHeader => "subqueries",
                    PatternKind.ValuesHeader => "VALUES",
                    PatternKind.ExistsHeader or PatternKind.NotExistsHeader => "EXISTS",
                    PatternKind.GroupHeader => "nested groups",
                    _ => kind.ToString(),
                });
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(treeBuffer);
        }
        return template;
    }

    /// <summary>
    /// What a grouped query projects (§11.4): a projected variable is a GROUP BY variable, or a variable of the
    /// trailing VALUES, which is joined after grouping (§18.2.4.3). With aggregates and no GROUP BY there is one group
    /// and no GROUP BY variable.
    /// </summary>
    private void ValidateSelectVariables(SelectClause selectClause, GroupByClause groupBy, ValuesClause values, bool hasActualAggregates)
    {
        if (!groupBy.HasGroupBy && !hasActualAggregates)
            return;

        for (int i = 0; i < selectClause.ProjectedVariableCount; i++)
        {
            var (varStart, varLen) = selectClause.GetProjectedVariable(i);
            if (varLen == 0) continue;
            var varName = _source.Slice(varStart, varLen);

            bool found = false;
            for (int j = 0; j < groupBy.Count && !found; j++)
            {
                var (gbStart, gbLen) = groupBy.GetVariable(j);
                found = gbLen > 0 && VariableName.Same(varName, _source.Slice(gbStart, gbLen));
            }
            if (values.IsPresent)
            {
                var reader = new DataBlockReader(_source, values.Start);
                while (!found && reader.MoveNextVariable(out int vStart, out int vLen))
                    found = VariableName.Same(varName, _source.Slice(vStart, vLen));
            }
            if (found)
                continue;

            throw new SparqlParseException(groupBy.HasGroupBy
                ? $"Variable {varName.ToString()} in SELECT is not in GROUP BY and is not an aggregate"
                : $"Variable {varName.ToString()} is not allowed in SELECT with aggregates but no GROUP BY");
        }
    }

    private Query ParseConstructQuery(Prologue prologue)
    {
        ConsumeKeyword("CONSTRUCT");
        SkipWhitespace();

        // Check for CONSTRUCT WHERE shorthand (WHERE keyword or FROM before template)
        var span = PeekSpan(5);
        bool isShorthand = (span.Length >= 5 && span[..5].Equals("WHERE", StringComparison.OrdinalIgnoreCase)) ||
                           (span.Length >= 4 && span[..4].Equals("FROM", StringComparison.OrdinalIgnoreCase));

        ConstructTemplate template;
        DatasetClause[] datasets;
        WhereClause whereClause;

        if (isShorthand)
        {
            // CONSTRUCT WHERE { pattern } or CONSTRUCT FROM <g> WHERE { pattern }
            // The WHERE clause patterns serve as both template and query pattern
            datasets = ParseDatasetClauses();
            whereClause = ParseWhereClause();

            // constructwhere05/06: CONSTRUCT WHERE shorthand restricts what can appear in the pattern
            // Only basic triple patterns are allowed (no FILTER, GRAPH, OPTIONAL, etc.)
            template = ConstructTemplateFromWhere(whereClause.GroupStart);
        }
        else
        {
            // Standard CONSTRUCT { template } WHERE { pattern }
            template = ParseConstructTemplate();
            datasets = ParseDatasetClauses();
            whereClause = ParseWhereClause();
        }

        var modifier = ParseQuerySolutionModifier();

        return new Query
        {
            Type = QueryType.Construct,
            Prologue = prologue,
            ConstructTemplate = template,
            Datasets = datasets,
            WhereClause = whereClause,
            SolutionModifier = modifier
        };
    }

    /// <summary>
    /// [73] ConstructTemplate ::= '{' ConstructTriples? '}'; [74] ConstructTriples ::= TriplesSameSubject ( '.'
    /// ConstructTriples? )?. Read by the one template grammar Update uses (<see cref="ParseTriplesTemplate"/>), with no
    /// GRAPH block: collections, property lists, object lists and 'a' as in any template.
    /// </summary>
    private ConstructTemplate ParseConstructTemplate()
    {
        _constructListNodeCounter = 0; // template nodes are numbered per template
        if (Peek() != '{')
            throw new SparqlParseException("Expected '{' in CONSTRUCT template");
        Advance();

        var quads = new List<TemplateQuad>();
        ParseTriplesTemplate(default, quads);
        SkipWhitespace();
        if (Peek() != '}')
            throw new SparqlParseException("Expected '}' in CONSTRUCT template");
        Advance();

        var template = new ConstructTemplate();
        foreach (var q in quads)
            template.AddPattern(new TriplePattern { Subject = q.Subject, Predicate = q.Predicate, Object = q.Object });
        return template;
    }

    /// <summary>[11] DescribeQuery ::= 'DESCRIBE' ( VarOrIri+ | '*' ) DatasetClause* WhereClause? SolutionModifier</summary>
    private Query ParseDescribeQuery(Prologue prologue)
    {
        ConsumeKeyword("DESCRIBE");
        SkipWhitespace();

        var describe = new DescribeClause();
        if (Peek() == '*')
        {
            Advance();
            describe.All = true;
        }
        else
        {
            describe.TargetsStart = _position;
            var targets = new DescribeTargetReader(_source, _position);
            if (!targets.MoveNext(out _))
                throw new SparqlParseException("DESCRIBE expects variables, IRIs or '*'");
            while (targets.MoveNext(out _)) { }
            _position = targets.End;
        }

        var datasets = ParseDatasetClauses();
        var whereClause = new WhereClause { GroupStart = -1 };

        // [17] WhereClause ::= 'WHERE'? GroupGraphPattern, optional in a DESCRIBE
        SkipWhitespace();
        if (AtKeyword("WHERE") || Peek() == '{')
        {
            whereClause = ParseWhereClause();
        }

        var modifier = ParseQuerySolutionModifier();

        return new Query
        {
            Type = QueryType.Describe,
            Prologue = prologue,
            Describe = describe,
            Datasets = datasets,
            WhereClause = whereClause,
            SolutionModifier = modifier
        };
    }

    /// <summary>[12] AskQuery ::= 'ASK' DatasetClause* WhereClause SolutionModifier</summary>
    private Query ParseAskQuery(Prologue prologue)
    {
        ConsumeKeyword("ASK");
        SkipWhitespace();

        var datasets = ParseDatasetClauses();
        var whereClause = ParseWhereClause();
        var modifier = ParseQuerySolutionModifier();

        return new Query
        {
            Type = QueryType.Ask,
            Prologue = prologue,
            Datasets = datasets,
            WhereClause = whereClause,
            SolutionModifier = modifier
        };
    }

    private SelectClause ParseSelectClause()
    {
        ConsumeKeyword("SELECT");
        SkipWhitespace();

        var clause = new SelectClause();

        var span = PeekSpan(8);
        if (AtKeyword("DISTINCT"))
        {
            ConsumeKeyword("DISTINCT");
            SkipWhitespace();
            clause.Distinct = true;
        }
        else if (span.Length >= 7 && span[..7].Equals("REDUCED", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("REDUCED");
            SkipWhitespace();
            clause.Reduced = true;
        }

        // Parse variables, * or aggregate expressions
        if (Peek() == '*')
        {
            Advance();
            clause.SelectAll = true;
        }
        else
        {
            // Track aliases for duplicate detection (syn-bad-03)
            Span<int> aliasHashes = stackalloc int[24];
            int aliasCount = 0;

            // Parse projection list (variables and aggregate expressions)
            while (!IsAtEnd())
            {
                SkipWhitespace();

                // Check for aggregate expression: (COUNT(?x) AS ?alias)
                if (Peek() == '(')
                {
                    var agg = ParseAggregateExpression();
                    if (agg.Function != AggregateFunction.None)
                    {
                        // syn-bad-05: Aggregate expressions must have AS alias
                        if (agg.AliasLength == 0)
                        {
                            throw new SparqlParseException("Aggregate expressions must use AS to assign an alias variable");
                        }

                        // Check for duplicate alias
                        var aliasSpan = _source.Slice(agg.AliasStart, agg.AliasLength);
                        var aliasHash = VariableName.Hash(aliasSpan);
                        for (int i = 0; i < aliasCount; i++)
                        {
                            if (aliasHashes[i] == aliasHash)
                            {
                                throw new SparqlParseException($"Duplicate variable name in SELECT: {aliasSpan.ToString()}");
                            }
                        }
                        if (aliasCount < 24) aliasHashes[aliasCount++] = aliasHash;

                        clause.AddAggregate(agg);
                    }
                    else
                    {
                        // Check if this was a non-aggregate expression (expr AS ?var)
                        // The parser returned None function, but we may have parsed an alias
                        // We need to track these aliases too for duplicate detection and scope validation
                        if (agg.AliasLength > 0)
                        {
                            var aliasSpan = _source.Slice(agg.AliasStart, agg.AliasLength);
                            var aliasHash = VariableName.Hash(aliasSpan);
                            for (int i = 0; i < aliasCount; i++)
                            {
                                if (aliasHashes[i] == aliasHash)
                                {
                                    throw new SparqlParseException($"Duplicate variable name in SELECT: {aliasSpan.ToString()}");
                                }
                            }
                            if (aliasCount < 24) aliasHashes[aliasCount++] = aliasHash;

                            // Also add to aggregates list so scope validation can find the alias
                            clause.AddAggregate(agg);

                            // Extract embedded aggregates from the expression (e.g., MIN(?p), MAX(?p) in "(MIN(?p) + MAX(?p)) / 2")
                            // These need to be tracked separately so they can be computed during aggregation
                            ExtractEmbeddedAggregates(agg.VariableStart, agg.VariableLength, ref clause);
                        }
                    }
                }
                // Check for variable
                else if (VariableName.IsSigil(Peek()))
                {
                    // Store variable position for validation
                    var varStart = _position;
                    Advance(); // Skip '?'
                    while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                        Advance();

                    // Check for duplicate variable name
                    var varSpan = _source.Slice(varStart, _position - varStart);
                    var varHash = VariableName.Hash(varSpan);
                    for (int i = 0; i < aliasCount; i++)
                    {
                        if (aliasHashes[i] == varHash)
                        {
                            throw new SparqlParseException($"Duplicate variable name in SELECT: {varSpan.ToString()}");
                        }
                    }
                    if (aliasCount < 24) aliasHashes[aliasCount++] = varHash;

                    clause.AddProjectedVariable(varStart, _position - varStart);
                }
                else
                {
                    // syn-bad-05: Check for aggregate function names without enclosing parentheses
                    // e.g., SELECT COUNT(*) {} is invalid - must be SELECT (COUNT(*) AS ?c) {}
                    var aggSpan = PeekSpan(12);
                    if ((aggSpan.Length >= 5 && aggSpan[..5].Equals("COUNT", StringComparison.OrdinalIgnoreCase) && !char.IsLetterOrDigit(aggSpan[5])) ||
                        (aggSpan.Length >= 3 && aggSpan[..3].Equals("SUM", StringComparison.OrdinalIgnoreCase) && !char.IsLetterOrDigit(aggSpan[3])) ||
                        (aggSpan.Length >= 3 && aggSpan[..3].Equals("AVG", StringComparison.OrdinalIgnoreCase) && !char.IsLetterOrDigit(aggSpan[3])) ||
                        (aggSpan.Length >= 3 && aggSpan[..3].Equals("MIN", StringComparison.OrdinalIgnoreCase) && !char.IsLetterOrDigit(aggSpan[3])) ||
                        (aggSpan.Length >= 3 && aggSpan[..3].Equals("MAX", StringComparison.OrdinalIgnoreCase) && !char.IsLetterOrDigit(aggSpan[3])) ||
                        (aggSpan.Length >= 6 && aggSpan[..6].Equals("SAMPLE", StringComparison.OrdinalIgnoreCase) && !char.IsLetterOrDigit(aggSpan[6])) ||
                        (aggSpan.Length >= 12 && aggSpan[..12].Equals("GROUP_CONCAT", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new SparqlParseException("Aggregate expressions in SELECT must be enclosed in parentheses with AS alias: (FUNC(...) AS ?var)");
                    }

                    // End of projection list
                    break;
                }

                SkipWhitespace();
            }
        }

        return clause;
    }


    /// <summary>
    /// A SELECT expression, <c>'(' Expression 'AS' Var ')'</c> ([9]). An aggregate alone, <c>(COUNT(?x) AS ?n)</c>, is an
    /// aggregate entry, its argument read by <see cref="ReadAggregateArguments"/>; any other expression, aggregates
    /// inside it or not (<c>(MAX(?o) - MIN(?o) AS ?span)</c>), is an expression entry (Function None) whose text is
    /// evaluated, with its aggregates extracted by the caller.
    /// </summary>
    private AggregateExpression ParseAggregateExpression()
    {
        var agg = new AggregateExpression();

        if (Peek() != '(')
            return agg;

        Advance(); // Skip '('
        SkipWhitespace();

        // The expression's tokens up to its AS keyword, then the alias (syn-bad-04, syn-bad-05: AS is required)
        int exprStart = _position;
        while (!IsAtEnd() && Peek() != ')' && !AtKeyword("AS"))
            SkipExpressionToken();
        if (!AtKeyword("AS"))
            throw new SparqlParseException("Expression in SELECT must use AS to assign an alias variable");
        int exprEnd = _position;
        while (exprEnd > exprStart && char.IsWhiteSpace(_source[exprEnd - 1]))
            exprEnd--;
        if (exprEnd == exprStart)
            throw new SparqlParseException($"Expected an expression before AS at position {_position}");

        ConsumeKeyword("AS");
        SkipWhitespace();
        if (!VariableName.IsSigil(Peek()))
            throw new SparqlParseException($"Expected a variable after AS at position {_position}");
        agg.AliasStart = _position;
        Advance();
        while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
            Advance();
        agg.AliasLength = _position - agg.AliasStart;
        SkipWhitespace();
        if (Peek() != ')')
            throw new SparqlParseException($"Expected ')' to close the SELECT expression at position {_position}");
        Advance();

        var calls = new SparqlParser(_source).AggregateCallsIn(exprStart, exprEnd - exprStart);
        if (calls.Count == 1 && calls[0].NameStart == exprStart && calls[0].End == exprEnd)
        {
            agg.Function = calls[0].Function;
            var reader = new SparqlParser(_source) { _position = calls[0].ParenStart };
            reader.ReadAggregateArguments(ref agg);
            return agg;
        }

        agg.VariableStart = exprStart;
        agg.VariableLength = exprEnd - exprStart;
        return agg;
    }

    /// <summary>
    /// [127] Aggregate, from its '(': <c>'(' 'DISTINCT'? ( '*' | Expression ) ( ';' 'SEPARATOR' '=' String )? ')'</c>
    /// (the separator for GROUP_CONCAT only). Sets the aggregate's DISTINCT, its argument (<c>*</c>, or the
    /// expression, which may be one variable) and its separator's content; the cursor ends past the ')'. The argument
    /// is read with the expression token rule, so a ')' or ';' inside a literal or a bracket does not end it. The one
    /// reader of an aggregate's argument, for an aggregate a SELECT expression is and one inside an expression.
    /// </summary>
    private void ReadAggregateArguments(ref AggregateExpression agg)
    {
        Advance(); // '('
        SkipWhitespace();
        if (AtKeyword("DISTINCT"))
        {
            ConsumeKeyword("DISTINCT");
            SkipWhitespace();
            agg.Distinct = true;
        }

        agg.VariableStart = _position;
        while (!IsAtEnd() && Peek() is not (')' or ';' or ','))
            SkipExpressionToken();
        if (Peek() == ',')
            throw new SparqlParseException($"An aggregate takes one argument; unexpected ',' at position {_position}"); // syn-bad-06
        int end = _position;
        while (end > agg.VariableStart && char.IsWhiteSpace(_source[end - 1]))
            end--;
        agg.VariableLength = end - agg.VariableStart;
        if (agg.VariableLength == 0)
            throw new SparqlParseException($"Expected an expression or '*' as the aggregate's argument at position {_position}");

        if (Peek() == ';')
        {
            Advance();
            SkipWhitespace();
            if (agg.Function != AggregateFunction.GroupConcat || !AtKeyword("SEPARATOR"))
                throw new SparqlParseException($"Expected ')' to close the aggregate at position {_position}");
            ConsumeKeyword("SEPARATOR");
            SkipWhitespace();
            if (Peek() != '=')
                throw new SparqlParseException($"Expected '=' after SEPARATOR at position {_position}");
            Advance();
            SkipWhitespace();
            if (Peek() is not ('"' or '\''))
                throw new SparqlParseException($"Expected a string after SEPARATOR = at position {_position}");
            int literalStart = _position;
            SkipStringLiteral();
            int quotes = _position - literalStart >= 6 && _source[literalStart + 1] == _source[literalStart]
                && _source[literalStart + 2] == _source[literalStart] ? 3 : 1;
            agg.SeparatorStart = literalStart + quotes;
            agg.SeparatorLength = _position - quotes - agg.SeparatorStart;
            SkipWhitespace();
        }

        if (Peek() != ')')
            throw new SparqlParseException($"Expected ')' to close the aggregate at position {_position}");
        Advance();
    }

    /// <summary>
    /// Extracts embedded aggregate function calls from an expression and adds them to the SelectClause.
    /// For example, "(MIN(?p) + MAX(?p)) / 2" contains MIN(?p) and MAX(?p) which need to be tracked.
    /// </summary>
    private void ExtractEmbeddedAggregates(int exprStart, int exprLength, ref SelectClause clause)
    {
        foreach (var (function, _, parenStart, _) in new SparqlParser(_source).AggregateCallsIn(exprStart, exprLength))
        {
            // A hidden aggregate (no alias), unless the clause has the same aggregate already
            var embedded = AggregateCallAt(function, parenStart);
            bool present = false;
            for (int i = 0; i < clause.AggregateCount && !present; i++)
                present = SameAggregate(clause.GetAggregate(i), embedded);
            if (!present)
                clause.AddAggregate(embedded);
        }
    }

    /// <summary>
    /// §18.2.4.1: the aggregates of HAVING and ORDER BY are computed for each group, as the SELECT clause's are. Each
    /// one the clause does not have is added to it, hidden (no alias); an aggregate in either makes the query group.
    /// </summary>
    private void AddModifierAggregates(ref SelectClause select, in SolutionModifier modifier)
    {
        if (modifier.Having.HasHaving)
            ExtractEmbeddedAggregates(modifier.Having.ExpressionStart, modifier.Having.ExpressionLength, ref select);
        for (int c = 0; c < modifier.OrderBy.Count; c++)
        {
            var condition = modifier.OrderBy.GetCondition(c);
            ExtractEmbeddedAggregates(condition.ExpressionStart, condition.ExpressionLength, ref select);
        }
    }

    /// <summary>The aggregate call of <paramref name="function"/> whose '(' is at <paramref name="parenStart"/>: its
    /// DISTINCT, argument and separator (<see cref="ReadAggregateArguments"/>), with no alias.</summary>
    internal readonly AggregateExpression AggregateCallAt(AggregateFunction function, int parenStart)
    {
        var call = new AggregateExpression { Function = function };
        var reader = new SparqlParser(_source) { _position = parenStart };
        reader.ReadAggregateArguments(ref call);
        return call;
    }

    /// <summary>
    /// Whether two aggregates are one: the same function, DISTINCT, separator, and argument token for token
    /// (<see cref="SameExpression"/>), so COUNT(?x) in HAVING is the SELECT's count( $x ).
    /// </summary>
    internal readonly bool SameAggregate(in AggregateExpression a, in AggregateExpression b)
        => a.Function == b.Function && a.Distinct == b.Distinct
           && _source.Slice(a.SeparatorStart, a.SeparatorLength).SequenceEqual(_source.Slice(b.SeparatorStart, b.SeparatorLength))
           && SameExpression(a.VariableStart, a.VariableLength, b.VariableStart, b.VariableLength);

    /// <summary>
    /// [13] DatasetClause ::= 'FROM' ( DefaultGraphClause | NamedGraphClause )
    /// [14] DefaultGraphClause ::= SourceSelector
    /// [15] NamedGraphClause ::= 'NAMED' SourceSelector
    /// [16] SourceSelector ::= iri
    /// </summary>
    private DatasetClause[] ParseDatasetClauses()
    {
        const int MaxDatasetClauses = 16;
        Span<DatasetClause> clauses = stackalloc DatasetClause[MaxDatasetClauses];
        int count = 0;

        SkipWhitespace();

        // Loop while we see FROM keyword
        while (!IsAtEnd())
        {
            var span = PeekSpan(4);
            if (span.Length < 4 || !span.Equals("FROM", StringComparison.OrdinalIgnoreCase))
                break;

            if (count >= MaxDatasetClauses)
                throw new SparqlParseException($"Too many dataset clauses (max {MaxDatasetClauses})");

            ConsumeKeyword("FROM");
            SkipWhitespace();

            // Check for NAMED keyword
            bool isNamed = false;
            span = PeekSpan(5);
            if (span.Length >= 5 && span.Equals("NAMED", StringComparison.OrdinalIgnoreCase))
            {
                ConsumeKeyword("NAMED");
                SkipWhitespace();
                isNamed = true;
            }

            // Parse the IRI
            var iri = ParseTerm();
            if (!iri.IsIri)
                throw new SparqlParseException("Expected IRI in FROM clause");

            clauses[count++] = isNamed
                ? DatasetClause.Named(iri.Start, iri.Length)
                : DatasetClause.Default(iri.Start, iri.Length);

            SkipWhitespace();
        }

        if (count == 0)
            return Array.Empty<DatasetClause>();

        // Copy to heap array
        var result = new DatasetClause[count];
        for (int i = 0; i < count; i++)
            result[i] = clauses[i];

        return result;
    }

    private WhereClause ParseWhereClause()
    {
        SkipWhitespace();

        var span = PeekSpan(5);
        if (span.Length >= 5 && span[..5].Equals("WHERE", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("WHERE");
        }

        SkipWhitespace();
        if (Peek() != '{')
            throw new SparqlParseException($"Expected '{{' to open the WHERE group (position {_position})");

        // [53] GroupGraphPattern, parsed once, into the pattern tree execution evaluates from here (ADR-055 D1)
        int groupStart = _position;
        ParseWhereGroup(groupStart);
        return new WhereClause { GroupStart = groupStart };
    }

    /// <summary>
    /// [8] SubSelect ::= SelectClause WhereClause SolutionModifier ValuesClause, parsed into the pattern tree at the
    /// cursor (ADR-055 D4): a SubSelectHeader whose one child is the WHERE group, parsed by the same group parser as
    /// every other group. The header records where the solution modifiers start, so its clauses can be read again
    /// without its WHERE (<see cref="ParseSubSelectClausesAt"/>).
    /// </summary>
    private void ParseSubSelectTree(scoped ref PatternArray pa, int depth)
    {
        int header = pa.BeginSubSelect(_position);
        ParseSubSelectHead();
        int where = pa.Count;
        ParseGroupTree(ref pa, depth + 1); // [17] WhereClause ::= 'WHERE'? GroupGraphPattern
        SkipWhitespace();
        int modifiersStart = _position;
        ParseSubSelectTail(ref pa, where, pa[header].GraphTermStart, modifiersStart);
        pa.EndSubSelect(header, modifiersStart, _position);
    }

    /// <summary>[9] SelectClause and the optional WHERE keyword: moves the cursor to the WHERE group.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)] // keeps the clause structs out of the recursive group parser's frame
    private void ParseSubSelectHead()
    {
        ParseSelectClause();
        SkipWhitespace();
        if (AtKeyword("WHERE"))
        {
            ConsumeKeyword("WHERE");
            SkipWhitespace();
        }
    }

    /// <summary>
    /// SolutionModifier ValuesClause, checked against the SelectClause as a query's are (<see cref="ValidateSelectScope"/>),
    /// with the WHERE the slots from <paramref name="where"/> on: moves the cursor past the sub-SELECT.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ParseSubSelectTail(scoped ref PatternArray pa, int where, int selectStart, int modifiersStart)
    {
        var clauses = ParseSubSelectClausesAt(selectStart, modifiersStart);
        ValidateGroupBySemantics(clauses.Select, clauses.Modifier.GroupBy, clauses.Values);
        if (clauses.Select.AggregateCount > 0)
            ThrowIfAssignedInScope(ref pa, where, in clauses);
    }

    /// <summary>
    /// <see cref="ThrowIfAssignedInScope(SelectClause, GroupByClause, ValuesClause, ref InScopeVariables)"/> for a
    /// sub-SELECT, whose WHERE is the slots from <paramref name="where"/> on.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)] // the in-scope set stays out of the sub-SELECT parse's frame
    private void ThrowIfAssignedInScope(scoped ref PatternArray pa, int where, in SubSelect clauses)
    {
        var visible = new InScopeVariables(_source);
        try
        {
            AddInScopeVariables(ref pa, where, pa.Count, ref visible);
            ThrowIfAssignedInScope(clauses.Select, clauses.Modifier.GroupBy, clauses.Values, ref visible);
        }
        finally
        {
            visible.Dispose();
        }
    }

    /// <summary>
    /// A sub-SELECT's clauses without its WHERE: the SelectClause at <paramref name="selectStart"/> and the solution
    /// modifiers at <paramref name="modifiersStart"/>, both recorded on its SubSelectHeader.
    /// </summary>
    internal SubSelect ParseSubSelectClausesAt(int selectStart, int modifiersStart)
    {
        var subSelect = new SubSelect();
        _position = selectStart;
        subSelect.Select = ParseSelectClause();
        _position = modifiersStart;
        ParseSubSelectSolutionModifiers(ref subSelect);
        AddModifierAggregates(ref subSelect.Select, subSelect.Modifier);
        return subSelect;
    }

    /// <summary>
    /// A sub-SELECT's SolutionModifier ValuesClause: the same [18] and [28] parses a query uses.
    /// </summary>
    private void ParseSubSelectSolutionModifiers(ref SubSelect subSelect)
    {
        subSelect.Modifier = ParseSolutionModifier();
        SkipWhitespace();
        if (AtKeyword("VALUES"))
        {
            subSelect.Values = ParseValuesClause();
            SkipWhitespace();
        }
    }
}
