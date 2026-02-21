// SparqlExplain.cs
// SPARQL query plan explanation and visualization
// No external dependencies, only BCL
// .NET 10 / C# 14

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Execution.Operators;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// Type of operator in the query plan.
/// </summary>
internal enum ExplainOperatorType
{
    /// <summary>Root SELECT/ASK/CONSTRUCT query.</summary>
    Query,

    /// <summary>Single triple pattern scan.</summary>
    TriplePatternScan,

    /// <summary>Multi-pattern nested loop join.</summary>
    NestedLoopJoin,

    /// <summary>OPTIONAL left outer join.</summary>
    LeftOuterJoin,

    /// <summary>UNION alternative patterns.</summary>
    Union,

    /// <summary>MINUS anti-join.</summary>
    Minus,

    /// <summary>GRAPH clause (fixed IRI).</summary>
    GraphScan,

    /// <summary>GRAPH clause (variable binding).</summary>
    GraphVariable,

    /// <summary>Subquery execution.</summary>
    SubQuery,

    /// <summary>SERVICE federated query.</summary>
    Service,

    /// <summary>FILTER expression evaluation.</summary>
    Filter,

    /// <summary>BIND expression assignment.</summary>
    Bind,

    /// <summary>VALUES inline data.</summary>
    Values,

    /// <summary>ORDER BY sorting.</summary>
    Sort,

    /// <summary>DISTINCT duplicate elimination.</summary>
    Distinct,

    /// <summary>LIMIT/OFFSET result slicing.</summary>
    Slice,

    /// <summary>GROUP BY aggregation.</summary>
    GroupBy,

    /// <summary>HAVING filter on groups.</summary>
    Having,

    /// <summary>Projection of selected variables.</summary>
    Project
}

/// <summary>
/// A node in the query execution plan tree.
/// </summary>
internal sealed class ExplainNode
{
    /// <summary>Type of operator.</summary>
    public ExplainOperatorType OperatorType { get; set; }

    /// <summary>Human-readable description.</summary>
    public string Description { get; set; } = "";

    /// <summary>Variables bound by this operator.</summary>
    public List<string> OutputVariables { get; } = new();

    /// <summary>Child operators (inputs to this operator).</summary>
    public List<ExplainNode> Children { get; } = new();

    /// <summary>Estimated row count (before execution).</summary>
    public long? EstimatedRows { get; set; }

    /// <summary>Actual row count (after execution with ANALYZE).</summary>
    public long? ActualRows { get; set; }

    /// <summary>Execution time in milliseconds (after ANALYZE).</summary>
    public double? ExecutionTimeMs { get; set; }

    /// <summary>Additional properties for this operator.</summary>
    public Dictionary<string, string> Properties { get; } = new();
}

/// <summary>
/// Query plan explanation with optional execution statistics.
/// </summary>
internal sealed class ExplainPlan
{
    /// <summary>The root of the execution plan tree.</summary>
    public ExplainNode Root { get; init; } = new() { OperatorType = ExplainOperatorType.Query };

    /// <summary>Total execution time in milliseconds (if analyzed).</summary>
    public double? TotalExecutionTimeMs { get; set; }

    /// <summary>Total rows returned (if analyzed).</summary>
    public long? TotalRows { get; set; }

    /// <summary>Whether this plan includes execution statistics.</summary>
    public bool IsAnalyzed { get; set; }

    /// <summary>The original SPARQL query.</summary>
    public string Query { get; init; } = "";

    /// <summary>
    /// Format the plan as a human-readable string.
    /// </summary>
    public string Format(ExplainFormat format = ExplainFormat.Text)
    {
        return format switch
        {
            ExplainFormat.Text => FormatText(),
            ExplainFormat.Json => FormatJson(),
            _ => FormatText()
        };
    }

    private string FormatText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("QUERY PLAN");
        sb.AppendLine(new string('─', 60));

        FormatNode(sb, Root, 0, true);

        if (IsAnalyzed && TotalExecutionTimeMs.HasValue)
        {
            sb.AppendLine(new string('─', 60));
            sb.AppendLine($"Total Time: {TotalExecutionTimeMs:F2} ms");
            if (TotalRows.HasValue)
                sb.AppendLine($"Total Rows: {TotalRows}");
        }

