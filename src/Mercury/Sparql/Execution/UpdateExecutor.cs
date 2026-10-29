using System;
using System.Collections.Generic;
using System.Threading;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Execution.Federated;
using SkyOmega.Mercury.Sparql.Execution.Operators;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// SPARQL Update executor for INSERT/DELETE/CLEAR/DROP/COPY/MOVE/ADD operations.
///
/// Supported operations:
/// - INSERT DATA: Add triples/quads directly
/// - DELETE DATA: Delete triples/quads directly
/// - CLEAR: Clear graphs (DEFAULT, NAMED, ALL, or specific graph)
/// - DROP: Drop graphs (same targets as CLEAR)
/// - CREATE: Create empty named graph (no-op in this implementation)
/// - COPY: Copy triples from source to destination graph
/// - MOVE: Move triples from source to destination graph
/// - ADD: Add triples from source graph to destination graph
///
/// - DELETE WHERE / DELETE-INSERT WHERE: the WHERE is evaluated by TreeJoinExecutor over the pattern tree, as a
///   query's WHERE is (ADR-052), and each solution instantiates the templates once
///
/// LOAD support:
/// - LOAD: Requires LoadExecutor instance (optional parameter)
/// </summary>
internal class UpdateExecutor
{
    private readonly QuadStore _store;
    private readonly string _source;
    private readonly UpdateOperation _update;
    private readonly LoadExecutor? _loadExecutor;
    private string? _expandedTerm; // Buffer for expanded prefixed names
    private string? _literalScratch; // ADR-044: scratch owner for canonicalized literals

    // Blank-node scope: within one operation's data, or one solution of a template, a label (or a template node)
    // names one node; every scope mints new nodes (SPARQL 1.1 Update §3.1.1, §3.1.3)
    private Rdf.BlankNodeScope _bnodeScope = new();

    public UpdateExecutor(QuadStore store, ReadOnlySpan<char> source, UpdateOperation update)
        : this(store, source, update, null)
    {
    }

    public UpdateExecutor(QuadStore store, ReadOnlySpan<char> source, UpdateOperation update, LoadExecutor? loadExecutor)
    {
        _store = store;
        _source = source.ToString();
        _update = update;
        _loadExecutor = loadExecutor;
    }

    /// <summary>
    /// Execute the update operation.
    /// Returns the result with success status and affected count.
    /// </summary>
    public UpdateResult Execute()
    {
        return _update.Type switch
        {
            QueryType.InsertData => ExecuteInsertData(),
            QueryType.DeleteData => ExecuteDeleteData(),
            QueryType.DeleteWhere => ExecuteDeleteWhere(),
            QueryType.Modify => ExecuteModify(),
            QueryType.Clear => ExecuteClear(),
            QueryType.Drop => ExecuteDrop(),
            QueryType.Create => ExecuteCreate(),
            QueryType.Copy => ExecuteCopy(),
            QueryType.Move => ExecuteMove(),
            QueryType.Add => ExecuteAdd(),
            QueryType.Load => ExecuteLoad(),
            _ => new UpdateResult { Success = false, ErrorMessage = $"Unknown update type: {_update.Type}" }
        };
    }

    /// <summary>
    /// Execute a sequence of update operations — the one path for every update request (ADR-046);
    /// a single operation is a sequence of length 1.
    /// Operations run in order. A failing operation stops the sequence; a SILENT operation
    /// reports its own failure as success, so it does not stop it. Each operation commits on
    /// its own, so operations before a failure keep their effects: the request is sequential
    /// with abort, not all-or-nothing.
    /// </summary>
    /// <param name="store">The quad store.</param>
    /// <param name="source">The original source string containing all operations.</param>
    /// <param name="operations">The parsed update operations.</param>
    /// <param name="loadExecutor">Optional LoadExecutor for LOAD operations.</param>
    /// <returns>Combined result with total affected count.</returns>
    public static UpdateResult ExecuteSequence(
        QuadStore store,
        ReadOnlySpan<char> source,
        UpdateOperation[] operations,
        LoadExecutor? loadExecutor = null)
    {
        if (operations == null || operations.Length == 0)
            return new UpdateResult { Success = true, AffectedCount = 0 };

        var totalAffected = 0;

        for (int i = 0; i < operations.Length; i++)
        {
            QueryCancellation.ThrowIfCancellationRequested();

            var executor = new UpdateExecutor(store, source, operations[i], loadExecutor);
            var result = executor.Execute();

            if (!result.Success)
            {
                return new UpdateResult
                {
                    Success = false,
                    AffectedCount = totalAffected,
                    ErrorMessage = operations.Length == 1
                        ? result.ErrorMessage
                        : $"Update operation {i + 1} of {operations.Length} failed; operations after it were not run: {result.ErrorMessage}"
                };
            }

            totalAffected += result.AffectedCount;
        }

        return new UpdateResult { Success = true, AffectedCount = totalAffected };
    }

