// probe-pattern-arena.cs — EEE Emergence probe for ADR-045 (B)
//
// Question: can a pooled, index-child node arena give us a REAL nesting pattern tree
// (GRAPH / UNION / OPTIONAL / nested groups) with:
//   (1) active-graph threaded as a single evaluator PARAMETER (default = unnamed graph),
//   (2) an explicit, calibrated recursion-depth guardrail, and
//   (3) ~0 steady-state allocation under pooling (controlled, not stack-only)?
//
// Standalone design prototype — no Mercury dependency. Run:
//   dotnet run --no-cache tools/probe-pattern-arena.cs
//
// This validates the SHAPE of the primitive, not the parser. If it holds, the arena
// becomes the foundation for the unified parse + execute path.

using System;
using System.Buffers;
using System.Collections.Generic;

const int MaxDepth = 128; // guardrail (calibrated separately from the W3C corpus)

var arena = new PatternArena(initialNodes: 256);
var sink = new List<(int s, int p, int o, int graph)>();

// --- Demonstration 1: nesting + active-graph as a parameter, ONE evaluator -------------
// Shape:  { GRAPH 7 { { (1 2 3) } UNION { OPTIONAL { (4 5 6) } } }  .  (8 9 10) }
//         the (8 9 10) triple sits at top level => DEFAULT (unnamed) graph.
int root = BuildSampleTree(arena);
sink.Clear();
Eval(arena, root, activeGraph: 0, depth: 0, sink);

Console.WriteLine("== Demo 1: active-graph threading (one recursive evaluator, graph as a parameter) ==");
foreach (var t in sink)
    Console.WriteLine($"   triple ({t.s} {t.p} {t.o})  ->  graph {(t.graph == 0 ? "DEFAULT(unnamed)" : t.graph.ToString())}");

// --- Demonstration 2: explicit depth guardrail ----------------------------------------
arena.Reset();
int deep = BuildDeepNest(arena, levels: 200);
Console.Write("\n== Demo 2: depth guardrail on a 200-level synthetic nest -> ");
try { Eval(arena, deep, 0, 0, null); Console.WriteLine($"walked OK (no trip; guard={MaxDepth})"); }
catch (InvalidOperationException ex) { Console.WriteLine($"tripped as designed: {ex.Message}"); }

// --- Demonstration 3: steady-state allocation under pooling ---------------------------
for (int w = 0; w < 200; w++) { arena.Reset(); var r = BuildSampleTree(arena); Eval(arena, r, 0, 0, null); } // warm up
long before = GC.GetAllocatedBytesForCurrentThread();
const int N = 200_000;
for (int i = 0; i < N; i++) { arena.Reset(); var r = BuildSampleTree(arena); Eval(arena, r, 0, 0, null); }
long after = GC.GetAllocatedBytesForCurrentThread();
Console.WriteLine($"\n== Demo 3: {(after - before)} bytes allocated over {N:N0} build+eval cycles  =  {(double)(after - before) / N:F4} bytes/cycle");
Console.WriteLine($"   (arena rented once via ArrayPool; Reset() reuses it; child lists built with stackalloc spans)");

// Returns node count visited; throws if depth guard is exceeded.
int Eval(PatternArena a, int node, int activeGraph, int depth, List<(int, int, int, int)>? outSink)
{
    if (depth > MaxDepth)
        throw new InvalidOperationException($"pattern nesting depth {depth} exceeds MaxDepth {MaxDepth}");
    ref var n = ref a.NodeRef(node);
    int visited = 1;
    switch (n.Kind)
    {
        case NodeKind.Triple:
            outSink?.Add((n.A, n.B, n.C, activeGraph)); // a "scan" against the current active graph
            break;
        case NodeKind.Graph: // <-- the whole point: rebind active graph for the subtree
            for (int i = 0; i < n.ChildCount; i++)
                visited += Eval(a, a.Child(node, i), activeGraph: n.A, depth + 1, outSink);
            break;
        default: // Group / Union / Optional: same evaluator, active graph unchanged
            for (int i = 0; i < n.ChildCount; i++)
                visited += Eval(a, a.Child(node, i), activeGraph, depth + 1, outSink);
            break;
    }
    return visited;
}