        return sb.ToString();
    }

    private void FormatNode(StringBuilder sb, ExplainNode node, int depth, bool isLast)
    {
        var indent = new string(' ', depth * 2);
        var prefix = depth == 0 ? "" : (isLast ? "└─ " : "├─ ");

        sb.Append(indent);
        sb.Append(prefix);
        sb.Append(GetOperatorSymbol(node.OperatorType));
        sb.Append(' ');
        sb.Append(node.Description);

        // Show row counts if available
        if (node.EstimatedRows.HasValue || node.ActualRows.HasValue)
        {
            sb.Append(" (");
            if (node.ActualRows.HasValue)
                sb.Append($"rows={node.ActualRows}");
            else if (node.EstimatedRows.HasValue)
                sb.Append($"est={node.EstimatedRows}");
            if (node.ExecutionTimeMs.HasValue)
                sb.Append($", time={node.ExecutionTimeMs:F2}ms");
            sb.Append(')');
        }

        sb.AppendLine();

        // Show output variables
        if (node.OutputVariables.Count > 0)
        {
            sb.Append(indent);
            sb.Append(depth == 0 ? "  " : (isLast ? "   " : "│  "));
            sb.Append("→ binds: ");
            sb.AppendLine(string.Join(", ", node.OutputVariables));
        }

        // Show properties
        foreach (var (key, value) in node.Properties)
        {
            sb.Append(indent);
            sb.Append(depth == 0 ? "  " : (isLast ? "   " : "│  "));
            sb.Append($"  {key}: {value}");
            sb.AppendLine();
        }

        // Recurse to children
        for (int i = 0; i < node.Children.Count; i++)
        {
            FormatNode(sb, node.Children[i], depth + 1, i == node.Children.Count - 1);
        }
    }

    private static string GetOperatorSymbol(ExplainOperatorType type) => type switch
    {
        ExplainOperatorType.Query => "→",
        ExplainOperatorType.TriplePatternScan => "⊳",
        ExplainOperatorType.NestedLoopJoin => "⋈",
        ExplainOperatorType.LeftOuterJoin => "⟕",
        ExplainOperatorType.Union => "∪",
        ExplainOperatorType.Minus => "−",
        ExplainOperatorType.GraphScan => "▣",
        ExplainOperatorType.GraphVariable => "▢",
        ExplainOperatorType.SubQuery => "⊂",
        ExplainOperatorType.Service => "⇄",
        ExplainOperatorType.Filter => "σ",
        ExplainOperatorType.Bind => "←",
        ExplainOperatorType.Values => "≡",
        ExplainOperatorType.Sort => "↑",
        ExplainOperatorType.Distinct => "δ",
        ExplainOperatorType.Slice => "⌊",
        ExplainOperatorType.GroupBy => "γ",
        ExplainOperatorType.Having => "η",
        ExplainOperatorType.Project => "π",
        _ => "?"
    };

    private string FormatJson()
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"query\": \"{EscapeJson(Query)}\",");
        sb.AppendLine($"  \"analyzed\": {(IsAnalyzed ? "true" : "false")},");
        if (TotalExecutionTimeMs.HasValue)
            sb.AppendLine($"  \"totalTimeMs\": {TotalExecutionTimeMs:F2},");
        if (TotalRows.HasValue)
            sb.AppendLine($"  \"totalRows\": {TotalRows},");
        sb.AppendLine("  \"plan\":");
        FormatNodeJson(sb, Root, 2);
        sb.AppendLine();
        sb.AppendLine("}");
        return sb.ToString();
    }

    private void FormatNodeJson(StringBuilder sb, ExplainNode node, int indent)
    {
        var pad = new string(' ', indent);
        sb.AppendLine($"{pad}{{");
        sb.AppendLine($"{pad}  \"operator\": \"{node.OperatorType}\",");
        sb.AppendLine($"{pad}  \"description\": \"{EscapeJson(node.Description)}\",");

        if (node.EstimatedRows.HasValue)
            sb.AppendLine($"{pad}  \"estimatedRows\": {node.EstimatedRows},");
        if (node.ActualRows.HasValue)
            sb.AppendLine($"{pad}  \"actualRows\": {node.ActualRows},");
        if (node.ExecutionTimeMs.HasValue)
            sb.AppendLine($"{pad}  \"executionTimeMs\": {node.ExecutionTimeMs:F2},");

        if (node.OutputVariables.Count > 0)
        {
            sb.Append($"{pad}  \"outputVariables\": [");
            sb.Append(string.Join(", ", node.OutputVariables.ConvertAll(v => $"\"{v}\"")));
            sb.AppendLine("],");
        }

        if (node.Properties.Count > 0)
        {
            sb.AppendLine($"{pad}  \"properties\": {{");
            var propList = new List<string>();
            foreach (var (key, value) in node.Properties)
            {
                propList.Add($"{pad}    \"{key}\": \"{EscapeJson(value)}\"");
            }
            sb.AppendLine(string.Join(",\n", propList));
            sb.AppendLine($"{pad}  }},");
        }

        sb.Append($"{pad}  \"children\": [");
        if (node.Children.Count > 0)
        {
            sb.AppendLine();
            for (int i = 0; i < node.Children.Count; i++)
            {
                FormatNodeJson(sb, node.Children[i], indent + 4);
                if (i < node.Children.Count - 1)
                    sb.Append(",");
                sb.AppendLine();
            }
            sb.Append($"{pad}  ");
        }
        sb.AppendLine("]");
        sb.Append($"{pad}}}");
    }

    private static string EscapeJson(string s)
    {
        return s.Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
    }
}