    /// <summary>INSERT DATA: the data instantiated once, as a template with no variables, in one batch.</summary>
    private UpdateResult ExecuteInsertData()
    {
        var quads = InstantiateData(_update.InsertData);
        if (quads.Count == 0)
            return new UpdateResult { Success = true, AffectedCount = 0 };

        _store.BeginBatch();
        try
        {
            foreach (var (s, p, o, g) in quads)
                _store.AddCurrentBatched(s, p, o, g);
            _store.CommitBatch();
            return new UpdateResult { Success = true, AffectedCount = quads.Count };
        }
        catch (Exception ex)
        {
            _store.RollbackBatch();
            return new UpdateResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>DELETE DATA: the data instantiated once; the count is the quads that held.</summary>
    private UpdateResult ExecuteDeleteData()
    {
        var quads = InstantiateData(_update.DeleteData);
        if (quads.Count == 0)
            return new UpdateResult { Success = true, AffectedCount = 0 };

        _store.BeginBatch();
        try
        {
            var deletedCount = 0;
            foreach (var (s, p, o, g) in quads)
            {
                if (_store.DeleteCurrentBatched(s, p, o, g))
                    deletedCount++;
            }
            _store.CommitBatch();
            return new UpdateResult { Success = true, AffectedCount = deletedCount };
        }
        catch (Exception ex)
        {
            _store.RollbackBatch();
            return new UpdateResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>[49] QuadData instantiated with no solution: a template without variables, one blank-node scope.</summary>
    private List<(string s, string p, string o, string? g)> InstantiateData(TemplateQuad[]? data)
    {
        var quads = new List<(string s, string p, string o, string? g)>();
        BeginBnodeScope();
        InstantiateTemplate(data, new MaterializedRow(new BindingTable(Span<Binding>.Empty)), activeGraph: null, quads);
        return quads;
    }

    /// <summary>
    /// DELETE WHERE QuadPattern: DELETE QuadPattern WHERE { QuadPattern } (SPARQL 1.1 Update §3.1.3.3). The parser
    /// recorded the QuadPattern both as the delete template and as the WHERE group, so this is Modify with no
    /// INSERT template, no WITH and no USING.
    /// </summary>
    private UpdateResult ExecuteDeleteWhere()
    {
        var toDelete = new List<(string s, string p, string o, string? g)>();

        _store.AcquireReadLock();
        try
        {
            foreach (var row in EvaluateWhere(defaultGraphs: null, namedGraphs: null))
                InstantiateTemplate(_update.DeleteTemplate, row, activeGraph: null, toDelete);
        }
        finally
        {
            _store.ReleaseReadLock();
        }

        return ApplyChanges(toDelete, toInsert: null);
    }

    /// <summary>
    /// DELETE/INSERT … WHERE (SPARQL 1.1 Update §3.1.3). The WHERE's solutions are computed first, then every
    /// solution instantiates the DELETE template and the INSERT template once; the deletions are applied before
    /// the insertions.
    /// <list type="bullet">
    /// <item>Dataset for the WHERE: USING / USING NAMED when present; otherwise WITH &lt;g&gt; is the default graph
    /// and every named graph stays accessible; otherwise the store's default graph.</item>
    /// <item>Template triples outside a GRAPH block go to the WITH graph, or the default graph without WITH —
    /// USING never changes where templates write.</item>
    /// </list>
    /// </summary>
    private UpdateResult ExecuteModify()
    {
        string? withGraph = _update.WithGraphLength > 0
            ? ExpandPrefixedName(_source.AsSpan(_update.WithGraphStart, _update.WithGraphLength)).ToString()
            : null;

        string[]? defaultGraphs;
        string[]? namedGraphs;
        if (_update.UsingClauses is { Length: > 0 })
            QueryExecutor.ResolveDataset(_update.UsingClauses, _source.AsSpan(), PrefixMappings, out defaultGraphs, out namedGraphs);
        else
        {
            defaultGraphs = withGraph != null ? new[] { withGraph } : null;
            namedGraphs = null;
        }

        var toDelete = new List<(string s, string p, string o, string? g)>();
        var toInsert = new List<(string s, string p, string o, string? g)>();

        _store.AcquireReadLock();
        try
        {
            foreach (var row in EvaluateWhere(defaultGraphs, namedGraphs))
            {
                BeginBnodeScope(); // each solution's template blank nodes are new nodes
                InstantiateTemplate(_update.DeleteTemplate, row, withGraph, toDelete);
                InstantiateTemplate(_update.InsertTemplate, row, withGraph, toInsert);
            }
        }
        finally
        {
            _store.ReleaseReadLock();
        }

        return ApplyChanges(toDelete, toInsert);
    }

    /// <summary>
    /// ADR-052: evaluate this operation's WHERE group exactly as a query's WHERE is evaluated — parse the pattern
    /// tree from the offset the parser recorded and run it through <see cref="TreeJoinExecutor"/> with the given
    /// dataset. An empty group yields one row, the empty solution. Caller holds the store's read lock.
    /// </summary>
    private List<MaterializedRow> EvaluateWhere(string[]? defaultGraphs, string[]? namedGraphs)
    {
        // Pooled like the query path: per-operation setup, not a hot path.
        const int treeBytes = PatternSlot.Size * 1024;
        var treeBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(treeBytes);
        try
        {
            var tree = new PatternArray(treeBuffer.AsSpan(0, treeBytes));
            int root = new SparqlParser(_source.AsSpan()).ParsePatternTreeAt(_update.WhereGroupStart, ref tree);

            var trigramCandidates = TreeJoinExecutor.ResolveTrigramCandidates(_store, ref tree, root, _source);
            var executor = new TreeJoinExecutor(_store, _source, PrefixMappings, _source,
                namedGraphs: namedGraphs, defaultGraphs: defaultGraphs, trigramCandidatesByVar: trigramCandidates);
            return executor.Evaluate(ref tree, root, activeGraph: "");
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(treeBuffer);
        }
    }

    /// <summary>
    /// Instantiate a DELETE / INSERT / DELETE WHERE template with one WHERE solution. Triples outside a GRAPH block
    /// go to <paramref name="activeGraph"/> (the WITH graph; null = the default graph); a GRAPH block's triples go
    /// to its graph, which may be a variable. A triple with an unbound variable is not instantiated (§3.1.3).
    /// </summary>
    private void InstantiateTemplate(TemplateQuad[]? template, MaterializedRow row, string? activeGraph,
        List<(string s, string p, string o, string? g)> sink)
    {
        if (template == null)
            return;
        foreach (var quad in template)
        {
            var graph = quad.InGraphBlock ? InstantiateTerm(quad.Graph, row) : activeGraph;
            if (quad.InGraphBlock && graph == null)
                continue;
            var s = InstantiateTerm(quad.Subject, row);
            var p = InstantiateTerm(quad.Predicate, row);
            var o = InstantiateTerm(quad.Object, row);
            if (s != null && p != null && o != null)
                sink.Add((s, p, o, graph));
        }
    }

    /// <summary>
    /// A template term's value under one solution: a variable's binding (null when unbound), a blank node's
    /// per-operation identity, or the term itself with its prefixed name expanded.
    /// </summary>
    private string? InstantiateTerm(Term term, MaterializedRow row)
    {
        // A template node (a collection's or a property list's node) is a blank node of the scope; -1..-8 are rdf: IRIs
        if (term.Start < 0)
        {
            if (Operators.SyntheticTerms.IsListNodeOffset(term.Start))
                return GetScopedBlankNode("t" + Operators.SyntheticTerms.ListNodeIndex(term.Start).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToString();
            return Operators.SyntheticTerms.GetSyntheticIri(term.Start).ToString();
        }

        var termSpan = _source.AsSpan(term.Start, term.Length);

        if (term.Type == TermType.Variable)
        {
            int hash = VariableName.Hash(termSpan);
            for (int i = 0; i < row.BindingCount; i++)
            {
                if (row.GetHash(i) == hash)
                    return LiteralForm.TermForBinding(row.GetValue(i), row.GetValueType(i), out _).ToString();
            }
            return null;
        }

        // Same label within one operation = same node; different operations = different nodes (via _bnodeBase).
        if (term.Type == TermType.BlankNode || (termSpan.Length > 2 && termSpan[0] == '_' && termSpan[1] == ':'))
            return GetScopedBlankNode(termSpan).ToString();

        return ExpandPrefixedName(termSpan).ToString();
    }

    /// <summary>Apply the deletions, then the insertions, in one batch. The affected count is the triples deleted
    /// plus the triples inserted.</summary>
    private UpdateResult ApplyChanges(List<(string s, string p, string o, string? g)> toDelete,
        List<(string s, string p, string o, string? g)>? toInsert)
    {
        if (toDelete.Count == 0 && (toInsert == null || toInsert.Count == 0))
            return new UpdateResult { Success = true, AffectedCount = 0 };

        // A quad both deleted and inserted holds afterwards, as it did before. Ending it at T and starting it again at
        // T would put a version boundary into its history that no fact change caused, so it leaves both sets
        // (ADR-054 D4).
        if (toInsert is { Count: > 0 } && toDelete.Count > 0)
        {
            var inserted = new HashSet<(string s, string p, string o, string? g)>(toInsert);
            var unchanged = new HashSet<(string s, string p, string o, string? g)>();
            foreach (var quad in toDelete)
                if (inserted.Contains(quad))
                    unchanged.Add(quad);
            if (unchanged.Count > 0)
            {
                toDelete.RemoveAll(unchanged.Contains);
                toInsert.RemoveAll(unchanged.Contains);
            }
        }

        _store.BeginBatch();
        try
        {
            var deletedCount = 0;
            foreach (var (s, p, o, g) in toDelete)
            {
                var graphSpan = g != null ? g.AsSpan() : ReadOnlySpan<char>.Empty;
                if (_store.DeleteCurrentBatched(s.AsSpan(), p.AsSpan(), o.AsSpan(), graphSpan))
                    deletedCount++;
            }

            var insertedCount = 0;
            if (toInsert != null)
            {
                foreach (var (s, p, o, g) in toInsert)
                {
                    var graphSpan = g != null ? g.AsSpan() : ReadOnlySpan<char>.Empty;
                    _store.AddCurrentBatched(s.AsSpan(), p.AsSpan(), o.AsSpan(), graphSpan);
                }
                insertedCount = toInsert.Count;
            }
            _store.CommitBatch();

            return new UpdateResult { Success = true, AffectedCount = deletedCount + insertedCount };
        }
        catch (Exception ex)
        {
            _store.RollbackBatch();
            return new UpdateResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private UpdateResult ExecuteClear()
    {
        return ClearOrDropGraph(_update.DestinationGraph, isClear: true);
    }

    private UpdateResult ExecuteDrop()
    {
        return ClearOrDropGraph(_update.DestinationGraph, isClear: false);
    }

    private UpdateResult ClearOrDropGraph(GraphTarget target, bool isClear)
    {
        // CLEAR and DROP have the same effect in this implementation
        // (we don't track graph existence separately)
        try
        {
            switch (target.Type)
            {
                case GraphTargetType.Default:
                    return ClearDefaultGraph();

                case GraphTargetType.Named:
                    return ClearAllNamedGraphs();

                case GraphTargetType.All:
                    var r1 = ClearDefaultGraph();
                    var r2 = ClearAllNamedGraphs();
                    return new UpdateResult
                    {
                        Success = r1.Success && r2.Success,
                        AffectedCount = r1.AffectedCount + r2.AffectedCount
                    };

                case GraphTargetType.Graph:
                    var graphIri = ExpandPrefixedName(_source.AsSpan(target.IriStart, target.IriLength)).ToString();
                    return ClearSpecificGraph(graphIri);

                default:
                    return new UpdateResult { Success = false, ErrorMessage = "Unknown graph target type" };
            }
        }
        catch (Exception ex)
        {
            if (_update.Silent)
                return new UpdateResult { Success = true, AffectedCount = 0 };
            return new UpdateResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private UpdateResult ClearDefaultGraph()
    {
        var toDelete = new System.Collections.Generic.List<(string s, string p, string o)>();

        _store.AcquireReadLock();
        try
        {
            var results = _store.QueryCurrent(
                ReadOnlySpan<char>.Empty,
                ReadOnlySpan<char>.Empty,
                ReadOnlySpan<char>.Empty);
            try
            {
                while (results.MoveNext())
                {
                    var t = results.Current;
                    // Only include triples from default graph (no graph IRI)
                    if (t.Graph.IsEmpty)
                    {
                        toDelete.Add((t.Subject.ToString(), t.Predicate.ToString(), t.Object.ToString()));
                    }
                }
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            _store.ReleaseReadLock();
        }

        if (toDelete.Count == 0)
            return new UpdateResult { Success = true, AffectedCount = 0 };

        _store.BeginBatch();
        try
        {
            var deletedCount = 0;
            foreach (var (s, p, o) in toDelete)
            {
                if (_store.DeleteCurrentBatched(s.AsSpan(), p.AsSpan(), o.AsSpan()))
                    deletedCount++;
            }
            _store.CommitBatch();
            return new UpdateResult { Success = true, AffectedCount = deletedCount };
        }
        catch (Exception ex)
        {
            _store.RollbackBatch();
            return new UpdateResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private UpdateResult ClearAllNamedGraphs()
    {
        // Get all named graphs
        var graphs = new System.Collections.Generic.List<string>();

        _store.AcquireReadLock();
        try
        {
            var graphEnum = _store.GetNamedGraphs();
            while (graphEnum.MoveNext())
            {
                graphs.Add(graphEnum.Current.ToString());
            }
        }
        finally
        {
            _store.ReleaseReadLock();
        }

        var totalDeleted = 0;
        foreach (var graph in graphs)
        {
            var result = ClearSpecificGraph(graph);
            if (!result.Success)
                return result;
            totalDeleted += result.AffectedCount;
        }

        return new UpdateResult { Success = true, AffectedCount = totalDeleted };
    }

    private UpdateResult ClearSpecificGraph(string graphIri)
    {
        var toDelete = new System.Collections.Generic.List<(string s, string p, string o)>();

        _store.AcquireReadLock();
        try
        {
            var results = _store.QueryCurrent(
                ReadOnlySpan<char>.Empty,
                ReadOnlySpan<char>.Empty,
                ReadOnlySpan<char>.Empty,
                graphIri.AsSpan());
            try
            {
                while (results.MoveNext())
                {
                    var t = results.Current;
                    toDelete.Add((t.Subject.ToString(), t.Predicate.ToString(), t.Object.ToString()));
                }
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            _store.ReleaseReadLock();
        }

        if (toDelete.Count == 0)
            return new UpdateResult { Success = true, AffectedCount = 0 };

        _store.BeginBatch();
        try
        {
            var deletedCount = 0;
            foreach (var (s, p, o) in toDelete)
            {
                if (_store.DeleteCurrentBatched(s.AsSpan(), p.AsSpan(), o.AsSpan(), graphIri.AsSpan()))
                    deletedCount++;
            }
            _store.CommitBatch();
            return new UpdateResult { Success = true, AffectedCount = deletedCount };
        }
        catch (Exception ex)
        {
            _store.RollbackBatch();
            return new UpdateResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private UpdateResult ExecuteCreate()
    {
        // CREATE GRAPH is a no-op in this implementation
        // Named graphs are created implicitly when triples are added
        return new UpdateResult { Success = true, AffectedCount = 0 };
    }

    private UpdateResult ExecuteCopy()
    {
        // COPY src TO dst: Clear dst, then copy all triples from src to dst
        var srcGraph = ResolveGraphTarget(_update.SourceGraph);
        var dstGraph = ResolveGraphTarget(_update.DestinationGraph);

        // COPY to self is a no-op
        if (srcGraph == dstGraph || (srcGraph != null && dstGraph != null && srcGraph.Equals(dstGraph, StringComparison.Ordinal)))
        {
            return new UpdateResult { Success = true, AffectedCount = 0 };
        }

        try
        {
            // First clear destination
            if (dstGraph == null)
                ClearDefaultGraph();
            else
                ClearSpecificGraph(dstGraph);

            // Then copy triples
            return CopyTriples(srcGraph, dstGraph);
        }
        catch (Exception ex)
        {
            if (_update.Silent)
                return new UpdateResult { Success = true, AffectedCount = 0 };
            return new UpdateResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private UpdateResult ExecuteMove()
    {
        // MOVE src TO dst: Copy src to dst, then clear src
        var srcGraph = ResolveGraphTarget(_update.SourceGraph);
        var dstGraph = ResolveGraphTarget(_update.DestinationGraph);

        // MOVE to self is a no-op
        if (srcGraph == dstGraph || (srcGraph != null && dstGraph != null && srcGraph.Equals(dstGraph, StringComparison.Ordinal)))
        {
            return new UpdateResult { Success = true, AffectedCount = 0 };
        }

        try
        {
            // First clear destination
            if (dstGraph == null)
                ClearDefaultGraph();
            else
                ClearSpecificGraph(dstGraph);

            // Copy triples
            var copyResult = CopyTriples(srcGraph, dstGraph);
            if (!copyResult.Success)
                return copyResult;

            // Clear source
            UpdateResult clearResult;
            if (srcGraph == null)
                clearResult = ClearDefaultGraph();
            else
                clearResult = ClearSpecificGraph(srcGraph);

            return new UpdateResult
            {
                Success = true,
                AffectedCount = copyResult.AffectedCount + clearResult.AffectedCount
            };
        }
        catch (Exception ex)
        {
            if (_update.Silent)
                return new UpdateResult { Success = true, AffectedCount = 0 };
            return new UpdateResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private UpdateResult ExecuteAdd()
    {
        // ADD src TO dst: Copy triples from src to dst (without clearing dst)
        var srcGraph = ResolveGraphTarget(_update.SourceGraph);
        var dstGraph = ResolveGraphTarget(_update.DestinationGraph);

        try
        {
            return CopyTriples(srcGraph, dstGraph);
        }
        catch (Exception ex)
        {
            if (_update.Silent)
                return new UpdateResult { Success = true, AffectedCount = 0 };
            return new UpdateResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private UpdateResult ExecuteLoad()
    {
        // LOAD requires a LoadExecutor instance
        if (_loadExecutor == null)
        {
            if (_update.Silent)
                return new UpdateResult { Success = true, AffectedCount = 0 };
            return new UpdateResult
            {
                Success = false,
                ErrorMessage = "LOAD operation requires a LoadExecutor instance. Pass LoadExecutor to UpdateExecutor constructor."
            };
        }

        // Get source URI - strip angle brackets if present
        var sourceUriSpan = GetTermValue(_update.SourceUriStart, _update.SourceUriLength);
        var sourceUri = sourceUriSpan.ToString();
        if (sourceUri.StartsWith('<') && sourceUri.EndsWith('>'))
            sourceUri = sourceUri[1..^1];

        // Get destination graph
        var destGraph = ResolveGraphTarget(_update.DestinationGraph);

        // Execute async LOAD operation synchronously
        // This is necessary because Execute() is synchronous
        return _loadExecutor.ExecuteAsync(sourceUri, destGraph, _update.Silent, _store)
            .GetAwaiter().GetResult();
    }

    private string? ResolveGraphTarget(GraphTarget target)
    {
        return target.Type switch
        {
            GraphTargetType.Default => null,
            GraphTargetType.Graph => ExpandPrefixedName(_source.AsSpan(target.IriStart, target.IriLength)).ToString(),
            _ => null
        };
    }

    private UpdateResult CopyTriples(string? srcGraph, string? dstGraph)
    {
        var toCopy = new System.Collections.Generic.List<(string s, string p, string o)>();

        _store.AcquireReadLock();
        try
        {
            var srcGraphSpan = srcGraph != null ? srcGraph.AsSpan() : ReadOnlySpan<char>.Empty;
            var results = _store.QueryCurrent(
                ReadOnlySpan<char>.Empty,
                ReadOnlySpan<char>.Empty,
                ReadOnlySpan<char>.Empty,
                srcGraphSpan);
            try
            {
                while (results.MoveNext())
                {
                    var t = results.Current;
                    // Only include triples from the source graph
                    var isFromSourceGraph = srcGraph == null
                        ? t.Graph.IsEmpty
                        : t.Graph.Equals(srcGraph.AsSpan(), StringComparison.Ordinal);

                    if (isFromSourceGraph)
                    {
                        toCopy.Add((t.Subject.ToString(), t.Predicate.ToString(), t.Object.ToString()));
                    }
                }
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            _store.ReleaseReadLock();
        }

        if (toCopy.Count == 0)
            return new UpdateResult { Success = true, AffectedCount = 0 };

        _store.BeginBatch();
        try
        {
            var dstGraphSpan = dstGraph != null ? dstGraph.AsSpan() : ReadOnlySpan<char>.Empty;
            foreach (var (s, p, o) in toCopy)
            {
                _store.AddCurrentBatched(s.AsSpan(), p.AsSpan(), o.AsSpan(), dstGraphSpan);
            }
            _store.CommitBatch();
            return new UpdateResult { Success = true, AffectedCount = toCopy.Count };
        }
        catch (Exception ex)
        {
            _store.RollbackBatch();
            return new UpdateResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private ReadOnlySpan<char> GetTermValue(int start, int length)
    {
        if (length == 0)
            return ReadOnlySpan<char>.Empty;

        var term = _source.AsSpan(start, length);

        // Handle blank nodes with scoped identity
        if (term.Length > 2 && term[0] == '_' && term[1] == ':')
        {
            return GetScopedBlankNode(term);
        }

        return ExpandPrefixedName(term);
    }

    /// <summary>The node <paramref name="label"/> (a written _:label, or a template node's name) is in the current
    /// scope: the same label is the same node until the scope ends.</summary>
    private ReadOnlySpan<char> GetScopedBlankNode(ReadOnlySpan<char> label)
    {
        _expandedTerm = _bnodeScope.Label(label); // Use existing buffer field for span lifetime
        return _expandedTerm.AsSpan();
    }

    /// <summary>
    /// Begin a new blank node scope: an operation's data, or one solution of a template. Its labels name new nodes.
    /// </summary>
    private void BeginBnodeScope()
    {
        _bnodeScope = new Rdf.BlankNodeScope();
    }

    /// <summary>
    /// Expands a prefixed name to its full IRI using the prologue prefix mappings.
    /// Also handles 'a' shorthand for rdf:type.
    /// Returns the original span if not a prefixed name or no matching prefix found.
    /// </summary>
    private PrefixMapping[]? _updatePrefixMappings;
    private PrefixMapping[]? PrefixMappings => _updatePrefixMappings ??= PrefixExpander.Extract(_update.Prologue);

    private ReadOnlySpan<char> ExpandPrefixedName(ReadOnlySpan<char> term)
    {
        // IRIs, 'a' and prefixed names through the shared expander (it throws on an undefined prefix); a literal,
        // number or boolean is stored as the term it denotes (LiteralForm.StoredTerm), as Turtle stores it
        var expanded = PrefixExpander.TryExpand(term, PrefixMappings, _source.AsSpan());
        if (expanded is null) return LiteralForm.StoredTerm(term, PrefixMappings, _source.AsSpan(), out _literalScratch);
        _expandedTerm = expanded;
        return _expandedTerm.AsSpan();
    }
}
