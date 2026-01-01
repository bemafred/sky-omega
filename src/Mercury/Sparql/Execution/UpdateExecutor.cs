using System;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime.Buffers;
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
        // Use helper methods to check pattern counts without copying the 8KB GraphPattern
        if (_update.WhereClause.Pattern.PatternCount == 0 && _update.WhereClause.Pattern.GraphClauseCount == 0)
            return new UpdateResult { Success = true, AffectedCount = 0 };

        // For simple single-pattern DELETE WHERE without variables or GRAPH clauses, we can execute directly
        if (_update.WhereClause.Pattern.PatternCount == 1 && _update.WhereClause.Pattern.GraphClauseCount == 0 && !HasVariables(_update.WhereClause.Pattern.GetPattern(0)))
        {
            var tp = _update.WhereClause.Pattern.GetPattern(0);
            var subject = _source.AsSpan(tp.Subject.Start, tp.Subject.Length);
            var predicate = _source.AsSpan(tp.Predicate.Start, tp.Predicate.Length);
            var obj = _source.AsSpan(tp.Object.Start, tp.Object.Length);

            var deleted = _store.DeleteCurrent(subject, predicate, obj);
            return new UpdateResult { Success = true, AffectedCount = deleted ? 1 : 0 };
        }

        // Execute the WHERE pattern to find matching bindings using isolated method
        var toDelete = new System.Collections.Generic.List<(string s, string p, string o, string? g)>();

        // Special path for GRAPH-only patterns to avoid stack overflow
        if (_update.WhereClause.Pattern.PatternCount == 0 && _update.WhereClause.Pattern.GraphClauseCount > 0)
        {
            ExecuteDeleteWhereGraphOnly(toDelete);
        }
        else
        {
            ExecuteDeleteWhereQuery(toDelete);
        }

        if (toDelete.Count == 0)
            return new UpdateResult { Success = true, AffectedCount = 0 };

        // Delete collected triples
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
    /// Execute DELETE WHERE with GRAPH-only patterns directly without QueryExecutor/QueryResults.
    /// This avoids creating large Query and QueryResults structs that cause stack overflow.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void ExecuteDeleteWhereGraphOnly(List<(string s, string p, string o, string? g)> toDelete)
    {
        _store.AcquireReadLock();
        try
        {
            // Process each GRAPH clause directly
            for (int i = 0; i < _update.WhereClause.Pattern.GraphClauseCount; i++)
            {
                var gc = _update.WhereClause.Pattern.GetGraphClause(i);
                if (gc.PatternCount == 0)
                    continue;

                if (gc.IsVariable)
                {
                    // Variable graph - iterate all named graphs
                    foreach (var graphIri in _store.GetNamedGraphs())
                    {
                        ExecuteGraphClausePatternsForDelete(gc, graphIri.ToString(), toDelete);
                    }
                }
                else
                {
                    // Fixed IRI graph
                    var graphIri = _source.AsSpan(gc.Graph.Start, gc.Graph.Length).ToString();
                    ExecuteGraphClausePatternsForDelete(gc, graphIri, toDelete);
                }
            }
        }
        finally
        {
            _store.ReleaseReadLock();
        }
    }

    /// <summary>
    /// Execute patterns within a GRAPH clause and collect triples for deletion.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void ExecuteGraphClausePatternsForDelete(GraphClause gc, string graphIri, List<(string s, string p, string o, string? g)> toDelete)
    {
        // For single pattern in GRAPH clause - simple scan
        if (gc.PatternCount == 1)
        {
            var tp = gc.GetPattern(0);
            var subject = ResolveTermForQuery(tp.Subject);
            var predicate = ResolveTermForQuery(tp.Predicate);
            var obj = ResolveTermForQuery(tp.Object);

            var results = _store.QueryCurrent(subject, predicate, obj, graphIri.AsSpan());
            try
            {
                while (results.MoveNext())
                {
                    var current = results.Current;
                    toDelete.Add((current.Subject.ToString(), current.Predicate.ToString(), current.Object.ToString(), graphIri));
                }
            }
            finally
            {
                results.Dispose();
            }
        }
        else
        {
            // Multiple patterns - use nested loop join
            // Start with first pattern results, then filter by subsequent patterns
            var tp0 = gc.GetPattern(0);
            var subject0 = ResolveTermForQuery(tp0.Subject);
            var predicate0 = ResolveTermForQuery(tp0.Predicate);
            var obj0 = ResolveTermForQuery(tp0.Object);

            var results0 = _store.QueryCurrent(subject0, predicate0, obj0, graphIri.AsSpan());
            try
            {
                while (results0.MoveNext())
                {
                    var current = results0.Current;
                    // For DELETE WHERE, we delete the matching triples from the first pattern
                    // (variables would need more sophisticated handling for multi-pattern joins)
                    toDelete.Add((current.Subject.ToString(), current.Predicate.ToString(), current.Object.ToString(), graphIri));
                }
            }
            finally
            {
                results0.Dispose();
            }
        }
    }

    /// <summary>
    /// Resolve a term for querying - returns the literal value or null for wildcards.
    /// </summary>
    private ReadOnlySpan<char> ResolveTermForQuery(Term term)
    {
        if (term.Type == TermType.Variable)
            return ReadOnlySpan<char>.Empty; // Wildcard
        return _source.AsSpan(term.Start, term.Length);
    }

    /// <summary>
    /// Execute DELETE WHERE query in isolated method to minimize stack usage.
    /// The Query struct (~30KB) lives only in this method's stack frame.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void ExecuteDeleteWhereQuery(List<(string s, string p, string o, string? g)> toDelete)
    {
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
            try
            {
                while (results.MoveNext())
                {
                    var bindings = results.Current;

                    // For DELETE WHERE, the delete template is the same as the WHERE pattern
                    // Instantiate each base pattern with the bindings (default graph)
                    for (int i = 0; i < _update.WhereClause.Pattern.PatternCount; i++)
                    {
                        var tp = _update.WhereClause.Pattern.GetPattern(i);

                        // Skip optional patterns for deletion
                        if (_update.WhereClause.Pattern.IsOptional(i))
                            continue;

                        var s = InstantiateTerm(tp.Subject, bindings);
                        var p = InstantiateTerm(tp.Predicate, bindings);
                        var o = InstantiateTerm(tp.Object, bindings);

                        // Only delete if all terms are bound (no unbound variables)
                        if (s != null && p != null && o != null)
                        {
                            toDelete.Add((s, p, o, null));
                        }
                    }

                    // Process GRAPH clauses - delete from specified graphs
                    for (int i = 0; i < _update.WhereClause.Pattern.GraphClauseCount; i++)
                    {
                        var gc = _update.WhereClause.Pattern.GetGraphClause(i);
                        var graphIri = ResolveGraphTerm(gc.Graph, bindings);

                        for (int j = 0; j < gc.PatternCount; j++)
                        {
                            var tp = gc.GetPattern(j);
                            var s = InstantiateTerm(tp.Subject, bindings);
                            var p = InstantiateTerm(tp.Predicate, bindings);
                            var o = InstantiateTerm(tp.Object, bindings);

                            if (s != null && p != null && o != null && graphIri != null)
                            {
                                toDelete.Add((s, p, o, graphIri));
                            }
                        }
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

        // Extract WITH graph if present
        string? withGraph = null;
        if (_update.WithGraphLength > 0)
        {
            withGraph = _source.Substring(_update.WithGraphStart, _update.WithGraphLength);
        }

        // Collect all delete and insert operations (now with graph context)
        var toDelete = new System.Collections.Generic.List<(string s, string p, string o, string? g)>();
        var toInsert = new System.Collections.Generic.List<(string s, string p, string o, string? g)>();

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
            if (wherePattern.PatternCount == 0 && wherePattern.GraphClauseCount == 0)
            {
                ProcessModifyTemplates(default, toDelete, toInsert, withGraph);
            }
            // WITH clause with simple single pattern - use direct store query to avoid stack overflow
            else if (withGraph != null && wherePattern.GraphClauseCount == 0 && wherePattern.PatternCount == 1)
            {
                ExecuteModifyWithSimplePattern(wherePattern, withGraph, toDelete, toInsert);
            }
            // WITH clause with multiple patterns - run on thread with larger stack
            else if (withGraph != null && wherePattern.GraphClauseCount == 0 && wherePattern.PatternCount > 1)
            {
                ExecuteModifyWithMultiPatternOnThread(query, withGraph, toDelete, toInsert);
            }
            else
            {
                // Standard path - no WITH clause or has GRAPH clauses
                var executor = new QueryExecutor(_store, _source.AsSpan(), query);
                var results = executor.Execute();
                try
                {
                    while (results.MoveNext())
                    {
                        ProcessModifyTemplates(results.Current, toDelete, toInsert, withGraph);
                    }
                }
                finally
                {
                    results.Dispose();
                }
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
            foreach (var (s, p, o, g) in toDelete)
            {
                var graphSpan = g != null ? g.AsSpan() : ReadOnlySpan<char>.Empty;
                if (_store.DeleteCurrentBatched(s.AsSpan(), p.AsSpan(), o.AsSpan(), graphSpan))
                    deletedCount++;
            }

            foreach (var (s, p, o, g) in toInsert)
            {
                var graphSpan = g != null ? g.AsSpan() : ReadOnlySpan<char>.Empty;
                _store.AddCurrentBatched(s.AsSpan(), p.AsSpan(), o.AsSpan(), graphSpan);
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

    /// <summary>
    /// Execute modify with simple single pattern using direct store query.
    /// Avoids QueryExecutor stack issues for WITH clause.
    /// </summary>
    private void ExecuteModifyWithSimplePattern(
        GraphPattern pattern,
        string withGraph,
        System.Collections.Generic.List<(string s, string p, string o, string? g)> toDelete,
        System.Collections.Generic.List<(string s, string p, string o, string? g)> toInsert)
    {
        var tp = pattern.GetPattern(0);
        var subjectSpan = tp.Subject.Type == TermType.Variable
            ? ReadOnlySpan<char>.Empty
            : _source.AsSpan(tp.Subject.Start, tp.Subject.Length);
        var predicateSpan = tp.Predicate.Type == TermType.Variable
            ? ReadOnlySpan<char>.Empty
            : _source.AsSpan(tp.Predicate.Start, tp.Predicate.Length);
        var objectSpan = tp.Object.Type == TermType.Variable
            ? ReadOnlySpan<char>.Empty
            : _source.AsSpan(tp.Object.Start, tp.Object.Length);

        var results = _store.QueryCurrent(subjectSpan, predicateSpan, objectSpan, withGraph.AsSpan());

        var bindings = new Binding[16];
        var stringBuffer = PooledBufferManager.Shared.Rent<char>(1024).Array!;
        var bindingTable = new BindingTable(bindings, stringBuffer);
        try
        {
            while (results.MoveNext())
            {
                var t = results.Current;
                // Only include triples from the WITH graph
                if (!t.Graph.Equals(withGraph.AsSpan(), StringComparison.Ordinal))
                    continue;

                bindingTable.Clear();

                // Bind variables from the matched triple
                if (tp.Subject.Type == TermType.Variable)
                {
                    var varName = _source.AsSpan(tp.Subject.Start, tp.Subject.Length);
                    bindingTable.Bind(varName, t.Subject);
                }
                if (tp.Predicate.Type == TermType.Variable)
                {
                    var varName = _source.AsSpan(tp.Predicate.Start, tp.Predicate.Length);
                    bindingTable.Bind(varName, t.Predicate);
                }
                if (tp.Object.Type == TermType.Variable)
                {
                    var varName = _source.AsSpan(tp.Object.Start, tp.Object.Length);
                    bindingTable.Bind(varName, t.Object);
                }

                ProcessModifyTemplates(bindingTable, toDelete, toInsert, withGraph);
            }
        }
        finally
        {
            results.Dispose();
            PooledBufferManager.Shared.Return(stringBuffer);
        }
    }

    /// <summary>
    /// Execute modify with multiple patterns on separate thread with larger stack.
    /// </summary>
    private void ExecuteModifyWithMultiPatternOnThread(
        Query query,
        string withGraph,
        System.Collections.Generic.List<(string s, string p, string o, string? g)> toDelete,
        System.Collections.Generic.List<(string s, string p, string o, string? g)> toInsert)
    {
        var store = _store;
        var source = _source;
        var update = _update;

        // Run on thread with larger stack to handle large struct copies
        var thread = new System.Threading.Thread(() =>
        {
            // Add WITH graph as dataset clause
            var modifiedQuery = query;
            modifiedQuery.Datasets = new[] { DatasetClause.Default(update.WithGraphStart, update.WithGraphLength) };

            var executor = new QueryExecutor(store, source.AsSpan(), modifiedQuery);
            var results = executor.Execute();
            try
            {
                while (results.MoveNext())
                {
                    // Copy bindings since we need to use them across template processing
                    var b = results.Current;
                    var deleteTemplate = update.DeleteTemplate;
                    var insertTemplate = update.InsertTemplate;

                    // Process DELETE template - base patterns use WITH graph
                    for (int i = 0; i < deleteTemplate.PatternCount; i++)
                    {
                        var tp = deleteTemplate.GetPattern(i);
                        var s = InstantiateTermFromSpan(tp.Subject, b, source);
                        var p = InstantiateTermFromSpan(tp.Predicate, b, source);
                        var o = InstantiateTermFromSpan(tp.Object, b, source);

                        if (s != null && p != null && o != null)
                        {
                            lock (toDelete)
                            {
                                toDelete.Add((s, p, o, withGraph));
                            }
                        }
                    }

                    // Process INSERT template - base patterns use WITH graph
                    for (int i = 0; i < insertTemplate.PatternCount; i++)
                    {
                        var tp = insertTemplate.GetPattern(i);
                        var s = InstantiateTermFromSpan(tp.Subject, b, source);
                        var p = InstantiateTermFromSpan(tp.Predicate, b, source);
                        var o = InstantiateTermFromSpan(tp.Object, b, source);

                        if (s != null && p != null && o != null)
                        {
                            lock (toInsert)
                            {
                                toInsert.Add((s, p, o, withGraph));
                            }
                        }
                    }
                }
            }
            finally
            {
                results.Dispose();
            }
        }, 4 * 1024 * 1024); // 4MB stack

        thread.Start();
        thread.Join();
    }

    /// <summary>
    /// Instantiate a term from a span-based source.
    /// </summary>
    private static string? InstantiateTermFromSpan(Term term, BindingTable bindings, string source)
    {
        var termSpan = source.AsSpan(term.Start, term.Length);

        if (term.Type == TermType.Variable)
        {
            var idx = bindings.FindBinding(termSpan);
            if (idx >= 0)
                return bindings.GetString(idx).ToString();
            return null;
        }

        return termSpan.ToString();
    }

    private void ProcessModifyTemplates(
        BindingTable bindings,
        System.Collections.Generic.List<(string s, string p, string o, string? g)> toDelete,
        System.Collections.Generic.List<(string s, string p, string o, string? g)> toInsert,
        string? withGraph)
    {
        // Process DELETE template
        var deleteTemplate = _update.DeleteTemplate;

        // Process base patterns (use WITH graph as default)
        for (int i = 0; i < deleteTemplate.PatternCount; i++)
        {
            var tp = deleteTemplate.GetPattern(i);
            var s = InstantiateTerm(tp.Subject, bindings);
            var p = InstantiateTerm(tp.Predicate, bindings);
            var o = InstantiateTerm(tp.Object, bindings);

            if (s != null && p != null && o != null)
            {
                toDelete.Add((s, p, o, withGraph));
            }
        }

        // Process explicit GRAPH clauses in DELETE template (override WITH graph)
        for (int i = 0; i < deleteTemplate.GraphClauseCount; i++)
        {
            var gc = deleteTemplate.GetGraphClause(i);
            var graphIri = ResolveGraphTerm(gc.Graph, bindings);

            for (int j = 0; j < gc.PatternCount; j++)
            {
                var tp = gc.GetPattern(j);
                var s = InstantiateTerm(tp.Subject, bindings);
                var p = InstantiateTerm(tp.Predicate, bindings);
                var o = InstantiateTerm(tp.Object, bindings);

                if (s != null && p != null && o != null && graphIri != null)
                {
                    toDelete.Add((s, p, o, graphIri));
                }
            }
        }

        // Process INSERT template
        var insertTemplate = _update.InsertTemplate;

        // Process base patterns (use WITH graph as default)
        for (int i = 0; i < insertTemplate.PatternCount; i++)
        {
            var tp = insertTemplate.GetPattern(i);
            var s = InstantiateTerm(tp.Subject, bindings);
            var p = InstantiateTerm(tp.Predicate, bindings);
            var o = InstantiateTerm(tp.Object, bindings);

            if (s != null && p != null && o != null)
            {
                toInsert.Add((s, p, o, withGraph));
            }
        }

        // Process explicit GRAPH clauses in INSERT template (override WITH graph)
        for (int i = 0; i < insertTemplate.GraphClauseCount; i++)
        {
            var gc = insertTemplate.GetGraphClause(i);
            var graphIri = ResolveGraphTerm(gc.Graph, bindings);

            for (int j = 0; j < gc.PatternCount; j++)
            {
                var tp = gc.GetPattern(j);
                var s = InstantiateTerm(tp.Subject, bindings);
                var p = InstantiateTerm(tp.Predicate, bindings);
                var o = InstantiateTerm(tp.Object, bindings);

                if (s != null && p != null && o != null && graphIri != null)
                {
                    toInsert.Add((s, p, o, graphIri));
                }
            }
        }
    }

    /// <summary>
    /// Resolve a graph term to its IRI string.
    /// Returns null if the term is a variable that is not bound.
    /// </summary>
    private string? ResolveGraphTerm(Term term, BindingTable bindings)
    {
        var termSpan = _source.AsSpan(term.Start, term.Length);

        if (term.Type == TermType.Variable)
        {
            var idx = bindings.FindBinding(termSpan);
            if (idx >= 0)
                return bindings.GetString(idx).ToString();
            return null;
        }

        return termSpan.ToString();
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

        return _source.AsSpan(start, length);
    }
}
