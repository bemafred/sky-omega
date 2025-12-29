using System;
using SkyOmega.Mercury.Storage;

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
/// Partially supported:
/// - DELETE WHERE: Requires pattern matching (simplified implementation)
/// - DELETE/INSERT WHERE: Requires pattern matching (simplified implementation)
///
/// Not supported:
/// - LOAD: Requires external HTTP client
/// </summary>
public class UpdateExecutor
{
    private readonly QuadStore _store;
    private readonly string _source;
    private readonly UpdateOperation _update;

    public UpdateExecutor(QuadStore store, ReadOnlySpan<char> source, UpdateOperation update)
    {
        _store = store;
        _source = source.ToString();
        _update = update;
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
            QueryType.Load => new UpdateResult { Success = false, ErrorMessage = "LOAD operation requires external HTTP client and is not supported" },
            _ => new UpdateResult { Success = false, ErrorMessage = $"Unknown update type: {_update.Type}" }
        };
    }

    private UpdateResult ExecuteInsertData()
    {
        if (_update.InsertData == null || _update.InsertData.Length == 0)
            return new UpdateResult { Success = true, AffectedCount = 0 };

        _store.BeginBatch();
        try
        {
            foreach (var quad in _update.InsertData)
            {
                var subject = GetTermValue(quad.SubjectStart, quad.SubjectLength);
                var predicate = GetTermValue(quad.PredicateStart, quad.PredicateLength);
                var obj = GetTermValue(quad.ObjectStart, quad.ObjectLength);
                var graph = quad.GraphLength > 0
                    ? _source.AsSpan(quad.GraphStart, quad.GraphLength)
                    : ReadOnlySpan<char>.Empty;

                _store.AddCurrentBatched(subject, predicate, obj, graph);
            }
            _store.CommitBatch();

            return new UpdateResult { Success = true, AffectedCount = _update.InsertData.Length };
        }
        catch (Exception ex)
        {
            _store.RollbackBatch();
            return new UpdateResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private UpdateResult ExecuteDeleteData()
    {
        if (_update.DeleteData == null || _update.DeleteData.Length == 0)
            return new UpdateResult { Success = true, AffectedCount = 0 };

        _store.BeginBatch();
        try
        {
            var deletedCount = 0;
            foreach (var quad in _update.DeleteData)
            {
                var subject = GetTermValue(quad.SubjectStart, quad.SubjectLength);
                var predicate = GetTermValue(quad.PredicateStart, quad.PredicateLength);
                var obj = GetTermValue(quad.ObjectStart, quad.ObjectLength);
                var graph = quad.GraphLength > 0
                    ? _source.AsSpan(quad.GraphStart, quad.GraphLength)
                    : ReadOnlySpan<char>.Empty;

                if (_store.DeleteCurrentBatched(subject, predicate, obj, graph))
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

    private UpdateResult ExecuteDeleteWhere()
    {
        // DELETE WHERE requires pattern matching
        // For now, return an error suggesting to use the simpler pattern-matching approach
        var pattern = _update.WhereClause.Pattern;
        if (pattern.PatternCount == 0)
            return new UpdateResult { Success = true, AffectedCount = 0 };

        // For simple single-pattern DELETE WHERE without variables, we can execute directly
        // For complex patterns, we need proper query execution
        if (pattern.PatternCount == 1 && !HasVariables(pattern.GetPattern(0)))
        {
            var tp = pattern.GetPattern(0);
            var subject = _source.AsSpan(tp.Subject.Start, tp.Subject.Length);
            var predicate = _source.AsSpan(tp.Predicate.Start, tp.Predicate.Length);
            var obj = _source.AsSpan(tp.Object.Start, tp.Object.Length);

            var deleted = _store.DeleteCurrent(subject, predicate, obj);
            return new UpdateResult { Success = true, AffectedCount = deleted ? 1 : 0 };
        }

        // Complex DELETE WHERE with variables requires full query execution
        // This is a simplified placeholder - full implementation would need QueryExecutor integration
        return new UpdateResult
        {
            Success = false,
            ErrorMessage = "DELETE WHERE with variables requires QueryExecutor integration (use DELETE DATA for concrete triples)"
        };
    }

    private UpdateResult ExecuteModify()
    {
        // DELETE/INSERT ... WHERE requires pattern matching
        // Similar to DELETE WHERE, complex cases need QueryExecutor integration
        return new UpdateResult
        {
            Success = false,
            ErrorMessage = "DELETE/INSERT WHERE requires QueryExecutor integration (use INSERT DATA/DELETE DATA for concrete triples)"
        };
    }

    private bool HasVariables(TriplePattern tp)
    {
        return tp.Subject.Type == TermType.Variable ||
               tp.Predicate.Type == TermType.Variable ||
               tp.Object.Type == TermType.Variable;
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
                    var graphIri = _source.Substring(target.IriStart, target.IriLength);
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

            while (results.MoveNext())
            {
                var t = results.Current;
                // Only include triples from default graph (no graph IRI)
                if (t.Graph.IsEmpty)
                {
                    toDelete.Add((t.Subject.ToString(), t.Predicate.ToString(), t.Object.ToString()));
                }
            }
            results.Dispose();
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

            while (results.MoveNext())
            {
                var t = results.Current;
                toDelete.Add((t.Subject.ToString(), t.Predicate.ToString(), t.Object.ToString()));
            }
            results.Dispose();
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

    private string? ResolveGraphTarget(GraphTarget target)
    {
        return target.Type switch
        {
            GraphTargetType.Default => null,
            GraphTargetType.Graph => _source.Substring(target.IriStart, target.IriLength),
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
            results.Dispose();
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

        return _source.AsSpan(start, length);
    }
}

/// <summary>
/// Result of executing a SPARQL Update operation.
/// </summary>
public struct UpdateResult
{
    /// <summary>
    /// Whether the operation completed successfully.
    /// </summary>
    public bool Success;

    /// <summary>
    /// Number of triples/quads affected by the operation.
    /// </summary>
    public int AffectedCount;

    /// <summary>
    /// Error message if Success is false.
    /// </summary>
    public string? ErrorMessage;
}
