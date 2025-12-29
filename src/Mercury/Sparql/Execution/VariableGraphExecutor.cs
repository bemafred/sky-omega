using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// Static helper for executing GRAPH ?g queries.
/// Separated from QueryExecutor to avoid carrying the large Query struct on the stack.
/// </summary>
internal static class VariableGraphExecutor
{
    /// <summary>
    /// Configuration for variable graph execution.
    /// Heap-allocated to avoid passing large structs on the stack.
    /// </summary>
    internal sealed class ExecutionConfig
    {
        public QuadStore Store = null!;
        public string Source = null!;
        public GraphClause GraphClause;
        public GraphPattern Pattern;
        public string[]? NamedGraphs;
        public Binding[] Bindings = null!;
        public char[] StringBuffer = null!;
        public int Limit;
        public int Offset;
        public bool Distinct;
        public OrderByClause OrderBy;
        public GroupByClause GroupBy;
        public SelectClause SelectClause;
        public HavingClause Having;
    }

    /// <summary>
    /// Execute a variable graph query and return collected results.
    /// Used when running on a separate thread to avoid ref struct lifetime issues.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static List<MaterializedRow> ExecuteAndCollect(ExecutionConfig config)
    {
        var results = new List<MaterializedRow>();
        ExecuteIntoList(config, results);
        return results;
    }

    /// <summary>
    /// Core execution logic that populates a list of materialized rows.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ExecuteIntoList(ExecutionConfig config, List<MaterializedRow> results)
    {
        var sourceSpan = config.Source.AsSpan();
        var graphVarName = sourceSpan.Slice(config.GraphClause.Graph.Start, config.GraphClause.Graph.Length);
        var graphsToIterate = config.NamedGraphs ?? GetAllNamedGraphs(config.Store);
        var patternCount = config.GraphClause.PatternCount;

        if (patternCount == 0 || graphsToIterate.Length == 0)
            return;

        // Only support single pattern for now to minimize stack usage
        if (patternCount > 1)
        {
            // Multi-pattern - use nested collection (may still have issues)
            var tp0 = config.GraphClause.GetPattern(0);
            var tp1 = patternCount > 1 ? config.GraphClause.GetPattern(1) : default;
            var tp2 = patternCount > 2 ? config.GraphClause.GetPattern(2) : default;
            var tp3 = patternCount > 3 ? config.GraphClause.GetPattern(3) : default;

            var scanBindings = new Binding[16];
            var scanStringBuffer = new char[1024];

            foreach (var graphIri in graphsToIterate)
            {
                var graphSpan = graphIri.AsSpan();
                CollectMultiPattern(results, config.Store, config.Source, graphSpan, graphVarName, patternCount,
                    tp0, tp1, tp2, tp3, scanBindings, scanStringBuffer);
            }
            return;
        }

        // Get the single pattern
        var tp = config.GraphClause.GetPattern(0);

        // Create binding storage once
        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
        var bindingTable = new BindingTable(bindings, stringBuffer);

        // Resolve pattern terms
        ReadOnlySpan<char> subject = tp.Subject.IsVariable ? ReadOnlySpan<char>.Empty : sourceSpan.Slice(tp.Subject.Start, tp.Subject.Length);
        ReadOnlySpan<char> predicate = tp.Predicate.IsVariable ? ReadOnlySpan<char>.Empty : sourceSpan.Slice(tp.Predicate.Start, tp.Predicate.Length);
        ReadOnlySpan<char> obj = tp.Object.IsVariable ? ReadOnlySpan<char>.Empty : sourceSpan.Slice(tp.Object.Start, tp.Object.Length);

        // Inline collection for each graph
        foreach (var graphIri in graphsToIterate)
        {
            var graphSpan = graphIri.AsSpan();

            var enumerator = config.Store.QueryCurrent(subject, predicate, obj, graphSpan);
            try
            {
                while (enumerator.MoveNext())
                {
                    var triple = enumerator.Current;
                    bindingTable.Clear();

                    // Bind subject
                    if (tp.Subject.IsVariable)
                    {
                        var varName = sourceSpan.Slice(tp.Subject.Start, tp.Subject.Length);
                        bindingTable.Bind(varName, triple.Subject);
                    }

                    // Bind predicate
                    if (tp.Predicate.IsVariable)
                    {
                        var varName = sourceSpan.Slice(tp.Predicate.Start, tp.Predicate.Length);
                        var idx = bindingTable.FindBinding(varName);
                        if (idx >= 0)
                        {
                            if (!triple.Predicate.SequenceEqual(bindingTable.GetString(idx)))
                                continue;
                        }
                        else
                        {
                            bindingTable.Bind(varName, triple.Predicate);
                        }
                    }

                    // Bind object
                    if (tp.Object.IsVariable)
                    {
                        var varName = sourceSpan.Slice(tp.Object.Start, tp.Object.Length);
                        var idx = bindingTable.FindBinding(varName);
                        if (idx >= 0)
                        {
                            if (!triple.Object.SequenceEqual(bindingTable.GetString(idx)))
                                continue;
                        }
                        else
                        {
                            bindingTable.Bind(varName, triple.Object);
                        }
                    }

                    // Bind graph variable
                    bindingTable.Bind(graphVarName, graphSpan);
                    results.Add(new MaterializedRow(bindingTable));
                }
            }
            finally
            {
                enumerator.Dispose();
            }
        }
    }