/// <summary>
/// Output format for explain plans.
/// </summary>
internal enum ExplainFormat
{
    /// <summary>Human-readable text format.</summary>
    Text,

    /// <summary>JSON format for programmatic consumption.</summary>
    Json
}

/// <summary>
/// Generates query execution plans from parsed SPARQL queries.
/// </summary>
internal sealed class SparqlExplainer
{
    private readonly string _source;
    private readonly Query _query;
    private readonly QueryPlanner? _planner;
    private readonly List<int> _boundVariables = new();

    public SparqlExplainer(ReadOnlySpan<char> source, in Query query)
        : this(source, in query, null) { }

    /// <summary>
    /// Create an explainer with optional query planner for cardinality estimation.
    /// </summary>
    public SparqlExplainer(ReadOnlySpan<char> source, in Query query, QueryPlanner? planner)
    {
        _source = source.ToString();
        _query = query;
        _planner = planner;
    }

    /// <summary>
    /// Generate an explain plan without executing the query.
    /// </summary>
    public ExplainPlan Explain()
    {
        var plan = new ExplainPlan
        {
            Query = _source,
            IsAnalyzed = false
        };

        plan.Root.OperatorType = ExplainOperatorType.Query;
        plan.Root.Description = GetQueryTypeDescription();

        BuildPlanTree(plan.Root);

        return plan;
    }

    /// <summary>
    /// Generate an explain plan and execute the query to collect statistics.
    /// </summary>
    public ExplainPlan ExplainAnalyze(QuadStore store)
    {
        var plan = Explain();
        plan.IsAnalyzed = true;

        var sw = Stopwatch.StartNew();
        long rowCount = 0;

        store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(store, _source.AsSpan(), in _query);
            try
            {
                if (_query.Type == QueryType.Ask)
                {
                    var result = executor.ExecuteAsk();
                    rowCount = result ? 1 : 0;
                }
                else
                {
                    var results = executor.Execute();
                    while (results.MoveNext())
                    {
                        rowCount++;
                    }
                    results.Dispose();
                }
            }
            finally
            {
                executor.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }

        sw.Stop();
        plan.TotalExecutionTimeMs = sw.Elapsed.TotalMilliseconds;
        plan.TotalRows = rowCount;
        plan.Root.ActualRows = rowCount;
        plan.Root.ExecutionTimeMs = sw.Elapsed.TotalMilliseconds;

        return plan;
    }

    private string GetQueryTypeDescription()
    {
        var desc = _query.Type switch
        {
            QueryType.Select => "SELECT",
            QueryType.Ask => "ASK",
            QueryType.Construct => "CONSTRUCT",
            QueryType.Describe => "DESCRIBE",
            _ => _query.Type.ToString().ToUpperInvariant()
        };

        if (_query.SelectClause.Distinct)
            desc = desc + " DISTINCT";
        if (_query.SelectClause.Reduced)
            desc = desc + " REDUCED";
        if (_query.SelectClause.SelectAll)
            desc = desc + " *";

        return desc;
    }

