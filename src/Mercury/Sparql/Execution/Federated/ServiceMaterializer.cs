using System;
using System.Collections.Generic;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Execution.Operators;

namespace SkyOmega.Mercury.Sparql.Execution.Federated;

/// <summary>
/// Shared pool for SERVICE clause materialization.
/// Uses QuadStorePool to efficiently reuse temp stores across queries.
/// </summary>
internal static class ServiceStorePool
{
    /// <summary>
    /// Global pool for SERVICE temp stores.
    /// Sized for typical concurrent SERVICE usage (2x processor count).
    /// </summary>
    public static readonly QuadStorePool Instance = new(
        maxConcurrent: Environment.ProcessorCount * 2,
        purpose: "service");
}

/// <summary>
/// Orchestrates SERVICE clause materialization before query execution.
/// Converts SERVICE results into temporary QuadStores that can be queried
/// using standard TriplePatternScan operators.
///
/// <para>
/// <b>Why materialization?</b> SERVICE clauses have fundamentally different
/// access semantics than local patterns:
/// <list type="bullet">
///   <item>Remote: async HTTP I/O vs sync index access</item>
///   <item>Heap-allocated results vs stack-based iteration</item>
///   <item>No backtracking capability vs B+Tree cursor reset</item>
/// </list>
/// By materializing SERVICE results to a local QuadStore, we unify the
/// execution model - all patterns become local index scans.
/// </para>
/// </summary>
internal sealed class ServiceMaterializer : IDisposable
{
    private readonly ISparqlServiceExecutor _executor;
    private readonly QuadStorePool _pool;
    private readonly ServiceMaterializerOptions _options;
    private readonly List<QuadStore> _rentedStores = new();
    private bool _disposed;

    /// <summary>
    /// Creates a materializer using the specified executor and pool.
    /// </summary>
    /// <param name="executor">The service executor for remote queries.</param>
    /// <param name="pool">Optional pool; defaults to ServiceStorePool.Instance.</param>
    /// <param name="options">Optional options; defaults to ServiceMaterializerOptions.Default.</param>
    public ServiceMaterializer(
        ISparqlServiceExecutor executor,
        QuadStorePool? pool = null,
        ServiceMaterializerOptions? options = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _pool = pool ?? ServiceStorePool.Instance;
        _options = options ?? ServiceMaterializerOptions.Default;
    }

    /// <summary>
    /// Materializes a SERVICE clause's results into a temporary QuadStore.
    /// The store can then be queried using TriplePatternScan.
    /// </summary>
    /// <param name="clause">The SERVICE clause to materialize.</param>
    /// <param name="source">The query source string.</param>
    /// <returns>A QuadStore containing the SERVICE results as triples.</returns>
    /// <exception cref="SparqlServiceException">If SERVICE execution fails and not SILENT.</exception>
    public QuadStore Materialize(ServiceClause clause, ReadOnlySpan<char> source)
    {
        return Materialize(clause, source, default, hasBindings: false);
    }

    /// <summary>
    /// Materializes a SERVICE clause's results into a temporary QuadStore.
    /// The store can then be queried using TriplePatternScan.
    /// </summary>
    /// <param name="clause">The SERVICE clause to materialize.</param>
    /// <param name="source">The query source string.</param>
    /// <param name="incomingBindings">Bindings from outer patterns.</param>
    /// <returns>A QuadStore containing the SERVICE results as triples.</returns>
    /// <exception cref="SparqlServiceException">If SERVICE execution fails and not SILENT.</exception>
    public QuadStore Materialize(ServiceClause clause, ReadOnlySpan<char> source,
        BindingTable incomingBindings)
    {
        return Materialize(clause, source, incomingBindings, hasBindings: true);
    }