    /// <summary>
    /// Execute a variable graph query (GRAPH ?g) using completely flat iteration.
    /// Takes a heap-allocated config to avoid stack overflow from large struct parameters.
    /// IMPORTANT: All collection logic is inlined to minimize stack frame depth.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static QueryResults Execute(ExecutionConfig config)
    {
        var results = new List<MaterializedRow>();
        var sourceSpan = config.Source.AsSpan();
        var graphVarName = sourceSpan.Slice(config.GraphClause.Graph.Start, config.GraphClause.Graph.Length);
        var graphsToIterate = config.NamedGraphs ?? GetAllNamedGraphs(config.Store);
        var patternCount = config.GraphClause.PatternCount;

        if (patternCount == 0 || graphsToIterate.Length == 0)
            return QueryResults.Empty();

        // Only support single pattern for now to minimize stack usage
        // Multi-pattern requires nested joins which would increase stack depth
        if (patternCount > 1)
        {
            // Fall back to multi-pattern which may still cause issues
            return ExecuteMultiPattern(config, results, sourceSpan, graphVarName, graphsToIterate);
        }

        // Get the single pattern
        var tp = config.GraphClause.GetPattern(0);

        // Create binding storage once
        var scanBindings = new Binding[16];
        var scanStringBuffer = new char[1024];
        var bindingTable = new BindingTable(scanBindings, scanStringBuffer);

        // Resolve pattern terms
        ReadOnlySpan<char> subject = tp.Subject.IsVariable ? ReadOnlySpan<char>.Empty : sourceSpan.Slice(tp.Subject.Start, tp.Subject.Length);
        ReadOnlySpan<char> predicate = tp.Predicate.IsVariable ? ReadOnlySpan<char>.Empty : sourceSpan.Slice(tp.Predicate.Start, tp.Predicate.Length);
        ReadOnlySpan<char> obj = tp.Object.IsVariable ? ReadOnlySpan<char>.Empty : sourceSpan.Slice(tp.Object.Start, tp.Object.Length);

        // Inline collection for each graph - NO method calls to avoid additional stack frames
        foreach (var graphIri in graphsToIterate)
        {
            var graphSpan = graphIri.AsSpan();

            // Query this graph directly - enumerator is on stack but no additional call frames
            var enumerator = config.Store.QueryCurrent(subject, predicate, obj, graphSpan);
            try
            {
                while (enumerator.MoveNext())
                {
                    var triple = enumerator.Current;
                    bindingTable.Clear();

                    // Bind subject
                    if (tp.Subject.IsVariable)
                    {
                        var varName = sourceSpan.Slice(tp.Subject.Start, tp.Subject.Length);
                        bindingTable.Bind(varName, triple.Subject);
                    }

                    // Bind predicate
                    if (tp.Predicate.IsVariable)
                    {
                        var varName = sourceSpan.Slice(tp.Predicate.Start, tp.Predicate.Length);
                        var idx = bindingTable.FindBinding(varName);
                        if (idx >= 0)
                        {
                            if (!triple.Predicate.SequenceEqual(bindingTable.GetString(idx)))
                                continue;
                        }
                        else
                        {
                            bindingTable.Bind(varName, triple.Predicate);
                        }
                    }

                    // Bind object
                    if (tp.Object.IsVariable)
                    {
                        var varName = sourceSpan.Slice(tp.Object.Start, tp.Object.Length);
                        var idx = bindingTable.FindBinding(varName);
                        if (idx >= 0)
                        {
                            if (!triple.Object.SequenceEqual(bindingTable.GetString(idx)))
                                continue;
                        }
                        else
                        {
                            bindingTable.Bind(varName, triple.Object);
                        }
                    }

                    // Bind graph variable
                    bindingTable.Bind(graphVarName, graphSpan);
                    results.Add(new MaterializedRow(bindingTable));
                }
            }
            finally
            {
                enumerator.Dispose();
            }
        }

        if (results.Count == 0)
            return QueryResults.Empty();

        return QueryResults.FromMaterialized(results, config.Pattern, sourceSpan, config.Store,
            config.Bindings, config.StringBuffer, config.Limit, config.Offset, config.Distinct,
            config.OrderBy, config.GroupBy, config.SelectClause, config.Having);
    }

