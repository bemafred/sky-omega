using System;
using SkyOmega.Mercury.Sparql;

namespace SkyOmega.Mercury.Sparql.Parsing;

public ref partial struct SparqlParser
{
    /// <summary>
    /// [7] SelectQuery ::= SelectClause DatasetClause* WhereClause SolutionModifier
    /// [2] Query ::= Prologue ( SelectQuery | ... ) ValuesClause
    /// </summary>
    private Query ParseSelectQuery(Prologue prologue)
    {
        var selectClause = ParseSelectClause();
        var datasets = ParseDatasetClauses();
        var whereClause = ParseWhereClause();
        var modifier = ParseSolutionModifier();

        // Validate GROUP BY semantics
        ValidateGroupBySemantics(selectClause, modifier.GroupBy);

        // Validate SELECT alias scope conflicts with subqueries (syntax-SELECTscope2)
        ValidateSelectSubqueryScope(selectClause, ref whereClause.Pattern);

        var query = new Query
        {
            Type = QueryType.Select,
            Prologue = prologue,
            SelectClause = selectClause,
            Datasets = datasets,
            WhereClause = whereClause,
            SolutionModifier = modifier
        };

        // Parse optional trailing VALUES clause
        SkipWhitespace();
        var span = PeekSpan(6);
        if (span.Length >= 6 && span[..6].Equals("VALUES", StringComparison.OrdinalIgnoreCase))
        {
            query.Values = ParseQueryValues();
        }

        return query;
    }

    /// <summary>
    /// Parse trailing VALUES clause for a query.
    /// VALUES ?var { value1 value2 ... } or VALUES (?var1 ?var2) { (val1 val2) ... }
    /// UNDEF values are stored with length = -1.
    /// </summary>
    private ValuesClause ParseQueryValues()
    {
        ConsumeKeyword("VALUES");
        SkipWhitespace();

        var values = new ValuesClause();

        // Check for multi-variable form: (?var1 ?var2 ...)
        if (Peek() == '(')
        {
            Advance(); // Skip '('
            SkipWhitespace();

            // Parse all variables
            while (!IsAtEnd() && Peek() == '?')
            {
                int varStart = _position;
                Advance(); // Skip '?'
                while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                    Advance();
                values.AddVariable(varStart, _position - varStart);
                SkipWhitespace();
            }

            if (Peek() == ')')
                Advance(); // Skip ')'
            SkipWhitespace();
        }
        else if (Peek() == '?')
        {
            int varStart = _position;
            Advance(); // Skip '?'
            while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                Advance();
            values.AddVariable(varStart, _position - varStart);
            SkipWhitespace();
        }
        else
        {
            return values;
        }

        int varCount = values.VariableCount;

        // Expect '{'
        if (Peek() != '{')
            return values;

        Advance(); // Skip '{'
        SkipWhitespace();

        // Parse value rows
        while (!IsAtEnd() && Peek() != '}')
        {
            SkipWhitespace();

            if (Peek() == '(')
            {
                Advance(); // Skip '('
                SkipWhitespace();

                int rowValueCount = 0;
                while (!IsAtEnd() && Peek() != ')')
                {
                    // Check for UNDEF first
                    var span = PeekSpan(5);
                    if (span.Length >= 5 && span[..5].Equals("UNDEF", StringComparison.OrdinalIgnoreCase))
                    {
                        ConsumeKeyword("UNDEF");
                        values.AddValue(0, -1); // Mark as UNDEF with length = -1
                        rowValueCount++;
                    }
                    else
                    {
                        int valueStart = _position;
                        int valueLen = ParseValuesValue();
                        if (valueLen > 0)
                        {
                            values.AddValue(valueStart, valueLen);
                            rowValueCount++;
                        }
                        else
                        {
                            break;
                        }
                    }
                    SkipWhitespace();
                }

                // Validate cardinality
                if (varCount > 0 && rowValueCount != varCount)
                {
                    if (rowValueCount < varCount)
                        throw new SparqlParseException($"VALUES row has {rowValueCount} values but {varCount} variables declared");
                    else
                        throw new SparqlParseException($"VALUES row has {rowValueCount} values but only {varCount} variables declared");
                }

                if (Peek() == ')')
                    Advance(); // Skip ')'
                SkipWhitespace();
            }
            else
            {
                // Single value (for single variable form)
                // Check for UNDEF
                var span = PeekSpan(5);
                if (span.Length >= 5 && span[..5].Equals("UNDEF", StringComparison.OrdinalIgnoreCase))
                {
                    ConsumeKeyword("UNDEF");
                    values.AddValue(0, -1); // Mark as UNDEF with length = -1
                }
                else
                {
                    int valueStart = _position;
                    int valueLen = ParseValuesValue();
                    if (valueLen > 0)
                    {
                        values.AddValue(valueStart, valueLen);
                    }
                }
                SkipWhitespace();
            }
        }

        if (Peek() == '}')
            Advance(); // Skip '}'

        return values;
    }

    /// <summary>
    /// Validates that SELECT clause aliases don't conflict with subquery projections (syntax-SELECTscope2).
    /// </summary>
    private void ValidateSelectSubqueryScope(SelectClause selectClause, ref GraphPattern pattern)
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
                var hash = ComputeSpanHash(_source.Slice(agg.AliasStart, agg.AliasLength));
                if (selectAliasCount < 32) selectAliasHashes[selectAliasCount++] = hash;
            }
        }

        // If no aliases to check, nothing to do
        if (selectAliasCount == 0) return;

        // Check each subquery's projected variables for conflicts
        for (int i = 0; i < pattern.SubQueryCount; i++)
        {
            var subquery = pattern.GetSubQuery(i);
            for (int j = 0; j < subquery.ProjectedVarCount; j++)
            {
                var (start, len) = subquery.GetProjectedVariable(j);
                if (len > 0)
                {
                    var hash = ComputeSpanHash(_source.Slice(start, len));
                    for (int k = 0; k < selectAliasCount; k++)
                    {
                        if (selectAliasHashes[k] == hash)
                        {
                            var varName = _source.Slice(start, len).ToString();
                            throw new SparqlParseException($"Variable {varName} in SELECT conflicts with same variable in subquery projection");
                        }
                    }
                }
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
    /// Validates CONSTRUCT WHERE shorthand restrictions.
    /// Only basic triple patterns are allowed (no FILTER, GRAPH, OPTIONAL, BIND, etc.)
    /// </summary>
    private void ValidateConstructWherePattern(GraphPattern pattern)
    {
        // constructwhere05: FILTER is not allowed
        if (pattern.FilterCount > 0)
        {
            throw new SparqlParseException("CONSTRUCT WHERE shorthand does not allow FILTER");
        }

        // constructwhere06: GRAPH is not allowed
        if (pattern.HasGraph)
        {
            throw new SparqlParseException("CONSTRUCT WHERE shorthand does not allow GRAPH");
        }

        // Other restrictions for shorthand form
        if (pattern.HasOptionalPatterns)
        {
            throw new SparqlParseException("CONSTRUCT WHERE shorthand does not allow OPTIONAL");
        }

        if (pattern.HasBinds)
        {
            throw new SparqlParseException("CONSTRUCT WHERE shorthand does not allow BIND");
        }

        if (pattern.HasMinus)
        {
            throw new SparqlParseException("CONSTRUCT WHERE shorthand does not allow MINUS");
        }

        if (pattern.HasUnion)
        {
            throw new SparqlParseException("CONSTRUCT WHERE shorthand does not allow UNION");
        }

        if (pattern.HasService)
        {
            throw new SparqlParseException("CONSTRUCT WHERE shorthand does not allow SERVICE");
        }

        if (pattern.HasSubQueries)
        {
            throw new SparqlParseException("CONSTRUCT WHERE shorthand does not allow subqueries");
        }

        if (pattern.HasValues)
        {
            throw new SparqlParseException("CONSTRUCT WHERE shorthand does not allow VALUES");
        }

        if (pattern.HasExists)
        {
            throw new SparqlParseException("CONSTRUCT WHERE shorthand does not allow EXISTS");
        }
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
                        if (varName.SequenceEqual(gbName))
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

    /// <summary>
    /// Checks if a variable is already in scope in the given GraphPattern.
    /// Used for BIND variable scoping validation (syntax-BINDscope6, syntax-BINDscope7).
    /// </summary>
    private bool IsVariableInScope(ref GraphPattern pattern, int varStart, int varLen)
    {
        var targetVar = _source.Slice(varStart, varLen);

        // Check all triple patterns for variable bindings
        for (int i = 0; i < pattern.PatternCount; i++)
        {
            var tp = pattern.GetPattern(i);

            // Check subject
            if (tp.Subject.Type == TermType.Variable &&
                _source.Slice(tp.Subject.Start, tp.Subject.Length).SequenceEqual(targetVar))
                return true;

            // Check predicate
            if (tp.Predicate.Type == TermType.Variable &&
                _source.Slice(tp.Predicate.Start, tp.Predicate.Length).SequenceEqual(targetVar))
                return true;

            // Check object
            if (tp.Object.Type == TermType.Variable &&
                _source.Slice(tp.Object.Start, tp.Object.Length).SequenceEqual(targetVar))
                return true;
        }

        // Check previous BIND expressions
        for (int i = 0; i < pattern.BindCount; i++)
        {
            var bind = pattern.GetBind(i);
            if (_source.Slice(bind.VarStart, bind.VarLength).SequenceEqual(targetVar))
                return true;
        }

        // Check GRAPH clause patterns
        for (int i = 0; i < pattern.GraphClauseCount; i++)
        {
            var graphClause = pattern.GetGraphClause(i);
            for (int j = 0; j < graphClause.PatternCount; j++)
            {
                var tp = graphClause.GetPattern(j);
                if (tp.Subject.Type == TermType.Variable &&
                    _source.Slice(tp.Subject.Start, tp.Subject.Length).SequenceEqual(targetVar))
                    return true;
                if (tp.Predicate.Type == TermType.Variable &&
                    _source.Slice(tp.Predicate.Start, tp.Predicate.Length).SequenceEqual(targetVar))
                    return true;
                if (tp.Object.Type == TermType.Variable &&
                    _source.Slice(tp.Object.Start, tp.Object.Length).SequenceEqual(targetVar))
                    return true;
            }
        }

        return false;
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
            ValidateConstructWherePattern(whereClause.Pattern);

            // Copy WHERE patterns to template
            template = new ConstructTemplate();
            for (int i = 0; i < whereClause.Pattern.PatternCount; i++)
            {
                template.AddPattern(whereClause.Pattern.GetPattern(i));
            }
        }
        else
        {
            // Standard CONSTRUCT { template } WHERE { pattern }
            template = ParseConstructTemplate();
            datasets = ParseDatasetClauses();
            whereClause = ParseWhereClause();
        }

        var modifier = ParseSolutionModifier();

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

    private ConstructTemplate ParseConstructTemplate()
    {
        var template = new ConstructTemplate();

        // Expect '{'
        if (Peek() != '{')
            throw new SparqlParseException("Expected '{' in CONSTRUCT template");

        Advance();
        SkipWhitespace();

        // Parse triple patterns until '}'
        while (!IsAtEnd() && Peek() != '}')
        {
            SkipWhitespace();
            if (Peek() == '}')
                break;

            // Parse subject
            var subject = ParseTerm();
            if (subject.Type == TermType.Variable && subject.Length == 0)
                break;

            SkipWhitespace();

            // Parse predicate
            var predicate = ParseTerm();

            SkipWhitespace();

            // Parse object
            var obj = ParseTerm();

            template.AddPattern(new TriplePattern
            {
                Subject = subject,
                Predicate = predicate,
                Object = obj
            });

            SkipWhitespace();

            // Handle semicolon-separated predicate-object lists (same subject)
            // Grammar: PropertyListNotEmpty ::= Verb ObjectList ( ';' ( Verb ObjectList )? )*
            while (Peek() == ';')
            {
                Advance(); // Skip ';'
                SkipWhitespace();

                // Check for empty predicate-object after semicolon (valid: "?s :p ?o ;")
                if (IsAtEnd() || Peek() == '}' || Peek() == '.')
                    break;

                // Parse next predicate-object pair with same subject
                predicate = ParseTerm();
                SkipWhitespace();
                obj = ParseTerm();

                template.AddPattern(new TriplePattern
                {
                    Subject = subject,
                    Predicate = predicate,
                    Object = obj
                });

                SkipWhitespace();
            }

            // Optional dot separator
            if (Peek() == '.')
            {
                Advance();
                SkipWhitespace();
            }
        }

        if (Peek() != '}')
            throw new SparqlParseException("Expected '}' in CONSTRUCT template");

        Advance();
        return template;
    }

    private Query ParseDescribeQuery(Prologue prologue)
    {
        ConsumeKeyword("DESCRIBE");
        SkipWhitespace();

        // Parse resource list or *
        var describeAll = Peek() == '*';
        if (describeAll)
        {
            Advance();
        }

        var datasets = ParseDatasetClauses();
        var whereClause = new WhereClause();

        // Optional WHERE clause for DESCRIBE
        SkipWhitespace();
        var span = PeekSpan(5);
        if (span.Length >= 5 && span[..5].Equals("WHERE", StringComparison.OrdinalIgnoreCase))
        {
            whereClause = ParseWhereClause();
        }

        var modifier = ParseSolutionModifier();

        return new Query
        {
            Type = QueryType.Describe,
            Prologue = prologue,
            DescribeAll = describeAll,
            Datasets = datasets,
            WhereClause = whereClause,
            SolutionModifier = modifier
        };
    }

    private Query ParseAskQuery(Prologue prologue)
    {
        ConsumeKeyword("ASK");
        SkipWhitespace();

        var datasets = ParseDatasetClauses();
        var whereClause = ParseWhereClause();

        return new Query
        {
            Type = QueryType.Ask,
            Prologue = prologue,
            Datasets = datasets,
            WhereClause = whereClause
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
                        var aliasHash = ComputeSpanHash(aliasSpan);
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
                            var aliasHash = ComputeSpanHash(aliasSpan);
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
                else if (Peek() == '?')
                {
                    // Store variable position for validation
                    var varStart = _position;
                    Advance(); // Skip '?'
                    while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                        Advance();

                    // Check for duplicate variable name
                    var varSpan = _source.Slice(varStart, _position - varStart);
                    var varHash = ComputeSpanHash(varSpan);
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
    /// Compute a hash for a span (for duplicate detection).
    /// </summary>
    private static int ComputeSpanHash(ReadOnlySpan<char> span)
    {
        // FNV-1a hash
        uint hash = 2166136261;
        foreach (var ch in span)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return (int)hash;
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

            // Parse through the expression to find AS keyword and alias
            int parenDepth = 0;
            bool foundAs = false;

            while (!IsAtEnd() && !(parenDepth == 0 && Peek() == ')'))
            {
                var ch = Peek();
                if (ch == '(')
                {
                    parenDepth++;
                    Advance();
                }
                else if (ch == ')')
                {
                    if (parenDepth == 0) break;
                    parenDepth--;
                    Advance();
                }
                else
                {
                    // Check for AS keyword at depth 0
                    if (parenDepth == 0)
                    {
                        var checkSpan = PeekSpan(3);
                        if (checkSpan.Length >= 2 && checkSpan[..2].Equals("AS", StringComparison.OrdinalIgnoreCase) &&
                            (checkSpan.Length < 3 || !char.IsLetterOrDigit(checkSpan[2])))
                        {
                            // Save expression end position (before whitespace and AS)
                            exprEnd = _position;
                            while (exprEnd > exprStart && char.IsWhiteSpace(_source[exprEnd - 1]))
                                exprEnd--;

                            ConsumeKeyword("AS");
                            SkipWhitespace();

                            // Parse the alias variable
                            if (Peek() == '?')
                            {
                                agg.AliasStart = _position;
                                Advance();
                                while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                                    Advance();
                                agg.AliasLength = _position - agg.AliasStart;
                                foundAs = true;
                            }
                            break;
                        }
                    }
                    Advance();
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
        else if (Peek() == '?')
        {
            Advance();
            while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                Advance();
            agg.VariableLength = _position - agg.VariableStart;
        }
        else
        {
            // Complex expression argument: IF(...), xsd:double(?x), etc.
            // Skip through the expression tracking parenthesis depth
            int depth = 0;
            while (!IsAtEnd())
            {
                var ch = Peek();

                // Check for separator clause (GROUP_CONCAT only)
                if (depth == 0 && ch == ';')
                    break;

                // Check for closing ) of aggregate function
                if (depth == 0 && ch == ')')
                    break;

                if (ch == '(')
                {
                    depth++;
                    Advance();
                }
                else if (ch == ')')
                {
                    depth--;
                    Advance();
                }
                else
                {
                    Advance();
                }
            }
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

            if (Peek() == '?')
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

        var aggregateFunctions = new[] { ("COUNT", 5), ("SUM", 3), ("AVG", 3), ("MIN", 3), ("MAX", 3), ("SAMPLE", 6), ("GROUP_CONCAT", 12) };

        int searchStart = 0;
        while (searchStart < expr.Length)
        {
            // Find the next aggregate function
            int bestMatch = -1;
            int bestMatchLen = 0;
            AggregateFunction bestFunc = AggregateFunction.None;

            foreach (var (funcName, funcLen) in aggregateFunctions)
            {
                if (searchStart + funcLen > expr.Length) continue;

                var slice = expr.Slice(searchStart);
                int idx = slice.ToString().IndexOf(funcName, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;

                int absIdx = searchStart + idx;
                // Verify it's followed by '(' and not part of a longer identifier
                if (absIdx + funcLen < expr.Length)
                {
                    // Skip whitespace between function name and '('
                    int parenIdx = absIdx + funcLen;
                    while (parenIdx < expr.Length && char.IsWhiteSpace(expr[parenIdx]))
                        parenIdx++;

                    if (parenIdx < expr.Length && expr[parenIdx] == '(')
                    {
                        // Check it's not part of a longer identifier (letter before)
                        if (absIdx > 0 && char.IsLetterOrDigit(expr[absIdx - 1]))
                            continue;

                        if (bestMatch < 0 || absIdx < bestMatch)
                        {
                            bestMatch = absIdx;
                            bestMatchLen = funcLen;
                            bestFunc = funcName switch
                            {
                                "COUNT" => AggregateFunction.Count,
                                "SUM" => AggregateFunction.Sum,
                                "AVG" => AggregateFunction.Avg,
                                "MIN" => AggregateFunction.Min,
                                "MAX" => AggregateFunction.Max,
                                "SAMPLE" => AggregateFunction.Sample,
                                "GROUP_CONCAT" => AggregateFunction.GroupConcat,
                                _ => AggregateFunction.None
                            };
                        }
                    }
                }
            }

            if (bestMatch < 0)
            {
                break; // No more aggregates found - this is expected
            }

            // Found an aggregate at bestMatch - parse its argument
            int funcStart = bestMatch;
            int parenStart = funcStart + bestMatchLen;
            while (parenStart < expr.Length && char.IsWhiteSpace(expr[parenStart]))
                parenStart++;

            if (parenStart >= expr.Length || expr[parenStart] != '(')
            {
                searchStart = bestMatch + 1;
                continue;
            }

            // Find matching closing paren
            int depth = 1;
            int parenEnd = parenStart + 1;
            while (parenEnd < expr.Length && depth > 0)
            {
                if (expr[parenEnd] == '(') depth++;
                else if (expr[parenEnd] == ')') depth--;
                parenEnd++;
            }

            if (depth != 0)
            {
                searchStart = bestMatch + 1;
                continue;
            }

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
            {
                searchStart = parenEnd;
                continue;
            }

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

            searchStart = parenEnd;
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
        var pattern = ParseGroupGraphPattern();

        return new WhereClause { Pattern = pattern };
    }

    /// <summary>
    /// [53] GroupGraphPattern ::= '{' ( SubSelect | GroupGraphPatternSub ) '}'
    /// </summary>
    private GraphPattern ParseGroupGraphPattern()
    {
        var pattern = new GraphPattern();

        SkipWhitespace();
        if (Peek() != '{')
            return pattern;

        Advance(); // Skip '{'
        SkipWhitespace();

        // Check for SubSelect: starts with "SELECT"
        var span = PeekSpan(6);
        if (span.Length >= 6 && span[..6].Equals("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            var subSelect = ParseSubSelect();
            pattern.AddSubQuery(subSelect);
        }
        else
        {
            ParseGroupGraphPatternSub(ref pattern);
        }

        SkipWhitespace();
        if (Peek() == '}')
            Advance(); // Skip '}'

        return pattern;
    }

    /// <summary>
    /// [8] SubSelect ::= SelectClause WhereClause SolutionModifier ValuesClause
    /// Parses a nested SELECT query inside { }.
    /// </summary>
    private SubSelect ParseSubSelect()
    {
        IncrementDepth("Subquery");
        try
        {
            return ParseSubSelectCore();
        }
        finally
        {
            DecrementDepth();
        }
    }

    private SubSelect ParseSubSelectCore()
    {
        var subSelect = new SubSelect();

        // Parse SELECT keyword
        ConsumeKeyword("SELECT");
        SkipWhitespace();

        // Check for DISTINCT
        var span = PeekSpan(8);
        if (span.Length >= 8 && span[..8].Equals("DISTINCT", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("DISTINCT");
            SkipWhitespace();
            subSelect.Distinct = true;
        }

        // Parse projected variables or *
        if (Peek() == '*')
        {
            Advance();
            subSelect.SelectAll = true;
        }
        else
        {
            // Parse projection list: variables ?x or expressions (expr AS ?var)
            while (Peek() == '?' || Peek() == '(')
            {
                if (Peek() == '?')
                {
                    var varStart = _position;
                    Advance(); // Skip '?'
                    while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                        Advance();
                    var varLen = _position - varStart;
                    subSelect.AddProjectedVariable(varStart, varLen);
                }
                else if (Peek() == '(')
                {
                    // Parse (expr AS ?var) or aggregate expression (COUNT(?x) AS ?c)
                    // Reuse ParseAggregateExpression which handles both cases
                    var agg = ParseAggregateExpression();

                    if (agg.Function != AggregateFunction.None)
                    {
                        // It's an aggregate - store both the aggregate and the projected variable
                        subSelect.AddAggregate(agg);
                        if (agg.AliasLength > 0)
                            subSelect.AddProjectedVariable(agg.AliasStart, agg.AliasLength);
                    }
                    else if (agg.AliasLength > 0)
                    {
                        // It's a non-aggregate expression with alias - just add as projected variable
                        subSelect.AddProjectedVariable(agg.AliasStart, agg.AliasLength);
                    }
                }
                SkipWhitespace();
            }
        }

        SkipWhitespace();

        // Parse WHERE keyword (optional per SPARQL grammar)
        span = PeekSpan(5);
        if (span.Length >= 5 && span[..5].Equals("WHERE", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("WHERE");
            SkipWhitespace();
        }

        // Parse the nested { } block
        if (Peek() == '{')
        {
            Advance(); // Skip '{'
            ParseSubSelectPatterns(ref subSelect);
            SkipWhitespace();
            if (Peek() == '}')
                Advance(); // Skip '}'
        }

        SkipWhitespace();

        // Parse solution modifiers (ORDER BY, LIMIT, OFFSET)
        ParseSubSelectSolutionModifiers(ref subSelect);

        return subSelect;
    }

    /// <summary>
    /// Parse patterns inside a subquery's WHERE clause.
    /// </summary>
    private void ParseSubSelectPatterns(ref SubSelect subSelect)
    {
        SkipWhitespace();

        // Check for UNION structure: { pattern } UNION { pattern }
        if (Peek() == '{')
        {
            ParseSubSelectGroupOrUnion(ref subSelect);
            return;
        }

        // Otherwise parse regular triple patterns
        while (!IsAtEnd() && Peek() != '}')
        {
            SkipWhitespace();

            // Check for nested group/subquery: { ... } or { SELECT ... }
            if (Peek() == '{')
            {
                ParseSubSelectNestedGroup(ref subSelect);
                SkipWhitespace();
                continue;
            }

            var span = PeekSpan(6);

            // Check for FILTER
            if (span.Length >= 6 && span[..6].Equals("FILTER", StringComparison.OrdinalIgnoreCase))
            {
                ParseSubSelectFilter(ref subSelect);
                continue;
            }

            // Check for GRAPH
            if (span.Length >= 5 && span[..5].Equals("GRAPH", StringComparison.OrdinalIgnoreCase))
            {
                ParseSubSelectGraph(ref subSelect);
                continue;
            }

            // Check for VALUES (inline data block inside WHERE)
            if (span.Length >= 6 && span[..6].Equals("VALUES", StringComparison.OrdinalIgnoreCase))
            {
                subSelect.Values = ParseSubSelectValues();
                continue;
            }

            // Parse triple pattern
            if (!TryParseSubSelectTriplePattern(ref subSelect))
                break;

            SkipWhitespace();

            // Optional dot after triple pattern
            if (Peek() == '.')
                Advance();
        }
    }

    /// <summary>
    /// Parse { pattern } UNION { pattern } UNION ... structure inside a subquery.
    /// </summary>
    private void ParseSubSelectGroupOrUnion(ref SubSelect subSelect)
    {
        // Parse first nested group
        ParseSubSelectNestedGroup(ref subSelect);

        SkipWhitespace();

        // Check for UNION keyword
        var span = PeekSpan(5);
        if (span.Length >= 5 && span[..5].Equals("UNION", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("UNION");
            SkipWhitespace();

            // Mark where UNION patterns start
            subSelect.HasUnion = true;
            subSelect.UnionStartIndex = subSelect.PatternCount;

            // Parse remaining UNION branches
            while (true)
            {
                ParseSubSelectNestedGroup(ref subSelect);
                SkipWhitespace();

                // Check for another UNION
                span = PeekSpan(5);
                if (span.Length >= 5 && span[..5].Equals("UNION", StringComparison.OrdinalIgnoreCase))
                {
                    ConsumeKeyword("UNION");
                    SkipWhitespace();
                    continue;
                }

                break;
            }
        }
    }

    /// <summary>
    /// Parse a nested { } group inside a subquery.
    /// Handles: triple patterns, FILTER, GRAPH, nested SELECT subqueries, and nested groups.
    /// </summary>
    private void ParseSubSelectNestedGroup(ref SubSelect subSelect)
    {
        SkipWhitespace();
        if (Peek() != '{')
            throw new SparqlParseException("Expected '{' for nested group pattern");

        Advance(); // Skip '{'
        SkipWhitespace();

        // Check for nested SELECT subquery: { SELECT ... }
        var selectSpan = PeekSpan(6);
        if (selectSpan.Length >= 6 && selectSpan[..6].Equals("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            // Parse the nested subquery and add to this subselect
            var nestedSubSelect = ParseSubSelect();
            subSelect.AddSubQuery(nestedSubSelect);

            SkipWhitespace();
            if (Peek() == '}')
                Advance(); // Skip closing '}'
            return;
        }

        // Parse patterns inside the nested group
        while (!IsAtEnd() && Peek() != '}')
        {
            SkipWhitespace();

            // Check for another nested group (which may contain a subquery)
            if (Peek() == '{')
            {
                ParseSubSelectNestedGroup(ref subSelect);
                SkipWhitespace();
                continue;
            }

            // Check for FILTER
            var span = PeekSpan(6);
            if (span.Length >= 6 && span[..6].Equals("FILTER", StringComparison.OrdinalIgnoreCase))
            {
                ParseSubSelectFilter(ref subSelect);
                continue;
            }

            // Check for GRAPH
            if (span.Length >= 5 && span[..5].Equals("GRAPH", StringComparison.OrdinalIgnoreCase))
            {
                ParseSubSelectGraph(ref subSelect);
                continue;
            }

            // Parse triple pattern
            if (!TryParseSubSelectTriplePattern(ref subSelect))
                break;

            SkipWhitespace();

            // Optional dot after triple pattern
            if (Peek() == '.')
                Advance();
        }

        SkipWhitespace();
        if (Peek() == '}')
            Advance(); // Skip '}'
    }

    /// <summary>
    /// Parse GRAPH clause inside a subquery.
    /// </summary>
    private void ParseSubSelectGraph(ref SubSelect subSelect)
    {
        ConsumeKeyword("GRAPH");
        SkipWhitespace();

        // Parse graph IRI or variable
        var graphTerm = ParseTerm();
        SkipWhitespace();

        // Store the graph context so subquery executor knows to filter by graph
        subSelect.GraphContext = graphTerm;

        // Expect { }
        if (Peek() != '{')
            throw new SparqlParseException("Expected '{' after GRAPH");

        Advance(); // Skip '{'
        SkipWhitespace();

        // Parse patterns inside GRAPH block
        while (!IsAtEnd() && Peek() != '}')
        {
            SkipWhitespace();

            // Check for FILTER
            var span = PeekSpan(6);
            if (span.Length >= 6 && span[..6].Equals("FILTER", StringComparison.OrdinalIgnoreCase))
            {
                ParseSubSelectFilter(ref subSelect);
                continue;
            }

            // Parse triple pattern
            if (!TryParseSubSelectTriplePattern(ref subSelect))
                break;

            SkipWhitespace();

            // Optional dot after triple pattern
            if (Peek() == '.')
                Advance();
        }

        SkipWhitespace();
        if (Peek() == '}')
            Advance(); // Skip '}'
    }

    /// <summary>
    /// Parse a FILTER expression inside a subquery.
    /// [68] Constraint ::= BrackettedExpression | BuiltInCall | FunctionCall
    /// </summary>
    private void ParseSubSelectFilter(ref SubSelect subSelect)
    {
        ConsumeKeyword("FILTER");
        SkipWhitespace();

        int exprStart;
        int exprEnd;

        if (Peek() == '(')
        {
            // BrackettedExpression: FILTER(expr)
            exprStart = _position;
            Advance(); // Skip '('
            int depth = 1;
            while (!IsAtEnd() && depth > 0)
            {
                var c = Peek();
                if (c == '(') depth++;
                else if (c == ')') depth--;
                Advance();
            }
            exprEnd = _position;
        }
        else if (char.IsLetter(Peek()))
        {
            // BuiltInCall or FunctionCall: FILTER CONTAINS(...) or FILTER fn:func(...)
            exprStart = _position;

            // Skip the function name (may include namespace prefix like fn:)
            while (!IsAtEnd() && (char.IsLetterOrDigit(Peek()) || Peek() == '_' || Peek() == ':'))
                Advance();

            SkipWhitespace();

            // Parse function arguments if present
            if (Peek() == '(')
            {
                int depth = 1;
                Advance(); // Skip '('
                while (!IsAtEnd() && depth > 0)
                {
                    var c = Peek();
                    if (c == '(') depth++;
                    else if (c == ')') depth--;
                    Advance();
                }
            }
            exprEnd = _position;
        }
        else
        {
            // Fallback: consume until delimiter
            exprStart = _position;
            while (!IsAtEnd() && Peek() != '.' && Peek() != '}')
                Advance();
            exprEnd = _position;
        }

        subSelect.AddFilter(new FilterExpr { Start = exprStart, Length = exprEnd - exprStart });
    }

    /// <summary>
    /// Try to parse a triple pattern inside a subquery.
    /// </summary>
    private bool TryParseSubSelectTriplePattern(ref SubSelect subSelect)
    {
        SkipWhitespace();

        // Check if we're at end of pattern
        if (IsAtEnd() || Peek() == '}')
            return false;

        // Parse subject
        var subject = ParseTerm();
        if (subject.Type == TermType.Variable && subject.Length == 0)
            return false;

        // Check if subject is a blank node property list
        Term actualSubject = subject;
        if (IsBlankNodePropertyList(subject))
        {
            actualSubject = ExpandBlankNodePropertyListForSubSelect(ref subSelect, subject);
        }

        SkipWhitespace();

        // Parse predicate
        var predicate = ParseTerm();

        SkipWhitespace();

        // Parse object
        var obj = ParseTerm();

        // Check if object is a blank node property list
        Term actualObject = obj;
        if (IsBlankNodePropertyList(obj))
        {
            actualObject = ExpandBlankNodePropertyListForSubSelect(ref subSelect, obj);
        }

        subSelect.AddPattern(new TriplePattern { Subject = actualSubject, Predicate = predicate, Object = actualObject });

        // Handle semicolon-separated predicate-object lists (same subject)
        SkipWhitespace();
        while (Peek() == ';')
        {
            Advance(); // Skip ';'
            SkipWhitespace();

            // Check for empty predicate-object after semicolon
            if (IsAtEnd() || Peek() == '}' || Peek() == '.')
                break;

            // Check for FILTER keyword
            var span = PeekSpan(6);
            if (span.Length >= 6 && span[..6].Equals("FILTER", StringComparison.OrdinalIgnoreCase))
                break;

            // Parse next predicate-object pair with same subject
            var nextPredicate = ParseTerm();
            SkipWhitespace();
            var nextObj = ParseTerm();

            // Check if object is a blank node property list
            Term actualNextObj = nextObj;
            if (IsBlankNodePropertyList(nextObj))
            {
                actualNextObj = ExpandBlankNodePropertyListForSubSelect(ref subSelect, nextObj);
            }

            subSelect.AddPattern(new TriplePattern { Subject = actualSubject, Predicate = nextPredicate, Object = actualNextObj });

            SkipWhitespace();
        }

        return true;
    }

    /// <summary>
    /// Parse solution modifiers for a subquery (ORDER BY, LIMIT, OFFSET, VALUES).
    /// [8] SubSelect ::= SelectClause WhereClause SolutionModifier ValuesClause
    /// </summary>
    private void ParseSubSelectSolutionModifiers(ref SubSelect subSelect)
    {
        // Parse GROUP BY (must come before HAVING and ORDER BY)
        var span = PeekSpan(8);
        if (span.Length >= 5 && span[..5].Equals("GROUP", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("GROUP");
            SkipWhitespace();
            ConsumeKeyword("BY");
            SkipWhitespace();
            subSelect.GroupBy = ParseGroupByClause();
            SkipWhitespace();
        }

        // Parse HAVING (must come after GROUP BY, before ORDER BY)
        span = PeekSpan(6);
        if (span.Length >= 6 && span[..6].Equals("HAVING", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("HAVING");
            SkipWhitespace();

            // HAVING expression is in parentheses
            if (Peek() == '(')
            {
                Advance(); // Skip '('
                var start = _position;

                // Find matching closing paren
                int depth = 1;
                while (!IsAtEnd() && depth > 0)
                {
                    var ch = Peek();
                    if (ch == '(') depth++;
                    else if (ch == ')') depth--;
                    if (depth > 0) Advance();
                }

                var length = _position - start;
                subSelect.Having = new HavingClause { ExpressionStart = start, ExpressionLength = length };

                if (Peek() == ')')
                    Advance(); // Skip ')'
            }
            SkipWhitespace();
        }

        // Parse ORDER BY
        span = PeekSpan(8);
        if (span.Length >= 5 && span[..5].Equals("ORDER", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("ORDER");
            SkipWhitespace();
            ConsumeKeyword("BY");
            SkipWhitespace();
            subSelect.OrderBy = ParseOrderByClause();
            SkipWhitespace();
        }

        // Parse LIMIT
        span = PeekSpan(5);
        if (span.Length >= 5 && span[..5].Equals("LIMIT", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("LIMIT");
            SkipWhitespace();
            subSelect.Limit = ParseInteger();
            SkipWhitespace();
        }

        // Parse OFFSET
        span = PeekSpan(6);
        if (span.Length >= 6 && span[..6].Equals("OFFSET", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("OFFSET");
            SkipWhitespace();
            subSelect.Offset = ParseInteger();
            SkipWhitespace();
        }

        // Parse VALUES clause (inline data at end of subquery)
        span = PeekSpan(6);
        if (span.Length >= 6 && span[..6].Equals("VALUES", StringComparison.OrdinalIgnoreCase))
        {
            subSelect.Values = ParseSubSelectValues();
            SkipWhitespace();
        }
    }

    /// <summary>
    /// Parse VALUES clause for a subquery: VALUES ?var { value1 value2 ... } or VALUES (?var1 ?var2) { (val1 val2) ... }
    /// UNDEF values are stored with length = -1.
    /// </summary>
    private ValuesClause ParseSubSelectValues()
    {
        ConsumeKeyword("VALUES");
        SkipWhitespace();

        var values = new ValuesClause();

        // Check for multi-variable form: (?var1 ?var2 ...)
        if (Peek() == '(')
        {
            Advance(); // Skip '('
            SkipWhitespace();

            // Parse all variables
            while (!IsAtEnd() && Peek() == '?')
            {
                int varStart = _position;
                Advance(); // Skip '?'
                while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                    Advance();
                values.AddVariable(varStart, _position - varStart);
                SkipWhitespace();
            }

            if (Peek() == ')')
                Advance(); // Skip ')'
            SkipWhitespace();
        }
        // Single variable form: ?var
        else if (Peek() == '?')
        {
            int varStart = _position;
            Advance(); // Skip '?'
            while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                Advance();
            values.AddVariable(varStart, _position - varStart);
            SkipWhitespace();
        }
        else
        {
            return values; // No valid VALUES
        }

        int varCount = values.VariableCount;

        // Expect '{'
        if (Peek() != '{')
            return values;

        Advance(); // Skip '{'
        SkipWhitespace();

        // Parse value rows
        while (!IsAtEnd() && Peek() != '}')
        {
            SkipWhitespace();

            // Check for parenthesized row: (val1 val2 ...)
            if (Peek() == '(')
            {
                Advance(); // Skip '('
                SkipWhitespace();

                int rowValueCount = 0;
                while (!IsAtEnd() && Peek() != ')')
                {
                    // Check for UNDEF first
                    var span = PeekSpan(5);
                    if (span.Length >= 5 && span[..5].Equals("UNDEF", StringComparison.OrdinalIgnoreCase))
                    {
                        ConsumeKeyword("UNDEF");
                        values.AddValue(0, -1); // Mark as UNDEF with length = -1
                        rowValueCount++;
                    }
                    else
                    {
                        int valueStart = _position;
                        int valueLen = ParseValuesValue();
                        if (valueLen > 0)
                        {
                            values.AddValue(valueStart, valueLen);
                            rowValueCount++;
                        }
                        else
                        {
                            break;
                        }
                    }
                    SkipWhitespace();
                }

                // Validate cardinality
                if (varCount > 0 && rowValueCount != varCount)
                {
                    if (rowValueCount < varCount)
                        throw new SparqlParseException($"VALUES row has {rowValueCount} values but {varCount} variables declared");
                    else
                        throw new SparqlParseException($"VALUES row has {rowValueCount} values but only {varCount} variables declared");
                }

                if (Peek() == ')')
                    Advance(); // Skip ')'
                SkipWhitespace();
            }
            else
            {
                // Single value (for single variable form)
                // Check for UNDEF
                var span = PeekSpan(5);
                if (span.Length >= 5 && span[..5].Equals("UNDEF", StringComparison.OrdinalIgnoreCase))
                {
                    ConsumeKeyword("UNDEF");
                    values.AddValue(0, -1); // Mark as UNDEF with length = -1
                }
                else
                {
                    int valueStart = _position;
                    int valueLen = ParseValuesValue();
                    if (valueLen > 0)
                    {
                        values.AddValue(valueStart, valueLen);
                    }
                }
                SkipWhitespace();
            }
        }

        if (Peek() == '}')
            Advance(); // Skip '}'

        return values;
    }

    /// <summary>
    /// [54] GroupGraphPatternSub ::= TriplesBlock? ( GraphPatternNotTriples '.'? TriplesBlock? )*
    /// </summary>
    private void ParseGroupGraphPatternSub(ref GraphPattern pattern)
    {
        SkipWhitespace();

        // Check for nested group that might be part of UNION: { { ... } UNION { ... } }
        if (Peek() == '{')
        {
            ParseGroupOrUnionGraphPattern(ref pattern);
            SkipWhitespace();
            // Don't return - continue to parse additional nested groups or patterns
        }

        while (!IsAtEnd() && Peek() != '}')
        {
            SkipWhitespace();

            // Check for keywords - peek enough for longest keyword (OPTIONAL = 8)
            var span = PeekSpan(8);

            // Check for FILTER
            if (span.Length >= 6 && span[..6].Equals("FILTER", StringComparison.OrdinalIgnoreCase))
            {
                ParseFilter(ref pattern);
                continue;
            }

            // Check for OPTIONAL
            if (span.Length >= 8 && span[..8].Equals("OPTIONAL", StringComparison.OrdinalIgnoreCase))
            {
                ParseOptional(ref pattern);
                continue;
            }

            // Check for MINUS
            if (span.Length >= 5 && span[..5].Equals("MINUS", StringComparison.OrdinalIgnoreCase))
            {
                ParseMinus(ref pattern);
                continue;
            }

            // Check for BIND
            if (span.Length >= 4 && span[..4].Equals("BIND", StringComparison.OrdinalIgnoreCase))
            {
                ParseBind(ref pattern);
                continue;
            }

            // Check for VALUES
            if (span.Length >= 6 && span[..6].Equals("VALUES", StringComparison.OrdinalIgnoreCase))
            {
                ParseValues(ref pattern);
                continue;
            }

            // Check for SERVICE
            if (span.Length >= 7 && span[..7].Equals("SERVICE", StringComparison.OrdinalIgnoreCase))
            {
                ParseService(ref pattern);
                continue;
            }

            // Check for GRAPH
            if (span.Length >= 5 && span[..5].Equals("GRAPH", StringComparison.OrdinalIgnoreCase))
            {
                ParseGraph(ref pattern);
                continue;
            }

            // syn-bad-08: Check for UNION without braces - must have { } around both sides
            if (span.Length >= 5 && span[..5].Equals("UNION", StringComparison.OrdinalIgnoreCase))
            {
                throw new SparqlParseException("UNION requires graph patterns enclosed in braces: { pattern } UNION { pattern }");
            }

            // syn-bad-07: Check for SELECT in middle of pattern - subqueries must be wrapped in { }
            if (span.Length >= 6 && span[..6].Equals("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                throw new SparqlParseException("SELECT (subquery) must be wrapped in braces: { SELECT ... }");
            }

            // Check for nested group { ... } which might be a subquery or UNION
            // Use ParseGroupOrUnionGraphPattern to handle { ... } UNION { ... } patterns
            if (Peek() == '{')
            {
                ParseGroupOrUnionGraphPattern(ref pattern);
                continue;
            }

            // Try to parse a triple pattern
            if (!TryParseTriplePattern(ref pattern))
                break;

            SkipWhitespace();

            // Optional dot after triple pattern
            if (Peek() == '.')
                Advance();
        }
    }

    /// <summary>
    /// [57] GroupOrUnionGraphPattern ::= GroupGraphPattern ( 'UNION' GroupGraphPattern )*
    /// Parses { pattern } UNION { pattern } structure.
    /// </summary>
    private void ParseGroupOrUnionGraphPattern(ref GraphPattern pattern)
    {
        // Parse first nested group
        ParseNestedGroupGraphPattern(ref pattern);

        SkipWhitespace();

        // Check for UNION
        var span = PeekSpan(5);
        if (span.Length >= 5 && span[..5].Equals("UNION", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("UNION");
            SkipWhitespace();

            // Mark where UNION patterns start
            pattern.StartUnionBranch();

            // Parse second nested group
            ParseNestedGroupGraphPattern(ref pattern);
        }
    }

    /// <summary>
    /// Parse a nested { ... } block and add its patterns to the parent pattern.
    /// Also handles subqueries: { SELECT ... }
    /// </summary>
    private void ParseNestedGroupGraphPattern(ref GraphPattern pattern)
    {
        SkipWhitespace();
        if (Peek() != '{')
            return;

        Advance(); // Skip '{'
        SkipWhitespace();

        // Increment scope depth - patterns inside nested groups have higher scope depth
        _scopeDepth++;

        // Record pattern and bind counts at start of nested group for BIND scope validation
        // BIND inside a nested { } only checks variables from this scope, not outer
        int nestedScopeStart = pattern.PatternCount;
        int bindScopeStart = pattern.BindCount;

        // Check for SubSelect: starts with "SELECT"
        var checkSpan = PeekSpan(6);
        if (checkSpan.Length >= 6 && checkSpan[..6].Equals("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            var subSelect = ParseSubSelect();
            pattern.AddSubQuery(subSelect);
            SkipWhitespace();
            if (Peek() == '}')
                Advance(); // Skip '}'
            _scopeDepth--; // Decrement scope depth before returning
            return;
        }

        // Parse patterns inside the nested group
        while (!IsAtEnd() && Peek() != '}')
        {
            SkipWhitespace();

            // Check for FILTER inside nested group
            var span = PeekSpan(8);
            if (span.Length >= 6 && span[..6].Equals("FILTER", StringComparison.OrdinalIgnoreCase))
            {
                ParseFilter(ref pattern);
                continue;
            }

            // Check for OPTIONAL inside nested group
            if (span.Length >= 8 && span[..8].Equals("OPTIONAL", StringComparison.OrdinalIgnoreCase))
            {
                ParseOptional(ref pattern);
                continue;
            }

            // Check for SERVICE inside nested group (enables { ... } UNION { SERVICE ... })
            if (span.Length >= 7 && span[..7].Equals("SERVICE", StringComparison.OrdinalIgnoreCase))
            {
                ParseService(ref pattern);
                continue;
            }

            // Check for BIND inside nested group
            // Pass scopeStartIndex to only check variables from this nested scope
            if (span.Length >= 4 && span[..4].Equals("BIND", StringComparison.OrdinalIgnoreCase))
            {
                ParseBind(ref pattern, nestedScopeStart, bindScopeStart);
                continue;
            }

            // Check for nested group { ... } which might be a subquery or UNION
            // This enables patterns like { { :s :p ?Y } UNION { :s :p ?Z } }
            if (Peek() == '{')
            {
                ParseGroupOrUnionGraphPattern(ref pattern);
                continue;
            }

            // Parse triple pattern
            if (!TryParseTriplePattern(ref pattern))
                break;

            SkipWhitespace();
            if (Peek() == '.')
                Advance();
        }

        SkipWhitespace();
        if (Peek() == '}')
            Advance(); // Skip '}'

        // Decrement scope depth - leaving nested group
        _scopeDepth--;
    }

    /// <summary>
    /// Parse FILTER constraint
    /// </summary>
    private void ParseFilter(ref GraphPattern pattern)
    {
        ConsumeKeyword("FILTER");
        SkipWhitespace();

        // Check for EXISTS or NOT EXISTS
        var span = PeekSpan(10);
        bool negated = false;

        if (span.Length >= 3 && span[..3].Equals("NOT", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("NOT");
            SkipWhitespace();
            span = PeekSpan(6);
            negated = true;
        }

        if (span.Length >= 6 && span[..6].Equals("EXISTS", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("EXISTS");
            SkipWhitespace();
            ParseExistsFilter(ref pattern, negated);
            return;
        }

        // If we consumed NOT but no EXISTS, it's an error - but for now just continue
        // Regular filter expression
        var start = _position;

        // FILTER can be: FILTER(expr) or FILTER BuiltInCall
        // [68] Constraint ::= BrackettedExpression | BuiltInCall | FunctionCall
        if (Peek() == '(')
        {
            // Check if this is FILTER( EXISTS ... ) or FILTER( NOT EXISTS ... )
            // We need to look past the '(' to see if EXISTS/NOT follows
            var savedPos = _position;
            Advance(); // Skip '('
            SkipWhitespace();

            var innerSpan = PeekSpan(10);

            if (innerSpan.Length >= 3 && innerSpan[..3].Equals("NOT", StringComparison.OrdinalIgnoreCase))
            {
                // Check if NOT is followed by EXISTS
                var checkPos = _position;
                ConsumeKeyword("NOT");
                SkipWhitespace();
                var afterNot = PeekSpan(6);
                if (afterNot.Length >= 6 && afterNot[..6].Equals("EXISTS", StringComparison.OrdinalIgnoreCase))
                {
                    ConsumeKeyword("EXISTS");
                    SkipWhitespace();
                    ParseExistsFilter(ref pattern, true);
                    // Skip the closing ')'
                    SkipWhitespace();
                    if (Peek() == ')') Advance();
                    return;
                }
                // Not followed by EXISTS, restore position and continue with regular filter
                _position = savedPos;
            }
            else if (innerSpan.Length >= 6 && innerSpan[..6].Equals("EXISTS", StringComparison.OrdinalIgnoreCase))
            {
                ConsumeKeyword("EXISTS");
                SkipWhitespace();
                ParseExistsFilter(ref pattern, false);
                // Skip the closing ')'
                SkipWhitespace();
                if (Peek() == ')') Advance();
                return;
            }
            else
            {
                // Not EXISTS, restore position
                _position = savedPos;
            }
        }

        if (Peek() == '(')
        {
            // BrackettedExpression: FILTER(expr)
            Advance(); // Skip '('
            start = _position;
            var depth = 1;
            while (!IsAtEnd() && depth > 0)
            {
                var ch = Advance();
                if (ch == '(') depth++;
                else if (ch == ')') depth--;
            }
            var length = _position - start - 1; // Exclude closing ')'
            pattern.AddFilter(new FilterExpr { Start = start, Length = length, ScopeDepth = _scopeDepth });
        }
        else if (char.IsLetter(Peek()))
        {
            // BuiltInCall or FunctionCall: FILTER CONTAINS(...) or FILTER fn:func(...)
            // Capture the function call including its arguments
            start = _position;

            // Skip the function name (may include namespace prefix like fn:)
            while (!IsAtEnd() && (char.IsLetterOrDigit(Peek()) || Peek() == '_' || Peek() == ':'))
                Advance();

            SkipWhitespace();

            // Expect opening parenthesis for function arguments
            if (Peek() == '(')
            {
                var depth = 1;
                Advance(); // Skip '('
                while (!IsAtEnd() && depth > 0)
                {
                    var ch = Advance();
                    if (ch == '(') depth++;
                    else if (ch == ')') depth--;
                }
            }

            var length = _position - start;
            pattern.AddFilter(new FilterExpr { Start = start, Length = length, ScopeDepth = _scopeDepth });
        }
    }

    /// <summary>
    /// Parse EXISTS or NOT EXISTS filter: [NOT] EXISTS { pattern }
    /// Also handles GRAPH inside EXISTS: EXISTS { GRAPH ?g { pattern } }
    /// </summary>
    private void ParseExistsFilter(ref GraphPattern pattern, bool negated)
    {
        if (Peek() != '{')
            return;

        Advance(); // Skip '{'
        SkipWhitespace();

        var existsFilter = new ExistsFilter { Negated = negated };

        // Parse patterns inside the EXISTS block
        while (!IsAtEnd() && Peek() != '}')
        {
            SkipWhitespace();

            // Check for GRAPH keyword inside EXISTS
            var span = PeekSpan(5);
            if (span.Length >= 5 && span[..5].Equals("GRAPH", StringComparison.OrdinalIgnoreCase))
            {
                ParseExistsGraphPattern(ref existsFilter);
                SkipWhitespace();
                continue;
            }

            // Try to parse a triple pattern
            if (!TryParseExistsTriplePattern(ref existsFilter))
                break;

            SkipWhitespace();

            // Skip optional '.'
            if (Peek() == '.')
                Advance();
        }

        SkipWhitespace();
        if (Peek() == '}')
            Advance(); // Skip '}'

        pattern.AddExistsFilter(existsFilter);
    }

    /// <summary>
    /// Parse GRAPH pattern inside an EXISTS block: GRAPH &lt;iri&gt;|?var { patterns }
    /// </summary>
    private void ParseExistsGraphPattern(ref ExistsFilter filter)
    {
        ConsumeKeyword("GRAPH");
        SkipWhitespace();

        // Parse graph term (IRI or variable)
        var graphTerm = ParseTerm();
        if (graphTerm.Type == TermType.Variable && graphTerm.Length == 0)
            return;

        filter.SetGraphContext(graphTerm);

        SkipWhitespace();

        if (Peek() != '{')
            return;

        Advance(); // Skip '{'
        SkipWhitespace();

        // Parse patterns inside the GRAPH block
        while (!IsAtEnd() && Peek() != '}')
        {
            SkipWhitespace();

            if (IsAtEnd() || Peek() == '}')
                break;

            var subject = ParseTerm();
            if (subject.Type == TermType.Variable && subject.Length == 0)
                break;

            SkipWhitespace();
            var predicate = ParseTerm();
            SkipWhitespace();
            var obj = ParseTerm();

            filter.AddPattern(new TriplePattern
            {
                Subject = subject,
                Predicate = predicate,
                Object = obj
            });

            SkipWhitespace();

            // Skip optional '.'
            if (Peek() == '.')
                Advance();
        }

        SkipWhitespace();
        if (Peek() == '}')
            Advance(); // Skip '}'
    }

    /// <summary>
    /// Parse a triple pattern inside an EXISTS block.
    /// </summary>
    private bool TryParseExistsTriplePattern(ref ExistsFilter filter)
    {
        SkipWhitespace();

        if (IsAtEnd() || Peek() == '}')
            return false;

        var subject = ParseTerm();
        if (subject.Type == TermType.Variable && subject.Length == 0)
            return false;

        SkipWhitespace();
        var predicate = ParseTerm();
        SkipWhitespace();
        var obj = ParseTerm();

        filter.AddPattern(new TriplePattern
        {
            Subject = subject,
            Predicate = predicate,
            Object = obj
        });

        SkipWhitespace();

        // Handle semicolon shorthand: ?s p1 o1 ; p2 o2 ; p3 o3
        // Semicolon means same subject, new predicate-object pair
        while (Peek() == ';')
        {
            Advance(); // Skip ';'
            SkipWhitespace();

            // Check for end of pattern (trailing semicolon before '.')
            if (IsAtEnd() || Peek() == '}' || Peek() == '.')
                break;

            var nextPredicate = ParseTerm();
            if (nextPredicate.Type == TermType.Variable && nextPredicate.Length == 0)
                break;

            SkipWhitespace();
            var nextObj = ParseTerm();

            filter.AddPattern(new TriplePattern
            {
                Subject = subject,
                Predicate = nextPredicate,
                Object = nextObj
            });

            SkipWhitespace();
        }

        return true;
    }

    /// <summary>
    /// Parse OPTIONAL clause: OPTIONAL { GroupGraphPattern }
    /// Patterns inside OPTIONAL are marked as optional in the parent pattern.
    /// </summary>
    private void ParseOptional(ref GraphPattern pattern)
    {
        ConsumeKeyword("OPTIONAL");
        SkipWhitespace();

        if (Peek() != '{')
            return;

        Advance(); // Skip '{'
        SkipWhitespace();

        // Parse patterns inside OPTIONAL and add them as optional
        while (!IsAtEnd() && Peek() != '}')
        {
            SkipWhitespace();

            var span = PeekSpan(7);

            // Check for nested FILTER inside OPTIONAL
            if (span.Length >= 6 && span[..6].Equals("FILTER", StringComparison.OrdinalIgnoreCase))
            {
                ParseFilter(ref pattern);
                continue;
            }

            // Check for SERVICE inside OPTIONAL
            if (span.Length >= 7 && span[..7].Equals("SERVICE", StringComparison.OrdinalIgnoreCase))
            {
                ParseOptionalService(ref pattern);
                continue;
            }

            // Try to parse a triple pattern
            if (!TryParseOptionalTriplePattern(ref pattern))
                break;

            SkipWhitespace();

            // Optional dot after triple pattern
            if (Peek() == '.')
                Advance();
        }

        SkipWhitespace();
        if (Peek() == '}')
            Advance(); // Skip '}'
    }

    /// <summary>
    /// Parse SERVICE inside OPTIONAL: OPTIONAL { SERVICE [SILENT] &lt;uri&gt; { patterns } }
    /// Sets IsOptional = true to preserve outer bindings when no match.
    /// </summary>
    private void ParseOptionalService(ref GraphPattern pattern)
    {
        ConsumeKeyword("SERVICE");
        SkipWhitespace();

        // Check for SILENT modifier
        var silent = false;
        var span = PeekSpan(6);
        if (span.Length >= 6 && span[..6].Equals("SILENT", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("SILENT");
            SkipWhitespace();
            silent = true;
        }

        // Parse endpoint term (IRI or variable)
        var endpointTerm = ParseTerm();
        if (endpointTerm.Type == TermType.Variable && endpointTerm.Length == 0)
            return;

        SkipWhitespace();

        if (Peek() != '{')
            return;

        Advance(); // Skip '{'
        SkipWhitespace();

        var serviceClause = new ServiceClause
        {
            Endpoint = endpointTerm,
            Silent = silent,
            IsOptional = true  // Key difference - preserve outer bindings on no match
        };

        // Parse patterns inside SERVICE block
        while (!IsAtEnd() && Peek() != '}')
        {
            SkipWhitespace();

            // Check for FILTER inside SERVICE
            span = PeekSpan(6);
            if (span.Length >= 6 && span[..6].Equals("FILTER", StringComparison.OrdinalIgnoreCase))
            {
                // Add filters to parent pattern
                ParseFilter(ref pattern);
                continue;
            }

            // Try to parse a triple pattern
            if (IsAtEnd() || Peek() == '}')
                break;

            var subject = ParseTerm();
            if (subject.Type == TermType.Variable && subject.Length == 0)
                break;

            SkipWhitespace();
            var predicate = ParseTerm();

            SkipWhitespace();
            var obj = ParseTerm();

            serviceClause.AddPattern(new TriplePattern
            {
                Subject = subject,
                Predicate = predicate,
                Object = obj
            });

            SkipWhitespace();

            // Optional dot after triple pattern
            if (Peek() == '.')
                Advance();
        }

        SkipWhitespace();
        if (Peek() == '}')
            Advance(); // Skip '}'

        pattern.AddServiceClause(serviceClause);
    }

    /// <summary>
    /// Parse MINUS clause: MINUS { GroupGraphPattern }
    /// Patterns inside MINUS are used to exclude matching solutions.
    /// Supports FILTER inside MINUS for conditional exclusion.
    /// </summary>
    private void ParseMinus(ref GraphPattern pattern)
    {
        ConsumeKeyword("MINUS");
        SkipWhitespace();

        if (Peek() != '{')
            return;

        // Start a new MINUS block for block-aware evaluation
        pattern.StartMinusBlock();

        Advance(); // Skip '{'
        SkipWhitespace();

        // Parse patterns inside MINUS and add them as minus patterns
        while (!IsAtEnd() && Peek() != '}')
        {
            SkipWhitespace();

            // Check for FILTER inside MINUS
            var span = PeekSpan(8);
            if (span.Length >= 6 && span[..6].Equals("FILTER", StringComparison.OrdinalIgnoreCase))
            {
                ConsumeKeyword("FILTER");
                SkipWhitespace();

                // Check for NOT EXISTS / EXISTS inside MINUS
                var notExistsSpan = PeekSpan(10);
                bool negated = false;
                if (notExistsSpan.Length >= 3 && notExistsSpan[..3].Equals("NOT", StringComparison.OrdinalIgnoreCase))
                {
                    ConsumeKeyword("NOT");
                    SkipWhitespace();
                    negated = true;
                }

                var existsSpan = PeekSpan(6);
                if (existsSpan.Length >= 6 && existsSpan[..6].Equals("EXISTS", StringComparison.OrdinalIgnoreCase))
                {
                    ConsumeKeyword("EXISTS");
                    SkipWhitespace();
                    ParseMinusExistsFilter(ref pattern, negated);
                    SkipWhitespace();
                    continue;
                }

                // Parse regular FILTER expression and store it for MINUS evaluation
                if (Peek() == '(')
                {
                    int filterStart = _position;
                    Advance(); // Skip '('
                    SkipWhitespace();

                    // Check if this is FILTER ( NOT EXISTS ... ) or FILTER ( EXISTS ... )
                    var innerSpan = PeekSpan(10);
                    // Variable tracks negation state for EXISTS patterns (used implicitly via parsing path)
                    if (innerSpan.Length >= 3 && innerSpan[..3].Equals("NOT", StringComparison.OrdinalIgnoreCase))
                    {
                        var savedPos = _position;
                        ConsumeKeyword("NOT");
                        SkipWhitespace();
                        var afterNot = PeekSpan(6);
                        if (afterNot.Length >= 6 && afterNot[..6].Equals("EXISTS", StringComparison.OrdinalIgnoreCase))
                        {
                            ConsumeKeyword("EXISTS");
                            SkipWhitespace();
                            ParseMinusExistsFilter(ref pattern, true);
                            SkipWhitespace();
                            if (Peek() == ')') Advance(); // Skip closing ')'
                            SkipWhitespace();
                            continue;
                        }
                        // Not EXISTS after NOT, restore position
                        _position = savedPos;
                    }
                    else if (innerSpan.Length >= 6 && innerSpan[..6].Equals("EXISTS", StringComparison.OrdinalIgnoreCase))
                    {
                        ConsumeKeyword("EXISTS");
                        SkipWhitespace();
                        ParseMinusExistsFilter(ref pattern, false);
                        SkipWhitespace();
                        if (Peek() == ')') Advance();
                        SkipWhitespace();
                        continue;
                    }

                    // Regular filter expression - restore to filterStart and scan for embedded EXISTS
                    _position = filterStart;
                    Advance(); // Skip '('

                    // Scan the filter expression for embedded [NOT] EXISTS patterns
                    // while tracking the end position
                    int filterContentStart = _position;
                    int depth = 1;
                    while (!IsAtEnd() && depth > 0)
                    {
                        var c = Peek();
                        if (c == '(') depth++;
                        else if (c == ')') depth--;

                        // Look for NOT EXISTS or EXISTS at current position
                        if (depth > 0 && (c == 'N' || c == 'n' || c == 'E' || c == 'e'))
                        {
                            var remainingSpan = PeekSpan(12); // "NOT EXISTS {" = 12 chars
                            bool foundNot = false;
                            int existsStartPos = _position;

                            // Check for "NOT"
                            if (remainingSpan.Length >= 3 &&
                                remainingSpan[..3].Equals("NOT", StringComparison.OrdinalIgnoreCase))
                            {
                                var savedPos = _position;
                                ConsumeKeyword("NOT");
                                SkipWhitespace();
                                remainingSpan = PeekSpan(7);
                                if (remainingSpan.Length >= 6 &&
                                    remainingSpan[..6].Equals("EXISTS", StringComparison.OrdinalIgnoreCase))
                                {
                                    foundNot = true;
                                    // Continue to EXISTS parsing below
                                }
                                else
                                {
                                    _position = savedPos;
                                    Advance();
                                    continue;
                                }
                            }

                            // Check for "EXISTS"
                            remainingSpan = PeekSpan(7);
                            if (remainingSpan.Length >= 6 &&
                                remainingSpan[..6].Equals("EXISTS", StringComparison.OrdinalIgnoreCase))
                            {
                                ConsumeKeyword("EXISTS");
                                SkipWhitespace();

                                // Parse the EXISTS pattern
                                if (Peek() == '{')
                                {
                                    // Calculate position relative to filter start (for CompoundExistsRef)
                                    int existsPosInFilter = existsStartPos - filterStart;

                                    // Record the EXISTS filter index before adding
                                    int existsFilterIndex = pattern.MinusExistsCount;

                                    // Parse the EXISTS pattern and add it
                                    ParseMinusExistsFilter(ref pattern, foundNot);

                                    // Calculate the length of the [NOT] EXISTS {...} portion
                                    int existsLength = _position - existsStartPos;

                                    // Store a reference for later substitution
                                    // Block index is the current block being parsed (MinusBlockCount - 1)
                                    int currentBlock = pattern.MinusBlockCount > 0 ? pattern.MinusBlockCount - 1 : 0;
                                    pattern.AddCompoundExistsRef(new CompoundExistsRef
                                    {
                                        StartInFilter = existsPosInFilter,
                                        Length = existsLength,
                                        ExistsFilterIndex = existsFilterIndex,
                                        Negated = foundNot,
                                        BlockIndex = currentBlock
                                    });

                                    // Continue scanning from here (don't call Advance)
                                    continue;
                                }
                            }
                        }

                        Advance();
                    }

                    // Store the filter expression (including outer parens)
                    pattern.SetMinusFilter(new FilterExpr
                    {
                        Start = filterStart,
                        Length = _position - filterStart
                    });
                }
                SkipWhitespace();
                continue;
            }

            // Check for OPTIONAL inside MINUS
            if (span.Length >= 8 && span[..8].Equals("OPTIONAL", StringComparison.OrdinalIgnoreCase))
            {
                ParseMinusOptional(ref pattern);
                SkipWhitespace();
                continue;
            }

            // Check for nested MINUS inside MINUS
            if (span.Length >= 5 && span[..5].Equals("MINUS", StringComparison.OrdinalIgnoreCase))
            {
                ParseNestedMinus(ref pattern);
                SkipWhitespace();
                continue;
            }

            // Try to parse a triple pattern
            var subject = ParseTerm();
            if (subject.Type == TermType.Variable && subject.Length == 0)
                break;

            SkipWhitespace();
            var predicate = ParseTerm();

            SkipWhitespace();
            var obj = ParseTerm();

            pattern.AddMinusPattern(new TriplePattern
            {
                Subject = subject,
                Predicate = predicate,
                Object = obj
            });

            SkipWhitespace();

            // Optional dot after triple pattern
            if (Peek() == '.')
                Advance();
        }

        // End this MINUS block before closing
        pattern.EndMinusBlock();

        SkipWhitespace();
        if (Peek() == '}')
            Advance(); // Skip '}'
    }

    /// <summary>
    /// Parse OPTIONAL clause inside MINUS: OPTIONAL { patterns }
    /// Patterns inside are added as optional MINUS patterns.
    /// </summary>
    private void ParseMinusOptional(ref GraphPattern pattern)
    {
        ConsumeKeyword("OPTIONAL");
        SkipWhitespace();

        if (Peek() != '{')
            return;

        Advance(); // Skip '{'
        SkipWhitespace();

        // Parse patterns inside OPTIONAL and add them as optional MINUS patterns
        while (!IsAtEnd() && Peek() != '}')
        {
            SkipWhitespace();

            if (IsAtEnd() || Peek() == '}')
                break;

            var subject = ParseTerm();
            if (subject.Type == TermType.Variable && subject.Length == 0)
                break;

            SkipWhitespace();
            var predicate = ParseTerm();

            SkipWhitespace();
            var obj = ParseTerm();

            // Add as OPTIONAL MINUS pattern (marked with optional flag)
            pattern.AddOptionalMinusPattern(new TriplePattern
            {
                Subject = subject,
                Predicate = predicate,
                Object = obj
            });

            SkipWhitespace();

            // Optional dot after triple pattern
            if (Peek() == '.')
                Advance();
        }

        SkipWhitespace();
        if (Peek() == '}')
            Advance(); // Skip '}'
    }

    /// <summary>
    /// Parse nested MINUS clause inside MINUS: MINUS { ... MINUS { ... } }
    /// Patterns inside are added to nested MINUS pattern storage.
    /// </summary>
    private void ParseNestedMinus(ref GraphPattern pattern)
    {
        ConsumeKeyword("MINUS");
        SkipWhitespace();

        if (Peek() != '{')
            return;

        // Start a new nested MINUS block
        pattern.StartNestedMinusBlock();

        Advance(); // Skip '{'
        SkipWhitespace();

        // Parse patterns inside nested MINUS
        while (!IsAtEnd() && Peek() != '}')
        {
            SkipWhitespace();

            // Check for FILTER inside nested MINUS
            var span = PeekSpan(8);
            if (span.Length >= 6 && span[..6].Equals("FILTER", StringComparison.OrdinalIgnoreCase))
            {
                ConsumeKeyword("FILTER");
                SkipWhitespace();

                // Check for NOT EXISTS / EXISTS inside nested MINUS
                var notExistsSpan = PeekSpan(10);
                bool negated = false;
                if (notExistsSpan.Length >= 3 && notExistsSpan[..3].Equals("NOT", StringComparison.OrdinalIgnoreCase))
                {
                    ConsumeKeyword("NOT");
                    SkipWhitespace();
                    negated = true;
                }

                var existsSpan = PeekSpan(6);
                if (existsSpan.Length >= 6 && existsSpan[..6].Equals("EXISTS", StringComparison.OrdinalIgnoreCase))
                {
                    ConsumeKeyword("EXISTS");
                    SkipWhitespace();
                    ParseNestedMinusExistsFilter(ref pattern, negated);
                    SkipWhitespace();
                    continue;
                }

                // Skip other filter expressions inside nested MINUS for now
                // (may need to be expanded later)
                SkipWhitespace();
                continue;
            }

            if (IsAtEnd() || Peek() == '}')
                break;

            // Try to parse a triple pattern
            var subject = ParseTerm();
            if (subject.Type == TermType.Variable && subject.Length == 0)
                break;

            SkipWhitespace();
            var predicate = ParseTerm();

            SkipWhitespace();
            var obj = ParseTerm();

            pattern.AddNestedMinusPattern(new TriplePattern
            {
                Subject = subject,
                Predicate = predicate,
                Object = obj
            });

            SkipWhitespace();

            // Optional dot after triple pattern
            if (Peek() == '.')
                Advance();
        }

        // End this nested MINUS block
        pattern.EndNestedMinusBlock();

        SkipWhitespace();
        if (Peek() == '}')
            Advance(); // Skip '}'
    }

    /// <summary>
    /// Parse EXISTS/NOT EXISTS filter inside nested MINUS block.
    /// </summary>
    private void ParseNestedMinusExistsFilter(ref GraphPattern pattern, bool negated)
    {
        if (Peek() != '{')
            return;

        Advance(); // Skip '{'
        SkipWhitespace();

        var existsFilter = new ExistsFilter { Negated = negated };

        // Parse patterns inside the EXISTS block
        while (!IsAtEnd() && Peek() != '}')
        {
            SkipWhitespace();

            if (IsAtEnd() || Peek() == '}')
                break;

            // Try to parse a triple pattern
            var subject = ParseTerm();
            if (subject.Type == TermType.Variable && subject.Length == 0)
                break;

            SkipWhitespace();
            var predicate = ParseTerm();
            SkipWhitespace();
            var obj = ParseTerm();

            existsFilter.AddPattern(new TriplePattern
            {
                Subject = subject,
                Predicate = predicate,
                Object = obj
            });

            SkipWhitespace();

            // Optional dot after triple pattern
            if (Peek() == '.')
                Advance();
        }

        SkipWhitespace();
        if (Peek() == '}')
            Advance(); // Skip '}'

        // Add the EXISTS filter to the current nested MINUS block
        pattern.AddNestedMinusExistsFilter(existsFilter);
    }

    /// <summary>
    /// Parse EXISTS/NOT EXISTS filter inside MINUS block.
    /// </summary>
    private void ParseMinusExistsFilter(ref GraphPattern pattern, bool negated)
    {
        if (Peek() != '{')
            return;

        Advance(); // Skip '{'
        SkipWhitespace();

        var existsFilter = new ExistsFilter { Negated = negated };

        // Parse patterns inside the EXISTS block
        while (!IsAtEnd() && Peek() != '}')
        {
            SkipWhitespace();

            // Try to parse a triple pattern with semicolon shorthand support
            var subject = ParseTerm();
            if (subject.Type == TermType.Variable && subject.Length == 0)
                break;

            SkipWhitespace();
            var predicate = ParseTerm();
            SkipWhitespace();
            var obj = ParseTerm();

            existsFilter.AddPattern(new TriplePattern
            {
                Subject = subject,
                Predicate = predicate,
                Object = obj
            });

            SkipWhitespace();

            // Handle semicolon shorthand
            while (Peek() == ';')
            {
                Advance(); // Skip ';'
                SkipWhitespace();

                if (IsAtEnd() || Peek() == '}' || Peek() == '.')
                    break;

                var nextPredicate = ParseTerm();
                if (nextPredicate.Type == TermType.Variable && nextPredicate.Length == 0)
                    break;

                SkipWhitespace();
                var nextObj = ParseTerm();

                existsFilter.AddPattern(new TriplePattern
                {
                    Subject = subject,
                    Predicate = nextPredicate,
                    Object = nextObj
                });

                SkipWhitespace();
            }

            // Skip optional '.'
            if (Peek() == '.')
                Advance();
        }

        SkipWhitespace();
        if (Peek() == '}')
            Advance(); // Skip '}'

        pattern.AddMinusExistsFilter(existsFilter);
    }

    /// <summary>
    /// Parse VALUES clause: VALUES ?var { value1 value2 ... } or VALUES (?var1 ?var2) { (val1 val2) ... }
    /// Supports single variable or multiple variables with cardinality validation.
    /// UNDEF values are stored with length = -1.
    /// </summary>
    private void ParseValues(ref GraphPattern pattern)
    {
        ConsumeKeyword("VALUES");
        SkipWhitespace();

        var values = new ValuesClause();

        // Check for multi-variable form: (?var1 ?var2 ...)
        if (Peek() == '(')
        {
            Advance(); // Skip '('
            SkipWhitespace();

            // Parse all variables
            while (!IsAtEnd() && Peek() == '?')
            {
                int varStart = _position;
                Advance(); // Skip '?'
                while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                    Advance();
                values.AddVariable(varStart, _position - varStart);
                SkipWhitespace();
            }

            if (Peek() == ')')
                Advance(); // Skip ')'
            SkipWhitespace();
        }
        // Single variable form: ?var
        else if (Peek() == '?')
        {
            int varStart = _position;
            Advance(); // Skip '?'
            while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                Advance();
            values.AddVariable(varStart, _position - varStart);
            SkipWhitespace();
        }
        else
        {
            return;
        }

        int varCount = values.VariableCount;

        // Expect '{'
        if (Peek() != '{')
            return;

        Advance(); // Skip '{'
        SkipWhitespace();

        // Parse value rows
        while (!IsAtEnd() && Peek() != '}')
        {
            SkipWhitespace();

            // Check for parenthesized row: (val1 val2 ...)
            if (Peek() == '(')
            {
                Advance(); // Skip '('
                SkipWhitespace();

                int rowValueCount = 0;
                while (!IsAtEnd() && Peek() != ')')
                {
                    // Check for UNDEF first
                    var span = PeekSpan(5);
                    if (span.Length >= 5 && span[..5].Equals("UNDEF", StringComparison.OrdinalIgnoreCase))
                    {
                        ConsumeKeyword("UNDEF");
                        values.AddValue(0, -1); // Mark as UNDEF with length = -1
                        rowValueCount++;
                    }
                    else
                    {
                        int valueStart = _position;
                        int valueLen = ParseValuesValue();
                        if (valueLen > 0)
                        {
                            values.AddValue(valueStart, valueLen);
                            rowValueCount++;
                        }
                        else
                        {
                            break;
                        }
                    }
                    SkipWhitespace();
                }

                // Validate cardinality
                if (rowValueCount != varCount)
                {
                    if (rowValueCount < varCount)
                        throw new SparqlParseException($"VALUES row has {rowValueCount} values but {varCount} variables declared");
                    else
                        throw new SparqlParseException($"VALUES row has {rowValueCount} values but only {varCount} variables declared");
                }

                if (Peek() == ')')
                    Advance(); // Skip ')'
                SkipWhitespace();
            }
            else
            {
                // Single value (for single variable form)
                // Check for UNDEF
                var span = PeekSpan(5);
                if (span.Length >= 5 && span[..5].Equals("UNDEF", StringComparison.OrdinalIgnoreCase))
                {
                    ConsumeKeyword("UNDEF");
                    values.AddValue(0, -1); // Mark as UNDEF with length = -1
                }
                else
                {
                    int valueStart = _position;
                    int valueLen = ParseValuesValue();
                    if (valueLen > 0)
                    {
                        values.AddValue(valueStart, valueLen);
                    }
                    else
                    {
                        break;
                    }
                }
                SkipWhitespace();
            }
        }

        SkipWhitespace();
        if (Peek() == '}')
            Advance(); // Skip '}'

        pattern.SetValues(values);
    }

    /// <summary>
    /// Parse a single value in a VALUES clause. Returns length of value parsed.
    /// </summary>
    private int ParseValuesValue()
    {
        int valueStart = _position;
        var ch = Peek();

        if (ch == '"')
        {
            // String literal
            Advance();
            while (!IsAtEnd() && Peek() != '"')
            {
                if (Peek() == '\\') Advance();
                Advance();
            }
            if (!IsAtEnd()) Advance(); // Skip closing '"'

            // Save position after closing quote (before any whitespace)
            int valueEnd = _position;

            // Check for language tag or datatype
            // Language tags and datatypes must immediately follow the string (no whitespace per spec)
            // but we'll be lenient and allow whitespace for compatibility
            if (Peek() == '@')
            {
                Advance();
                while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '-'))
                    Advance();
                return _position - valueStart;
            }
            else if (PeekSpan(2).Length >= 2 && PeekSpan(2)[0] == '^' && PeekSpan(2)[1] == '^')
            {
                Advance(); Advance(); // Skip ^^
                if (Peek() == '<')
                {
                    Advance();
                    while (!IsAtEnd() && Peek() != '>')
                        Advance();
                    if (!IsAtEnd()) Advance();
                }
                else
                {
                    // Prefixed name datatype (e.g., ^^xsd:integer)
                    while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == ':' || Peek() == '_'))
                        Advance();
                }
                return _position - valueStart;
            }

            // No language tag or datatype - return length without trailing whitespace
            return valueEnd - valueStart;
        }
        else if (ch == '<')
        {
            // IRI
            Advance();
            while (!IsAtEnd() && Peek() != '>')
                Advance();
            if (!IsAtEnd()) Advance(); // Skip '>'
            return _position - valueStart;
        }
        else if (IsDigit(ch) || ch == '-' || ch == '+')
        {
            // Numeric literal
            if (ch == '-' || ch == '+') Advance();
            while (!IsAtEnd() && (IsDigit(Peek()) || Peek() == '.'))
                Advance();
            return _position - valueStart;
        }
        else if (ch == ':')
        {
            // Prefixed name with empty prefix (e.g., :localName)
            Advance(); // Skip ':'
            while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_' || Peek() == '-' || Peek() == '.'))
                Advance();
            return _position - valueStart;
        }
        else if (ch == '_')
        {
            // Blank node (e.g., _:b0)
            Advance(); // Skip '_'
            if (!IsAtEnd() && Peek() == ':')
            {
                Advance(); // Skip ':'
                while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_' || Peek() == '-' || Peek() == '.'))
                    Advance();
            }
            return _position - valueStart;
        }
        else if (IsLetter(ch))
        {
            // Boolean, UNDEF, or prefixed name (e.g., true, false, UNDEF, ex:localName)
            while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == ':' || Peek() == '_' || Peek() == '-' || Peek() == '.'))
                Advance();
            return _position - valueStart;
        }

        return 0;
    }

    /// <summary>
    /// Parse BIND clause: BIND ( expression AS ?variable )
    /// </summary>
    /// <param name="pattern">The graph pattern to add the bind to</param>
    /// <param name="scopeStartIndex">The pattern index where current scope starts (for nested groups). Default 0 checks all patterns.</param>
    /// <param name="bindScopeStartIndex">The bind index where current scope starts (for nested groups/UNION branches). Default 0 checks all binds.</param>
    private void ParseBind(ref GraphPattern pattern, int scopeStartIndex = 0, int bindScopeStartIndex = 0)
    {
        ConsumeKeyword("BIND");
        SkipWhitespace();

        if (Peek() != '(')
            return;

        Advance(); // Skip '('
        SkipWhitespace();

        // Parse expression - capture everything until "AS"
        int exprStart = _position;
        int parenDepth = 0;

        while (!IsAtEnd())
        {
            var ch = Peek();
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
                // Check for "AS" keyword (with whitespace before)
                if (parenDepth == 0)
                {
                    var span = PeekSpan(3);
                    if (span.Length >= 2 && span[..2].Equals("AS", StringComparison.OrdinalIgnoreCase) &&
                        (span.Length < 3 || !char.IsLetterOrDigit(span[2])))
                    {
                        break;
                    }
                }
                Advance();
            }
        }

        int exprLength = _position - exprStart;

        // Trim trailing whitespace from expression
        while (exprLength > 0 && char.IsWhiteSpace(_source[exprStart + exprLength - 1]))
            exprLength--;

        SkipWhitespace();

        // Consume "AS"
        ConsumeKeyword("AS");
        SkipWhitespace();

        // Parse target variable
        int varStart = _position;
        if (Peek() == '?')
        {
            Advance(); // Skip '?'
            while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                Advance();
        }
        int varLength = _position - varStart;

        SkipWhitespace();

        // Skip closing ')'
        if (Peek() == ')')
            Advance();

        // BIND scope validation (syntax-BINDscope6/7): check if target variable is already bound
        var targetVar = _source.Slice(varStart, varLength);
        if (IsVariableInScope(ref pattern, targetVar, scopeStartIndex, bindScopeStartIndex))
        {
            throw new SparqlParseException($"BIND target variable {targetVar.ToString()} is already in scope - cannot rebind");
        }

        // Add the bind expression
        // Set AfterPatternIndex to current pattern count - 1 so BIND is evaluated after
        // all patterns added so far. This enables proper BIND semantics where:
        //   ?s ?p ?o .           # pattern 0
        //   BIND(?o+1 AS ?z)     # AfterPatternIndex = 0, evaluate after pattern 0
        //   ?s1 ?p1 ?z           # pattern 1 - now ?z is bound from BIND
        pattern.AddBind(new BindExpr
        {
            ExprStart = exprStart,
            ExprLength = exprLength,
            VarStart = varStart,
            VarLength = varLength,
            AfterPatternIndex = pattern.PatternCount - 1,  // Evaluate after all current patterns
            ScopeDepth = _scopeDepth  // Track scope depth for BIND scoping
        });
    }

    /// <summary>
    /// Check if a variable is already in scope within the current graph pattern.
    /// Used for BIND scope validation (syntax-BINDscope6/7).
    /// </summary>
    /// <param name="pattern">The graph pattern to check</param>
    /// <param name="varName">The variable name to look for</param>
    /// <param name="scopeStartIndex">Only check patterns from this index onwards (for nested groups)</param>
    /// <param name="bindScopeStartIndex">Only check binds from this index onwards (for nested groups/UNION branches)</param>
    private bool IsVariableInScope(ref GraphPattern pattern, ReadOnlySpan<char> varName, int scopeStartIndex = 0, int bindScopeStartIndex = 0)
    {
        // Check triple patterns in current scope (starting from scopeStartIndex for nested groups)
        for (int i = scopeStartIndex; i < pattern.PatternCount; i++)
        {
            var tp = pattern.GetPattern(i);
            if (TermMatchesVariable(tp.Subject, varName) ||
                TermMatchesVariable(tp.Predicate, varName) ||
                TermMatchesVariable(tp.Object, varName))
            {
                return true;
            }
        }

        // Check previous BIND expressions in current scope (starting from bindScopeStartIndex)
        // Each nested group / UNION branch has its own bind scope
        for (int i = bindScopeStartIndex; i < pattern.BindCount; i++)
        {
            var bind = pattern.GetBind(i);
            var bindVar = _source.Slice(bind.VarStart, bind.VarLength);
            if (varName.SequenceEqual(bindVar))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Check if a term matches a variable name.
    /// </summary>
    private bool TermMatchesVariable(Term term, ReadOnlySpan<char> varName)
    {
        if (!term.IsVariable)
            return false;

        var termVar = _source.Slice(term.Start, term.Length);
        return varName.SequenceEqual(termVar);
    }

    /// <summary>
    /// [59] ServiceGraphPattern ::= 'SERVICE' 'SILENT'? VarOrIri GroupGraphPattern
    /// Parses SERVICE [SILENT] &lt;uri&gt; { patterns } for federated queries.
    /// </summary>
    private void ParseService(ref GraphPattern pattern)
    {
        ConsumeKeyword("SERVICE");
        SkipWhitespace();

        // Check for SILENT modifier
        var silent = false;
        var span = PeekSpan(6);
        if (span.Length >= 6 && span[..6].Equals("SILENT", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("SILENT");
            SkipWhitespace();
            silent = true;
        }

        // Parse endpoint term (IRI or variable)
        var endpointTerm = ParseTerm();
        if (endpointTerm.Type == TermType.Variable && endpointTerm.Length == 0)
            return;

        SkipWhitespace();

        if (Peek() != '{')
            return;

        Advance(); // Skip '{'
        SkipWhitespace();

        var serviceClause = new ServiceClause { Endpoint = endpointTerm, Silent = silent };

        // Parse patterns inside SERVICE block
        while (!IsAtEnd() && Peek() != '}')
        {
            SkipWhitespace();

            // Check for FILTER inside SERVICE
            span = PeekSpan(6);
            if (span.Length >= 6 && span[..6].Equals("FILTER", StringComparison.OrdinalIgnoreCase))
            {
                // For now, add filters to parent pattern
                // (proper scoping would require nested patterns)
                ParseFilter(ref pattern);
                continue;
            }

            // Try to parse a triple pattern
            if (IsAtEnd() || Peek() == '}')
                break;

            var subject = ParseTerm();
            if (subject.Type == TermType.Variable && subject.Length == 0)
                break;

            SkipWhitespace();
            var predicate = ParseTerm();

            SkipWhitespace();
            var obj = ParseTerm();

            serviceClause.AddPattern(new TriplePattern
            {
                Subject = subject,
                Predicate = predicate,
                Object = obj
            });

            SkipWhitespace();

            // Optional dot after triple pattern
            if (Peek() == '.')
                Advance();
        }

        SkipWhitespace();
        if (Peek() == '}')
            Advance(); // Skip '}'

        pattern.AddServiceClause(serviceClause);
    }

    /// <summary>
    /// Parse GRAPH clause: GRAPH &lt;iri&gt; { patterns } or GRAPH ?var { patterns }
    /// </summary>
    private void ParseGraph(ref GraphPattern pattern)
    {
        ConsumeKeyword("GRAPH");
        SkipWhitespace();

        // Parse graph term (IRI or variable)
        var graphTerm = ParseTerm();
        if (graphTerm.Type == TermType.Variable && graphTerm.Length == 0)
            return;

        SkipWhitespace();

        if (Peek() != '{')
            return;

        Advance(); // Skip '{'
        SkipWhitespace();

        var graphClause = new GraphClause { Graph = graphTerm };

        // Parse patterns inside GRAPH block
        while (!IsAtEnd() && Peek() != '}')
        {
            SkipWhitespace();

            // Check for FILTER inside GRAPH
            var span = PeekSpan(8);
            if (span.Length >= 6 && span[..6].Equals("FILTER", StringComparison.OrdinalIgnoreCase))
            {
                // For now, add filters to parent pattern
                // (proper scoping would require nested patterns)
                ParseFilter(ref pattern);
                continue;
            }

            // Check for OPTIONAL inside GRAPH
            if (span.Length >= 8 && span[..8].Equals("OPTIONAL", StringComparison.OrdinalIgnoreCase))
            {
                // TODO: Handle OPTIONAL inside GRAPH properly
                ParseOptional(ref pattern);
                continue;
            }

            // Check for MINUS inside GRAPH
            if (span.Length >= 5 && span[..5].Equals("MINUS", StringComparison.OrdinalIgnoreCase))
            {
                // Add MINUS patterns to parent pattern for later evaluation
                // The graph context will be set during GRAPH clause execution
                ParseMinus(ref pattern);
                continue;
            }

            // Check for SELECT directly inside GRAPH (subquery without extra braces)
            // W3C test: GRAPH ?g {SELECT (count(*) AS ?c) WHERE { ?s :p ?x }}
            if (span.Length >= 6 && span[..6].Equals("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                // Parse subquery with graph context
                var subSelect = ParseSubSelect();
                subSelect.GraphContext = graphTerm;  // Set the graph context
                pattern.AddSubQuery(subSelect);
                continue;
            }

            // Check for VALUES directly inside GRAPH
            // W3C test: GRAPH ?g { VALUES (?g ?t) { ... } }
            if (span.Length >= 6 && span[..6].Equals("VALUES", StringComparison.OrdinalIgnoreCase))
            {
                // Parse VALUES and add to parent pattern
                // The graph context is already set via the GRAPH clause
                ParseValues(ref pattern);
                continue;
            }

            // Check for nested group { ... } which might be a subquery
            if (Peek() == '{')
            {
                Advance(); // Skip '{'
                SkipWhitespace();

                // Check if this is a subquery: { SELECT ... }
                var checkSpan = PeekSpan(6);
                if (checkSpan.Length >= 6 && checkSpan[..6].Equals("SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    // Parse subquery with graph context
                    var subSelect = ParseSubSelect();
                    subSelect.GraphContext = graphTerm;  // Set the graph context
                    pattern.AddSubQuery(subSelect);
                    SkipWhitespace();
                    if (Peek() == '}')
                        Advance(); // Skip '}'
                    continue;
                }
                else
                {
                    // Not a subquery - need to back up and parse as nested patterns
                    // For simplicity, reparse the nested group into the graph clause
                    while (!IsAtEnd() && Peek() != '}')
                    {
                        SkipWhitespace();
                        if (IsAtEnd() || Peek() == '}')
                            break;

                        var nestedSubject = ParseTerm();
                        if (nestedSubject.Type == TermType.Variable && nestedSubject.Length == 0)
                            break;

                        SkipWhitespace();
                        // Parse predicate or property path (handles sequences, alternatives, inverse, etc.)
                        var (nestedPredicate, nestedPath) = ParsePredicateOrPath();
                        SkipWhitespace();
                        var nestedObj = ParseTerm();

                        // Handle sequence paths: expand to multiple patterns with intermediate variables
                        if (nestedPath.Type == PathType.Sequence)
                        {
                            ExpandSequencePathIntoGraphClause(ref graphClause, nestedSubject, nestedObj, nestedPath);
                        }
                        else
                        {
                            graphClause.AddPattern(new TriplePattern
                            {
                                Subject = nestedSubject,
                                Predicate = nestedPredicate,
                                Object = nestedObj,
                                Path = nestedPath
                            });
                        }

                        SkipWhitespace();
                        if (Peek() == '.')
                            Advance();
                    }
                    SkipWhitespace();
                    if (Peek() == '}')
                        Advance(); // Skip inner '}'
                    continue;
                }
            }

            // Try to parse a triple pattern
            if (IsAtEnd() || Peek() == '}')
                break;

            var subject = ParseTerm();
            if (subject.Type == TermType.Variable && subject.Length == 0)
                break;

            SkipWhitespace();
            // Parse predicate or property path (handles sequences, alternatives, inverse, etc.)
            var (predicate, path) = ParsePredicateOrPath();

            SkipWhitespace();
            var obj = ParseTerm();

            // Handle sequence paths: expand to multiple patterns with intermediate variables
            if (path.Type == PathType.Sequence)
            {
                ExpandSequencePathIntoGraphClause(ref graphClause, subject, obj, path);
            }
            else
            {
                graphClause.AddPattern(new TriplePattern
                {
                    Subject = subject,
                    Predicate = predicate,
                    Object = obj,
                    Path = path
                });
            }

            SkipWhitespace();

            // Optional dot after triple pattern
            if (Peek() == '.')
                Advance();
        }

        SkipWhitespace();
        if (Peek() == '}')
            Advance(); // Skip '}'

        pattern.AddGraphClause(graphClause);
    }
}