int BuildSampleTree(PatternArena a)
{
    int t123 = a.Add(NodeKind.Triple, 1, 2, 3);
    Span<int> g1kids = stackalloc int[] { t123 };
    int grp1 = a.Add(NodeKind.Group); a.SetChildren(grp1, g1kids);

    int t456 = a.Add(NodeKind.Triple, 4, 5, 6);
    Span<int> optkids = stackalloc int[] { t456 };
    int opt = a.Add(NodeKind.Optional); a.SetChildren(opt, optkids);
    Span<int> g2kids = stackalloc int[] { opt };
    int grp2 = a.Add(NodeKind.Group); a.SetChildren(grp2, g2kids);

    Span<int> ukids = stackalloc int[] { grp1, grp2 };
    int union = a.Add(NodeKind.Union); a.SetChildren(union, ukids);

    Span<int> gkids = stackalloc int[] { union };
    int graph = a.Add(NodeKind.Graph, 7); a.SetChildren(graph, gkids); // GRAPH 7 { ... }

    int top = a.Add(NodeKind.Triple, 8, 9, 10); // default-graph triple
    Span<int> rootkids = stackalloc int[] { graph, top };
    int root = a.Add(NodeKind.Group); a.SetChildren(root, rootkids);
    return root;
}

int BuildDeepNest(PatternArena a, int levels)
{
    int leaf = a.Add(NodeKind.Triple, 1, 1, 1);
    int cur = leaf;
    Span<int> kids = stackalloc int[1]; // hoisted out of the loop (CA2014: stackalloc-in-loop grows the frame)
    for (int i = 0; i < levels; i++)
    {
        kids[0] = cur;
        int g = a.Add(NodeKind.Group); a.SetChildren(g, kids);
        cur = g;
    }
    return cur;
}

enum NodeKind : byte { Group, Triple, Graph, Union, Optional }

// Index-child arena: nodes + a child-index list, both rented from ArrayPool and reused via Reset().
// No per-node heap objects, no pointers; children referenced by index. Span-friendly.
sealed class PatternArena
{
    private PatternNode[] _nodes;
    private int[] _children;
    private int _nodeCount, _childCount;

    public PatternArena(int initialNodes)
    {
        _nodes = ArrayPool<PatternNode>.Shared.Rent(initialNodes);
        _children = ArrayPool<int>.Shared.Rent(initialNodes * 2);
    }

    public void Reset() { _nodeCount = 0; _childCount = 0; }
    public int NodeCount => _nodeCount;

    public int Add(NodeKind kind, int a = 0, int b = 0, int c = 0)
    {
        if (_nodeCount >= _nodes.Length) Grow(ref _nodes, _nodeCount);
        ref var n = ref _nodes[_nodeCount];
        n.Kind = kind; n.A = a; n.B = b; n.C = c; n.FirstChild = -1; n.ChildCount = 0;
        return _nodeCount++;
    }

    public void SetChildren(int parent, ReadOnlySpan<int> kids)
    {
        while (_childCount + kids.Length > _children.Length) Grow(ref _children, _childCount);
        int start = _childCount;
        for (int i = 0; i < kids.Length; i++) _children[_childCount++] = kids[i];
        _nodes[parent].FirstChild = start;
        _nodes[parent].ChildCount = kids.Length;
    }

    public ref PatternNode NodeRef(int i) => ref _nodes[i];
    public int Child(int parent, int i) => _children[_nodes[parent].FirstChild + i];

    private static void Grow<T>(ref T[] buf, int used)
    {
        var bigger = ArrayPool<T>.Shared.Rent(buf.Length * 2);
        Array.Copy(buf, bigger, used);
        ArrayPool<T>.Shared.Return(buf);
        buf = bigger;
    }
}

struct PatternNode
{
    public NodeKind Kind;
    public int A, B, C;        // Triple: s,p,o (term refs in the real thing); Graph: graph term in A
    public int FirstChild;     // index into the arena child list, -1 if none
    public int ChildCount;
}