    private void BuildPlanTree(ExplainNode root)
    {
        ref readonly var pattern = ref _query.WhereClause.Pattern;

        // Add solution modifiers at the top
        var currentNode = root;

        // LIMIT/OFFSET
        if (_query.SolutionModifier.Limit >= 0 || _query.SolutionModifier.Offset > 0)
        {
            var sliceNode = new ExplainNode
            {
                OperatorType = ExplainOperatorType.Slice,
                Description = GetSliceDescription()
            };
            currentNode.Children.Add(sliceNode);
            currentNode = sliceNode;
        }

        // ORDER BY
        if (_query.SolutionModifier.OrderBy.Count > 0)
        {
            var sortNode = new ExplainNode
            {
                OperatorType = ExplainOperatorType.Sort,
                Description = GetOrderByDescription()
            };
            currentNode.Children.Add(sortNode);
            currentNode = sortNode;
        }

        // DISTINCT
        if (_query.SelectClause.Distinct)
        {
            var distinctNode = new ExplainNode
            {
                OperatorType = ExplainOperatorType.Distinct,
                Description = "Remove duplicates"
            };
            currentNode.Children.Add(distinctNode);
            currentNode = distinctNode;
        }

        // GROUP BY with aggregates
        if (_query.SolutionModifier.GroupBy.Count > 0 || _query.SelectClause.HasAggregates)
        {
            var groupNode = new ExplainNode
            {
                OperatorType = ExplainOperatorType.GroupBy,
                Description = GetGroupByDescription()
            };
            currentNode.Children.Add(groupNode);
            currentNode = groupNode;
        }

        // Build WHERE clause operators
        BuildWhereClause(currentNode, in pattern);
    }

