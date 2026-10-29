using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// The matches of one store read in the graph a pattern reads: a single graph, or the default graph a dataset's FROM
/// clauses define, which is the RDF merge of the FROM graphs (SPARQL 1.1 §13.2) and is read as one graph
/// (<see cref="GraphMerge"/>). Every read a <see cref="Operators.TriplePatternScan"/> makes is one of these, so a
/// property path walks the merge, from one FROM graph into another.
/// </summary>
/// <remarks>
/// The scan holds several of these at once, by value, so the struct is the single graph's read and one reference:
/// the merge's state is in <see cref="GraphMerge"/>, allocated only for a merge.
/// </remarks>
internal struct MergedGraphEnumerator
{
    private TemporalResultEnumerator _single;
    private readonly GraphMerge? _merge;

    /// <summary>The matches of one graph.</summary>
    public MergedGraphEnumerator(TemporalResultEnumerator single)
    {
        _single = single;
        _merge = null;
    }

    /// <summary>The matches of a merge of graphs.</summary>
    public MergedGraphEnumerator(GraphMerge merge)
    {
        _single = default;
        _merge = merge;
    }

    public bool MoveNext() => _merge?.MoveNext() ?? _single.MoveNext();

    public ResolvedTemporalQuad Current => _merge == null ? _single.Current : _merge.Current;

    public void Dispose()
    {
        if (_merge == null)
            _single.Dispose();
        else
            _merge.Dispose();
    }
}

/// <summary>
/// The read of the RDF merge of a dataset's FROM graphs (SPARQL 1.1 §13.2), as one graph. The FROM graphs are read in
/// turn, and a triple an earlier one holds (under the same temporal scope) is skipped, so a triple in two FROM graphs
/// is in the merge once. Mercury's blank nodes are store-wide, so a triple with a blank node is the same triple in
/// either graph. With no graphs (FROM NAMED alone) the merge is the empty graph.
/// </summary>
/// <remarks>
/// The reads are opened up front, one per graph: each resolves its terms to atoms when it is opened, so none keeps a
/// span to the terms.
/// </remarks>
internal sealed class GraphMerge
{
    private readonly TemporalResultEnumerator[] _reads;
    private readonly string[] _graphs;
    private readonly QuadStore _store;
    private readonly TemporalScope _scope;
    private int _index;

    /// <param name="reads">One read per graph, in the order of <paramref name="graphs"/>.</param>
    /// <param name="graphs">The FROM graphs.</param>
    /// <param name="store">The store a match is looked up in, in the earlier graphs.</param>
    /// <param name="scope">The temporal scope of the reads and of the lookups.</param>
    public GraphMerge(TemporalResultEnumerator[] reads, string[] graphs, QuadStore store, TemporalScope scope)
    {
        _reads = reads;
        _graphs = graphs;
        _store = store;
        _scope = scope;
    }

    public bool MoveNext()
    {
        while (_index < _reads.Length)
        {
            ref var read = ref _reads[_index];
            while (read.MoveNext())
            {
                if (_index == 0 || !InAnEarlierGraph(read.Current))
                    return true;
            }
            _index++;
        }
        return false;
    }

    public ResolvedTemporalQuad Current => _reads[_index].Current;

    /// <summary>Whether a FROM graph before the one being read holds <paramref name="quad"/>'s triple.</summary>
    private bool InAnEarlierGraph(ResolvedTemporalQuad quad)
    {
        for (int i = 0; i < _index; i++)
        {
            var lookup = _scope.Query(_store, quad.Subject, quad.Predicate, quad.Object, _graphs[i]);
            try
            {
                if (lookup.MoveNext())
                    return true;
            }
            finally
            {
                lookup.Dispose();
            }
        }
        return false;
    }

    public void Dispose()
    {
        for (int i = 0; i < _reads.Length; i++)
            _reads[i].Dispose();
    }
}
