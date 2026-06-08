using System.Collections.Generic;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Sparql.Patterns;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// ADR-045 Step 2: the recursive parser (<see cref="SparqlParser.ParsePatternTree"/>) produces the
/// <see cref="PatternArray"/> tree directly from source. These tests assert the TREE SHAPE and the
/// ACTIVE-GRAPH threading — in particular that BIND / VALUES / property-list shorthand inside GRAPH are
/// children of the GraphHeader BY CONSTRUCTION (the divergence-class defects D1/D2 are unrepresentable here,
/// because GRAPH recurses into the same body parser as the default graph — "a default graph is also a graph").
/// </summary>
public class PatternTreeParserTests
{
    [Fact]
    public void BindInsideGraph_BecomesAChildOfTheGraphHeader_D2FixedByConstruction()
    {
        // D2 on the shipping path: `BIND` inside `GRAPH` fails to parse at all. Here it is just another child.
        const string source = "{ GRAPH <g> { ?s <p> ?v . BIND(\"x\" AS ?label) } }";
        var buffer = new byte[PatternSlot.Size * 32];
        var pa = new PatternArray(buffer);
        var parser = new SparqlParser(source.AsSpan());
        int root = parser.ParsePatternTree(ref pa);

        // root group has exactly one direct child: the GraphHeader.
        var top = pa.EnumerateDirectChildren(root);
        Assert.True(top.MoveNext());
        Assert.Equal(PatternKind.GraphHeader, top.Current.Kind);
        int graph = top.CurrentIndex;
        Assert.Equal("<g>", source.Substring(top.Current.GraphTermStart, top.Current.GraphTermLength));
        Assert.False(top.MoveNext());

        // The GraphHeader's two direct children are the triple and the BIND — both inside the graph.
        var inGraph = pa.EnumerateDirectChildren(graph);
        Assert.True(inGraph.MoveNext());
        Assert.Equal(PatternKind.Triple, inGraph.Current.Kind);
        Assert.True(inGraph.MoveNext());
        Assert.Equal(PatternKind.Bind, inGraph.Current.Kind);
        Assert.Equal("?label", source.Substring(inGraph.Current.BindVarStart, inGraph.Current.BindVarLength));
        Assert.Equal("\"x\"", source.Substring(inGraph.Current.BindExprStart, inGraph.Current.BindExprLength));
        Assert.False(inGraph.MoveNext());
    }

    [Fact]
    public void ValuesInsideGraph_BecomesAChildOfTheGraphHeader_D1FixedByConstruction()
    {
        // D1 on the shipping path: `VALUES` inside `GRAPH` lands on the parent and is gated off, so it does not
        // constrain. Here the ValuesHeader is a child of the GraphHeader, joined within the graph scope.
        const string source = "{ GRAPH <g> { VALUES ?s { <urn:x> } . ?s <p> ?o } }";
        var buffer = new byte[PatternSlot.Size * 32];
        var pa = new PatternArray(buffer);
        var parser = new SparqlParser(source.AsSpan());
        int root = parser.ParsePatternTree(ref pa);

        var top = pa.EnumerateDirectChildren(root);
        Assert.True(top.MoveNext());
        Assert.Equal(PatternKind.GraphHeader, top.Current.Kind);
        int graph = top.CurrentIndex;

        var inGraph = pa.EnumerateDirectChildren(graph);
        Assert.True(inGraph.MoveNext());
        Assert.Equal(PatternKind.ValuesHeader, inGraph.Current.Kind);
        // The VALUES leaf carries the full source span (re-parsed on evaluation for single- and multi-variable forms).
        Assert.Equal("VALUES ?s { <urn:x> }", source.Substring(inGraph.Current.ValuesVarStart, inGraph.Current.ValuesVarLength));
        Assert.True(inGraph.MoveNext());
        Assert.Equal(PatternKind.Triple, inGraph.Current.Kind);
        Assert.False(inGraph.MoveNext());
    }

    [Fact]
    public void PropertyListShorthandInsideGraph_ExpandsToSiblingTriples_1_7_71FixedByConstruction()
    {
        // 1.7.71 on the shipping path: the ';' property-list shorthand was dropped inside GRAPH. Here the body
        // parser handles it, so two triples with the same subject become two sibling children of the graph.
        const string source = "{ GRAPH <g> { ?s <p1> ?a ; <p2> ?b } }";
        var buffer = new byte[PatternSlot.Size * 32];
        var pa = new PatternArray(buffer);
        var parser = new SparqlParser(source.AsSpan());
        int root = parser.ParsePatternTree(ref pa);

        var top = pa.EnumerateDirectChildren(root);
        Assert.True(top.MoveNext());
        int graph = top.CurrentIndex;

        var inGraph = pa.EnumerateDirectChildren(graph);
        Assert.True(inGraph.MoveNext());
        Assert.Equal(PatternKind.Triple, inGraph.Current.Kind);
        Assert.Equal("?s", source.Substring(inGraph.Current.SubjectStart, inGraph.Current.SubjectLength));
        Assert.Equal("<p1>", source.Substring(inGraph.Current.PredicateStart, inGraph.Current.PredicateLength));
        Assert.True(inGraph.MoveNext());
        Assert.Equal(PatternKind.Triple, inGraph.Current.Kind);
        Assert.Equal("?s", source.Substring(inGraph.Current.SubjectStart, inGraph.Current.SubjectLength));
        Assert.Equal("<p2>", source.Substring(inGraph.Current.PredicateStart, inGraph.Current.PredicateLength));
        Assert.False(inGraph.MoveNext());
    }

