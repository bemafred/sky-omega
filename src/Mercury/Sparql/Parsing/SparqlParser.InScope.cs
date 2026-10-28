using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Parsing;

internal ref partial struct SparqlParser
{
    /// <summary>
    /// SPARQL 1.1 §18.2.1: adds the in-scope variables of the slots <c>[first, end)</c> of a tree parsed over this
    /// parser's source. A triple pattern's variables, a GRAPH variable, a BIND variable, a VALUES block's variables and
    /// a SERVICE pattern's variables are in scope, and every group, OPTIONAL and UNION passes its children's out. A
    /// sub-SELECT passes out what it projects (with <c>SELECT *</c>, its WHERE's variables and its VALUES'). MINUS and
    /// [NOT] EXISTS bodies, and FILTER expressions, bind nothing outside themselves. A blank node is not a variable.
    /// This is the one reading of the rule: <c>SELECT *</c>, <c>DESCRIBE *</c>, a sub-SELECT * and the BIND scope
    /// check all use it. The cursor is not moved.
    /// </summary>
    internal readonly void AddInScopeVariables(scoped ref PatternArray pa, int first, int end, scoped ref InScopeVariables into)
    {
        for (int i = first; i < end; i++)
        {
            var slot = pa[i];
            switch (slot.Kind)
            {
                case PatternKind.Triple:
                    AddIfVariable(slot.SubjectType, slot.SubjectStart, slot.SubjectLength, ref into);
                    AddIfVariable(slot.PredicateType, slot.PredicateStart, slot.PredicateLength, ref into);
                    AddIfVariable(slot.ObjectType, slot.ObjectStart, slot.ObjectLength, ref into);
                    break;
                case PatternKind.Bind:
                    AddIfVariable(TermType.Variable, slot.BindVarStart, slot.BindVarLength, ref into);
                    break;
                case PatternKind.GraphHeader:
                    AddIfVariable(slot.GraphTermType, slot.GraphTermStart, slot.GraphTermLength, ref into);
                    break; // its children follow
                case PatternKind.ValuesHeader:
                    AddDataBlockVariables(slot.ValuesVarStart, ref into);
                    break;
                case PatternKind.MinusHeader:
                    i = slot.ChildStartIndex + slot.ChildCount - 1;
                    break;
                case PatternKind.ExistsHeader:
                case PatternKind.NotExistsHeader:
                    i = slot.ExistsChildStart + slot.ExistsChildCount - 1;
                    break;
                case PatternKind.SubSelectHeader:
                    if (!AddSubSelectProjection(slot.GraphTermStart, slot.SubSelectModifiersStart, ref into))
                        i = slot.ChildStartIndex + slot.ChildCount - 1; // it projects by name: its WHERE stays inside
                    break; // with SELECT *, the walk goes on into its WHERE
                case PatternKind.ServiceHeader:
                    AddServiceVariables(slot.GraphTermStart, ref into);
                    break;
            }
        }
    }

    /// <summary>The in-scope variables of the whole WHERE group at <paramref name="whereGroupStart"/> (its '{').</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal readonly void AddInScopeVariables(int whereGroupStart, scoped ref InScopeVariables into)
    {
        const int treeBytes = PatternSlot.Size * 1024;
        var treeBuffer = ArrayPool<byte>.Shared.Rent(treeBytes);
        try
        {
            var tree = new PatternArray(treeBuffer.AsSpan(0, treeBytes));
            int root = new SparqlParser(_source).ParsePatternTreeAt(whereGroupStart, ref tree);
            var rootSlot = tree[root]; // a group or a sub-SELECT: its subtree is its child range
            AddInScopeVariables(ref tree, root, rootSlot.ChildStartIndex + rootSlot.ChildCount, ref into);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(treeBuffer);
        }
    }

    /// <summary>The variables of the VALUES clause at <paramref name="valuesStart"/> (its keyword).</summary>
    internal readonly void AddDataBlockVariables(int valuesStart, scoped ref InScopeVariables into)
    {
        var reader = new DataBlockReader(_source, valuesStart);
        while (reader.MoveNextVariable(out int start, out int length))
            into.Add(start, length);
    }

    private static void AddIfVariable(TermType type, int start, int length, scoped ref InScopeVariables into)
    {
        // Synthetic terms (expanded collections, quoted triples) have negative offsets and are never variables
        if (type == TermType.Variable && start >= 0 && length > 1)
            into.Add(start, length);
    }

    /// <summary>
    /// Adds what the sub-SELECT at <paramref name="selectStart"/> projects. Returns true for <c>SELECT *</c>, whose
    /// projection is its WHERE's in-scope variables (the caller walks on into them) and its trailing VALUES' variables.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)] // the clauses are large structs; keep them out of the walk's frame
    private readonly bool AddSubSelectProjection(int selectStart, int modifiersStart, scoped ref InScopeVariables into)
    {
        var sub = new SparqlParser(_source).ParseSubSelectClausesAt(selectStart, modifiersStart);
        if (sub.Select.SelectAll)
        {
            if (sub.Values.IsPresent)
                AddDataBlockVariables(sub.Values.Start, ref into);
            return true;
        }
        for (int v = 0; v < sub.Select.ProjectedVariableCount; v++)
        {
            var (start, length) = sub.Select.GetProjectedVariable(v);
            AddIfVariable(TermType.Variable, start, length, ref into);
        }
        for (int a = 0; a < sub.Select.AggregateCount; a++)
        {
            var aggregate = sub.Select.GetAggregate(a);
            AddIfVariable(TermType.Variable, aggregate.AliasStart, aggregate.AliasLength, ref into);
        }
        return false;
    }

    /// <summary>
    /// [59] ServiceGraphPattern ::= 'SERVICE' 'SILENT'? VarOrIri GroupGraphPattern: adds the in-scope variables of the
    /// group of the SERVICE at <paramref name="serviceStart"/>. The endpoint variable is the caller's to bind.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private readonly void AddServiceVariables(int serviceStart, scoped ref InScopeVariables into)
    {
        var head = new SparqlParser(_source);
        head._position = serviceStart;
        head.ConsumeKeyword("SERVICE");
        head.SkipWhitespace();
        if (KeywordIs(head.PeekSpan(6), "SILENT"))
        {
            head.ConsumeKeyword("SILENT");
            head.SkipWhitespace();
        }
        head.ParseTerm();
        head.SkipWhitespace();
        AddInScopeVariables(head._position, ref into);
    }
}
