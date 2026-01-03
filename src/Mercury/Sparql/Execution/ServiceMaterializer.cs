using System;
using System.Collections.Generic;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// Shared pool for SERVICE clause materialization.
/// Uses QuadStorePool to efficiently reuse temp stores across queries.
/// </summary>
public static class ServiceStorePool
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
public sealed class ServiceMaterializer : IDisposable
{
    private readonly ISparqlServiceExecutor _executor;
    private readonly QuadStorePool _pool;
    private readonly List<QuadStore> _rentedStores = new();
    private bool _disposed;

    /// <summary>
    /// Creates a materializer using the specified executor and pool.
    /// </summary>
    /// <param name="executor">The service executor for remote queries.</param>
    /// <param name="pool">Optional pool; defaults to ServiceStorePool.Instance.</param>
    public ServiceMaterializer(ISparqlServiceExecutor executor, QuadStorePool? pool = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _pool = pool ?? ServiceStorePool.Instance;
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
/// Scan operator for SERVICE clause results materialized to a temp QuadStore.
/// Wraps TriplePatternScan against the materialized store.
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
