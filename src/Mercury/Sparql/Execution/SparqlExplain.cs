// SparqlExplain.cs
// SPARQL query plan explanation and visualization
// No external dependencies, only BCL
// .NET 10 / C# 14

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Sparql.Patterns;
using System.Linq;
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

    /// <summary>Property-path evaluation (a triple whose predicate is a path).</summary>
    PathScan,

    /// <summary>FILTER EXISTS { … }.</summary>
    Exists,

    /// <summary>FILTER NOT EXISTS { … }.</summary>
    NotExists,

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
        => FormatNode(sb, node, "", depth == 0, isLast);

    /// <summary>
    /// One node and its subtree. <paramref name="lead"/> is the column drawn by the ancestors: "│  " under an
    /// ancestor with later siblings, "   " under a last child.
    /// </summary>
    private void FormatNode(StringBuilder sb, ExplainNode node, string lead, bool isRoot, bool isLast)
    {
        sb.Append(lead);
        if (!isRoot)
            sb.Append(isLast ? "└─ " : "├─ ");
        sb.Append(GetOperatorSymbol(node.OperatorType));
        sb.Append(' ');
        sb.Append(node.Description);

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

        // The node's own detail lines, and its children, hang under this column
        string childLead = isRoot ? "  " : lead + (isLast ? "   " : "│  ");
        string detailLead = childLead + (node.Children.Count > 0 ? "│ " : "  ");
        if (node.OutputVariables.Count > 0)
            sb.Append(detailLead).Append("→ binds: ").AppendLine(string.Join(", ", node.OutputVariables));
        foreach (var (key, value) in node.Properties)
            sb.Append(detailLead).Append("  ").Append(key).Append(": ").AppendLine(value);

        for (int i = 0; i < node.Children.Count; i++)
            FormatNode(sb, node.Children[i], childLead, isRoot: false, isLast: i == node.Children.Count - 1);
    }

    private static string GetOperatorSymbol(ExplainOperatorType type) => type switch
    {
        ExplainOperatorType.Query => "→",
        ExplainOperatorType.TriplePatternScan => "⊳",
        ExplainOperatorType.PathScan => "⇝",
        ExplainOperatorType.Exists => "∃",
        ExplainOperatorType.NotExists => "∄",
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
        // Add solution modifiers at the top
        var currentNode = root;

        // LIMIT/OFFSET
        if (_query.SolutionModifier.LimitOffset.HasLimit || _query.SolutionModifier.LimitOffset.Offset > 0)
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

        // The WHERE, as the pattern tree that executes (ADR-055 D6)
        if (_query.WhereClause.GroupStart >= 0)
            AddWhere(currentNode, _query.WhereClause.GroupStart);
    }

    private void AddWhere(ExplainNode parent, int groupStart)
    {
        const int treeBytes = PatternSlot.Size * 1024;
        var treeBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(treeBytes);
        try
        {
            var tree = new PatternArray(treeBuffer.AsSpan(0, treeBytes));
            int root = new SparqlParser(_source.AsSpan()).ParsePatternTreeAt(groupStart, ref tree);
            parent.Children.Add(NodeFor(ref tree, root));
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(treeBuffer);
        }
    }

    /// <summary>One tree node as an operator, by the mapping of ADR-055 D6.</summary>
    private ExplainNode NodeFor(ref PatternArray tree, int index)
    {
        var slot = tree[index];
        switch (slot.Kind)
        {
            case PatternKind.Triple:
            case PatternKind.MinusTriple:
                return slot.PathKind == PathType.None ? TripleNode(slot, index) : PathNode(slot);
            case PatternKind.GroupHeader:
                return GroupNode(ref tree, index);
            case PatternKind.UnionHeader:
                return Operator(ExplainOperatorType.Union, "Alternative patterns", ref tree, index);
            case PatternKind.OptionalHeader:
                return Operator(ExplainOperatorType.LeftOuterJoin, "OPTIONAL", ref tree, index);
            case PatternKind.MinusHeader:
                return Operator(ExplainOperatorType.Minus, "Anti-join (MINUS)", ref tree, index);
            case PatternKind.ExistsHeader:
                return Operator(ExplainOperatorType.Exists, "FILTER EXISTS", ref tree, index);
            case PatternKind.NotExistsHeader:
                return Operator(ExplainOperatorType.NotExists, "FILTER NOT EXISTS", ref tree, index);
            case PatternKind.GraphHeader:
            {
                var term = new Term { Type = slot.GraphTermType, Start = slot.GraphTermStart, Length = slot.GraphTermLength };
                bool variable = IsVariable(term);
                var node = Operator(variable ? ExplainOperatorType.GraphVariable : ExplainOperatorType.GraphScan,
                    $"GRAPH {GetTermString(term)}", ref tree, index);
                if (variable) node.OutputVariables.Add(GetTermString(term));
                return node;
            }
            case PatternKind.Filter:
                return new ExplainNode { OperatorType = ExplainOperatorType.Filter, Description = $"FILTER({Clip(slot.FilterStart, slot.FilterLength, 47)})" };
            case PatternKind.Bind:
            {
                var variable = _source.Substring(slot.BindVarStart, slot.BindVarLength);
                var node = new ExplainNode { OperatorType = ExplainOperatorType.Bind, Description = $"BIND({Clip(slot.BindExprStart, slot.BindExprLength, 40)}) AS {variable}" };
                node.OutputVariables.Add(variable);
                return node;
            }
            case PatternKind.ValuesHeader:
                return new ExplainNode { OperatorType = ExplainOperatorType.Values, Description = $"Inline data: {Clip(slot.ValuesVarStart, slot.ValuesVarLength, 50)}" };
            case PatternKind.ServiceHeader:
            {
                var head = _source.AsSpan(slot.GraphTermStart, slot.ServiceGroupStart - slot.GraphTermStart); // SERVICE [SILENT] ep
                return new ExplainNode { OperatorType = ExplainOperatorType.Service, Description = head.Trim().ToString() };
            }
            case PatternKind.SubSelectHeader:
                return SubSelectNode(ref tree, index);
            default:
                return new ExplainNode { OperatorType = ExplainOperatorType.Query, Description = slot.Kind.ToString() };
        }
    }

    /// <summary>A group joins its children in source order, the order TreeJoinExecutor runs them; FILTERs and EXISTS
    /// apply to the whole group, so they are drawn last. A group with one child is drawn as that child.</summary>
    private ExplainNode GroupNode(ref PatternArray tree, int index)
    {
        var steps = new List<ExplainNode>();
        var groupScoped = new List<ExplainNode>();
        var e = tree.EnumerateDirectChildren(index);
        while (e.MoveNext())
        {
            var kind = e.Current.Kind;
            var child = NodeFor(ref tree, e.CurrentIndex);
            if (kind is PatternKind.Filter or PatternKind.ExistsHeader or PatternKind.NotExistsHeader)
                groupScoped.Add(child);
            else
                steps.Add(child);
        }
        if (steps.Count + groupScoped.Count == 1)
            return steps.Count == 1 ? steps[0] : groupScoped[0];
        var join = new ExplainNode
        {
            OperatorType = ExplainOperatorType.NestedLoopJoin,
            Description = steps.Count == 0 ? "Empty group (one solution)" : steps.Count == 1 ? "Join 1 pattern" : $"Join {steps.Count} patterns"
        };
        join.Children.AddRange(steps);
        join.Children.AddRange(groupScoped);
        return join;
    }

    /// <summary>An operator over its children (UNION branches, or the group of OPTIONAL / MINUS / GRAPH / EXISTS).</summary>
    private ExplainNode Operator(ExplainOperatorType type, string description, ref PatternArray tree, int index)
    {
        var node = new ExplainNode { OperatorType = type, Description = description };
        if (type is ExplainOperatorType.GraphScan or ExplainOperatorType.GraphVariable)
        {
            node.Children.Add(GroupNode(ref tree, index)); // a GRAPH header holds its group's children directly
            return node;
        }
        var e = tree.EnumerateDirectChildren(index);
        while (e.MoveNext())
            node.Children.Add(NodeFor(ref tree, e.CurrentIndex));
        if (type is ExplainOperatorType.Exists or ExplainOperatorType.NotExists && node.Children.Count > 1)
        {
            var group = GroupNode(ref tree, index);
            node.Children.Clear();
            node.Children.Add(group);
        }
        return node;
    }

    private ExplainNode TripleNode(PatternSlot slot, int index) => CreateTriplePatternNode(slot.ToTriplePattern(), index);

    private ExplainNode PathNode(PatternSlot slot)
    {
        var s = new Term { Type = slot.SubjectType, Start = slot.SubjectStart, Length = slot.SubjectLength };
        var o = new Term { Type = slot.ObjectType, Start = slot.ObjectStart, Length = slot.ObjectLength };
        var node = new ExplainNode
        {
            OperatorType = ExplainOperatorType.PathScan,
            Description = $"{GetTermString(s)} {Clip(slot.PathIriStart, slot.PathIriLength, 40)} {GetTermString(o)}"
        };
        if (IsVariable(s)) node.OutputVariables.Add(GetTermString(s));
        if (IsVariable(o)) node.OutputVariables.Add(GetTermString(o));
        return node;
    }

    /// <summary>A sub-SELECT's own plan: its modifiers above its WHERE, the header's child subtree.</summary>
    private ExplainNode SubSelectNode(ref PatternArray tree, int index)
    {
        var slot = tree[index];
        var sub = new SparqlParser(_source.AsSpan()).ParseSubSelectClausesAt(slot.GraphTermStart, slot.SubSelectModifiersStart);
        var node = new ExplainNode
        {
            OperatorType = ExplainOperatorType.SubQuery,
            Description = "Nested SELECT" + (sub.Select.Distinct ? " DISTINCT" : "") + (sub.Select.SelectAll ? " *" : "")
        };
        var current = node;
        if (sub.Modifier.LimitOffset.HasLimit || sub.Modifier.LimitOffset.Offset > 0)
        {
            var slice = new ExplainNode
            {
                OperatorType = ExplainOperatorType.Slice,
                Description = string.Join(" ", new[] {
                    sub.Modifier.LimitOffset.HasLimit ? $"LIMIT {sub.Modifier.LimitOffset.Limit}" : null,
                    sub.Modifier.LimitOffset.Offset > 0 ? $"OFFSET {sub.Modifier.LimitOffset.Offset}" : null }.Where(x => x != null))
            };
            current.Children.Add(slice);
            current = slice;
        }
        if (sub.Modifier.OrderBy.HasOrderBy)
        {
            var sort = new ExplainNode { OperatorType = ExplainOperatorType.Sort, Description = $"Sort by {sub.Modifier.OrderBy.Count} expression(s)" };
            current.Children.Add(sort);
            current = sort;
        }
        if (sub.Modifier.GroupBy.HasGroupBy || sub.Select.HasRealAggregates)
        {
            var group = new ExplainNode { OperatorType = ExplainOperatorType.GroupBy, Description = "Aggregate" };
            current.Children.Add(group);
            current = group;
        }
        current.Children.Add(NodeFor(ref tree, slot.ChildStartIndex));
        return node;
    }

    /// <summary>The source text at [start, start+length), cut to <paramref name="max"/> characters.</summary>
    private string Clip(int start, int length, int max)
    {
        if (start < 0 || length <= 0) return "";
        var text = _source.AsSpan(start, length).Trim();
        return text.Length > max ? text[..max].ToString() + "..." : text.ToString();
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
        var hash = VariableName.Hash(name);
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
        // A term an expansion added: a join variable (a [ … ] or collection node, a reifier, a sequence's middle node)
        // is drawn by its name, an rdf: IRI as itself
        if (SyntheticTerms.IsSynthetic(term.Start))
        {
            var name = SyntheticTerms.GetSyntheticVarName(term.Start);
            if (!name.IsEmpty)
                return "?" + name.ToString();
            var iri = SyntheticTerms.GetSyntheticIri(term.Start);
            if (!iri.IsEmpty)
                return iri.ToString();
        }
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
        return VariableName.IsSigil(first);
    }

    private string GetSliceDescription()
    {
        var parts = new List<string>();
        var slice = _query.SolutionModifier.LimitOffset;
        if (slice.HasLimit)
            parts.Add($"LIMIT {slice.Limit}");
        if (slice.Offset > 0)
            parts.Add($"OFFSET {slice.Offset}");
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