    private QuadStore Materialize(ServiceClause clause, ReadOnlySpan<char> source,
        BindingTable incomingBindings, bool hasBindings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Resolve endpoint URI
        var endpoint = ResolveEndpoint(clause, source, incomingBindings, hasBindings);

        // Rent from pool - store is already Clear()'d
        var store = _pool.Rent();
        _rentedStores.Add(store);

        try
        {
            // Build and execute query
            var query = BuildSparqlQuery(clause, source, incomingBindings, hasBindings);
            List<ServiceResultRow> results;

            try
            {
                results = _executor.ExecuteSelectAsync(endpoint, query)
                    .AsTask().GetAwaiter().GetResult();
            }
            catch (SparqlServiceException)
            {
                if (clause.Silent)
                {
                    // SILENT - return empty store
                    return store;
                }
                throw;
            }
            catch (Exception ex)
            {
                if (clause.Silent)
                {
                    return store;
                }
                throw new SparqlServiceException($"SERVICE execution failed: {ex.Message}", ex)
                {
                    EndpointUri = endpoint,
                    Query = query
                };
            }

            // Load results into store as triples
            LoadResultsToStore(store, results, clause, source);
            return store;
        }
        catch
        {
            // On failure, return store to pool immediately
            _rentedStores.Remove(store);
            _pool.Return(store);
            throw;
        }
    }

    /// <summary>
    /// Resolves the endpoint URI, handling variable endpoints.
    /// </summary>
    private static string ResolveEndpoint(ServiceClause clause, ReadOnlySpan<char> source,
        BindingTable incomingBindings, bool hasBindings)
    {
        if (!clause.Endpoint.IsVariable)
        {
            var iri = source.Slice(clause.Endpoint.Start, clause.Endpoint.Length);
            // Strip angle brackets
            if (iri.Length > 2 && iri[0] == '<' && iri[^1] == '>')
                return iri[1..^1].ToString();
            return iri.ToString();
        }

        // Variable endpoint - look up from bindings
        if (!hasBindings)
            throw new InvalidOperationException("SERVICE with variable endpoint requires incoming bindings");

        var varName = source.Slice(clause.Endpoint.Start, clause.Endpoint.Length);
        var idx = incomingBindings.FindBinding(varName);
        if (idx < 0)
            throw new InvalidOperationException($"SERVICE endpoint variable {varName.ToString()} is not bound");

        var endpointUri = incomingBindings.GetString(idx).ToString();
        // Strip angle brackets if present
        if (endpointUri.StartsWith('<') && endpointUri.EndsWith('>'))
            return endpointUri[1..^1];
        return endpointUri;
    }

    /// <summary>
    /// Builds a SPARQL query from the SERVICE clause patterns.
    /// Substitutes bound variables from incoming bindings.
    /// </summary>
    private static string BuildSparqlQuery(ServiceClause clause, ReadOnlySpan<char> source,
        BindingTable incomingBindings, bool hasBindings)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("SELECT * WHERE { ");

