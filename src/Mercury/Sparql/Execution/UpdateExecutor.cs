using System;
using System.Threading;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime.Buffers;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Sparql.Types;

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
    private string? _expandedTerm; // Buffer for expanded prefixed names

    // Blank node scope tracking - per-statement bnode identity (W3C spec)
    // Same bnode label within one statement = same node
    // Same bnode label across statements = different nodes
    private Dictionary<string, string>? _bnodeScope;

    // Static counter to ensure globally unique blank node IDs across all operations
    // Each operation creates a new UpdateExecutor, but they all share this counter
    private static long s_globalBnodeCounter;
    private long _bnodeBase;

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

        // Reserve a unique bnode ID range for this operation
        // Each operation gets 10 million IDs, ensuring no collisions between operations
        _bnodeBase = Interlocked.Add(ref s_globalBnodeCounter, 10_000_000);
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
    /// Execute a sequence of update operations.
    /// Operations are executed in order; if any fails (and is not SILENT), execution stops.
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

        foreach (var op in operations)
        {
            var executor = new UpdateExecutor(store, source, op, loadExecutor);
            var result = executor.Execute();

            if (!result.Success)
            {
                // Return error immediately unless SILENT
                return new UpdateResult
                {
                    Success = false,
                    AffectedCount = totalAffected,
                    ErrorMessage = result.ErrorMessage
                };
            }

            totalAffected += result.AffectedCount;
        }

        return new UpdateResult { Success = true, AffectedCount = totalAffected };
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
                    ? ExpandPrefixedName(_source.AsSpan(quad.GraphStart, quad.GraphLength))
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
                    ? ExpandPrefixedName(_source.AsSpan(quad.GraphStart, quad.GraphLength))
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
            var subject = ExpandPrefixedName(_source.AsSpan(tp.Subject.Start, tp.Subject.Length));
            var predicate = ExpandPrefixedName(_source.AsSpan(tp.Predicate.Start, tp.Predicate.Length));
            var obj = ExpandPrefixedName(_source.AsSpan(tp.Object.Start, tp.Object.Length));

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
                    // Fixed IRI graph - expand prefixed names
                    var graphIri = ExpandPrefixedName(_source.AsSpan(gc.Graph.Start, gc.Graph.Length)).ToString();
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
    /// Expands prefixed names to full IRIs.
    /// </summary>
    private ReadOnlySpan<char> ResolveTermForQuery(Term term)
    {
        if (term.Type == TermType.Variable)
            return ReadOnlySpan<char>.Empty; // Wildcard
        return ExpandPrefixedName(_source.AsSpan(term.Start, term.Length));
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
    /// Expands prefixed names to full IRIs.
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

        // Handle blank nodes with scoped identity
        // Same label within one statement = same node
        // Different statements = different nodes (via unique _bnodeBase)
        if (term.Type == TermType.BlankNode || (termSpan.Length > 2 && termSpan[0] == '_' && termSpan[1] == ':'))
        {
            return GetScopedBlankNode(termSpan).ToString();
        }

        // Not a variable or blank node - return the literal value with prefix expansion
        return ExpandPrefixedName(termSpan).ToString();
    }

    private UpdateResult ExecuteModify()
    {
        // DELETE/INSERT ... WHERE: Execute WHERE pattern, then apply DELETE and INSERT templates
        var wherePattern = _update.WhereClause.Pattern;

        // Extract WITH graph if present - expand prefixed names
        string? withGraph = null;
        if (_update.WithGraphLength > 0)
        {
            withGraph = ExpandPrefixedName(_source.AsSpan(_update.WithGraphStart, _update.WithGraphLength)).ToString();
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
            Prologue = _update.Prologue,
            // Apply USING clauses as dataset specification for WHERE matching
            Datasets = _update.UsingClauses
        };

        _store.AcquireReadLock();
        try
        {
            // If there's no WHERE pattern (and no subqueries), execute once with empty bindings
            if (wherePattern.PatternCount == 0 && wherePattern.GraphClauseCount == 0 && wherePattern.SubQueryCount == 0)
            {
                ProcessModifyTemplates(default, toDelete, toInsert, withGraph);
            }
            // WITH clause with simple single pattern - use direct store query to avoid stack overflow
            else if (withGraph != null && wherePattern.GraphClauseCount == 0 && wherePattern.PatternCount == 1)
            {
                ExecuteModifyWithSimplePattern(wherePattern, withGraph, toDelete, toInsert);
            }
            // WITH clause with multiple patterns - isolated stack frame via NoInlining
            else if (withGraph != null && wherePattern.GraphClauseCount == 0 && wherePattern.PatternCount > 1)
            {
                ExecuteModifyWithMultiPattern(query, withGraph, toDelete, toInsert);
            }
            else
            {
                // Standard path - no WITH clause or has GRAPH clauses
                var executor = new QueryExecutor(_store, _source.AsSpan(), query);
                var results = executor.Execute();
                int resultCount = 0;
                try
                {
                    while (results.MoveNext())
                    {
                        resultCount++;
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
            : ExpandPrefixedName(_source.AsSpan(tp.Subject.Start, tp.Subject.Length));
        var predicateSpan = tp.Predicate.Type == TermType.Variable
            ? ReadOnlySpan<char>.Empty
            : ExpandPrefixedName(_source.AsSpan(tp.Predicate.Start, tp.Predicate.Length));
        var objectSpan = tp.Object.Type == TermType.Variable
            ? ReadOnlySpan<char>.Empty
            : ExpandPrefixedName(_source.AsSpan(tp.Object.Start, tp.Object.Length));

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
    /// Execute modify with multiple patterns - isolated stack frame via NoInlining.
    /// </summary>
    /// <remarks>
    /// ADR-009: This method replaces the previous Thread-based workaround.
    /// The [NoInlining] attribute prevents stack frame merging, keeping
    /// the Query struct (~8KB) and QueryResults (~22KB) isolated to this frame.
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void ExecuteModifyWithMultiPattern(
        Query query,
        string withGraph,
        System.Collections.Generic.List<(string s, string p, string o, string? g)> toDelete,
        System.Collections.Generic.List<(string s, string p, string o, string? g)> toInsert)
    {
        // Add WITH graph as dataset clause
        var modifiedQuery = query;
        modifiedQuery.Datasets = new[] { DatasetClause.Default(_update.WithGraphStart, _update.WithGraphLength) };

        var executor = new QueryExecutor(_store, _source.AsSpan(), modifiedQuery);
        var results = executor.Execute();
        try
        {
            while (results.MoveNext())
            {
                var b = results.Current;
                var deleteTemplate = _update.DeleteTemplate;
                var insertTemplate = _update.InsertTemplate;

                // Process DELETE template - base patterns use WITH graph
                for (int i = 0; i < deleteTemplate.PatternCount; i++)
                {
                    var tp = deleteTemplate.GetPattern(i);
                    var s = InstantiateTermFromSpan(tp.Subject, b);
                    var p = InstantiateTermFromSpan(tp.Predicate, b);
                    var o = InstantiateTermFromSpan(tp.Object, b);

                    if (s != null && p != null && o != null)
                    {
                        toDelete.Add((s, p, o, withGraph));
                    }
                }

                // Process INSERT template - base patterns use WITH graph
                for (int i = 0; i < insertTemplate.PatternCount; i++)
                {
                    var tp = insertTemplate.GetPattern(i);
                    var s = InstantiateTermFromSpan(tp.Subject, b);
                    var p = InstantiateTermFromSpan(tp.Predicate, b);
                    var o = InstantiateTermFromSpan(tp.Object, b);

                    if (s != null && p != null && o != null)
                    {
                        toInsert.Add((s, p, o, withGraph));
                    }
                }
            }
        }
        finally
        {
            results.Dispose();
        }
    }

    /// <summary>
    /// Instantiate a term from a span-based source.
    /// Expands prefixed names to full IRIs.
    /// </summary>
    private string? InstantiateTermFromSpan(Term term, BindingTable bindings)
    {
        var termSpan = _source.AsSpan(term.Start, term.Length);

        if (term.Type == TermType.Variable)
        {
            var idx = bindings.FindBinding(termSpan);
            if (idx >= 0)
                return bindings.GetString(idx).ToString();
            return null;
        }

        // Handle blank nodes with scoped identity
        if (term.Type == TermType.BlankNode || (termSpan.Length > 2 && termSpan[0] == '_' && termSpan[1] == ':'))
        {
            return GetScopedBlankNode(termSpan).ToString();
        }

        return ExpandPrefixedName(termSpan).ToString();
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
    /// Expands prefixed names to full IRIs.
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

        return ExpandPrefixedName(termSpan).ToString();
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

    /// <summary>
    /// Get or create a scoped blank node identifier.
    /// Same label within one statement = same node.
    /// Different operations get different IDs via unique _bnodeBase.
    /// </summary>
    private ReadOnlySpan<char> GetScopedBlankNode(ReadOnlySpan<char> label)
    {
        _bnodeScope ??= new Dictionary<string, string>();

        var labelStr = label.ToString();
        if (_bnodeScope.TryGetValue(labelStr, out var existing))
            return existing.AsSpan();

        // Use _bnodeBase + scope count to generate globally unique IDs
        // _bnodeBase is unique per UpdateExecutor instance
        var newBnode = $"_:b{_bnodeBase + _bnodeScope.Count}";
        _bnodeScope[labelStr] = newBnode;
        _expandedTerm = newBnode; // Use existing buffer field for span lifetime
        return _expandedTerm.AsSpan();
    }

    /// <summary>
    /// Begin a new blank node scope. Call at the start of each UPDATE statement.
    /// This ensures bnode labels in different statements create different nodes.
    /// </summary>
    private void BeginBnodeScope()
    {
        _bnodeScope?.Clear();
    }

    /// <summary>
    /// Expands a prefixed name to its full IRI using the prologue prefix mappings.
    /// Also handles 'a' shorthand for rdf:type.
    /// Returns the original span if not a prefixed name or no matching prefix found.
    /// </summary>
    private ReadOnlySpan<char> ExpandPrefixedName(ReadOnlySpan<char> term)
    {
        // Skip if already a full IRI, literal, or blank node
        if (term.Length == 0 || term[0] == '<' || term[0] == '"' || term[0] == '_')
            return term;

        // Handle 'a' shorthand for rdf:type (SPARQL keyword)
        if (term.Length == 1 && term[0] == 'a')
            return SyntheticTermHelper.RdfType.AsSpan();

        // Look for colon indicating prefixed name
        var colonIdx = term.IndexOf(':');
        if (colonIdx < 0)
            return term;

        var prefixCount = _update.Prologue.PrefixCount;
        if (prefixCount == 0)
            return term;

        // Include the colon in the prefix (stored prefixes include trailing colon, e.g., "ex:")
        var prefixWithColon = term.Slice(0, colonIdx + 1);
        var localPart = term.Slice(colonIdx + 1);

        // Find matching prefix in mappings
        for (int i = 0; i < prefixCount; i++)
        {
            var (prefixStart, prefixLength, iriStart, iriLength) = _update.Prologue.GetPrefix(i);
            var mappingPrefix = _source.AsSpan(prefixStart, prefixLength);
            if (prefixWithColon.SequenceEqual(mappingPrefix))
            {
                // Found matching prefix, expand to full IRI
                // The IRI is stored with angle brackets, e.g., "<http://example.org/>"
                var iriBase = _source.AsSpan(iriStart, iriLength);

                // Strip angle brackets from IRI base if present, then build full IRI
                var iriContent = iriBase;
                if (iriContent.Length >= 2 && iriContent[0] == '<' && iriContent[^1] == '>')
                    iriContent = iriContent.Slice(1, iriContent.Length - 2);

                // Build full IRI: <base + localPart>
                _expandedTerm = $"<{iriContent.ToString()}{localPart.ToString()}>";
                return _expandedTerm.AsSpan();
            }
        }

        return term;
    }
}
