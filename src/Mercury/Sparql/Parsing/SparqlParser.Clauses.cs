using System;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Execution.Operators;
using SkyOmega.Mercury.Sparql.Patterns;

namespace SkyOmega.Mercury.Sparql.Parsing;

internal ref partial struct SparqlParser
{
    /// <summary>
    /// [7] SelectQuery ::= SelectClause DatasetClause* WhereClause SolutionModifier
    /// </summary>
    private Query ParseSelectQuery(Prologue prologue)
    {
        var selectClause = ParseSelectClause();
        var datasets = ParseDatasetClauses();
        var whereClause = ParseWhereClause();
        var modifier = ParseQuerySolutionModifier();

        // Validate GROUP BY semantics
        ValidateGroupBySemantics(selectClause, modifier.GroupBy);

        // Validate SELECT alias scope conflicts with subqueries (syntax-SELECTscope2)
        ValidateSelectSubqueryScope(selectClause, whereClause.GroupStart);

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
    /// Validates that SELECT clause aliases don't conflict with subquery projections (syntax-SELECTscope2).
    /// </summary>
    private void ValidateSelectSubqueryScope(SelectClause selectClause, int whereGroupStart)
    {
        // Collect SELECT clause aliases
        Span<int> selectAliasHashes = stackalloc int[32];
        int selectAliasCount = 0;

        // Check aggregate expression aliases
        for (int i = 0; i < selectClause.AggregateCount; i++)
        {
            var agg = selectClause.GetAggregate(i);
            if (agg.AliasLength > 0)
            {
                var hash = VariableName.Hash(_source.Slice(agg.AliasStart, agg.AliasLength));
                if (selectAliasCount < 32) selectAliasHashes[selectAliasCount++] = hash;
            }
        }

        // If no aliases to check, nothing to do
        if (selectAliasCount == 0 || whereGroupStart < 0) return;

        // Check each sub-SELECT of the WHERE group (the group itself, or its direct children) for a projected
        // variable or expression alias that one of the outer aliases reuses
        const int treeBytes = PatternSlot.Size * 1024;
        var treeBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(treeBytes);
        try
        {
            var tree = new PatternArray(treeBuffer.AsSpan(0, treeBytes));
            int root = new SparqlParser(_source).ParsePatternTreeAt(whereGroupStart, ref tree);
            if (tree[root].Kind == PatternKind.SubSelectHeader)
                ThrowOnSubSelectAliasConflict(tree[root], selectAliasHashes[..selectAliasCount]);
            var e = tree.EnumerateDirectChildren(root);
            while (e.MoveNext())
            {
                if (e.Current.Kind == PatternKind.SubSelectHeader)
                    ThrowOnSubSelectAliasConflict(e.Current, selectAliasHashes[..selectAliasCount]);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(treeBuffer);
        }
    }

    /// <summary>syntax-SELECTscope2: an outer SELECT alias must not reuse a variable the sub-SELECT at
    /// <paramref name="subSelect"/> projects.</summary>
    private void ThrowOnSubSelectAliasConflict(PatternSlot subSelect, scoped ReadOnlySpan<int> aliasHashes)
    {
        var subquery = new SparqlParser(_source).ParseSubSelectClausesAt(subSelect.GraphTermStart, subSelect.SubSelectModifiersStart);
        int projected = subquery.Select.ProjectedVariableCount;
        for (int j = 0; j < projected + subquery.Select.AggregateCount; j++)
        {
            var (start, len) = j < projected
                ? subquery.Select.GetProjectedVariable(j)
                : (subquery.Select.GetAggregate(j - projected).AliasStart, subquery.Select.GetAggregate(j - projected).AliasLength);
            if (len == 0) continue;
            var hash = VariableName.Hash(_source.Slice(start, len));
            foreach (var aliasHash in aliasHashes)
            {
                if (aliasHash == hash)
                    throw new SparqlParseException($"Variable {_source.Slice(start, len).ToString()} in SELECT conflicts with same variable in subquery projection");
            }
        }
    }

    /// <summary>
    /// Validates GROUP BY semantic constraints according to SPARQL 1.1 spec.
    /// </summary>
    private void ValidateGroupBySemantics(SelectClause selectClause, GroupByClause groupBy)
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
            ValidateSelectVariables(selectClause, groupBy, hasActualAggregates);
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
    /// Validates that SELECT variables are properly scoped for GROUP BY.
    /// </summary>
    private void ValidateSelectVariables(SelectClause selectClause, GroupByClause groupBy, bool hasActualAggregates)
    {
        // If there are actual aggregate functions but no GROUP BY, non-aggregate variables are not allowed
        if (hasActualAggregates && !groupBy.HasGroupBy)
        {
            // Any projected variable is an error - only aggregates allowed
            for (int i = 0; i < selectClause.ProjectedVariableCount; i++)
            {
                var (start, len) = selectClause.GetProjectedVariable(i);
                if (len > 0)
                {
                    var varName = _source.Slice(start, len);
                    throw new SparqlParseException($"Variable {varName.ToString()} is not allowed in SELECT with aggregates but no GROUP BY");
                }
            }
        }

        // If there's GROUP BY, all projected variables must be in GROUP BY
        if (groupBy.HasGroupBy)
        {
            for (int i = 0; i < selectClause.ProjectedVariableCount; i++)
            {
                var (varStart, varLen) = selectClause.GetProjectedVariable(i);
                if (varLen == 0) continue;

                var varName = _source.Slice(varStart, varLen);
                bool found = false;

                // Check if variable is in GROUP BY
                for (int j = 0; j < groupBy.Count; j++)
                {
                    var (gbStart, gbLen) = groupBy.GetVariable(j);
                    if (gbLen > 0)
                    {
                        var gbName = _source.Slice(gbStart, gbLen);
                        if (VariableName.Same(varName, gbName))
                        {
                            found = true;
                            break;
                        }
                    }
                }

                if (!found)
                {
                    throw new SparqlParseException($"Variable {varName.ToString()} in SELECT is not in GROUP BY and is not an aggregate");
                }
            }
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
        if (KeywordIs(PeekSpan(5), "WHERE") || Peek() == '{')
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
        if (span.Length >= 8 && span[..8].Equals("DISTINCT", StringComparison.OrdinalIgnoreCase))
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


    private AggregateExpression ParseAggregateExpression()
    {
        var agg = new AggregateExpression();

        if (Peek() != '(')
            return agg;

        Advance(); // Skip '('
        SkipWhitespace();

        // Parse aggregate function name
        var span = PeekSpan(12);
        if (span.Length >= 12 && span[..12].Equals("GROUP_CONCAT", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("GROUP_CONCAT");
            agg.Function = AggregateFunction.GroupConcat;
        }
        else if (span.Length >= 5 && span[..5].Equals("COUNT", StringComparison.OrdinalIgnoreCase) &&
                 (span.Length == 5 || !char.IsLetterOrDigit(span[5])))
        {
            ConsumeKeyword("COUNT");
            agg.Function = AggregateFunction.Count;
        }
        else if (span.Length >= 3 && span[..3].Equals("SUM", StringComparison.OrdinalIgnoreCase) &&
                 (span.Length == 3 || !char.IsLetterOrDigit(span[3])))
        {
            ConsumeKeyword("SUM");
            agg.Function = AggregateFunction.Sum;
        }
        else if (span.Length >= 3 && span[..3].Equals("AVG", StringComparison.OrdinalIgnoreCase) &&
                 (span.Length == 3 || !char.IsLetterOrDigit(span[3])))
        {
            ConsumeKeyword("AVG");
            agg.Function = AggregateFunction.Avg;
        }
        else if (span.Length >= 3 && span[..3].Equals("MIN", StringComparison.OrdinalIgnoreCase) &&
                 (span.Length == 3 || !char.IsLetterOrDigit(span[3])))
        {
            ConsumeKeyword("MIN");
            agg.Function = AggregateFunction.Min;
        }
        else if (span.Length >= 3 && span[..3].Equals("MAX", StringComparison.OrdinalIgnoreCase) &&
                 (span.Length == 3 || !char.IsLetterOrDigit(span[3])))
        {
            ConsumeKeyword("MAX");
            agg.Function = AggregateFunction.Max;
        }
        else if (span.Length >= 6 && span[..6].Equals("SAMPLE", StringComparison.OrdinalIgnoreCase) &&
                 (span.Length == 6 || !char.IsLetterOrDigit(span[6])))
        {
            ConsumeKeyword("SAMPLE");
            agg.Function = AggregateFunction.Sample;
        }
        else
        {
            // Not a recognized aggregate - parse as expression (expr AS ?var)
            // Store expression start position for later evaluation
            int exprStart = _position;
            int exprEnd = _position;

            // The expression's tokens up to its AS keyword, then the alias
            bool foundAs = false;
            while (!IsAtEnd() && Peek() != ')' && !AtExpressionKeyword("AS"))
                SkipExpressionToken();
            if (AtExpressionKeyword("AS"))
            {
                exprEnd = _position;
                while (exprEnd > exprStart && char.IsWhiteSpace(_source[exprEnd - 1]))
                    exprEnd--;

                ConsumeKeyword("AS");
                SkipWhitespace();
                if (VariableName.IsSigil(Peek()))
                {
                    agg.AliasStart = _position;
                    Advance();
                    while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                        Advance();
                    agg.AliasLength = _position - agg.AliasStart;
                    foundAs = true;
                }
            }

            // syn-bad-04: Expression in SELECT must use AS to assign alias
            if (!foundAs)
            {
                throw new SparqlParseException("Expression in SELECT must use AS to assign an alias variable");
            }

            // Store expression in VariableStart/VariableLength for evaluation
            agg.VariableStart = exprStart;
            agg.VariableLength = exprEnd - exprStart;

            // Skip closing ')' of expression
            SkipWhitespace();
            if (Peek() == ')')
                Advance();

            return agg;
        }

        SkipWhitespace();

        // Expect '(' for function arguments
        if (Peek() != '(')
        {
            // Skip to closing paren
            while (!IsAtEnd() && Peek() != ')')
                Advance();
            if (Peek() == ')') Advance();
            return agg;
        }

        Advance(); // Skip '('
        SkipWhitespace();

        // Check for DISTINCT
        span = PeekSpan(8);
        if (span.Length >= 8 && span[..8].Equals("DISTINCT", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("DISTINCT");
            SkipWhitespace();
            agg.Distinct = true;
        }

        // Parse aggregate argument: variable, *, or expression
        // Store the start of the expression for evaluation
        agg.VariableStart = _position;

        if (Peek() == '*')
        {
            Advance();
            agg.VariableLength = 1;
        }
        else if (VariableName.IsSigil(Peek()))
        {
            Advance();
            while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                Advance();
            agg.VariableLength = _position - agg.VariableStart;
        }
        else
        {
            // An expression argument: IF(...), xsd:double(?x), etc., up to the aggregate's ')' or GROUP_CONCAT's ';'
            while (!IsAtEnd() && Peek() != ')' && Peek() != ';')
                SkipExpressionToken();
            agg.VariableLength = _position - agg.VariableStart;
        }

        SkipWhitespace();

        // For GROUP_CONCAT, parse optional SEPARATOR clause: ; SEPARATOR = "..."
        if (agg.Function == AggregateFunction.GroupConcat && Peek() == ';')
        {
            Advance(); // Skip ';'
            SkipWhitespace();

            span = PeekSpan(9);
            if (span.Length >= 9 && span[..9].Equals("SEPARATOR", StringComparison.OrdinalIgnoreCase))
            {
                ConsumeKeyword("SEPARATOR");
                SkipWhitespace();

                if (Peek() == '=')
                {
                    Advance(); // Skip '='
                    SkipWhitespace();

                    // Parse string literal (single or double quoted)
                    if (Peek() == '"' || Peek() == '\'')
                    {
                        var quote = Peek();
                        Advance(); // Skip opening quote
                        agg.SeparatorStart = _position;
                        while (!IsAtEnd() && Peek() != quote)
                            Advance();
                        agg.SeparatorLength = _position - agg.SeparatorStart;
                        if (Peek() == quote)
                            Advance(); // Skip closing quote
                    }
                }
            }

            SkipWhitespace();
        }

        // Skip closing ')' of function
        if (Peek() == ')')
            Advance();

        SkipWhitespace();

        // Parse AS ?alias
        span = PeekSpan(3);
        if (span.Length >= 2 && span[..2].Equals("AS", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("AS");
            SkipWhitespace();

            if (VariableName.IsSigil(Peek()))
            {
                agg.AliasStart = _position;
                Advance();
                while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                    Advance();
                agg.AliasLength = _position - agg.AliasStart;
            }
        }

        SkipWhitespace();

        // Skip closing ')' of expression
        if (Peek() == ')')
            Advance();

        return agg;
    }

    /// <summary>
    /// Extracts embedded aggregate function calls from an expression and adds them to the SelectClause.
    /// For example, "(MIN(?p) + MAX(?p)) / 2" contains MIN(?p) and MAX(?p) which need to be tracked.
    /// </summary>
    private void ExtractEmbeddedAggregates(int exprStart, int exprLength, ref SelectClause clause)
    {
        var expr = _source.Slice(exprStart, exprLength);

        foreach (var (bestFunc, _, parenStartAbs, endAbs) in new SparqlParser(_source).AggregateCallsIn(exprStart, exprLength))
        {
            int parenStart = parenStartAbs - exprStart;
            int parenEnd = endAbs - exprStart; // just past ')'

            // Extract the variable/argument (skip DISTINCT if present)
            // Work with positions, not strings, to get correct offsets
            int argStart = parenStart + 1;
            int argEnd = parenEnd - 1; // Position of ')'

            // Skip leading whitespace
            while (argStart < argEnd && char.IsWhiteSpace(expr[argStart]))
                argStart++;

            // Skip trailing whitespace
            while (argEnd > argStart && char.IsWhiteSpace(expr[argEnd - 1]))
                argEnd--;

            // Check for DISTINCT keyword
            bool isDistinct = false;
            var argSlice = expr.Slice(argStart, argEnd - argStart);
            if (argSlice.Length >= 8)
            {
                var distinctCheck = argSlice.Slice(0, 8);
                if (distinctCheck.Equals("DISTINCT", StringComparison.OrdinalIgnoreCase))
                {
                    isDistinct = true;
                    argStart += 8;
                    // Skip whitespace after DISTINCT
                    while (argStart < argEnd && char.IsWhiteSpace(expr[argStart]))
                        argStart++;
                }
            }

            // Now argStart..argEnd points to the actual variable/expression (without DISTINCT)
            int varLength = argEnd - argStart;
            if (varLength <= 0)
                continue;

            // Create a hidden aggregate entry
            var embeddedAgg = new AggregateExpression
            {
                Function = bestFunc,
                VariableStart = exprStart + argStart,
                VariableLength = varLength,
                Distinct = isDistinct,
                // No alias - this is a hidden aggregate
                AliasStart = 0,
                AliasLength = 0
            };

            // Add if not already present (check by function + variable)
            bool alreadyExists = false;
            for (int i = 0; i < clause.AggregateCount; i++)
            {
                var existing = clause.GetAggregate(i);
                if (existing.Function == embeddedAgg.Function &&
                    existing.Distinct == embeddedAgg.Distinct)
                {
                    var existingVar = _source.Slice(existing.VariableStart, existing.VariableLength);
                    var newVar = _source.Slice(embeddedAgg.VariableStart, embeddedAgg.VariableLength);
                    if (existingVar.Equals(newVar, StringComparison.Ordinal))
                    {
                        alreadyExists = true;
                        break;
                    }
                }
            }

            if (!alreadyExists)
            {
                clause.AddAggregate(embeddedAgg);
            }
        }
    }

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
        ParseGroupTree(ref pa, depth + 1); // [17] WhereClause ::= 'WHERE'? GroupGraphPattern
        SkipWhitespace();
        int modifiersStart = _position;
        ParseSubSelectTail(pa[header].GraphTermStart, modifiersStart);
        pa.EndSubSelect(header, modifiersStart, _position);
    }

    /// <summary>[9] SelectClause and the optional WHERE keyword: moves the cursor to the WHERE group.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)] // keeps the clause structs out of the recursive group parser's frame
    private void ParseSubSelectHead()
    {
        ParseSelectClause();
        SkipWhitespace();
        if (KeywordIs(PeekSpan(5), "WHERE"))
        {
            ConsumeKeyword("WHERE");
            SkipWhitespace();
        }
    }

    /// <summary>
    /// SolutionModifier ValuesClause, checked against the SelectClause as a query's are (GROUP BY rules): moves the
    /// cursor past the sub-SELECT.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ParseSubSelectTail(int selectStart, int modifiersStart)
    {
        var clauses = ParseSubSelectClausesAt(selectStart, modifiersStart);
        ValidateGroupBySemantics(clauses.Select, clauses.Modifier.GroupBy);
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
        return subSelect;
    }

    /// <summary>
    /// A sub-SELECT's SolutionModifier ValuesClause: the same [18] and [28] parses a query uses.
    /// </summary>
    private void ParseSubSelectSolutionModifiers(ref SubSelect subSelect)
    {
        subSelect.Modifier = ParseSolutionModifier();
        SkipWhitespace();
        if (KeywordIs(PeekSpan(6), "VALUES"))
        {
            subSelect.Values = ParseValuesClause();
            SkipWhitespace();
        }
    }
}