        for (int i = 0; i < clause.PatternCount; i++)
        {
            var pattern = clause.GetPattern(i);

            // Subject
            AppendTerm(sb, pattern.Subject, source, incomingBindings, hasBindings);
            sb.Append(' ');

            // Predicate
            AppendTerm(sb, pattern.Predicate, source, incomingBindings, hasBindings);
            sb.Append(' ');

            // Object
            AppendTerm(sb, pattern.Object, source, incomingBindings, hasBindings);
            sb.Append(" . ");
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendTerm(System.Text.StringBuilder sb, Term term,
        ReadOnlySpan<char> source, BindingTable incomingBindings, bool hasBindings)
    {
        var value = source.Slice(term.Start, term.Length);

        if (term.IsVariable && hasBindings)
        {
            // Check if variable is bound - substitute if so
            var idx = incomingBindings.FindBinding(value);
            if (idx >= 0)
            {
                sb.Append(incomingBindings.GetString(idx));
                return;
            }
        }

        sb.Append(value);
    }

    /// <summary>
    /// Loads SERVICE results into the temp store as triples.
    /// Each result row becomes triples based on the SERVICE patterns.
    /// </summary>
    private static void LoadResultsToStore(QuadStore store, List<ServiceResultRow> results,
        ServiceClause clause, ReadOnlySpan<char> source)
    {
        if (results.Count == 0)
            return;

        // For SERVICE results, we create synthetic triples that encode
        // the variable bindings. This allows TriplePatternScan to work normally.
        //
        // Strategy: Create triples with a synthetic predicate that encodes
        // the variable name, subject as row ID, object as value.
        //
        // Example: ?s <http://example.org/name> ?name from SERVICE
        // becomes: <_:row0> <_:var:s> <http://ex.org/person1> .
        //          <_:row0> <_:var:name> "Alice" .

        store.BeginBatch();
        try
        {
            for (int rowIndex = 0; rowIndex < results.Count; rowIndex++)
            {
                var row = results[rowIndex];
                var rowSubject = $"<_:row{rowIndex}>";

                foreach (var varName in row.Variables)
                {
                    var binding = row.GetBinding(varName);
                    var value = binding.ToRdfTerm();
                    var predicate = $"<_:var:{varName}>";

                    store.AddCurrentBatched(rowSubject, predicate, value);
                }
            }
            store.CommitBatch();
        }
        catch
        {
            store.RollbackBatch();
            throw;
        }
    }

    /// <summary>
    /// Fetches SERVICE results with threshold-based routing.
    /// Small result sets stay in-memory; large sets are materialized to indexed QuadStore.
    /// </summary>
    /// <param name="clause">The SERVICE clause to execute.</param>
    /// <param name="source">The query source string.</param>
    /// <param name="incomingBindings">Bindings from outer patterns.</param>
    /// <param name="hasBindings">Whether incoming bindings are present.</param>
    /// <returns>A result containing either in-memory list or indexed store.</returns>
    public ServiceFetchResult Fetch(
        ServiceClause clause,
        ReadOnlySpan<char> source,
        BindingTable incomingBindings,
        bool hasBindings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Resolve endpoint URI
        var endpoint = ResolveEndpoint(clause, source, incomingBindings, hasBindings);

        // Build and execute query
        var query = BuildSparqlQuery(clause, source, incomingBindings, hasBindings);
        List<ServiceResultRow> results;

        try
        {
            results = _executor.ExecuteSelectAsync(endpoint, query)
                .AsTask().GetAwaiter().GetResult();
        }
        catch (SparqlServiceException)
        {
            if (clause.Silent)
                return ServiceFetchResult.Empty();
            throw;
        }
        catch (Exception ex)
        {
            if (clause.Silent)
                return ServiceFetchResult.Empty();
            throw new SparqlServiceException($"SERVICE execution failed: {ex.Message}", ex)
            {
                EndpointUri = endpoint,
                Query = query
            };
        }

        // Threshold-based routing
        if (results.Count < _options.IndexedThreshold)
        {
            // Small result set - use in-memory path
            return ServiceFetchResult.InMemory(results);
        }

        // Large result set - materialize to indexed QuadStore
        var store = _pool.Rent();
        _rentedStores.Add(store);

        try
        {
            LoadResultsToStore(store, results, clause, source);

            // Extract variable names for indexed scan
            var variableNames = new List<string>();
            if (results.Count > 0)
            {
                foreach (var varName in results[0].Variables)
                {
                    variableNames.Add(varName);
                }
            }

            return ServiceFetchResult.Indexed(store, variableNames, results.Count);
        }
        catch
        {
            _rentedStores.Remove(store);
            _pool.Return(store);
            throw;
        }
    }

    /// <summary>
    /// Disposes the materializer and returns all rented stores to the pool.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var store in _rentedStores)
        {
            _pool.Return(store);
        }
        _rentedStores.Clear();
    }
}