    /// <summary>
    /// Execute multi-pattern variable graph query.
    /// This may still cause stack overflow for complex patterns.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static QueryResults ExecuteMultiPattern(ExecutionConfig config, List<MaterializedRow> results,
        ReadOnlySpan<char> sourceSpan, ReadOnlySpan<char> graphVarName, string[] graphsToIterate)
    {
        var patternCount = config.GraphClause.PatternCount;
        var tp0 = config.GraphClause.GetPattern(0);
        var tp1 = patternCount > 1 ? config.GraphClause.GetPattern(1) : default;
        var tp2 = patternCount > 2 ? config.GraphClause.GetPattern(2) : default;
        var tp3 = patternCount > 3 ? config.GraphClause.GetPattern(3) : default;

        var scanBindings = new Binding[16];
        var scanStringBuffer = new char[1024];

        foreach (var graphIri in graphsToIterate)
        {
            var graphSpan = graphIri.AsSpan();
            CollectMultiPattern(results, config.Store, config.Source, graphSpan, graphVarName, patternCount,
                tp0, tp1, tp2, tp3, scanBindings, scanStringBuffer);
        }

        if (results.Count == 0)
            return QueryResults.Empty();

        return QueryResults.FromMaterialized(results, config.Pattern, sourceSpan, config.Store,
            config.Bindings, config.StringBuffer, config.Limit, config.Offset, config.Distinct,
            config.OrderBy, config.GroupBy, config.SelectClause, config.Having);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CollectSinglePattern(
        List<MaterializedRow> results,
        QuadStore store,
        string source,
        ReadOnlySpan<char> graphIri,
        ReadOnlySpan<char> graphVarName,
        TriplePattern tp,
        Binding[] bindingStorage,
        char[] stringBuffer)
    {
        var sourceSpan = source.AsSpan();
        var bindingTable = new BindingTable(bindingStorage, stringBuffer);

        // Resolve terms
        ReadOnlySpan<char> subject = tp.Subject.IsVariable ? ReadOnlySpan<char>.Empty : sourceSpan.Slice(tp.Subject.Start, tp.Subject.Length);
        ReadOnlySpan<char> predicate = tp.Predicate.IsVariable ? ReadOnlySpan<char>.Empty : sourceSpan.Slice(tp.Predicate.Start, tp.Predicate.Length);
        ReadOnlySpan<char> obj = tp.Object.IsVariable ? ReadOnlySpan<char>.Empty : sourceSpan.Slice(tp.Object.Start, tp.Object.Length);

        var enumerator = store.QueryCurrent(subject, predicate, obj, graphIri);
        try
        {
            while (enumerator.MoveNext())
            {
                var triple = enumerator.Current;
                bindingTable.Clear();

                if (!TryBindTerm(tp.Subject, triple.Subject, sourceSpan, ref bindingTable)) continue;
                if (!TryBindTerm(tp.Predicate, triple.Predicate, sourceSpan, ref bindingTable)) continue;
                if (!TryBindTerm(tp.Object, triple.Object, sourceSpan, ref bindingTable)) continue;

                bindingTable.Bind(graphVarName, graphIri);
                results.Add(new MaterializedRow(bindingTable));
            }
        }
        finally
        {
            enumerator.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CollectMultiPattern(
        List<MaterializedRow> results,
        QuadStore store,
        string source,
        ReadOnlySpan<char> graphIri,
        ReadOnlySpan<char> graphVarName,
        int patternCount,
        TriplePattern tp0,
        TriplePattern tp1,
        TriplePattern tp2,
        TriplePattern tp3,
        Binding[] bindingStorage,
        char[] stringBuffer)
    {
        var sourceSpan = source.AsSpan();
        var bindingTable = new BindingTable(bindingStorage, stringBuffer);

        // Pattern 0
        var subj0 = tp0.Subject.IsVariable ? ReadOnlySpan<char>.Empty : sourceSpan.Slice(tp0.Subject.Start, tp0.Subject.Length);
        var pred0 = tp0.Predicate.IsVariable ? ReadOnlySpan<char>.Empty : sourceSpan.Slice(tp0.Predicate.Start, tp0.Predicate.Length);
        var obj0 = tp0.Object.IsVariable ? ReadOnlySpan<char>.Empty : sourceSpan.Slice(tp0.Object.Start, tp0.Object.Length);

        var enum0 = store.QueryCurrent(subj0, pred0, obj0, graphIri);
        try
        {
            while (enum0.MoveNext())
            {
                var t0 = enum0.Current;
                bindingTable.Clear();
                if (!TryBindTriple(tp0, t0, sourceSpan, ref bindingTable)) continue;

                if (patternCount == 1)
                {
                    bindingTable.Bind(graphVarName, graphIri);
                    results.Add(new MaterializedRow(bindingTable));
                    continue;
                }

                // Pattern 1
                var bc1 = bindingTable.Count;
                CollectPattern1(results, store, source, graphIri, graphVarName, patternCount,
                    tp1, tp2, tp3, ref bindingTable, bc1);
            }
        }
        finally
        {
            enum0.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CollectPattern1(
        List<MaterializedRow> results,
        QuadStore store,
        string source,
        ReadOnlySpan<char> graphIri,
        ReadOnlySpan<char> graphVarName,
        int patternCount,
        TriplePattern tp1,
        TriplePattern tp2,
        TriplePattern tp3,
        ref BindingTable bindingTable,
        int bindingCountBefore)
    {
        var sourceSpan = source.AsSpan();
        var subj1 = ResolveTerm(tp1.Subject, sourceSpan, ref bindingTable);
        var pred1 = ResolveTerm(tp1.Predicate, sourceSpan, ref bindingTable);
        var obj1 = ResolveTerm(tp1.Object, sourceSpan, ref bindingTable);

        var enum1 = store.QueryCurrent(subj1, pred1, obj1, graphIri);
        try
        {
            while (enum1.MoveNext())
            {
                var t1 = enum1.Current;
                bindingTable.TruncateTo(bindingCountBefore);
                if (!TryBindTriple(tp1, t1, sourceSpan, ref bindingTable)) continue;

                if (patternCount == 2)
                {
                    bindingTable.Bind(graphVarName, graphIri);
                    results.Add(new MaterializedRow(bindingTable));
                    continue;
                }

                // Pattern 2
                var bc2 = bindingTable.Count;
                CollectPattern2(results, store, source, graphIri, graphVarName, patternCount,
                    tp2, tp3, ref bindingTable, bc2);
            }
        }
        finally
        {
            enum1.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CollectPattern2(
        List<MaterializedRow> results,
        QuadStore store,
        string source,
        ReadOnlySpan<char> graphIri,
        ReadOnlySpan<char> graphVarName,
        int patternCount,
        TriplePattern tp2,
        TriplePattern tp3,
        ref BindingTable bindingTable,
        int bindingCountBefore)
    {
        var sourceSpan = source.AsSpan();
        var subj2 = ResolveTerm(tp2.Subject, sourceSpan, ref bindingTable);
        var pred2 = ResolveTerm(tp2.Predicate, sourceSpan, ref bindingTable);
        var obj2 = ResolveTerm(tp2.Object, sourceSpan, ref bindingTable);

        var enum2 = store.QueryCurrent(subj2, pred2, obj2, graphIri);
        try
        {
            while (enum2.MoveNext())
            {
                var t2 = enum2.Current;
                bindingTable.TruncateTo(bindingCountBefore);
                if (!TryBindTriple(tp2, t2, sourceSpan, ref bindingTable)) continue;

                if (patternCount == 3)
                {
                    bindingTable.Bind(graphVarName, graphIri);
                    results.Add(new MaterializedRow(bindingTable));
                    continue;
                }

                // Pattern 3
                var bc3 = bindingTable.Count;
                CollectPattern3(results, store, source, graphIri, graphVarName,
                    tp3, ref bindingTable, bc3);
            }
        }
        finally
        {
            enum2.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CollectPattern3(
        List<MaterializedRow> results,
        QuadStore store,
        string source,
        ReadOnlySpan<char> graphIri,
        ReadOnlySpan<char> graphVarName,
        TriplePattern tp3,
        ref BindingTable bindingTable,
        int bindingCountBefore)
    {
        var sourceSpan = source.AsSpan();
        var subj3 = ResolveTerm(tp3.Subject, sourceSpan, ref bindingTable);
        var pred3 = ResolveTerm(tp3.Predicate, sourceSpan, ref bindingTable);
        var obj3 = ResolveTerm(tp3.Object, sourceSpan, ref bindingTable);

        var enum3 = store.QueryCurrent(subj3, pred3, obj3, graphIri);
        try
        {
            while (enum3.MoveNext())
            {
                var t3 = enum3.Current;
                bindingTable.TruncateTo(bindingCountBefore);
                if (!TryBindTriple(tp3, t3, sourceSpan, ref bindingTable)) continue;

                bindingTable.Bind(graphVarName, graphIri);
                results.Add(new MaterializedRow(bindingTable));
            }
        }
        finally
        {
            enum3.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<char> ResolveTerm(Term term, ReadOnlySpan<char> source, scoped ref BindingTable bindings)
    {
        if (!term.IsVariable)
            return source.Slice(term.Start, term.Length);
        var varName = source.Slice(term.Start, term.Length);
        var idx = bindings.FindBinding(varName);
        return idx >= 0 ? bindings.GetString(idx) : ReadOnlySpan<char>.Empty;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryBindTerm(Term term, ReadOnlySpan<char> value, ReadOnlySpan<char> source, scoped ref BindingTable bindings)
    {
        if (!term.IsVariable) return true;
        var varName = source.Slice(term.Start, term.Length);
        var idx = bindings.FindBinding(varName);
        if (idx >= 0) return value.SequenceEqual(bindings.GetString(idx));
        bindings.Bind(varName, value);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryBindTriple(TriplePattern tp, ResolvedTemporalQuad triple, ReadOnlySpan<char> source, scoped ref BindingTable bindings)
    {
        if (!TryBindTerm(tp.Subject, triple.Subject, source, ref bindings)) return false;
        if (!TryBindTerm(tp.Predicate, triple.Predicate, source, ref bindings)) return false;
        if (!TryBindTerm(tp.Object, triple.Object, source, ref bindings)) return false;
        return true;
    }

    private static string[] GetAllNamedGraphs(QuadStore store)
    {
        var graphs = new List<string>();
        var graphEnum = store.GetNamedGraphs();
        while (graphEnum.MoveNext())
            graphs.Add(graphEnum.Current.ToString());
        return graphs.ToArray();
    }
}
