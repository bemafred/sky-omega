using System;
using System.Collections.Generic;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Sparql.Types;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// ADR-045 foundation: <see cref="PatternArray"/> completed to a uniformly-recursive index-child tree.
/// Validates that nested group-like headers (Group / Union / Optional) compose, that DIRECT-child
/// iteration skips nested subtrees, and that ONE recursive walk threads the active graph as a parameter
/// (default = the unnamed graph; a GRAPH header rebinds it for its subtree — "a default graph is also a graph").
/// </summary>
public class PatternTreeNestingTests
{
    [Fact]
    public void NestedGroups_DirectChildrenSkipSubtrees_AndOneWalkThreadsActiveGraph()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 32];
        var pa = new PatternArray(buffer);

        // { T1 . GRAPH <g> { T2 } . { { T3 } UNION { OPTIONAL { T4 } } } }
        // Each triple's id is carried in SubjectStart; the graph marker in GraphTermStart.
        int top = pa.BeginGroupHeader(PatternKind.GroupHeader);
        pa.AddTriple(TermType.Iri, 1, 1, TermType.Iri, 0, 0, TermType.Iri, 0, 0);   // T1 — default graph
        int g = pa.BeginGraph(TermType.Iri, 100, 1);                                 // GRAPH <g> (marker 100)
        pa.AddTriple(TermType.Iri, 2, 1, TermType.Iri, 0, 0, TermType.Iri, 0, 0);    // T2 — in graph g
        pa.EndGraph(g);
        int u = pa.BeginGroupHeader(PatternKind.UnionHeader);
        int b1 = pa.BeginGroupHeader(PatternKind.GroupHeader);
        pa.AddTriple(TermType.Iri, 3, 1, TermType.Iri, 0, 0, TermType.Iri, 0, 0);    // T3 — union branch 1
        pa.EndGroupHeader(b1);
        int b2 = pa.BeginGroupHeader(PatternKind.GroupHeader);
        int o = pa.BeginGroupHeader(PatternKind.OptionalHeader);
        pa.AddTriple(TermType.Iri, 4, 1, TermType.Iri, 0, 0, TermType.Iri, 0, 0);    // T4 — optional in branch 2
        pa.EndGroupHeader(o);
        pa.EndGroupHeader(b2);
        pa.EndGroupHeader(u);
        pa.EndGroupHeader(top);

        // Top has exactly three DIRECT children — T1, the GRAPH header, the UNION header — nested subtrees skipped.
        int direct = 0;
        var e = pa.EnumerateDirectChildren(top);
        while (e.MoveNext()) direct++;
        Assert.Equal(3, direct);

        // One recursive evaluator; the active graph is a parameter (0 = unnamed / default).
        var seen = new List<(int triple, int graph)>();
        Walk(pa, top, activeGraph: 0, seen);

        Assert.Equal(new[] { (1, 0), (2, 100), (3, 0), (4, 0) }, seen);
    }

    private static void Walk(PatternArray pa, int headerIndex, int activeGraph, List<(int, int)> seen)
    {
        var children = pa.EnumerateDirectChildren(headerIndex);
        while (children.MoveNext())
        {
            var slot = children.Current;
            int index = children.CurrentIndex;
            switch (slot.Kind)
            {
                case PatternKind.Triple:
                    seen.Add((slot.SubjectStart, activeGraph));
                    break;
                case PatternKind.GraphHeader:
                    Walk(pa, index, slot.GraphTermStart, seen); // GRAPH rebinds the active graph for its subtree
                    break;
                case PatternKind.GroupHeader:
                case PatternKind.UnionHeader:
                case PatternKind.OptionalHeader:
                case PatternKind.MinusHeader:
                    Walk(pa, index, activeGraph, seen);
                    break;
            }
        }
    }
}
