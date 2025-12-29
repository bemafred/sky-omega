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
    /// Execute a variable graph query (GRAPH ?g) using completely flat iteration.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static QueryResults Execute(
        QuadStore store,
        string source,
        GraphClause graphClause,
        GraphPattern pattern,
        string[]? namedGraphs,
        Binding[] bindings,
        char[] stringBuffer,
        int limit,
        int offset,
        bool distinct,
        OrderByClause orderBy,
        GroupByClause groupBy,
        SelectClause selectClause,
        HavingClause having)
    {
        var results = new List<MaterializedRow>();
        var sourceSpan = source.AsSpan();
        var graphVarName = sourceSpan.Slice(graphClause.Graph.Start, graphClause.Graph.Length);
        var graphsToIterate = namedGraphs ?? GetAllNamedGraphs(store);
        var patternCount = graphClause.PatternCount;

        if (patternCount == 0 || graphsToIterate.Length == 0)
            return QueryResults.Empty();

        // Get patterns from graph clause (up to 4)
        var tp0 = graphClause.GetPattern(0);
        var tp1 = patternCount > 1 ? graphClause.GetPattern(1) : default;
        var tp2 = patternCount > 2 ? graphClause.GetPattern(2) : default;
        var tp3 = patternCount > 3 ? graphClause.GetPattern(3) : default;

        // Create binding storage once
        var scanBindings = new Binding[16];
        var scanStringBuffer = new char[1024];

        foreach (var graphIri in graphsToIterate)
        {
            var graphSpan = graphIri.AsSpan();

            if (patternCount == 1)
            {
                CollectSinglePattern(results, store, source, graphSpan, graphVarName, tp0, scanBindings, scanStringBuffer);
            }
            else
            {
                CollectMultiPattern(results, store, source, graphSpan, graphVarName, patternCount,
                    tp0, tp1, tp2, tp3, scanBindings, scanStringBuffer);
            }
        }

        if (results.Count == 0)
            return QueryResults.Empty();

        return QueryResults.FromMaterialized(results, pattern, sourceSpan, store,
            bindings, stringBuffer, limit, offset, distinct, orderBy, groupBy, selectClause, having);
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
