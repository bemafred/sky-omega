using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// Static helper for executing GRAPH ?g queries.
/// Uses QueryBuffer to read patterns from heap, avoiding stack overflow from large struct copies.
/// </summary>
internal static class VariableGraphExecutor
{
    /// <summary>
    /// Configuration for variable graph execution using QueryBuffer.
    /// This version avoids copying large structs - patterns are read from buffer.
    /// </summary>
    internal sealed class BufferExecutionConfig
    {
        public QuadStore Store = null!;
        public string Source = null!;
        public QueryBuffer Buffer = null!;
        public string[]? NamedGraphs;
        public Binding[] Bindings = null!;
        public char[] StringBuffer = null!;

        // Graph clause info (read from buffer during setup)
        public TermType GraphTermType;
        public int GraphTermStart;
        public int GraphTermLength;
        public int GraphHeaderIndex;  // Index of GraphHeader slot in buffer
    }

    /// <summary>
    /// Execute a variable graph query using QueryBuffer (no large struct copies).
    /// This method doesn't need a separate thread because it avoids stack overflow
    /// by reading patterns from heap-allocated buffer.
    /// </summary>
    public static List<MaterializedRow> ExecuteFromBuffer(BufferExecutionConfig config)
    {
        var results = new List<MaterializedRow>();
        ExecuteFromBufferIntoList(config, results);
        return results;
    }