/// <summary>
/// Result of SERVICE clause fetch - either in-memory or indexed.
/// </summary>
internal readonly struct ServiceFetchResult
{
    /// <summary>
    /// Whether this result uses the indexed path.
    /// </summary>
    public bool IsIndexed { get; }

    /// <summary>
    /// In-memory results (when IsIndexed = false).
    /// </summary>
    public List<ServiceResultRow>? Results { get; }

    /// <summary>
    /// Indexed QuadStore (when IsIndexed = true).
    /// </summary>
    public Storage.QuadStore? Store { get; }

    /// <summary>
    /// Variable names for indexed scan.
    /// </summary>
    public List<string>? VariableNames { get; }

    /// <summary>
    /// Row count for indexed scan.
    /// </summary>
    public int RowCount { get; }

    private ServiceFetchResult(
        bool isIndexed,
        List<ServiceResultRow>? results,
        Storage.QuadStore? store,
        List<string>? variableNames,
        int rowCount)
    {
        IsIndexed = isIndexed;
        Results = results;
        Store = store;
        VariableNames = variableNames;
        RowCount = rowCount;
    }

    /// <summary>
    /// Creates an empty result (for SILENT failures).
    /// </summary>
    public static ServiceFetchResult Empty() =>
        new(false, new List<ServiceResultRow>(), null, null, 0);

    /// <summary>
    /// Creates an in-memory result.
    /// </summary>
    public static ServiceFetchResult InMemory(List<ServiceResultRow> results) =>
        new(false, results, null, null, results.Count);

    /// <summary>
    /// Creates an indexed result.
    /// </summary>
    public static ServiceFetchResult Indexed(
        Storage.QuadStore store,
        List<string> variableNames,
        int rowCount) =>
        new(true, null, store, variableNames, rowCount);
}

/// <summary>
/// Options for SERVICE clause materialization.
/// </summary>
internal sealed class ServiceMaterializerOptions
{
    /// <summary>
    /// Result count threshold for in-memory vs indexed path.
    /// Results below this threshold use linear in-memory scan.
    /// Results at or above use indexed QuadStore.
    /// Default: 500 (B+Tree indexing pays off for joins at this scale).
    /// </summary>
    public int IndexedThreshold { get; set; } = 500;

    /// <summary>
    /// Default options instance.
    /// </summary>
    public static ServiceMaterializerOptions Default { get; } = new();
}

/// <summary>
/// Scan operator for SERVICE clause results using in-memory linear scan.
/// Best for small result sets (&lt; 500 rows).
/// Conforms to <see cref="IScan"/> contract (duck typing).
/// </summary>
internal ref struct ServicePatternScan
{
    private readonly List<ServiceResultRow> _results;
    private readonly int _initialBindingsCount;

    private int _resultIndex;
    private bool _exhausted;

    /// <summary>
    /// Creates a scan over materialized SERVICE results.
    /// </summary>
    /// <param name="results">The materialized SERVICE results.</param>
    /// <param name="initialBindings">The binding table with initial bindings.</param>
    public ServicePatternScan(
        List<ServiceResultRow> results,
        BindingTable initialBindings)
    {
        _results = results;
        _initialBindingsCount = initialBindings.Count;
        _resultIndex = 0;
        _exhausted = false;
    }

    /// <summary>
    /// Advances to the next SERVICE result, binding variables.
    /// </summary>
    public bool MoveNext(ref BindingTable bindings)
    {
        if (_exhausted)
            return false;

        while (_resultIndex < _results.Count)
        {
            // Truncate to initial bindings before each attempt
            bindings.TruncateTo(_initialBindingsCount);

            var row = _results[_resultIndex++];
            bool compatible = true;

            // Bind variables from current result row, checking for conflicts
            foreach (var varName in row.Variables)
            {
                var binding = row.GetBinding(varName);
                var rdfTerm = binding.ToRdfTerm();

                // Add ? prefix to match SPARQL variable naming
                var fullVarName = $"?{varName}";
                var fullVarSpan = fullVarName.AsSpan();

                // Check if variable is already bound (join condition)
                var existingIdx = bindings.FindBinding(fullVarSpan);
                if (existingIdx >= 0)
                {
                    // Variable already bound - check if values match
                    var existingValue = bindings.GetString(existingIdx);
                    if (!existingValue.SequenceEqual(rdfTerm.AsSpan()))
                    {
                        // Values don't match - skip this row
                        compatible = false;
                        break;
                    }
                    // Values match - no need to re-bind
                }
                else
                {
                    // Variable not yet bound - add it
                    bindings.Bind(fullVarSpan, rdfTerm.AsSpan());
                }
            }

            if (compatible)
                return true;
        }

        _exhausted = true;
        return false;
    }

    /// <summary>
    /// Disposes the scan. No-op for this implementation.
    /// </summary>
    public void Dispose()
    {
        // No resources to release - results are owned by caller
    }
}

