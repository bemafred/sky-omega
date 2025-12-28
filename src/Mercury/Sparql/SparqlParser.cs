using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SkyOmega.Mercury.Sparql;

/// <summary>
/// Zero-allocation SPARQL 1.1 parser using stack-based parsing and Span&lt;T&gt;
/// Based on SPARQL 1.1 Query Language EBNF grammar
/// </summary>
public ref struct SparqlParser
{
    private ReadOnlySpan<char> _source;
    private int _position;
    
    public SparqlParser(ReadOnlySpan<char> source)
    {
        _source = source;
        _position = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsAtEnd() => _position >= _source.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char Peek() => IsAtEnd() ? '\0' : _source[_position];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char Advance() => IsAtEnd() ? '\0' : _source[_position++];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> PeekSpan(int length)
    {
        var remaining = _source.Length - _position;
        return remaining < length ? ReadOnlySpan<char>.Empty : _source.Slice(_position, length);
    }

    private void SkipWhitespace()
    {
        while (!IsAtEnd())
        {
            var ch = Peek();
            if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n')
            {
                Advance();
            }
            else if (ch == '#') // Comment
            {
                while (!IsAtEnd() && Peek() != '\n')
                    Advance();
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>
    /// [1] QueryUnit ::= Query
    /// </summary>
    public Query ParseQuery()
    {
        SkipWhitespace();
        return ParseQueryInternal();
    }

    /// <summary>
    /// [2] Query ::= Prologue ( SelectQuery | ConstructQuery | DescribeQuery | AskQuery ) ValuesClause
    /// </summary>
    private Query ParseQueryInternal()
    {
        var prologue = ParsePrologue();
        
        SkipWhitespace();
        var queryType = DetermineQueryType();
        
        return queryType switch
        {
            QueryType.Select => ParseSelectQuery(prologue),
            QueryType.Construct => ParseConstructQuery(prologue),
            QueryType.Describe => ParseDescribeQuery(prologue),
            QueryType.Ask => ParseAskQuery(prologue),
            _ => throw new SparqlParseException("Expected SELECT, CONSTRUCT, DESCRIBE, or ASK")
        };
    }

    private QueryType DetermineQueryType()
    {
        var span = PeekSpan(10);
        if (span.Length >= 6 && span[..6].Equals("SELECT", StringComparison.OrdinalIgnoreCase))
            return QueryType.Select;
        if (span.Length >= 9 && span[..9].Equals("CONSTRUCT", StringComparison.OrdinalIgnoreCase))
            return QueryType.Construct;
        if (span.Length >= 8 && span[..8].Equals("DESCRIBE", StringComparison.OrdinalIgnoreCase))
            return QueryType.Describe;
        if (span.Length >= 3 && span[..3].Equals("ASK", StringComparison.OrdinalIgnoreCase))
            return QueryType.Ask;
        
        return QueryType.Unknown;
    }

    /// <summary>
    /// [4] Prologue ::= ( BaseDecl | PrefixDecl )*
    /// </summary>
    private Prologue ParsePrologue()
    {
        var prologue = new Prologue();
        
        while (true)
        {
            SkipWhitespace();
            var span = PeekSpan(7);
            
            if (span.Length >= 4 && span[..4].Equals("BASE", StringComparison.OrdinalIgnoreCase))
            {
                ParseBaseDecl(ref prologue);
            }
            else if (span.Length >= 6 && span[..6].Equals("PREFIX", StringComparison.OrdinalIgnoreCase))
            {
                ParsePrefixDecl(ref prologue);
            }
            else
            {
                break;
            }
        }
        
        return prologue;
    }

    private void ParseBaseDecl(ref Prologue prologue)
    {
        ConsumeKeyword("BASE");
        SkipWhitespace();
        var start = _position;
        ParseIriRef();
        prologue.BaseUriStart = start;
        prologue.BaseUriLength = _position - start;
    }

    private void ParsePrefixDecl(ref Prologue prologue)
    {
        ConsumeKeyword("PREFIX");
        SkipWhitespace();
        var prefix = ParsePnameNs();
        SkipWhitespace();
        var iri = ParseIriRef();
        prologue.AddPrefix(prefix, iri);
    }

    /// <summary>
    /// [7] SelectQuery ::= SelectClause DatasetClause* WhereClause SolutionModifier
    /// </summary>
    private Query ParseSelectQuery(Prologue prologue)
    {
        var selectClause = ParseSelectClause();
        var datasets = ParseDatasetClauses();
        var whereClause = ParseWhereClause();
        var modifier = ParseSolutionModifier();
        
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

    private Query ParseConstructQuery(Prologue prologue)
    {
        ConsumeKeyword("CONSTRUCT");
        SkipWhitespace();
        
        var template = ParseConstructTemplate();
        var datasets = ParseDatasetClauses();
        var whereClause = ParseWhereClause();
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
                        clause.AddAggregate(agg);
                    }
                }
                // Check for variable
                else if (Peek() == '?')
                {
                    // Skip over variable (not stored currently, just parsed)
                    Advance();
                    while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                        Advance();
                }
                else
                {
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
        var span = PeekSpan(6);
        if (span.Length >= 5 && span[..5].Equals("COUNT", StringComparison.OrdinalIgnoreCase))
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
        else
        {
            // Not a recognized aggregate, skip to closing paren
            while (!IsAtEnd() && Peek() != ')')
                Advance();
            if (Peek() == ')') Advance();
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

    private DatasetClause[] ParseDatasetClauses()
    {
        // Simplified - return empty for now
        return Array.Empty<DatasetClause>();
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
            return;
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

            // Check for GRAPH
            if (span.Length >= 5 && span[..5].Equals("GRAPH", StringComparison.OrdinalIgnoreCase))
            {
                ParseGraph(ref pattern);
                continue;
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

            // Check for nested FILTER inside OPTIONAL
            var span = PeekSpan(6);
            if (span.Length >= 6 && span[..6].Equals("FILTER", StringComparison.OrdinalIgnoreCase))
            {
                ParseFilter(ref pattern);
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
    /// Parse VALUES clause: VALUES ?var { value1 value2 ... }
    /// Supports single variable with multiple values.
    /// </summary>
    private void ParseValues(ref GraphPattern pattern)
    {
        ConsumeKeyword("VALUES");
        SkipWhitespace();

        // Parse variable
        if (Peek() != '?')
            return;

        var values = new ValuesClause();
        values.VarStart = _position;

        Advance(); // Skip '?'
        while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
            Advance();

        values.VarLength = _position - values.VarStart;

        SkipWhitespace();

        // Expect '{'
        if (Peek() != '{')
            return;

        Advance(); // Skip '{'
        SkipWhitespace();

        // Parse values
        while (!IsAtEnd() && Peek() != '}')
        {
            SkipWhitespace();

            int valueStart = _position;
            int valueLen = 0;

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
                valueLen = _position - valueStart;
            }
            else if (ch == '<')
            {
                // IRI
                Advance();
                while (!IsAtEnd() && Peek() != '>')
                    Advance();
                if (!IsAtEnd()) Advance(); // Skip '>'
                valueLen = _position - valueStart;
            }
            else if (IsDigit(ch) || ch == '-' || ch == '+')
            {
                // Numeric literal
                if (ch == '-' || ch == '+') Advance();
                while (!IsAtEnd() && (IsDigit(Peek()) || Peek() == '.'))
                    Advance();
                valueLen = _position - valueStart;
            }
            else if (IsLetter(ch))
            {
                // Boolean or prefixed name
                while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == ':'))
                    Advance();
                valueLen = _position - valueStart;
            }
            else
            {
                break;
            }

            if (valueLen > 0)
            {
                values.AddValue(valueStart, valueLen);
            }

            SkipWhitespace();
        }

        SkipWhitespace();
        if (Peek() == '}')
            Advance(); // Skip '}'

        pattern.SetValues(values);
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

    /// <summary>
    /// Parse a triple pattern and add it as optional.
    /// </summary>
    private bool TryParseOptionalTriplePattern(ref GraphPattern pattern)
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

        pattern.AddOptionalPattern(new TriplePattern
        {
            Subject = subject,
            Predicate = predicate,
            Object = obj
        });

        return true;
    }

    /// <summary>
    /// Try to parse a triple pattern (subject predicate object)
    /// Supports property paths in the predicate position.
    /// </summary>
    private bool TryParseTriplePattern(ref GraphPattern pattern)
    {
        SkipWhitespace();

        // Check if we're at end of pattern
        if (IsAtEnd() || Peek() == '}')
            return false;

        var subject = ParseTerm();
        if (subject.Type == TermType.Variable && subject.Length == 0)
            return false;

        SkipWhitespace();

        // Parse predicate - may be a property path
        var (predicate, path) = ParsePredicateOrPath();

        SkipWhitespace();
        var obj = ParseTerm();

        pattern.AddPattern(new TriplePattern
        {
            Subject = subject,
            Predicate = predicate,
            Object = obj,
            Path = path
        });

        return true;
    }

    /// <summary>
    /// Parse a predicate which may be a property path expression.
    /// Returns both the simple Term (for backwards compatibility) and a PropertyPath.
    /// </summary>
    private (Term predicate, PropertyPath path) ParsePredicateOrPath()
    {
        SkipWhitespace();
        var ch = Peek();

        // Check for inverse path: ^predicate
        if (ch == '^')
        {
            Advance(); // Skip '^'
            SkipWhitespace();
            var iri = ParseTerm();
            return (iri, PropertyPath.Inverse(iri));
        }

        // Parse the base term
        var term = ParseTerm();

        // Check for path modifier after the term
        SkipWhitespace();
        ch = Peek();

        if (ch == '*')
        {
            Advance();
            return (term, PropertyPath.ZeroOrMore(term));
        }

        if (ch == '+')
        {
            Advance();
            return (term, PropertyPath.OneOrMore(term));
        }

        if (ch == '?')
        {
            // Need to distinguish from variable - '?' followed by letter is variable
            var next = PeekAt(1);
            if (!IsLetter(next) && next != '_')
            {
                Advance();
                return (term, PropertyPath.ZeroOrOne(term));
            }
        }

        // Check for sequence or alternative (simple case: iri1/iri2 or iri1|iri2)
        if (ch == '/')
        {
            var leftStart = term.Start;
            var leftLength = term.Length;
            Advance(); // Skip '/'
            SkipWhitespace();
            var right = ParseTerm();
            return (term, PropertyPath.Sequence(leftStart, leftLength, right.Start, right.Length));
        }

        if (ch == '|')
        {
            var leftStart = term.Start;
            var leftLength = term.Length;
            Advance(); // Skip '|'
            SkipWhitespace();
            var right = ParseTerm();
            return (term, PropertyPath.Alternative(leftStart, leftLength, right.Start, right.Length));
        }

        // Simple predicate - no path
        return (term, default);
    }

    /// <summary>
    /// Parse a term (variable, IRI, or literal)
    /// </summary>
    private Term ParseTerm()
    {
        SkipWhitespace();
        var ch = Peek();

        // Variable: ?name or $name
        if (ch == '?' || ch == '$')
        {
            return ParseVariable();
        }

        // IRI: <uri>
        if (ch == '<')
        {
            return ParseTermIriRef();
        }

        // Prefixed name: prefix:local
        if (IsLetter(ch))
        {
            return ParsePrefixedNameOrKeyword();
        }

        // Literal: "string" or 'string'
        if (ch == '"' || ch == '\'')
        {
            return ParseLiteral();
        }

        // Numeric literal
        if (IsDigit(ch) || ch == '-' || ch == '+')
        {
            return ParseNumericLiteral();
        }

        // Blank node: _:name
        if (ch == '_')
        {
            return ParseBlankNode();
        }

        // 'a' as shorthand for rdf:type
        if (ch == 'a' && !IsLetterOrDigit(PeekAt(1)))
        {
            Advance();
            return Term.Iri(_position - 1, 1); // 'a' shorthand
        }

        return default;
    }

    /// <summary>
    /// Parse a variable: ?name or $name
    /// </summary>
    private Term ParseVariable()
    {
        var start = _position;
        Advance(); // Skip ? or $

        while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
            Advance();

        return Term.Variable(start, _position - start);
    }

    /// <summary>
    /// Parse an IRI reference: &lt;uri&gt; (returns Term)
    /// Includes angle brackets in the term for matching against stored IRIs.
    /// </summary>
    private Term ParseTermIriRef()
    {
        // Include angle brackets in the term for matching against stored IRIs
        var start = _position;
        Advance(); // Skip '<'

        while (!IsAtEnd() && Peek() != '>')
            Advance();

        if (Peek() == '>')
            Advance(); // Include '>'

        var length = _position - start;
        return Term.Iri(start, length);
    }

    /// <summary>
    /// Parse a prefixed name or check for keyword
    /// </summary>
    private Term ParsePrefixedNameOrKeyword()
    {
        var start = _position;

        // Read prefix part
        while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
            Advance();

        // Check for colon (prefixed name)
        if (Peek() == ':')
        {
            Advance(); // Skip ':'

            // Read local part
            while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_' || Peek() == '-' || Peek() == '.'))
            {
                // Don't include trailing dot
                if (Peek() == '.' && !IsLetterOrDigit(PeekAt(1)))
                    break;
                Advance();
            }

            return Term.Iri(start, _position - start);
        }

        // Not a prefixed name - might be a keyword, reset
        // Check if this looks like a keyword we should skip
        var word = _source.Slice(start, _position - start);
        if (word.Equals("FILTER", StringComparison.OrdinalIgnoreCase) ||
            word.Equals("OPTIONAL", StringComparison.OrdinalIgnoreCase) ||
            word.Equals("UNION", StringComparison.OrdinalIgnoreCase))
        {
            _position = start; // Reset
            return default;
        }

        // Treat as bare word (error in strict parsing, but we'll be lenient)
        return Term.Iri(start, _position - start);
    }

    /// <summary>
    /// Parse a string literal
    /// </summary>
    private Term ParseLiteral()
    {
        var quote = Peek();
        var start = _position;

        Advance(); // Skip opening quote

        // Check for long string (""" or ''')
        if (Peek() == quote && PeekAt(1) == quote)
        {
            Advance();
            Advance();
            // Long string - read until closing """
            while (!IsAtEnd())
            {
                if (Peek() == quote && PeekAt(1) == quote && PeekAt(2) == quote)
                {
                    Advance();
                    Advance();
                    Advance();
                    break;
                }
                Advance();
            }
        }
        else
        {
            // Short string - read until closing quote
            while (!IsAtEnd() && Peek() != quote)
            {
                if (Peek() == '\\')
                    Advance(); // Skip escape
                Advance();
            }

            if (Peek() == quote)
                Advance(); // Skip closing quote
        }

        // Check for language tag @en or datatype ^^<type>
        if (Peek() == '@')
        {
            Advance();
            while (!IsAtEnd() && (IsLetter(Peek()) || Peek() == '-'))
                Advance();
        }
        else if (Peek() == '^' && PeekAt(1) == '^')
        {
            Advance();
            Advance();
            ParseTerm(); // Parse datatype IRI
        }

        return Term.Literal(start, _position - start);
    }

    /// <summary>
    /// Parse a numeric literal
    /// </summary>
    private Term ParseNumericLiteral()
    {
        var start = _position;

        if (Peek() == '-' || Peek() == '+')
            Advance();

        while (!IsAtEnd() && IsDigit(Peek()))
            Advance();

        // Decimal or double
        if (Peek() == '.')
        {
            Advance();
            while (!IsAtEnd() && IsDigit(Peek()))
                Advance();
        }

        // Exponent
        if (Peek() == 'e' || Peek() == 'E')
        {
            Advance();
            if (Peek() == '-' || Peek() == '+')
                Advance();
            while (!IsAtEnd() && IsDigit(Peek()))
                Advance();
        }

        return Term.Literal(start, _position - start);
    }

    /// <summary>
    /// Parse a blank node: _:name
    /// </summary>
    private Term ParseBlankNode()
    {
        var start = _position;
        Advance(); // Skip '_'

        if (Peek() == ':')
        {
            Advance(); // Skip ':'
            while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_' || Peek() == '-' || Peek() == '.'))
            {
                if (Peek() == '.' && !IsLetterOrDigit(PeekAt(1)))
                    break;
                Advance();
            }
        }

        return Term.BlankNode(start, _position - start);
    }

    /// <summary>
    /// Skip until closing brace (for unsupported constructs)
    /// </summary>
    private void SkipUntilClosingBrace()
    {
        var depth = 0;
        while (!IsAtEnd())
        {
            var ch = Advance();
            if (ch == '{') depth++;
            else if (ch == '}')
            {
                if (depth == 0) break;
                depth--;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char PeekAt(int offset)
    {
        var pos = _position + offset;
        return pos >= _source.Length ? '\0' : _source[pos];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsLetter(char ch) => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDigit(char ch) => ch >= '0' && ch <= '9';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsLetterOrDigit(char ch) => IsLetter(ch) || IsDigit(ch);

    private SolutionModifier ParseSolutionModifier()
    {
        var modifier = new SolutionModifier();
        SkipWhitespace();

        // Parse GROUP BY (must come before ORDER BY)
        var span = PeekSpan(8);
        if (span.Length >= 5 && span[..5].Equals("GROUP", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("GROUP");
            SkipWhitespace();
            ConsumeKeyword("BY");
            SkipWhitespace();

            modifier.GroupBy = ParseGroupByClause();
        }

        // Parse HAVING (must come after GROUP BY, before ORDER BY)
        SkipWhitespace();
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
                modifier.Having = new HavingClause { ExpressionStart = start, ExpressionLength = length };

                if (Peek() == ')')
                    Advance(); // Skip ')'
            }
        }

        // Parse ORDER BY
        SkipWhitespace();
        span = PeekSpan(8);
        if (span.Length >= 5 && span[..5].Equals("ORDER", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("ORDER");
            SkipWhitespace();
            ConsumeKeyword("BY");
            SkipWhitespace();
            
            modifier.OrderBy = ParseOrderByClause();
        }
        
        // Parse LIMIT
        SkipWhitespace();
        span = PeekSpan(5);
        if (span.Length >= 5 && span[..5].Equals("LIMIT", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("LIMIT");
            SkipWhitespace();
            modifier.Limit = ParseInteger();
        }
        
        // Parse OFFSET
        SkipWhitespace();
        span = PeekSpan(6);
        if (span.Length >= 6 && span[..6].Equals("OFFSET", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("OFFSET");
            SkipWhitespace();
            modifier.Offset = ParseInteger();
        }
        
        return modifier;
    }

    private GroupByClause ParseGroupByClause()
    {
        var clause = new GroupByClause();

        // Parse one or more grouping variables
        while (!IsAtEnd() && clause.Count < GroupByClause.MaxVariables)
        {
            SkipWhitespace();

            // Check for variable
            if (Peek() != '?')
                break;

            var start = _position;
            Advance(); // Skip '?'

            // Parse variable name
            while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                Advance();

            var length = _position - start;
            if (length > 1) // Must have at least one character after '?'
            {
                clause.AddVariable(start, length);
            }

            SkipWhitespace();
        }

        return clause;
    }

    private OrderByClause ParseOrderByClause()
    {
        var clause = new OrderByClause();

        // Parse one or more order conditions
        while (!IsAtEnd() && clause.Count < 4)
        {
            SkipWhitespace();

            var direction = OrderDirection.Ascending;
            bool hasDirectionKeyword = false;

            // Check for ASC/DESC keyword with parenthesis: ASC(?var) or DESC(?var)
            var span = PeekSpan(4);
            if (span.Length >= 3 && span[..3].Equals("ASC", StringComparison.OrdinalIgnoreCase))
            {
                ConsumeKeyword("ASC");
                SkipWhitespace();
                hasDirectionKeyword = true;
            }
            else if (span.Length >= 4 && span[..4].Equals("DESC", StringComparison.OrdinalIgnoreCase))
            {
                ConsumeKeyword("DESC");
                SkipWhitespace();
                direction = OrderDirection.Descending;
                hasDirectionKeyword = true;
            }

            // If we had ASC/DESC, expect parenthesis around variable
            if (hasDirectionKeyword)
            {
                if (Peek() == '(')
                {
                    Advance(); // Skip '('
                    SkipWhitespace();
                }
            }

            // Parse variable
            if (Peek() == '?')
            {
                var varStart = _position;
                Advance(); // Skip '?'
                while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                    Advance();
                var varLength = _position - varStart;

                clause.AddCondition(varStart, varLength, direction);

                // Skip closing parenthesis if we had direction keyword
                if (hasDirectionKeyword)
                {
                    SkipWhitespace();
                    if (Peek() == ')')
                        Advance();
                }
            }
            else
            {
                // Not a variable - stop parsing order conditions
                break;
            }

            SkipWhitespace();

            // Check if next token is LIMIT, OFFSET, or end - if so, stop
            span = PeekSpan(6);
            if (span.Length >= 5 && span[..5].Equals("LIMIT", StringComparison.OrdinalIgnoreCase))
                break;
            if (span.Length >= 6 && span[..6].Equals("OFFSET", StringComparison.OrdinalIgnoreCase))
                break;

            // Check for ASC/DESC or variable to continue
            if (span.Length >= 3 && span[..3].Equals("ASC", StringComparison.OrdinalIgnoreCase))
                continue;
            if (span.Length >= 4 && span[..4].Equals("DESC", StringComparison.OrdinalIgnoreCase))
                continue;
            if (Peek() == '?')
                continue;

            break;
        }

        return clause;
    }

    private int ParseInteger()
    {
        var start = _position;
        
        while (!IsAtEnd() && char.IsDigit(Peek()))
            Advance();
        
        var span = _source.Slice(start, _position - start);
        return int.TryParse(span, out var result) ? result : 0;
    }

    private ReadOnlySpan<char> ParseIriRef()
    {
        if (Peek() != '<')
            throw new SparqlParseException("Expected '<' for IRI reference");
        
        var start = _position;
        Advance(); // Skip '<'
        
        while (!IsAtEnd() && Peek() != '>')
            Advance();
        
        if (IsAtEnd())
            throw new SparqlParseException("Unterminated IRI reference");
        
        var end = _position;
        Advance(); // Skip '>'
        
        return _source.Slice(start, end - start + 1);
    }

    private ReadOnlySpan<char> ParsePnameNs()
    {
        var start = _position;
        
        while (!IsAtEnd() && Peek() != ':')
            Advance();
        
        if (IsAtEnd())
            throw new SparqlParseException("Expected ':' in prefix name");
        
        Advance(); // Skip ':'
        return _source.Slice(start, _position - start);
    }

    private void ConsumeKeyword(ReadOnlySpan<char> keyword)
    {
        var span = PeekSpan(keyword.Length);
        if (!span.Equals(keyword, StringComparison.OrdinalIgnoreCase))
            throw new SparqlParseException($"Expected keyword '{keyword.ToString()}'");
        
        _position += keyword.Length;
    }
}

public enum QueryType
{
    Unknown,
    Select,
    Construct,
    Describe,
    Ask
}

public struct Query
{
    public QueryType Type;
    public Prologue Prologue;
    public SelectClause SelectClause;
    public ConstructTemplate ConstructTemplate;
    public bool DescribeAll;
    public DatasetClause[] Datasets;
    public WhereClause WhereClause;
    public SolutionModifier SolutionModifier;
}

public struct Prologue
{
    private const int MaxPrefixes = 32;
    private int _prefixCount;
    private unsafe fixed byte _prefixData[2048]; // Inline storage

    public int BaseUriStart;   // Start offset in source span
    public int BaseUriLength;  // Length in source span

    public void AddPrefix(ReadOnlySpan<char> prefix, ReadOnlySpan<char> iri)
    {
        // Store prefixes in inline buffer
        _prefixCount++;
    }
}

public struct SelectClause
{
    public const int MaxAggregates = 8;
    public bool Distinct;
    public bool Reduced;
    public bool SelectAll;

    private int _aggregateCount;
    // Inline storage for up to 8 aggregate expressions
    private AggregateExpression _a0, _a1, _a2, _a3, _a4, _a5, _a6, _a7;

    public readonly int AggregateCount => _aggregateCount;
    public readonly bool HasAggregates => _aggregateCount > 0;

    public void AddAggregate(AggregateExpression agg)
    {
        if (_aggregateCount >= MaxAggregates)
            throw new SparqlParseException("Too many aggregate expressions (max 8)");

        switch (_aggregateCount)
        {
            case 0: _a0 = agg; break;
            case 1: _a1 = agg; break;
            case 2: _a2 = agg; break;
            case 3: _a3 = agg; break;
            case 4: _a4 = agg; break;
            case 5: _a5 = agg; break;
            case 6: _a6 = agg; break;
            case 7: _a7 = agg; break;
        }
        _aggregateCount++;
    }

    public readonly AggregateExpression GetAggregate(int index)
    {
        return index switch
        {
            0 => _a0,
            1 => _a1,
            2 => _a2,
            3 => _a3,
            4 => _a4,
            5 => _a5,
            6 => _a6,
            7 => _a7,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }
}

public struct AggregateExpression
{
    public AggregateFunction Function;
    public int VariableStart;   // The variable being aggregated (e.g., ?x in COUNT(?x))
    public int VariableLength;
    public int AliasStart;      // The alias (e.g., ?count in AS ?count)
    public int AliasLength;
    public bool Distinct;       // COUNT(DISTINCT ?x)
}

public enum AggregateFunction
{
    None = 0,
    Count,
    Sum,
    Avg,
    Min,
    Max
}

public struct DatasetClause
{
}

public struct WhereClause
{
    public GraphPattern Pattern;
}

/// <summary>
/// A graph pattern containing triple patterns and filters.
/// Uses inline storage for zero-allocation parsing.
/// </summary>
public struct GraphPattern
{
    public const int MaxTriplePatterns = 32;
    public const int MaxFilters = 16;
    public const int MaxBinds = 8;
    public const int MaxMinusPatterns = 8;
    public const int MaxExistsFilters = 4;
    public const int MaxGraphClauses = 4;
    public const int MaxSubQueries = 2;

    private int _patternCount;
    private int _filterCount;
    private int _bindCount;
    private int _minusPatternCount;
    private int _existsFilterCount;
    private int _graphClauseCount;
    private int _subQueryCount;
    private uint _optionalFlags; // Bitmask: bit N = 1 means pattern N is optional
    private int _unionStartIndex; // If > 0, patterns from this index are the UNION branch

    // Inline storage for triple patterns (32 * 24 bytes = 768 bytes)
    private TriplePattern _p0, _p1, _p2, _p3, _p4, _p5, _p6, _p7;
    private TriplePattern _p8, _p9, _p10, _p11, _p12, _p13, _p14, _p15;
    private TriplePattern _p16, _p17, _p18, _p19, _p20, _p21, _p22, _p23;
    private TriplePattern _p24, _p25, _p26, _p27, _p28, _p29, _p30, _p31;

    // Inline storage for filter expression offsets (16 * 8 bytes = 128 bytes)
    private FilterExpr _f0, _f1, _f2, _f3, _f4, _f5, _f6, _f7;
    private FilterExpr _f8, _f9, _f10, _f11, _f12, _f13, _f14, _f15;

    // Inline storage for bind expressions (8 * 16 bytes = 128 bytes)
    private BindExpr _b0, _b1, _b2, _b3, _b4, _b5, _b6, _b7;

    // Inline storage for MINUS patterns (8 * 24 bytes = 192 bytes)
    private TriplePattern _m0, _m1, _m2, _m3, _m4, _m5, _m6, _m7;

    // Inline storage for EXISTS/NOT EXISTS filters (4 * ~100 bytes)
    private ExistsFilter _e0, _e1, _e2, _e3;

    // Inline storage for GRAPH clauses (4 * ~200 bytes)
    private GraphClause _g0, _g1, _g2, _g3;

    // Inline storage for subqueries (2 * ~500 bytes)
    private SubSelect _sq0, _sq1;

    // VALUES clause storage
    private ValuesClause _values;

    public readonly int PatternCount => _patternCount;
    public readonly int FilterCount => _filterCount;
    public readonly int BindCount => _bindCount;
    public readonly int MinusPatternCount => _minusPatternCount;
    public readonly int ExistsFilterCount => _existsFilterCount;
    public readonly int GraphClauseCount => _graphClauseCount;
    public readonly int SubQueryCount => _subQueryCount;
    public readonly bool HasBinds => _bindCount > 0;
    public readonly bool HasMinus => _minusPatternCount > 0;
    public readonly bool HasExists => _existsFilterCount > 0;
    public readonly bool HasGraph => _graphClauseCount > 0;
    public readonly bool HasSubQueries => _subQueryCount > 0;
    public readonly bool HasValues => _values.HasValues;
    public readonly bool HasOptionalPatterns => _optionalFlags != 0;
    public readonly bool HasUnion => _unionStartIndex > 0;

    public readonly ValuesClause Values => _values;

    /// <summary>
    /// Count of patterns in the first branch (before UNION).
    /// </summary>
    public readonly int FirstBranchPatternCount => HasUnion ? _unionStartIndex : _patternCount;

    /// <summary>
    /// Count of patterns in the UNION branch.
    /// </summary>
    public readonly int UnionBranchPatternCount => HasUnion ? _patternCount - _unionStartIndex : 0;

    /// <summary>
    /// Get a pattern from the UNION branch.
    /// </summary>
    public readonly TriplePattern GetUnionPattern(int index) => GetPattern(_unionStartIndex + index);

    /// <summary>
    /// Count of required (non-optional) patterns.
    /// </summary>
    public readonly int RequiredPatternCount
    {
        get
        {
            int count = 0;
            // Only count patterns in first branch (before UNION)
            int limit = HasUnion ? _unionStartIndex : _patternCount;
            for (int i = 0; i < limit; i++)
            {
                if (!IsOptional(i)) count++;
            }
            return count;
        }
    }

    /// <summary>
    /// Mark the start of UNION patterns.
    /// </summary>
    public void StartUnionBranch()
    {
        _unionStartIndex = _patternCount;
    }

    public void AddPattern(TriplePattern pattern)
    {
        if (_patternCount >= MaxTriplePatterns) return;
        SetPattern(_patternCount++, pattern);
    }

    /// <summary>
    /// Add a pattern from an OPTIONAL clause.
    /// </summary>
    public void AddOptionalPattern(TriplePattern pattern)
    {
        if (_patternCount >= MaxTriplePatterns) return;
        _optionalFlags |= (1u << _patternCount);
        SetPattern(_patternCount++, pattern);
    }

    /// <summary>
    /// Check if a pattern at the given index is optional.
    /// </summary>
    public readonly bool IsOptional(int index) => (_optionalFlags & (1u << index)) != 0;

    public void AddFilter(FilterExpr filter)
    {
        if (_filterCount >= MaxFilters) return;
        SetFilter(_filterCount++, filter);
    }

    public readonly TriplePattern GetPattern(int index)
    {
        return index switch
        {
            0 => _p0, 1 => _p1, 2 => _p2, 3 => _p3,
            4 => _p4, 5 => _p5, 6 => _p6, 7 => _p7,
            8 => _p8, 9 => _p9, 10 => _p10, 11 => _p11,
            12 => _p12, 13 => _p13, 14 => _p14, 15 => _p15,
            16 => _p16, 17 => _p17, 18 => _p18, 19 => _p19,
            20 => _p20, 21 => _p21, 22 => _p22, 23 => _p23,
            24 => _p24, 25 => _p25, 26 => _p26, 27 => _p27,
            28 => _p28, 29 => _p29, 30 => _p30, 31 => _p31,
            _ => default
        };
    }

    private void SetPattern(int index, TriplePattern pattern)
    {
        switch (index)
        {
            case 0: _p0 = pattern; break; case 1: _p1 = pattern; break;
            case 2: _p2 = pattern; break; case 3: _p3 = pattern; break;
            case 4: _p4 = pattern; break; case 5: _p5 = pattern; break;
            case 6: _p6 = pattern; break; case 7: _p7 = pattern; break;
            case 8: _p8 = pattern; break; case 9: _p9 = pattern; break;
            case 10: _p10 = pattern; break; case 11: _p11 = pattern; break;
            case 12: _p12 = pattern; break; case 13: _p13 = pattern; break;
            case 14: _p14 = pattern; break; case 15: _p15 = pattern; break;
            case 16: _p16 = pattern; break; case 17: _p17 = pattern; break;
            case 18: _p18 = pattern; break; case 19: _p19 = pattern; break;
            case 20: _p20 = pattern; break; case 21: _p21 = pattern; break;
            case 22: _p22 = pattern; break; case 23: _p23 = pattern; break;
            case 24: _p24 = pattern; break; case 25: _p25 = pattern; break;
            case 26: _p26 = pattern; break; case 27: _p27 = pattern; break;
            case 28: _p28 = pattern; break; case 29: _p29 = pattern; break;
            case 30: _p30 = pattern; break; case 31: _p31 = pattern; break;
        }
    }

    public readonly FilterExpr GetFilter(int index)
    {
        return index switch
        {
            0 => _f0, 1 => _f1, 2 => _f2, 3 => _f3,
            4 => _f4, 5 => _f5, 6 => _f6, 7 => _f7,
            8 => _f8, 9 => _f9, 10 => _f10, 11 => _f11,
            12 => _f12, 13 => _f13, 14 => _f14, 15 => _f15,
            _ => default
        };
    }

    private void SetFilter(int index, FilterExpr filter)
    {
        switch (index)
        {
            case 0: _f0 = filter; break; case 1: _f1 = filter; break;
            case 2: _f2 = filter; break; case 3: _f3 = filter; break;
            case 4: _f4 = filter; break; case 5: _f5 = filter; break;
            case 6: _f6 = filter; break; case 7: _f7 = filter; break;
            case 8: _f8 = filter; break; case 9: _f9 = filter; break;
            case 10: _f10 = filter; break; case 11: _f11 = filter; break;
            case 12: _f12 = filter; break; case 13: _f13 = filter; break;
            case 14: _f14 = filter; break; case 15: _f15 = filter; break;
        }
    }

    public void AddBind(BindExpr bind)
    {
        if (_bindCount >= MaxBinds) return;
        SetBind(_bindCount++, bind);
    }

    public readonly BindExpr GetBind(int index)
    {
        return index switch
        {
            0 => _b0, 1 => _b1, 2 => _b2, 3 => _b3,
            4 => _b4, 5 => _b5, 6 => _b6, 7 => _b7,
            _ => default
        };
    }

    private void SetBind(int index, BindExpr bind)
    {
        switch (index)
        {
            case 0: _b0 = bind; break; case 1: _b1 = bind; break;
            case 2: _b2 = bind; break; case 3: _b3 = bind; break;
            case 4: _b4 = bind; break; case 5: _b5 = bind; break;
            case 6: _b6 = bind; break; case 7: _b7 = bind; break;
        }
    }

    public void AddMinusPattern(TriplePattern pattern)
    {
        if (_minusPatternCount >= MaxMinusPatterns) return;
        SetMinusPattern(_minusPatternCount++, pattern);
    }

    public readonly TriplePattern GetMinusPattern(int index)
    {
        return index switch
        {
            0 => _m0, 1 => _m1, 2 => _m2, 3 => _m3,
            4 => _m4, 5 => _m5, 6 => _m6, 7 => _m7,
            _ => default
        };
    }

    private void SetMinusPattern(int index, TriplePattern pattern)
    {
        switch (index)
        {
            case 0: _m0 = pattern; break; case 1: _m1 = pattern; break;
            case 2: _m2 = pattern; break; case 3: _m3 = pattern; break;
            case 4: _m4 = pattern; break; case 5: _m5 = pattern; break;
            case 6: _m6 = pattern; break; case 7: _m7 = pattern; break;
        }
    }

    public void AddExistsFilter(ExistsFilter filter)
    {
        if (_existsFilterCount >= MaxExistsFilters) return;
        SetExistsFilter(_existsFilterCount++, filter);
    }

    public readonly ExistsFilter GetExistsFilter(int index)
    {
        return index switch
        {
            0 => _e0,
            1 => _e1,
            2 => _e2,
            3 => _e3,
            _ => default
        };
    }

    private void SetExistsFilter(int index, ExistsFilter filter)
    {
        switch (index)
        {
            case 0: _e0 = filter; break;
            case 1: _e1 = filter; break;
            case 2: _e2 = filter; break;
            case 3: _e3 = filter; break;
        }
    }

    public void AddGraphClause(GraphClause clause)
    {
        if (_graphClauseCount >= MaxGraphClauses) return;
        SetGraphClause(_graphClauseCount++, clause);
    }

    public readonly GraphClause GetGraphClause(int index)
    {
        return index switch
        {
            0 => _g0,
            1 => _g1,
            2 => _g2,
            3 => _g3,
            _ => default
        };
    }

    private void SetGraphClause(int index, GraphClause clause)
    {
        switch (index)
        {
            case 0: _g0 = clause; break;
            case 1: _g1 = clause; break;
            case 2: _g2 = clause; break;
            case 3: _g3 = clause; break;
        }
    }

    public void SetValues(ValuesClause values)
    {
        _values = values;
    }

    public void AddSubQuery(SubSelect subQuery)
    {
        if (_subQueryCount >= MaxSubQueries) return;
        SetSubQuery(_subQueryCount++, subQuery);
    }

    public readonly SubSelect GetSubQuery(int index)
    {
        return index switch
        {
            0 => _sq0,
            1 => _sq1,
            _ => default
        };
    }

    private void SetSubQuery(int index, SubSelect subQuery)
    {
        switch (index)
        {
            case 0: _sq0 = subQuery; break;
            case 1: _sq1 = subQuery; break;
        }
    }
}

/// <summary>
/// A triple pattern with subject, predicate, and object terms.
/// Supports property paths in the predicate position.
/// </summary>
public struct TriplePattern
{
    public Term Subject;
    public Term Predicate;
    public Term Object;
    public PropertyPath Path;  // Used when HasPropertyPath is true

    public readonly bool HasPropertyPath => Path.Type != PathType.None;
}

/// <summary>
/// A property path expression for SPARQL 1.1 property paths.
/// Supports: ^iri (inverse), iri+ (one or more), iri* (zero or more),
/// iri? (zero or one), path1/path2 (sequence), path1|path2 (alternative)
/// </summary>
public struct PropertyPath
{
    public PathType Type;
    public Term Iri;           // The IRI for simple paths
    public int LeftStart;      // For sequence/alternative: offset of left operand
    public int LeftLength;
    public int RightStart;     // For sequence/alternative: offset of right operand
    public int RightLength;

    public static PropertyPath Simple(Term iri) =>
        new() { Type = PathType.None, Iri = iri };

    public static PropertyPath Inverse(Term iri) =>
        new() { Type = PathType.Inverse, Iri = iri };

    public static PropertyPath ZeroOrMore(Term iri) =>
        new() { Type = PathType.ZeroOrMore, Iri = iri };

    public static PropertyPath OneOrMore(Term iri) =>
        new() { Type = PathType.OneOrMore, Iri = iri };

    public static PropertyPath ZeroOrOne(Term iri) =>
        new() { Type = PathType.ZeroOrOne, Iri = iri };

    public static PropertyPath Sequence(int leftStart, int leftLength, int rightStart, int rightLength) =>
        new() { Type = PathType.Sequence, LeftStart = leftStart, LeftLength = leftLength, RightStart = rightStart, RightLength = rightLength };

    public static PropertyPath Alternative(int leftStart, int leftLength, int rightStart, int rightLength) =>
        new() { Type = PathType.Alternative, LeftStart = leftStart, LeftLength = leftLength, RightStart = rightStart, RightLength = rightLength };
}

/// <summary>
/// Type of property path expression.
/// </summary>
public enum PathType : byte
{
    None = 0,        // Simple IRI predicate (not a property path)
    Inverse,         // ^iri - traverse in reverse direction
    ZeroOrMore,      // iri* - zero or more hops
    OneOrMore,       // iri+ - one or more hops
    ZeroOrOne,       // iri? - zero or one hop
    Sequence,        // path1/path2 - sequence of paths
    Alternative      // path1|path2 - alternative paths
}

/// <summary>
/// A term in a triple pattern - can be a variable, IRI, literal, or blank node.
/// Uses offsets into the source string for zero-allocation.
/// </summary>
public struct Term
{
    public TermType Type;
    public int Start;   // Offset into source
    public int Length;  // Length in source

    public static Term Variable(int start, int length) =>
        new() { Type = TermType.Variable, Start = start, Length = length };

    public static Term Iri(int start, int length) =>
        new() { Type = TermType.Iri, Start = start, Length = length };

    public static Term Literal(int start, int length) =>
        new() { Type = TermType.Literal, Start = start, Length = length };

    public static Term BlankNode(int start, int length) =>
        new() { Type = TermType.BlankNode, Start = start, Length = length };

    public readonly bool IsVariable => Type == TermType.Variable;
    public readonly bool IsIri => Type == TermType.Iri;
    public readonly bool IsLiteral => Type == TermType.Literal;
    public readonly bool IsBlankNode => Type == TermType.BlankNode;
}

/// <summary>
/// Type of term in a triple pattern.
/// </summary>
public enum TermType : byte
{
    Variable,
    Iri,
    Literal,
    BlankNode
}

/// <summary>
/// A FILTER expression reference (offset into source).
/// </summary>
public struct FilterExpr
{
    public int Start;   // Offset into source (after "FILTER")
    public int Length;  // Length of expression
}

/// <summary>
/// An EXISTS or NOT EXISTS filter: FILTER [NOT] EXISTS { pattern }
/// Stores the pattern for later evaluation against the store.
/// </summary>
public struct ExistsFilter
{
    public const int MaxPatterns = 4;

    public bool Negated;         // true for NOT EXISTS, false for EXISTS
    private int _patternCount;

    // Inline storage for up to 4 triple patterns
    private TriplePattern _p0, _p1, _p2, _p3;

    public readonly int PatternCount => _patternCount;
    public readonly bool HasPatterns => _patternCount > 0;

    public void AddPattern(TriplePattern pattern)
    {
        if (_patternCount >= MaxPatterns) return;
        switch (_patternCount)
        {
            case 0: _p0 = pattern; break;
            case 1: _p1 = pattern; break;
            case 2: _p2 = pattern; break;
            case 3: _p3 = pattern; break;
        }
        _patternCount++;
    }

    public readonly TriplePattern GetPattern(int index)
    {
        return index switch
        {
            0 => _p0,
            1 => _p1,
            2 => _p2,
            3 => _p3,
            _ => default
        };
    }
}

/// <summary>
/// A GRAPH clause: GRAPH &lt;iri&gt; { patterns } or GRAPH ?var { patterns }
/// Stores the graph term and patterns to be evaluated within that graph context.
/// </summary>
public struct GraphClause
{
    public const int MaxPatterns = 8;

    public Term Graph;           // The graph IRI or variable
    private int _patternCount;

    // Inline storage for up to 8 triple patterns
    private TriplePattern _p0, _p1, _p2, _p3, _p4, _p5, _p6, _p7;

    public readonly int PatternCount => _patternCount;
    public readonly bool HasPatterns => _patternCount > 0;
    public readonly bool IsVariable => Graph.IsVariable;

    public void AddPattern(TriplePattern pattern)
    {
        if (_patternCount >= MaxPatterns) return;
        switch (_patternCount)
        {
            case 0: _p0 = pattern; break;
            case 1: _p1 = pattern; break;
            case 2: _p2 = pattern; break;
            case 3: _p3 = pattern; break;
            case 4: _p4 = pattern; break;
            case 5: _p5 = pattern; break;
            case 6: _p6 = pattern; break;
            case 7: _p7 = pattern; break;
        }
        _patternCount++;
    }

    public readonly TriplePattern GetPattern(int index)
    {
        return index switch
        {
            0 => _p0,
            1 => _p1,
            2 => _p2,
            3 => _p3,
            4 => _p4,
            5 => _p5,
            6 => _p6,
            7 => _p7,
            _ => default
        };
    }
}

/// <summary>
/// A BIND expression: BIND(expression AS ?variable)
/// </summary>
public struct BindExpr
{
    public int ExprStart;    // Start of expression
    public int ExprLength;   // Length of expression
    public int VarStart;     // Start of target variable (including ?)
    public int VarLength;    // Length of target variable
}

/// <summary>
/// A VALUES clause: VALUES ?var { value1 value2 ... }
/// Stores a single variable and up to 8 inline values.
/// </summary>
public struct ValuesClause
{
    public const int MaxValues = 8;

    public int VarStart;     // Start of variable name (including ?)
    public int VarLength;    // Length of variable name
    private int _valueCount;

    // Inline storage for value offsets (8 values * 2 ints = 64 bytes)
    private int _v0Start, _v0Len, _v1Start, _v1Len, _v2Start, _v2Len, _v3Start, _v3Len;
    private int _v4Start, _v4Len, _v5Start, _v5Len, _v6Start, _v6Len, _v7Start, _v7Len;

    public readonly int ValueCount => _valueCount;
    public readonly bool HasValues => _valueCount > 0;

    public void AddValue(int start, int length)
    {
        if (_valueCount >= MaxValues) return;
        SetValue(_valueCount++, start, length);
    }

    public readonly (int Start, int Length) GetValue(int index)
    {
        return index switch
        {
            0 => (_v0Start, _v0Len), 1 => (_v1Start, _v1Len),
            2 => (_v2Start, _v2Len), 3 => (_v3Start, _v3Len),
            4 => (_v4Start, _v4Len), 5 => (_v5Start, _v5Len),
            6 => (_v6Start, _v6Len), 7 => (_v7Start, _v7Len),
            _ => (0, 0)
        };
    }

    private void SetValue(int index, int start, int length)
    {
        switch (index)
        {
            case 0: _v0Start = start; _v0Len = length; break;
            case 1: _v1Start = start; _v1Len = length; break;
            case 2: _v2Start = start; _v2Len = length; break;
            case 3: _v3Start = start; _v3Len = length; break;
            case 4: _v4Start = start; _v4Len = length; break;
            case 5: _v5Start = start; _v5Len = length; break;
            case 6: _v6Start = start; _v6Len = length; break;
            case 7: _v7Start = start; _v7Len = length; break;
        }
    }
}

/// <summary>
/// A subquery: { SELECT ... WHERE { ... } } inside an outer WHERE clause.
/// Only projected variables from the subquery are visible to the outer query.
/// </summary>
public struct SubSelect
{
    public const int MaxProjectedVars = 8;
    public const int MaxPatterns = 16;
    public const int MaxFilters = 8;

    // SELECT clause flags
    public bool Distinct;
    public bool SelectAll;  // SELECT * means project all

    // Projected variables
    private int _projectedVarCount;
    private int _pv0Start, _pv0Len, _pv1Start, _pv1Len, _pv2Start, _pv2Len, _pv3Start, _pv3Len;
    private int _pv4Start, _pv4Len, _pv5Start, _pv5Len, _pv6Start, _pv6Len, _pv7Start, _pv7Len;

    // Triple patterns
    private int _patternCount;
    private TriplePattern _p0, _p1, _p2, _p3, _p4, _p5, _p6, _p7;
    private TriplePattern _p8, _p9, _p10, _p11, _p12, _p13, _p14, _p15;

    // Filters
    private int _filterCount;
    private FilterExpr _f0, _f1, _f2, _f3, _f4, _f5, _f6, _f7;

    // Solution modifiers
    public int Limit;
    public int Offset;
    public OrderByClause OrderBy;

    public readonly int ProjectedVarCount => _projectedVarCount;
    public readonly int PatternCount => _patternCount;
    public readonly int FilterCount => _filterCount;
    public readonly bool HasPatterns => _patternCount > 0;
    public readonly bool HasFilters => _filterCount > 0;
    public readonly bool HasOrderBy => OrderBy.HasOrderBy;

    public void AddProjectedVariable(int start, int length)
    {
        if (_projectedVarCount >= MaxProjectedVars) return;
        SetProjectedVariable(_projectedVarCount++, start, length);
    }

    public readonly (int Start, int Length) GetProjectedVariable(int index)
    {
        return index switch
        {
            0 => (_pv0Start, _pv0Len), 1 => (_pv1Start, _pv1Len),
            2 => (_pv2Start, _pv2Len), 3 => (_pv3Start, _pv3Len),
            4 => (_pv4Start, _pv4Len), 5 => (_pv5Start, _pv5Len),
            6 => (_pv6Start, _pv6Len), 7 => (_pv7Start, _pv7Len),
            _ => (0, 0)
        };
    }

    private void SetProjectedVariable(int index, int start, int length)
    {
        switch (index)
        {
            case 0: _pv0Start = start; _pv0Len = length; break;
            case 1: _pv1Start = start; _pv1Len = length; break;
            case 2: _pv2Start = start; _pv2Len = length; break;
            case 3: _pv3Start = start; _pv3Len = length; break;
            case 4: _pv4Start = start; _pv4Len = length; break;
            case 5: _pv5Start = start; _pv5Len = length; break;
            case 6: _pv6Start = start; _pv6Len = length; break;
            case 7: _pv7Start = start; _pv7Len = length; break;
        }
    }

    public void AddPattern(TriplePattern pattern)
    {
        if (_patternCount >= MaxPatterns) return;
        SetPattern(_patternCount++, pattern);
    }

    public readonly TriplePattern GetPattern(int index)
    {
        return index switch
        {
            0 => _p0, 1 => _p1, 2 => _p2, 3 => _p3,
            4 => _p4, 5 => _p5, 6 => _p6, 7 => _p7,
            8 => _p8, 9 => _p9, 10 => _p10, 11 => _p11,
            12 => _p12, 13 => _p13, 14 => _p14, 15 => _p15,
            _ => default
        };
    }

    private void SetPattern(int index, TriplePattern pattern)
    {
        switch (index)
        {
            case 0: _p0 = pattern; break; case 1: _p1 = pattern; break;
            case 2: _p2 = pattern; break; case 3: _p3 = pattern; break;
            case 4: _p4 = pattern; break; case 5: _p5 = pattern; break;
            case 6: _p6 = pattern; break; case 7: _p7 = pattern; break;
            case 8: _p8 = pattern; break; case 9: _p9 = pattern; break;
            case 10: _p10 = pattern; break; case 11: _p11 = pattern; break;
            case 12: _p12 = pattern; break; case 13: _p13 = pattern; break;
            case 14: _p14 = pattern; break; case 15: _p15 = pattern; break;
        }
    }

    public void AddFilter(FilterExpr filter)
    {
        if (_filterCount >= MaxFilters) return;
        SetFilter(_filterCount++, filter);
    }

    public readonly FilterExpr GetFilter(int index)
    {
        return index switch
        {
            0 => _f0, 1 => _f1, 2 => _f2, 3 => _f3,
            4 => _f4, 5 => _f5, 6 => _f6, 7 => _f7,
            _ => default
        };
    }

    private void SetFilter(int index, FilterExpr filter)
    {
        switch (index)
        {
            case 0: _f0 = filter; break; case 1: _f1 = filter; break;
            case 2: _f2 = filter; break; case 3: _f3 = filter; break;
            case 4: _f4 = filter; break; case 5: _f5 = filter; break;
            case 6: _f6 = filter; break; case 7: _f7 = filter; break;
        }
    }
}

public struct ConstructTemplate
{
    public const int MaxPatterns = 16;
    private int _patternCount;

    // Inline storage for up to 16 template triple patterns
    private TriplePattern _p0, _p1, _p2, _p3, _p4, _p5, _p6, _p7;
    private TriplePattern _p8, _p9, _p10, _p11, _p12, _p13, _p14, _p15;

    public readonly int PatternCount => _patternCount;
    public readonly bool HasPatterns => _patternCount > 0;

    public void AddPattern(TriplePattern pattern)
    {
        if (_patternCount >= MaxPatterns)
            throw new SparqlParseException("Too many patterns in CONSTRUCT template (max 16)");

        switch (_patternCount)
        {
            case 0: _p0 = pattern; break;
            case 1: _p1 = pattern; break;
            case 2: _p2 = pattern; break;
            case 3: _p3 = pattern; break;
            case 4: _p4 = pattern; break;
            case 5: _p5 = pattern; break;
            case 6: _p6 = pattern; break;
            case 7: _p7 = pattern; break;
            case 8: _p8 = pattern; break;
            case 9: _p9 = pattern; break;
            case 10: _p10 = pattern; break;
            case 11: _p11 = pattern; break;
            case 12: _p12 = pattern; break;
            case 13: _p13 = pattern; break;
            case 14: _p14 = pattern; break;
            case 15: _p15 = pattern; break;
        }
        _patternCount++;
    }

    public readonly TriplePattern GetPattern(int index)
    {
        return index switch
        {
            0 => _p0,
            1 => _p1,
            2 => _p2,
            3 => _p3,
            4 => _p4,
            5 => _p5,
            6 => _p6,
            7 => _p7,
            8 => _p8,
            9 => _p9,
            10 => _p10,
            11 => _p11,
            12 => _p12,
            13 => _p13,
            14 => _p14,
            15 => _p15,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }
}

public struct SolutionModifier
{
    public GroupByClause GroupBy;
    public HavingClause Having;
    public OrderByClause OrderBy;
    public int Limit;
    public int Offset;
}

public struct HavingClause
{
    public int ExpressionStart;   // Start offset of HAVING expression in source
    public int ExpressionLength;  // Length of expression

    public readonly bool HasHaving => ExpressionLength > 0;
}

public struct GroupByClause
{
    public const int MaxVariables = 8;
    private int _count;

    // Inline storage for up to 8 grouping variables (start, length pairs)
    private int _v0Start, _v0Len, _v1Start, _v1Len, _v2Start, _v2Len, _v3Start, _v3Len;
    private int _v4Start, _v4Len, _v5Start, _v5Len, _v6Start, _v6Len, _v7Start, _v7Len;

    public readonly int Count => _count;
    public readonly bool HasGroupBy => _count > 0;

    public void AddVariable(int start, int length)
    {
        if (_count >= MaxVariables)
            throw new SparqlParseException("Too many GROUP BY variables (max 8)");

        switch (_count)
        {
            case 0: _v0Start = start; _v0Len = length; break;
            case 1: _v1Start = start; _v1Len = length; break;
            case 2: _v2Start = start; _v2Len = length; break;
            case 3: _v3Start = start; _v3Len = length; break;
            case 4: _v4Start = start; _v4Len = length; break;
            case 5: _v5Start = start; _v5Len = length; break;
            case 6: _v6Start = start; _v6Len = length; break;
            case 7: _v7Start = start; _v7Len = length; break;
        }
        _count++;
    }

    public readonly (int Start, int Length) GetVariable(int index)
    {
        return index switch
        {
            0 => (_v0Start, _v0Len),
            1 => (_v1Start, _v1Len),
            2 => (_v2Start, _v2Len),
            3 => (_v3Start, _v3Len),
            4 => (_v4Start, _v4Len),
            5 => (_v5Start, _v5Len),
            6 => (_v6Start, _v6Len),
            7 => (_v7Start, _v7Len),
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }
}

public struct OrderByClause
{
    // Store up to 4 order conditions inline
    private OrderCondition _cond0, _cond1, _cond2, _cond3;
    private int _count;

    public readonly int Count => _count;
    public readonly bool HasOrderBy => _count > 0;

    public void AddCondition(int variableStart, int variableLength, OrderDirection direction)
    {
        var cond = new OrderCondition(variableStart, variableLength, direction);
        switch (_count)
        {
            case 0: _cond0 = cond; break;
            case 1: _cond1 = cond; break;
            case 2: _cond2 = cond; break;
            case 3: _cond3 = cond; break;
            default: return; // Ignore beyond 4
        }
        _count++;
    }

    public readonly OrderCondition GetCondition(int index)
    {
        return index switch
        {
            0 => _cond0,
            1 => _cond1,
            2 => _cond2,
            3 => _cond3,
            _ => default
        };
    }
}

public readonly struct OrderCondition
{
    public readonly int VariableStart;
    public readonly int VariableLength;
    public readonly OrderDirection Direction;

    public OrderCondition(int start, int length, OrderDirection direction)
    {
        VariableStart = start;
        VariableLength = length;
        Direction = direction;
    }
}

public enum OrderDirection
{
    Ascending,
    Descending
}

/// <summary>
/// Binding table for variable bindings during query execution.
/// Zero-allocation design using stackalloc buffers.
/// </summary>
public ref struct BindingTable
{
    private Span<Binding> _bindings;
    private int _count;
    private Span<char> _stringBuffer;
    private int _stringOffset;

    public BindingTable(Span<Binding> storage)
    {
        _bindings = storage;
        _count = 0;
        _stringBuffer = Span<char>.Empty;
        _stringOffset = 0;
    }

    public BindingTable(Span<Binding> storage, Span<char> stringBuffer)
    {
        _bindings = storage;
        _count = 0;
        _stringBuffer = stringBuffer;
        _stringOffset = 0;
    }

    /// <summary>
    /// Bind an integer value to a variable.
    /// </summary>
    public void Bind(ReadOnlySpan<char> variableName, long value)
    {
        if (_count >= _bindings.Length) return;

        // Format the value as string and store in buffer
        Span<char> temp = stackalloc char[24];
        if (!value.TryFormat(temp, out int written))
            return;

        if (_stringOffset + written > _stringBuffer.Length) return;

        temp.Slice(0, written).CopyTo(_stringBuffer.Slice(_stringOffset));

        ref var binding = ref _bindings[_count++];
        binding.VariableNameHash = ComputeHash(variableName);
        binding.Type = BindingValueType.Integer;
        binding.IntegerValue = value;
        binding.StringOffset = _stringOffset;
        binding.StringLength = written;
        _stringOffset += written;
    }

    /// <summary>
    /// Bind a double value to a variable.
    /// </summary>
    public void Bind(ReadOnlySpan<char> variableName, double value)
    {
        if (_count >= _bindings.Length) return;

        // Format the value as string and store in buffer
        Span<char> temp = stackalloc char[32];
        if (!value.TryFormat(temp, out int written))
            return;

        if (_stringOffset + written > _stringBuffer.Length) return;

        temp.Slice(0, written).CopyTo(_stringBuffer.Slice(_stringOffset));

        ref var binding = ref _bindings[_count++];
        binding.VariableNameHash = ComputeHash(variableName);
        binding.Type = BindingValueType.Double;
        binding.DoubleValue = value;
        binding.StringOffset = _stringOffset;
        binding.StringLength = written;
        _stringOffset += written;
    }

    /// <summary>
    /// Bind a boolean value to a variable.
    /// </summary>
    public void Bind(ReadOnlySpan<char> variableName, bool value)
    {
        if (_count >= _bindings.Length) return;

        // Store string representation
        var str = value ? "true" : "false";
        var len = str.Length;
        if (_stringOffset + len > _stringBuffer.Length) return;

        str.AsSpan().CopyTo(_stringBuffer.Slice(_stringOffset));

        ref var binding = ref _bindings[_count++];
        binding.VariableNameHash = ComputeHash(variableName);
        binding.Type = BindingValueType.Boolean;
        binding.BooleanValue = value;
        binding.StringOffset = _stringOffset;
        binding.StringLength = len;
        _stringOffset += len;
    }

    /// <summary>
    /// Bind a string value to a variable.
    /// Copies the string into the internal buffer.
    /// </summary>
    public void Bind(ReadOnlySpan<char> variableName, ReadOnlySpan<char> value)
    {
        if (_count >= _bindings.Length) return;
        if (_stringOffset + value.Length > _stringBuffer.Length) return;

        // Copy string to buffer
        value.CopyTo(_stringBuffer.Slice(_stringOffset));

        ref var binding = ref _bindings[_count++];
        binding.VariableNameHash = ComputeHash(variableName);
        binding.Type = BindingValueType.String;
        binding.StringOffset = _stringOffset;
        binding.StringLength = value.Length;

        _stringOffset += value.Length;
    }

    /// <summary>
    /// Bind a URI value to a variable.
    /// Copies the URI into the internal buffer.
    /// </summary>
    public void BindUri(ReadOnlySpan<char> variableName, ReadOnlySpan<char> value)
    {
        if (_count >= _bindings.Length) return;
        if (_stringOffset + value.Length > _stringBuffer.Length) return;

        // Copy string to buffer
        value.CopyTo(_stringBuffer.Slice(_stringOffset));

        ref var binding = ref _bindings[_count++];
        binding.VariableNameHash = ComputeHash(variableName);
        binding.Type = BindingValueType.Uri;
        binding.StringOffset = _stringOffset;
        binding.StringLength = value.Length;

        _stringOffset += value.Length;
    }

    /// <summary>
    /// Bind a string value using a pre-computed hash.
    /// Used for ORDER BY result reconstruction.
    /// </summary>
    public void BindWithHash(int variableNameHash, ReadOnlySpan<char> value)
    {
        if (_count >= _bindings.Length) return;
        if (_stringOffset + value.Length > _stringBuffer.Length) return;

        // Copy string to buffer
        value.CopyTo(_stringBuffer.Slice(_stringOffset));

        ref var binding = ref _bindings[_count++];
        binding.VariableNameHash = variableNameHash;
        binding.Type = BindingValueType.String;
        binding.StringOffset = _stringOffset;
        binding.StringLength = value.Length;

        _stringOffset += value.Length;
    }

    /// <summary>
    /// Try to get the binding for a variable.
    /// Returns the index if found, -1 otherwise.
    /// </summary>
    public readonly int FindBinding(ReadOnlySpan<char> variableName)
    {
        var hash = ComputeHash(variableName);
        for (int i = 0; i < _count; i++)
        {
            if (_bindings[i].VariableNameHash == hash)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Find the index of a binding by its pre-computed hash.
    /// </summary>
    public readonly int FindBindingByHash(int hash)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_bindings[i].VariableNameHash == hash)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Get the type of the binding at the given index.
    /// </summary>
    public readonly BindingValueType GetType(int index) => _bindings[index].Type;

    /// <summary>
    /// Get the integer value at the given index.
    /// </summary>
    public readonly long GetInteger(int index) => _bindings[index].IntegerValue;

    /// <summary>
    /// Get the double value at the given index.
    /// </summary>
    public readonly double GetDouble(int index) => _bindings[index].DoubleValue;

    /// <summary>
    /// Get the boolean value at the given index.
    /// </summary>
    public readonly bool GetBoolean(int index) => _bindings[index].BooleanValue;

    /// <summary>
    /// Get the string value at the given index.
    /// </summary>
    public readonly ReadOnlySpan<char> GetString(int index)
    {
        ref readonly var binding = ref _bindings[index];
        return _stringBuffer.Slice(binding.StringOffset, binding.StringLength);
    }

    /// <summary>
    /// Get the variable name hash at the given index.
    /// </summary>
    public readonly int GetVariableHash(int index) => _bindings[index].VariableNameHash;

    /// <summary>
    /// Clear all bindings for reuse with next row.
    /// </summary>
    public void Clear()
    {
        _count = 0;
        _stringOffset = 0;
    }

    /// <summary>
    /// Truncate bindings to a previous count.
    /// Used for backtracking in multi-pattern joins.
    /// Note: String buffer space is not reclaimed (acceptable for fixed buffer).
    /// </summary>
    public void TruncateTo(int count)
    {
        if (count < _count)
        {
            _count = count;
            // String buffer is not truncated - we leave gaps, which is fine for fixed buffer
            // In a more sophisticated implementation, we'd track string offset per binding
        }
    }

    /// <summary>
    /// Number of bound variables.
    /// </summary>
    public readonly int Count => _count;

    /// <summary>
    /// Get the raw binding data for direct access.
    /// </summary>
    public readonly ReadOnlySpan<Binding> GetBindings() => _bindings.Slice(0, _count);

    /// <summary>
    /// Get the string buffer for direct access.
    /// </summary>
    public readonly ReadOnlySpan<char> GetStringBuffer() => _stringBuffer.Slice(0, _stringOffset);

    private static int ComputeHash(ReadOnlySpan<char> value)
    {
        // FNV-1a hash
        uint hash = 2166136261;
        foreach (var ch in value)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return (int)hash;
    }
}

/// <summary>
/// Type of value in a binding.
/// </summary>
public enum BindingValueType : byte
{
    Unbound = 0,
    Uri = 1,
    String = 2,
    Integer = 3,
    Double = 4,
    Boolean = 5
}

/// <summary>
/// Single variable binding (unmanaged for stackalloc)
/// </summary>
public struct Binding
{
    public int VariableNameHash;
    public BindingValueType Type;
    public long IntegerValue;
    public double DoubleValue;
    public bool BooleanValue;
    public int StringOffset;
    public int StringLength;
}

public class SparqlParseException : Exception
{
    public SparqlParseException(string message) : base(message) { }
}
