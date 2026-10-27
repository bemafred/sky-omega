using System;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Patterns;

namespace SkyOmega.Mercury.Sparql.Parsing;

internal ref partial struct SparqlParser
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
        ValidateSelectSubqueryScope(selectClause, whereClause.GroupStart);

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
                var hash = ComputeSpanHash(_source.Slice(agg.AliasStart, agg.AliasLength));
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
            var hash = ComputeSpanHash(_source.Slice(start, len));
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
                    template.AddPattern(e.Current.ToTriplePattern());
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
            template = ConstructTemplateFromWhere(whereClause.GroupStart);
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
        _constructListNodeCounter = 0;  // Reset counter for each template

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

            // Parse subject - check for collection first
            Term subject;
            if (Peek() == '(')
            {
                subject = ParseConstructCollection(ref template);
            }
            else
            {
                subject = ParseTerm();
            }
            if (subject.Type == TermType.Variable && subject.Length == 0)
                break;

            SkipWhitespace();

            // Parse predicate
            var predicate = ParseTerm();

            SkipWhitespace();

            // Parse object - check for collection first
            Term obj;
            if (Peek() == '(')
            {
                obj = ParseConstructCollection(ref template);
            }
            else
            {
                obj = ParseTerm();
            }

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

                // Parse object - check for collection first
                if (Peek() == '(')
                {
                    obj = ParseConstructCollection(ref template);
                }
                else
                {
                    obj = ParseTerm();
                }

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

    /// <summary>
    /// Parse an RDF collection (list) in CONSTRUCT template and expand to rdf:first/rdf:rest patterns.
    /// Returns a blank node term representing the list head.
    /// </summary>
    private Term ParseConstructCollection(ref ConstructTemplate template)
    {
        Advance(); // Skip '('
        SkipWhitespace();

        // Handle empty collection () = rdf:nil
        if (Peek() == ')')
        {
            Advance();
            // Return synthetic rdf:nil (offset -8)
            return new Term { Type = TermType.Iri, Start = -8, Length = 0 };
        }

        // Generate first blank node for the list head
        int headNodeIndex = _constructListNodeCounter++;
        if (headNodeIndex >= 32)
            throw new SparqlParseException("Too many list nodes in CONSTRUCT template (max 32)");

        // Create head node term (synthetic blank node with offset -400 - index)
        var headNode = new Term { Type = TermType.BlankNode, Start = -(400 + headNodeIndex), Length = 0 };
        var currentNode = headNode;
        int currentNodeIndex = headNodeIndex;

        // Parse list items
        while (!IsAtEnd() && Peek() != ')')
        {
            SkipWhitespace();
            if (Peek() == ')')
                break;

            // Parse item (could be a nested collection)
            Term item;
            if (Peek() == '(')
            {
                item = ParseConstructCollection(ref template);
            }
            else
            {
                item = ParseTerm();
            }

            // Add rdf:first triple: currentNode rdf:first item
            // Predicate is synthetic rdf:first (offset -6)
            template.AddPattern(new TriplePattern
            {
                Subject = currentNode,
                Predicate = new Term { Type = TermType.Iri, Start = -6, Length = 0 },
                Object = item
            });

            SkipWhitespace();

            // Check if this is the last item
            if (Peek() == ')')
            {
                // Add rdf:rest rdf:nil triple
                // Predicate is synthetic rdf:rest (offset -7), Object is synthetic rdf:nil (offset -8)
                template.AddPattern(new TriplePattern
                {
                    Subject = currentNode,
                    Predicate = new Term { Type = TermType.Iri, Start = -7, Length = 0 },
                    Object = new Term { Type = TermType.Iri, Start = -8, Length = 0 }
                });
            }
            else
            {
                // Create next node and add rdf:rest triple to it
                int nextNodeIndex = _constructListNodeCounter++;
                if (nextNodeIndex >= 32)
                    throw new SparqlParseException("Too many list nodes in CONSTRUCT template (max 32)");

                var nextNode = new Term { Type = TermType.BlankNode, Start = -(400 + nextNodeIndex), Length = 0 };

                template.AddPattern(new TriplePattern
                {
                    Subject = currentNode,
                    Predicate = new Term { Type = TermType.Iri, Start = -7, Length = 0 },
                    Object = nextNode
                });

                currentNode = nextNode;
                currentNodeIndex = nextNodeIndex;
            }
        }

        if (Peek() == ')')
            Advance();

        return headNode;
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
        var whereClause = new WhereClause { GroupStart = -1 };

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

    private static int ComputeSpanHash(ReadOnlySpan<char> span) => Fnv1a.Hash(span);

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
        ParseSubSelectTail();
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

    /// <summary>SolutionModifier ValuesClause: moves the cursor past the sub-SELECT.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ParseSubSelectTail()
    {
        var modifiers = new SubSelect();
        ParseSubSelectSolutionModifiers(ref modifiers);
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

        subSelect.LimitOffset = ParseLimitOffsetClauses();
        SkipWhitespace();

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
            pattern.AddFilter(new FilterExpr { Start = start, Length = length });
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
            pattern.AddFilter(new FilterExpr { Start = start, Length = length });
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
    /// Parse VALUES clause: VALUES ?var { value1 value2 ... } or VALUES (?var1 ?var2) { (val1 val2) ... }
    /// Supports single variable or multiple variables with cardinality validation.
    /// UNDEF values are stored with length = -1.
    /// </summary>
    internal void ParseValues(ref GraphPattern pattern)
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
    /// [59] ServiceGraphPattern ::= 'SERVICE' 'SILENT'? VarOrIri GroupGraphPattern
    /// Parses SERVICE [SILENT] &lt;uri&gt; { patterns } for federated queries.
    /// </summary>
    internal void ParseService(ref GraphPattern pattern)
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

}