/// <summary>
/// Scan operator for SERVICE results materialized to an indexed QuadStore.
/// Best for large result sets (&gt;= 500 rows) where B+Tree indexing pays off.
/// Uses synthetic triples: &lt;_:row{N}&gt; &lt;_:var:{varName}&gt; value .
/// Conforms to <see cref="IScan"/> contract (duck typing).
/// </summary>
internal ref struct IndexedServicePatternScan
{
    private readonly Storage.QuadStore _store;
    private readonly List<string> _variableNames;
    private readonly int _initialBindingsCount;
    private readonly int _rowCount;

    private int _currentRow;
    private bool _exhausted;

    /// <summary>
    /// Creates an indexed scan over SERVICE results materialized to a QuadStore.
    /// </summary>
    /// <param name="store">The QuadStore containing materialized SERVICE results.</param>
    /// <param name="variableNames">The variable names present in SERVICE results.</param>
    /// <param name="rowCount">The number of result rows.</param>
    /// <param name="initialBindings">The binding table with initial bindings.</param>
    public IndexedServicePatternScan(
        Storage.QuadStore store,
        List<string> variableNames,
        int rowCount,
        BindingTable initialBindings)
    {
        _store = store;
        _variableNames = variableNames;
        _rowCount = rowCount;
        _initialBindingsCount = initialBindings.Count;
        _currentRow = 0;
        _exhausted = false;
    }

    /// <summary>
    /// Advances to the next SERVICE result row, binding variables.
    /// Uses indexed lookup against the materialized QuadStore.
    /// </summary>
    public bool MoveNext(ref BindingTable bindings)
    {
        if (_exhausted)
            return false;

        while (_currentRow < _rowCount)
        {
            // Truncate to initial bindings before each attempt
            bindings.TruncateTo(_initialBindingsCount);

            var rowSubject = $"<_:row{_currentRow}>";
            _currentRow++;

            bool compatible = true;

            // For each variable, query the store for its value in this row
            foreach (var varName in _variableNames)
            {
                var predicate = $"<_:var:{varName}>";

                // Query store: rowSubject predicate ?value
                var results = _store.QueryCurrent(rowSubject.AsSpan(), predicate.AsSpan(), default);
                try
                {
                    if (!results.MoveNext())
                    {
                        // Variable not present in this row - skip
                        continue;
                    }

                    var value = results.Current.Object;

                    // Add ? prefix to match SPARQL variable naming
                    var fullVarName = $"?{varName}";
                    var fullVarSpan = fullVarName.AsSpan();

                    // Check if variable is already bound (join condition)
                    var existingIdx = bindings.FindBinding(fullVarSpan);
                    if (existingIdx >= 0)
                    {
                        // Variable already bound - check if values match
                        var existingValue = bindings.GetString(existingIdx);
                        if (!existingValue.SequenceEqual(value))
                        {
                            // Values don't match - skip this row
                            compatible = false;
                            break;
                        }
                        // Values match - no need to re-bind
                    }
                    else
                    {
                        // Variable not yet bound - add it
                        bindings.Bind(fullVarSpan, value);
                    }
                }
                finally
                {
                    results.Dispose();
                }
            }

            if (compatible)
                return true;
        }

        _exhausted = true;
        return false;
    }

    /// <summary>
    /// Disposes the scan. Store is managed by ServiceMaterializer.
    /// </summary>
    public void Dispose()
    {
        // Store lifecycle is managed by ServiceMaterializer, not the scan
    }
}