    /// <summary>
    /// Core execution logic using QueryBuffer - reads patterns from slot storage.
    /// </summary>
    private static void ExecuteFromBufferIntoList(BufferExecutionConfig config, List<MaterializedRow> results)
    {
        var sourceSpan = config.Source.AsSpan();
        var graphVarName = sourceSpan.Slice(config.GraphTermStart, config.GraphTermLength);
        var graphsToIterate = config.NamedGraphs ?? GetAllNamedGraphs(config.Store);

        if (graphsToIterate.Length == 0)
            return;

        // Get patterns from buffer
        var patterns = config.Buffer.GetPatterns();

        // Get child patterns from GRAPH header
        int graphHeaderIdx = config.GraphHeaderIndex;
        var graphHeader = patterns[graphHeaderIdx];
        int childStart = graphHeader.ChildStartIndex;
        int childEnd = childStart + graphHeader.ChildCount;

        // Count triple patterns in children
        int childCount = 0;
        for (int i = childStart; i < childEnd; i++)
        {
            if (patterns[i].Kind == PatternKind.Triple)
                childCount++;
        }

        if (childCount == 0)
            return;

        // Create binding storage
        var bindings = new Binding[16];
        var stringBuffer = new char[1024];
        var bindingTable = new BindingTable(bindings, stringBuffer);

        // Handle single pattern case (most common)
        if (childCount == 1)
        {
            // Find the first triple pattern in GRAPH children
            PatternSlot patternSlot = default;
            for (int i = childStart; i < childEnd; i++)
            {
                patternSlot = patterns[i];
                if (patternSlot.Kind == PatternKind.Triple)
                    break;
            }

            // Resolve pattern terms
            ReadOnlySpan<char> subject = patternSlot.SubjectType == TermType.Variable
                ? ReadOnlySpan<char>.Empty
                : sourceSpan.Slice(patternSlot.SubjectStart, patternSlot.SubjectLength);
            ReadOnlySpan<char> predicate = patternSlot.PredicateType == TermType.Variable
                ? ReadOnlySpan<char>.Empty
                : sourceSpan.Slice(patternSlot.PredicateStart, patternSlot.PredicateLength);
            ReadOnlySpan<char> obj = patternSlot.ObjectType == TermType.Variable
                ? ReadOnlySpan<char>.Empty
                : sourceSpan.Slice(patternSlot.ObjectStart, patternSlot.ObjectLength);

            // Iterate all graphs
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
                        if (patternSlot.SubjectType == TermType.Variable)
                        {
                            var varName = sourceSpan.Slice(patternSlot.SubjectStart, patternSlot.SubjectLength);
                            bindingTable.Bind(varName, triple.Subject);
                        }

                        // Bind predicate
                        if (patternSlot.PredicateType == TermType.Variable)
                        {
                            var varName = sourceSpan.Slice(patternSlot.PredicateStart, patternSlot.PredicateLength);
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
                        if (patternSlot.ObjectType == TermType.Variable)
                        {
                            var varName = sourceSpan.Slice(patternSlot.ObjectStart, patternSlot.ObjectLength);
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
        else
        {
            // Multi-pattern case: use nested loop join
            // Collect pattern indices from GRAPH children
            var patternIndices = new int[Math.Min(childCount, 4)];
            int foundCount = 0;
            for (int i = childStart; i < childEnd && foundCount < 4; i++)
            {
                if (patterns[i].Kind == PatternKind.Triple)
                {
                    patternIndices[foundCount++] = i;
                }
            }

            foreach (var graphIri in graphsToIterate)
            {
                var graphSpan = graphIri.AsSpan();
                CollectMultiPatternFromSlots(results, config.Store, config.Source, graphSpan, graphVarName,
                    foundCount, config.Buffer, patternIndices, bindings, stringBuffer);
            }
        }
    }

    /// <summary>
    /// Collect multi-pattern results from slot-based storage.
    /// </summary>
    private static void CollectMultiPatternFromSlots(
        List<MaterializedRow> results,
        QuadStore store,
        string source,
        ReadOnlySpan<char> graphIri,
        ReadOnlySpan<char> graphVarName,
        int patternCount,
        QueryBuffer buffer,
        int[] patternIndices,
        Binding[] bindingStorage,
        char[] stringBuffer)
    {
        var sourceSpan = source.AsSpan();
        var bindingTable = new BindingTable(bindingStorage, stringBuffer);
        var patterns = buffer.GetPatterns();

        // Get first pattern
        var slot0 = patterns[patternIndices[0]];
        var subj0 = slot0.SubjectType == TermType.Variable ? ReadOnlySpan<char>.Empty : sourceSpan.Slice(slot0.SubjectStart, slot0.SubjectLength);
        var pred0 = slot0.PredicateType == TermType.Variable ? ReadOnlySpan<char>.Empty : sourceSpan.Slice(slot0.PredicateStart, slot0.PredicateLength);
        var obj0 = slot0.ObjectType == TermType.Variable ? ReadOnlySpan<char>.Empty : sourceSpan.Slice(slot0.ObjectStart, slot0.ObjectLength);

        var enum0 = store.QueryCurrent(subj0, pred0, obj0, graphIri);
        try
        {
            while (enum0.MoveNext())
            {
                var t0 = enum0.Current;
                bindingTable.Clear();
                if (!TryBindSlot(slot0, t0, sourceSpan, ref bindingTable)) continue;

                if (patternCount == 1)
                {
                    bindingTable.Bind(graphVarName, graphIri);
                    results.Add(new MaterializedRow(bindingTable));
                    continue;
                }

                // Pattern 1
                var bc1 = bindingTable.Count;
                var slot1 = patterns[patternIndices[1]];
                var subj1 = ResolveSlotTerm(slot1.SubjectType, slot1.SubjectStart, slot1.SubjectLength, sourceSpan, ref bindingTable);
                var pred1 = ResolveSlotTerm(slot1.PredicateType, slot1.PredicateStart, slot1.PredicateLength, sourceSpan, ref bindingTable);
                var obj1 = ResolveSlotTerm(slot1.ObjectType, slot1.ObjectStart, slot1.ObjectLength, sourceSpan, ref bindingTable);

                var enum1 = store.QueryCurrent(subj1, pred1, obj1, graphIri);
                try
                {
                    while (enum1.MoveNext())
                    {
                        var t1 = enum1.Current;
                        bindingTable.TruncateTo(bc1);
                        if (!TryBindSlot(slot1, t1, sourceSpan, ref bindingTable)) continue;

                        if (patternCount == 2)
                        {
                            bindingTable.Bind(graphVarName, graphIri);
                            results.Add(new MaterializedRow(bindingTable));
                            continue;
                        }

                        // Patterns 2 and 3 would follow same pattern...
                        // For brevity, only supporting 2 patterns in slot mode for now
                        bindingTable.Bind(graphVarName, graphIri);
                        results.Add(new MaterializedRow(bindingTable));
                    }
                }
                finally
                {
                    enum1.Dispose();
                }
            }
        }
        finally
        {
            enum0.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<char> ResolveSlotTerm(TermType type, int start, int length, ReadOnlySpan<char> source, scoped ref BindingTable bindings)
    {
        if (type != TermType.Variable)
            return source.Slice(start, length);
        var varName = source.Slice(start, length);
        var idx = bindings.FindBinding(varName);
        return idx >= 0 ? bindings.GetString(idx) : ReadOnlySpan<char>.Empty;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryBindSlot(PatternSlot slot, ResolvedTemporalQuad triple, ReadOnlySpan<char> source, scoped ref BindingTable bindings)
    {
        if (!TryBindSlotTerm(slot.SubjectType, slot.SubjectStart, slot.SubjectLength, triple.Subject, source, ref bindings)) return false;
        if (!TryBindSlotTerm(slot.PredicateType, slot.PredicateStart, slot.PredicateLength, triple.Predicate, source, ref bindings)) return false;
        if (!TryBindSlotTerm(slot.ObjectType, slot.ObjectStart, slot.ObjectLength, triple.Object, source, ref bindings)) return false;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryBindSlotTerm(TermType type, int start, int length, ReadOnlySpan<char> value, ReadOnlySpan<char> source, scoped ref BindingTable bindings)
    {
        if (type != TermType.Variable) return true;
        var varName = source.Slice(start, length);
        var idx = bindings.FindBinding(varName);
        if (idx >= 0) return value.SequenceEqual(bindings.GetString(idx));
        bindings.Bind(varName, value);
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