    [Fact]
    public void MultiBranchUnion_WrapsTheFirstBranch_AndAppendsTheRest()
    {
        // Exercises PatternArray.WrapSubtree: UNION's header kind is only known after the first branch parses.
        const string source = "{ { ?a <p> ?b } UNION { ?c <p> ?d } UNION { ?e <p> ?f } }";
        var buffer = new byte[PatternSlot.Size * 32];
        var pa = new PatternArray(buffer);
        var parser = new SparqlParser(source.AsSpan());
        int root = parser.ParsePatternTree(ref pa);

        // The root group has a single direct child: the UnionHeader.
        var top = pa.EnumerateDirectChildren(root);
        Assert.True(top.MoveNext());
        Assert.Equal(PatternKind.UnionHeader, top.Current.Kind);
        int union = top.CurrentIndex;
        Assert.False(top.MoveNext());

        // The union has exactly three GroupHeader branches, each with one triple.
        int branchCount = 0;
        var branches = pa.EnumerateDirectChildren(union);
        while (branches.MoveNext())
        {
            Assert.Equal(PatternKind.GroupHeader, branches.Current.Kind);
            int triples = 0;
            var inBranch = pa.EnumerateDirectChildren(branches.CurrentIndex);
            while (inBranch.MoveNext())
            {
                Assert.Equal(PatternKind.Triple, inBranch.Current.Kind);
                triples++;
            }
            Assert.Equal(1, triples);
            branchCount++;
        }
        Assert.Equal(3, branchCount);
    }

    [Fact]
    public void MixedNesting_OneWalkThreadsTheActiveGraph()
    {
        // Mirror of PatternTreeNestingTests but driven from SPARQL TEXT through the recursive parser:
        //   { ?a <p> ?b . GRAPH <g> { ?s <p> ?o } . { { ?c <p> ?d } UNION { OPTIONAL { ?e <p> ?f } } } }
        // Only the triple inside GRAPH sees a named active graph; every other triple sees the default (unnamed) graph.
        const string source =
            "{ ?a <p> ?b . GRAPH <g> { ?s <p> ?o } . { { ?c <p> ?d } UNION { OPTIONAL { ?e <p> ?f } } } }";
        var buffer = new byte[PatternSlot.Size * 64];
        var pa = new PatternArray(buffer);
        var parser = new SparqlParser(source.AsSpan());
        int root = parser.ParsePatternTree(ref pa);

        // Top has three direct children: a triple, the GraphHeader, and the nested group wrapping the union.
        var kinds = new List<PatternKind>();
        var top = pa.EnumerateDirectChildren(root);
        while (top.MoveNext()) kinds.Add(top.Current.Kind);
        Assert.Equal(new[] { PatternKind.Triple, PatternKind.GraphHeader, PatternKind.GroupHeader }, kinds);

        // One recursive walk; the active graph is a parameter (the default graph is start = -1).
        var seen = new List<(string subject, string graph)>();
        Walk(pa, source, root, graphStart: -1, graphLength: 0, seen);

        Assert.Equal(new[]
        {
            ("?a", "(default)"),
            ("?s", "<g>"),
            ("?c", "(default)"),
            ("?e", "(default)"),
        }, seen);
    }

    /// <summary>
    /// One recursive evaluator over the tree. A GraphHeader rebinds the active graph for its subtree; every other
    /// group-like header threads the active graph through unchanged. This is the single walk the unified executor
    /// will perform (active graph as a parameter), exercised here against the parser's output.
    /// </summary>
    private static void Walk(PatternArray pa, string source, int headerIndex, int graphStart, int graphLength,
        List<(string, string)> seen)
    {
        var children = pa.EnumerateDirectChildren(headerIndex);
        while (children.MoveNext())
        {
            var slot = children.Current;
            int index = children.CurrentIndex;
            switch (slot.Kind)
            {
                case PatternKind.Triple:
                    string subject = source.Substring(slot.SubjectStart, slot.SubjectLength);
                    string graph = graphStart < 0 ? "(default)" : source.Substring(graphStart, graphLength);
                    seen.Add((subject, graph));
                    break;
                case PatternKind.GraphHeader:
                    Walk(pa, source, index, slot.GraphTermStart, slot.GraphTermLength, seen);
                    break;
                case PatternKind.GroupHeader:
                case PatternKind.UnionHeader:
                case PatternKind.OptionalHeader:
                case PatternKind.MinusHeader:
                    Walk(pa, source, index, graphStart, graphLength, seen);
                    break;
            }
        }
    }
}
