using System;
using SkyOmega.Mercury.Sparql;

namespace SkyOmega.Mercury.Sparql.Parsing;

public ref partial struct SparqlParser
{
    /// <summary>
    /// [7] SelectQuery ::= SelectClause DatasetClause* WhereClause SolutionModifier
    /// </summary>
    private Query ParseSelectQuery(Prologue prologue)
    {
        var selectClause = ParseSelectClause();
        var datasets = ParseDatasetClauses();
        var whereClause = ParseWhereClause();
        var modifier = ParseSolutionModifier();

        // Validate GROUP BY semantics
        ValidateGroupBySemantics(selectClause, modifier.GroupBy);

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
    /// Validates GROUP BY semantic constraints according to SPARQL 1.1 spec.
    /// </summary>
    private void ValidateGroupBySemantics(SelectClause selectClause, GroupByClause groupBy)
    {
        // Rule 1: SELECT * is not allowed with GROUP BY (syn-bad-01)
        if (selectClause.SelectAll && groupBy.HasGroupBy)
        {
            throw new SparqlParseException("SELECT * is not allowed with GROUP BY");
        }

        // Rule 2: If SELECT has aggregates without GROUP BY, only aggregates/constants allowed (agg10)
        // Rule 3: With GROUP BY, non-aggregate SELECT items must appear in GROUP BY (agg09, syn-bad-02, group06)
        // These require tracking SELECT variables, which is done by ValidateSelectVariables
        if (selectClause.HasProjectedVariables && (groupBy.HasGroupBy || selectClause.HasAggregates))
        {
            ValidateSelectVariables(selectClause, groupBy);
        }
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
    private void ValidateSelectVariables(SelectClause selectClause, GroupByClause groupBy)
    {
        // If there are aggregates but no GROUP BY, non-aggregate variables are not allowed
        if (selectClause.HasAggregates && !groupBy.HasGroupBy)
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
                        // We need to track these aliases too for duplicate detection
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
        else if (span.Length >= 5 && span[..5].Equals("COUNT", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("COUNT");
            agg.Function = AggregateFunction.Count;
        }
        else if (span.Length >= 3 && span[..3].Equals("SUM", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("SUM");
            agg.Function = AggregateFunction.Sum;
        }
        else if (span.Length >= 3 && span[..3].Equals("AVG", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("AVG");
            agg.Function = AggregateFunction.Avg;
        }
        else if (span.Length >= 3 && span[..3].Equals("MIN", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("MIN");
            agg.Function = AggregateFunction.Min;
        }
        else if (span.Length >= 3 && span[..3].Equals("MAX", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("MAX");
            agg.Function = AggregateFunction.Max;
        }
        else if (span.Length >= 6 && span[..6].Equals("SAMPLE", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("SAMPLE");
            agg.Function = AggregateFunction.Sample;
        }
        else
        {
            // Not a recognized aggregate - parse as expression (expr AS ?var)
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

        // Parse variable or *
        if (Peek() == '*')
        {
            agg.VariableStart = _position;
            Advance();
            agg.VariableLength = 1;
        }
        else if (Peek() == '?')
        {
            agg.VariableStart = _position;
            Advance();
            while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                Advance();
            agg.VariableLength = _position - agg.VariableStart;
        }

        SkipWhitespace();

        // syn-bad-06: Check for invalid multiple arguments (only COUNT can take *)
        // All aggregate functions take exactly 1 expression argument
        if (Peek() == ',')
        {
            var funcName = agg.Function.ToString().ToUpper();
            throw new SparqlParseException($"{funcName} aggregate function takes exactly 1 argument");
        }

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
            // Parse variable list
            while (Peek() == '?')
            {
                var varStart = _position;
                Advance(); // Skip '?'
                while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                    Advance();
                var varLen = _position - varStart;
                subSelect.AddProjectedVariable(varStart, varLen);
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
    }

    /// <summary>
    /// Parse a FILTER expression inside a subquery.
    /// </summary>
    private void ParseSubSelectFilter(ref SubSelect subSelect)
    {
        ConsumeKeyword("FILTER");
        SkipWhitespace();

        int exprStart;
        int exprEnd;

        if (Peek() == '(')
        {
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
        else
        {
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

        SkipWhitespace();

        // Parse predicate
        var predicate = ParseTerm();

        SkipWhitespace();

        // Parse object
        var obj = ParseTerm();

        subSelect.AddPattern(new TriplePattern { Subject = subject, Predicate = predicate, Object = obj });

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

            subSelect.AddPattern(new TriplePattern { Subject = subject, Predicate = nextPredicate, Object = nextObj });

            SkipWhitespace();
        }

        return true;
    }

    /// <summary>
    /// Parse solution modifiers for a subquery (ORDER BY, LIMIT, OFFSET).
    /// </summary>
    private void ParseSubSelectSolutionModifiers(ref SubSelect subSelect)
    {
        // Parse ORDER BY
        var span = PeekSpan(8);
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

            // Check for nested group { ... } which might be a subquery or UNION
            if (Peek() == '{')
            {
                ParseNestedGroupGraphPattern(ref pattern);
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

        // Check for SubSelect: starts with "SELECT"
        var checkSpan = PeekSpan(6);
        if (checkSpan.Length >= 6 && checkSpan[..6].Equals("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            var subSelect = ParseSubSelect();
            pattern.AddSubQuery(subSelect);
            SkipWhitespace();
            if (Peek() == '}')
                Advance(); // Skip '}'
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

        // FILTER can be: FILTER(expr) or FILTER expr
        if (Peek() == '(')
        {
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
            pattern.AddFilter(new FilterExpr { Start = start, Length = length });
        }
    }

    /// <summary>
    /// Parse EXISTS or NOT EXISTS filter: [NOT] EXISTS { pattern }
    /// </summary>
    private void ParseExistsFilter(ref GraphPattern pattern, bool negated)
    {
        if (Peek() != '{')
            return;

        Advance(); // Skip '{'
        SkipWhitespace();

        var existsFilter = new ExistsFilter { Negated = negated };

        // Parse triple patterns inside the EXISTS block
        while (!IsAtEnd() && Peek() != '}')
        {
            SkipWhitespace();

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
    /// </summary>
    private void ParseMinus(ref GraphPattern pattern)
    {
        ConsumeKeyword("MINUS");
        SkipWhitespace();

        if (Peek() != '{')
            return;

        Advance(); // Skip '{'
        SkipWhitespace();

        // Parse patterns inside MINUS and add them as minus patterns
        while (!IsAtEnd() && Peek() != '}')
        {
            SkipWhitespace();

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

        SkipWhitespace();
        if (Peek() == '}')
            Advance(); // Skip '}'
    }

    /// <summary>
    /// Parse VALUES clause: VALUES ?var { value1 value2 ... } or VALUES (?var1 ?var2) { (val1 val2) ... }
    /// Supports single variable or multiple variables with cardinality validation.
    /// </summary>
    private void ParseValues(ref GraphPattern pattern)
    {
        ConsumeKeyword("VALUES");
        SkipWhitespace();

        var values = new ValuesClause();
        int varCount = 0;

        // Check for multi-variable form: (?var1 ?var2 ...)
        if (Peek() == '(')
        {
            Advance(); // Skip '('
            SkipWhitespace();

            // Count and parse variables
            while (!IsAtEnd() && Peek() == '?')
            {
                if (varCount == 0)
                {
                    // Store first variable for backwards compatibility
                    values.VarStart = _position;
                }
                Advance(); // Skip '?'
                while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                    Advance();
                if (varCount == 0)
                {
                    values.VarLength = _position - values.VarStart;
                }
                varCount++;
                SkipWhitespace();
            }

            if (Peek() == ')')
                Advance(); // Skip ')'
            SkipWhitespace();
        }
        // Single variable form: ?var
        else if (Peek() == '?')
        {
            values.VarStart = _position;
            Advance(); // Skip '?'
            while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                Advance();
            values.VarLength = _position - values.VarStart;
            varCount = 1;
            SkipWhitespace();
        }
        else
        {
            return;
        }

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
                    int valueStart = _position;
                    int valueLen = ParseValuesValue();
                    if (valueLen > 0)
                    {
                        values.AddValue(valueStart, valueLen);
                        rowValueCount++;
                    }
                    else if (Peek() == ')')
                    {
                        break;
                    }
                    else
                    {
                        // Skip UNDEF
                        var span = PeekSpan(5);
                        if (span.Length >= 5 && span[..5].Equals("UNDEF", StringComparison.OrdinalIgnoreCase))
                        {
                            ConsumeKeyword("UNDEF");
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

            // Check for language tag or datatype
            SkipWhitespace();
            if (Peek() == '@')
            {
                Advance();
                while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '-'))
                    Advance();
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
            }
            return _position - valueStart;
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
        else if (IsLetter(ch))
        {
            // Boolean or prefixed name
            while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == ':'))
                Advance();
            return _position - valueStart;
        }

        return 0;
    }

    /// <summary>
    /// Parse BIND clause: BIND ( expression AS ?variable )
    /// </summary>
    private void ParseBind(ref GraphPattern pattern)
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

        // Add the bind expression
        pattern.AddBind(new BindExpr
        {
            ExprStart = exprStart,
            ExprLength = exprLength,
            VarStart = varStart,
            VarLength = varLength
        });
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
            var span = PeekSpan(6);
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

            graphClause.AddPattern(new TriplePattern
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

        pattern.AddGraphClause(graphClause);
    }
}
