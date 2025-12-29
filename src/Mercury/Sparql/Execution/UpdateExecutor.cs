using System;
using SkyOmega.Mercury.Sparql.Patterns;
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
/// LOAD support:
/// - LOAD: Requires LoadExecutor instance (optional parameter)
/// </summary>
public class UpdateExecutor
{
    private readonly QuadStore _store;
    private readonly string _source;
    private readonly UpdateOperation _update;
    private readonly LoadExecutor? _loadExecutor;

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
        // DELETE WHERE: The pattern serves as both the WHERE clause and delete template
        var pattern = _update.WhereClause.Pattern;
        if (pattern.PatternCount == 0)
            return new UpdateResult { Success = true, AffectedCount = 0 };

        // For simple single-pattern DELETE WHERE without variables, we can execute directly
        if (pattern.PatternCount == 1 && !HasVariables(pattern.GetPattern(0)))
        {
            var tp = pattern.GetPattern(0);
            var subject = _source.AsSpan(tp.Subject.Start, tp.Subject.Length);
            var predicate = _source.AsSpan(tp.Predicate.Start, tp.Predicate.Length);
            var obj = _source.AsSpan(tp.Object.Start, tp.Object.Length);

            var deleted = _store.DeleteCurrent(subject, predicate, obj);
            return new UpdateResult { Success = true, AffectedCount = deleted ? 1 : 0 };
        }

        // Execute the WHERE pattern to find matching bindings
        var toDelete = new System.Collections.Generic.List<(string s, string p, string o)>();

        // Build a SELECT * query from the pattern
        var query = new Query
        {
            Type = QueryType.Select,
            SelectClause = new SelectClause { SelectAll = true },
            WhereClause = _update.WhereClause,
            Prologue = _update.Prologue
        };

        _store.AcquireReadLock();
        try
        {
            var executor = new QueryExecutor(_store, _source.AsSpan(), query);
            var results = executor.Execute();

            while (results.MoveNext())
            {
                var bindings = results.Current;

                // For DELETE WHERE, the delete template is the same as the WHERE pattern
                // Instantiate each pattern with the bindings
                for (int i = 0; i < pattern.PatternCount; i++)
                {
                    var tp = pattern.GetPattern(i);

                    // Skip optional patterns for deletion
                    if (pattern.IsOptional(i))
                        continue;

                    var s = InstantiateTerm(tp.Subject, bindings);
                    var p = InstantiateTerm(tp.Predicate, bindings);
                    var o = InstantiateTerm(tp.Object, bindings);

                    // Only delete if all terms are bound (no unbound variables)
                    if (s != null && p != null && o != null)
                    {
                        toDelete.Add((s, p, o));
                    }
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

        // Delete collected triples
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

    /// <summary>
    /// Instantiate a term using variable bindings.
    /// Returns null if the term is a variable that is not bound.
    /// </summary>
    private string? InstantiateTerm(Term term, BindingTable bindings)
    {
        var termSpan = _source.AsSpan(term.Start, term.Length);

        if (term.Type == TermType.Variable)
        {
            var idx = bindings.FindBinding(termSpan);
            if (idx >= 0)
                return bindings.GetString(idx).ToString();
            // Variable not bound
            return null;
        }

        // Not a variable - return the literal value
        return termSpan.ToString();
    }

    private UpdateResult ExecuteModify()
    {
        // DELETE/INSERT ... WHERE: Execute WHERE pattern, then apply DELETE and INSERT templates
        var wherePattern = _update.WhereClause.Pattern;

        // Collect all delete and insert operations
        var toDelete = new System.Collections.Generic.List<(string s, string p, string o)>();
        var toInsert = new System.Collections.Generic.List<(string s, string p, string o)>();

        // Build a SELECT * query from the WHERE pattern
        var query = new Query
        {
            Type = QueryType.Select,
            SelectClause = new SelectClause { SelectAll = true },
            WhereClause = _update.WhereClause,
            Prologue = _update.Prologue
        };

        _store.AcquireReadLock();
        try
        {
            // If there's no WHERE pattern, execute once with empty bindings
            if (wherePattern.PatternCount == 0)
            {
                ProcessModifyTemplates(default, toDelete, toInsert);
            }
            else
            {
                var executor = new QueryExecutor(_store, _source.AsSpan(), query);
                var results = executor.Execute();

                while (results.MoveNext())
                {
                    ProcessModifyTemplates(results.Current, toDelete, toInsert);
                }
                results.Dispose();
            }
        }
        finally
        {
            _store.ReleaseReadLock();
        }

        // Apply the changes
        _store.BeginBatch();
        try
        {
            var deletedCount = 0;
            foreach (var (s, p, o) in toDelete)
            {
                if (_store.DeleteCurrentBatched(s.AsSpan(), p.AsSpan(), o.AsSpan()))
                    deletedCount++;
            }

            foreach (var (s, p, o) in toInsert)
            {
                _store.AddCurrentBatched(s.AsSpan(), p.AsSpan(), o.AsSpan());
            }
            _store.CommitBatch();

            return new UpdateResult { Success = true, AffectedCount = deletedCount + toInsert.Count };
        }
        catch (Exception ex)
        {
            _store.RollbackBatch();
            return new UpdateResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private void ProcessModifyTemplates(
        BindingTable bindings,
        System.Collections.Generic.List<(string s, string p, string o)> toDelete,
        System.Collections.Generic.List<(string s, string p, string o)> toInsert)
    {
        // Process DELETE template
        var deleteTemplate = _update.DeleteTemplate;
        for (int i = 0; i < deleteTemplate.PatternCount; i++)
        {
            var tp = deleteTemplate.GetPattern(i);
            var s = InstantiateTerm(tp.Subject, bindings);
            var p = InstantiateTerm(tp.Predicate, bindings);
            var o = InstantiateTerm(tp.Object, bindings);

            if (s != null && p != null && o != null)
            {
                toDelete.Add((s, p, o));
            }
        }

        // Process INSERT template
        var insertTemplate = _update.InsertTemplate;
        for (int i = 0; i < insertTemplate.PatternCount; i++)
        {
            var tp = insertTemplate.GetPattern(i);
            var s = InstantiateTerm(tp.Subject, bindings);
            var p = InstantiateTerm(tp.Predicate, bindings);
            var o = InstantiateTerm(tp.Object, bindings);

            if (s != null && p != null && o != null)
            {
                toInsert.Add((s, p, o));
            }
        }
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