    private void BuildWhereClause(ExplainNode parent, ref readonly GraphPattern pattern)
    {
        // Handle UNION
        if (pattern.HasUnion)
        {
            var unionNode = new ExplainNode
            {
                OperatorType = ExplainOperatorType.Union,
                Description = "Alternative patterns"
            };
            parent.Children.Add(unionNode);

            // First branch
            var firstBranchNode = new ExplainNode
            {
                OperatorType = ExplainOperatorType.NestedLoopJoin,
                Description = $"Branch 1 ({pattern.FirstBranchPatternCount} patterns)"
            };
            unionNode.Children.Add(firstBranchNode);
            AddTriplePatterns(firstBranchNode, in pattern, 0, pattern.FirstBranchPatternCount);

            // Second branch
            var secondBranchNode = new ExplainNode
            {
                OperatorType = ExplainOperatorType.NestedLoopJoin,
                Description = $"Branch 2 ({pattern.UnionBranchPatternCount} patterns)"
            };
            unionNode.Children.Add(secondBranchNode);
            AddTriplePatterns(secondBranchNode, in pattern, pattern.FirstBranchPatternCount, pattern.PatternCount);

            return;
        }

        // Handle GRAPH clauses
        if (pattern.HasGraph)
        {
            for (int i = 0; i < pattern.GraphClauseCount; i++)
            {
                var gc = pattern.GetGraphClause(i);
                var isVariable = IsVariable(gc.Graph);
                var graphNode = new ExplainNode
                {
                    OperatorType = isVariable ? ExplainOperatorType.GraphVariable : ExplainOperatorType.GraphScan,
                    Description = GetGraphDescription(gc)
                };

                if (isVariable)
                {
                    graphNode.OutputVariables.Add(GetTermString(gc.Graph));
                }

                parent.Children.Add(graphNode);
            }
        }

        // Handle SERVICE clauses
        if (pattern.HasService)
        {
            for (int i = 0; i < pattern.ServiceClauseCount; i++)
            {
                var svc = pattern.GetServiceClause(i);
                var serviceNode = new ExplainNode
                {
                    OperatorType = ExplainOperatorType.Service,
                    Description = GetServiceDescription(svc)
                };
                if (svc.Silent)
                    serviceNode.Properties["silent"] = "true";
                parent.Children.Add(serviceNode);
            }
        }

        // Handle subqueries
        if (pattern.HasSubQueries)
        {
            for (int i = 0; i < pattern.SubQueryCount; i++)
            {
                var sq = pattern.GetSubQuery(i);
                var subNode = new ExplainNode
                {
                    OperatorType = ExplainOperatorType.SubQuery,
                    Description = "Nested SELECT"
                };
                parent.Children.Add(subNode);
            }
        }

        // Regular triple patterns
        var requiredCount = pattern.RequiredPatternCount;
        var optionalStart = -1;

        // Find where optional patterns start
        for (int i = 0; i < pattern.PatternCount && i < pattern.FirstBranchPatternCount; i++)
        {
            if (pattern.IsOptional(i) && optionalStart < 0)
                optionalStart = i;
        }

        if (requiredCount > 0)
        {
            if (requiredCount == 1)
            {
                // Single pattern scan
                int idx = 0;
                for (int i = 0; i < pattern.PatternCount; i++)
                {
                    if (!pattern.IsOptional(i)) { idx = i; break; }
                }
                var scanNode = CreateTriplePatternNode(pattern.GetPattern(idx), idx);
                parent.Children.Add(scanNode);
            }
            else
            {
                // Multi-pattern join
                var joinNode = new ExplainNode
                {
                    OperatorType = ExplainOperatorType.NestedLoopJoin,
                    Description = $"Join {requiredCount} patterns"
                };
                parent.Children.Add(joinNode);

                for (int i = 0; i < pattern.PatternCount && (optionalStart < 0 || i < optionalStart); i++)
                {
                    if (!pattern.IsOptional(i))
                    {
                        var scanNode = CreateTriplePatternNode(pattern.GetPattern(i), i);
                        joinNode.Children.Add(scanNode);
                    }
                }
            }
        }

        // OPTIONAL patterns
        if (pattern.HasOptionalPatterns)
        {
            var optionalCount = pattern.PatternCount - requiredCount;
            if (optionalCount > 0)
            {
                var optNode = new ExplainNode
                {
                    OperatorType = ExplainOperatorType.LeftOuterJoin,
                    Description = $"OPTIONAL ({optionalCount} patterns)"
                };
                parent.Children.Add(optNode);

                for (int i = 0; i < pattern.PatternCount; i++)
                {
                    if (pattern.IsOptional(i))
                    {
                        var scanNode = CreateTriplePatternNode(pattern.GetPattern(i), i);
                        optNode.Children.Add(scanNode);
                    }
                }
            }
        }

        // MINUS patterns
        if (pattern.HasMinus)
        {
            var minusNode = new ExplainNode
            {
                OperatorType = ExplainOperatorType.Minus,
                Description = $"Anti-join ({pattern.MinusPatternCount} patterns)"
            };
            parent.Children.Add(minusNode);
        }

        // FILTER expressions
        if (pattern.FilterCount > 0)
        {
            for (int i = 0; i < pattern.FilterCount; i++)
            {
                var filter = pattern.GetFilter(i);
                var filterNode = new ExplainNode
                {
                    OperatorType = ExplainOperatorType.Filter,
                    Description = GetFilterDescription(filter)
                };
                parent.Children.Add(filterNode);
            }
        }

        // BIND expressions
        if (pattern.HasBinds)
        {
            for (int i = 0; i < pattern.BindCount; i++)
            {
                var bind = pattern.GetBind(i);
                var bindNode = new ExplainNode
                {
                    OperatorType = ExplainOperatorType.Bind,
                    Description = GetBindDescription(bind)
                };
                bindNode.OutputVariables.Add(GetBindVariable(bind));
                parent.Children.Add(bindNode);
            }
        }

        // VALUES clause
        if (pattern.HasValues)
        {
            var valuesNode = new ExplainNode
            {
                OperatorType = ExplainOperatorType.Values,
                Description = $"Inline data ({pattern.Values.ValueCount} values)"
            };
            parent.Children.Add(valuesNode);
        }
    }

    private void AddTriplePatterns(ExplainNode parent, ref readonly GraphPattern pattern, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            var scanNode = CreateTriplePatternNode(pattern.GetPattern(i), i);
            parent.Children.Add(scanNode);
        }
    }

    private ExplainNode CreateTriplePatternNode(TriplePattern tp, int index)
    {
        var node = new ExplainNode
        {
            OperatorType = ExplainOperatorType.TriplePatternScan,
            Description = GetTriplePatternDescription(tp)
        };

        // Add bound variables
        if (IsVariable(tp.Subject))
            node.OutputVariables.Add(GetTermString(tp.Subject));
        if (IsVariable(tp.Predicate))
            node.OutputVariables.Add(GetTermString(tp.Predicate));
        if (IsVariable(tp.Object))
            node.OutputVariables.Add(GetTermString(tp.Object));

        node.Properties["index"] = index.ToString();

        // Estimate cardinality if planner is available
        if (_planner != null)
        {
            var estimate = _planner.EstimateCardinality(in tp, _source.AsSpan(), _boundVariables);
            node.EstimatedRows = (long)estimate;

            // Track variables bound by this pattern for subsequent estimates
            TrackBoundVariables(tp);
        }

        return node;
    }

    /// <summary>
    /// Track variables bound by a pattern for accurate cardinality estimation.
    /// </summary>
    private void TrackBoundVariables(TriplePattern tp)
    {
        if (IsVariable(tp.Subject))
            AddVariableHash(tp.Subject);
        if (IsVariable(tp.Predicate))
            AddVariableHash(tp.Predicate);
        if (IsVariable(tp.Object))
            AddVariableHash(tp.Object);
    }

    private void AddVariableHash(Term term)
    {
        var name = _source.AsSpan().Slice(term.Start, term.Length);
        var hash = Fnv1a.Hash(name);
        if (!_boundVariables.Contains(hash))
            _boundVariables.Add(hash);
    }

    private string GetTriplePatternDescription(TriplePattern tp)
    {
        var s = GetTermString(tp.Subject);
        var p = GetTermString(tp.Predicate);
        var o = GetTermString(tp.Object);
        return $"{s} {p} {o}";
    }

    private string GetTermString(Term term)
    {
        if (term.Length == 0)
            return "?";

        var value = _source.AsSpan().Slice(term.Start, term.Length);

        // Truncate long IRIs
        if (value.Length > 40 && value[0] == '<')
        {
            return $"<...{value.Slice(value.Length - 35).ToString()}";
        }

        return value.ToString();
    }

    private bool IsVariable(Term term)
    {
        if (term.Length == 0) return false;
        var first = _source[term.Start];
        return first == '?' || first == '$';
    }

    private string GetSliceDescription()
    {
        var parts = new List<string>();
        if (_query.SolutionModifier.Limit >= 0)
            parts.Add($"LIMIT {_query.SolutionModifier.Limit}");
        if (_query.SolutionModifier.Offset > 0)
            parts.Add($"OFFSET {_query.SolutionModifier.Offset}");
        return string.Join(" ", parts);
    }

    private string GetOrderByDescription()
    {
        var count = _query.SolutionModifier.OrderBy.Count;
        return $"Sort by {count} expression(s)";
    }

    private string GetGroupByDescription()
    {
        var groupCount = _query.SolutionModifier.GroupBy.Count;
        var aggCount = _query.SelectClause.AggregateCount;
        var parts = new List<string>();
        if (groupCount > 0)
            parts.Add($"GROUP BY {groupCount} var(s)");
        if (aggCount > 0)
            parts.Add($"{aggCount} aggregate(s)");
        return string.Join(", ", parts);
    }

    private string GetGraphDescription(GraphClause gc)
    {
        var graphTerm = GetTermString(gc.Graph);
        return $"GRAPH {graphTerm} ({gc.PatternCount} patterns)";
    }

    private string GetServiceDescription(ServiceClause svc)
    {
        var endpoint = GetTermString(svc.Endpoint);
        var silent = svc.Silent ? " SILENT" : "";
        return $"SERVICE{silent} {endpoint}";
    }

    private string GetFilterDescription(FilterExpr filter)
    {
        var expr = _source.AsSpan().Slice(filter.Start, filter.Length);
        if (expr.Length > 50)
            return $"FILTER({expr.Slice(0, 47).ToString()}...)";
        return $"FILTER({expr.ToString()})";
    }

    private string GetBindDescription(BindExpr bind)
    {
        var varName = GetBindVariable(bind);
        return $"BIND AS {varName}";
    }

    private string GetBindVariable(BindExpr bind)
    {
        if (bind.VarLength == 0) return "?";
        return _source.AsSpan().Slice(bind.VarStart, bind.VarLength).ToString();
    }
}

/// <summary>
/// Extension methods for explain functionality.
/// </summary>
internal static class SparqlExplainExtensions
{
    /// <summary>
    /// Generate an explain plan for a parsed query.
    /// </summary>
    public static ExplainPlan Explain(this Query query, ReadOnlySpan<char> source)
    {
        var explainer = new SparqlExplainer(source, in query);
        return explainer.Explain();
    }

    /// <summary>
    /// Generate an explain plan with execution statistics.
    /// </summary>
    public static ExplainPlan ExplainAnalyze(this Query query, ReadOnlySpan<char> source, QuadStore store)
    {
        var explainer = new SparqlExplainer(source, in query);
        return explainer.ExplainAnalyze(store);
    }
}
