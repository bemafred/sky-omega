using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Execution.Federated;
using SkyOmega.Mercury.Sparql.Execution.Operators;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury;

/// <summary>
/// Facade for SPARQL query and update operations against a QuadStore.
/// Encapsulates the parse-execute-materialize pipeline, including correct
/// variable name extraction, cancellation token handling, and read lock management.
/// </summary>
public static class SparqlEngine
{
    /// <summary>
    /// Execute a SPARQL SELECT, ASK, CONSTRUCT, or DESCRIBE query.
    /// </summary>
    /// <param name="store">The store to query.</param>
    /// <param name="sparql">The SPARQL query string.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A <see cref="QueryResult"/> with the query results and timing information.</returns>
    public static QueryResult Query(QuadStore store, string sparql, CancellationToken ct = default)
        => QueryCore(store, sparql, ct, reorderBgp: false);

    /// <summary>
    /// Benchmark-only entry that runs with the tree's BGP-reorder knob (the QueryPlanner selectivity model) enabled or
    /// disabled, to measure planned-tree vs unplanned-tree. Identical materialization to <see cref="Query"/>.
    /// </summary>
    internal static QueryResult QueryWithBgpReorder(QuadStore store, string sparql, bool reorderBgp = false,
        CancellationToken ct = default)
        => QueryCore(store, sparql, ct, reorderBgp: reorderBgp);

    private static QueryResult QueryCore(QuadStore store, string sparql, CancellationToken ct, bool reorderBgp = false)
    {
        var sw = Stopwatch.StartNew();
        QueryResult result;
        var queryKind = QueryMetricsKind.Select; // updated once the parser identifies the query type

        try
        {
            var parser = new SparqlParser(sparql.AsSpan());
            Query parsed;

            try
            {
                parsed = parser.ParseQuery();
            }
            catch (SparqlParseException ex)
            {
                result = new QueryResult { Success = false, Kind = ExecutionResultKind.Error, ErrorMessage = ex.Message, ParseTime = sw.Elapsed };
                EmitQueryMetrics(store, queryKind, result);
                return result;
            }

            queryKind = MapQueryKind(parsed.Type);
            var parseTime = sw.Elapsed;
            sw.Restart();

            QueryCancellation.SetToken(ct);
            store.AcquireReadLock();
            try
            {
                var planner = new QueryPlanner(store.Statistics, store.Atoms);

                using var executor = new QueryExecutor(store, sparql.AsSpan(), parsed, null, planner);
                executor.ReorderBgpInTree = reorderBgp;

                result = parsed.Type switch
                {
                    QueryType.Select => ExecuteSelect(executor, sparql, parsed, parseTime, sw),
                    QueryType.Ask => new QueryResult
                    {
                        Success = true,
                        Kind = ExecutionResultKind.Ask,
                        AskResult = executor.ExecuteAsk(),
                        ParseTime = parseTime,
                        ExecutionTime = sw.Elapsed
                    },
                    QueryType.Construct => ExecuteConstruct(executor, parseTime, sw),
                    QueryType.Describe => ExecuteTriples(executor, ExecutionResultKind.Describe, parseTime, sw),
                    _ => new QueryResult
                    {
                        Success = false,
                        Kind = ExecutionResultKind.Error,
                        ErrorMessage = $"Unsupported query type: {parsed.Type}",
                        ParseTime = parseTime,
                        ExecutionTime = sw.Elapsed
                    }
                };
            }
            finally
            {
                store.ReleaseReadLock();
                QueryCancellation.ClearToken();
            }
        }
        catch (OperationCanceledException)
        {
            result = new QueryResult { Success = false, Kind = ExecutionResultKind.Error, ErrorMessage = "Query cancelled", ParseTime = sw.Elapsed };
        }
        catch (Exception ex)
        {
            result = new QueryResult { Success = false, Kind = ExecutionResultKind.Error, ErrorMessage = ex.Message, ParseTime = sw.Elapsed };
        }

        EmitQueryMetrics(store, queryKind, result);
        return result;
    }

    /// <summary>
    /// Emit a <see cref="QueryMetrics"/> record to the store's listeners if any are attached.
    /// The early-out gates struct construction so the no-listener path stays zero-overhead.
    /// ADR-035: fans out to both legacy <c>QueryMetricsListener</c> and umbrella
    /// <c>ObservabilityListener</c>; reference-equality avoids double emission.
    /// </summary>
    private static void EmitQueryMetrics(QuadStore store, QueryMetricsKind kind, QueryResult result)
    {
        if (store.QueryMetricsListener is null && store.ObservabilityListener is null) return;

        var rows = result.Rows?.Count
            ?? result.Triples?.Count
            ?? (result.Kind == ExecutionResultKind.Ask ? (result.AskResult == true ? 1L : 0L) : 0L);

        var metrics = new QueryMetrics(
            Timestamp: DateTimeOffset.UtcNow,
            Profile: store.Schema.Profile,
            Kind: kind,
            ParseTime: result.ParseTime,
            ExecutionTime: result.ExecutionTime,
            RowsReturned: rows,
            Success: result.Success,
            ErrorMessage: result.ErrorMessage);

        store.EmitQueryMetrics(in metrics);
    }

    private static QueryMetricsKind MapQueryKind(QueryType type) => type switch
    {
        QueryType.Select => QueryMetricsKind.Select,
        QueryType.Ask => QueryMetricsKind.Ask,
        QueryType.Construct => QueryMetricsKind.Construct,
        QueryType.Describe => QueryMetricsKind.Describe,
        _ => QueryMetricsKind.Select
    };

    /// <summary>
    /// Execute a SPARQL UPDATE (INSERT DATA, DELETE DATA, CLEAR, DROP, etc.).
    /// </summary>
    /// <param name="store">The store to update.</param>
    /// <param name="sparql">The SPARQL update string.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>An <see cref="UpdateResult"/> with the operation result and timing information.</returns>
    public static UpdateResult Update(QuadStore store, string sparql, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var parser = new SparqlParser(sparql.AsSpan());
            UpdateOperation[] operations;

            // ADR-046: a request is a ';'-separated sequence ([29] Update). Parse all of it
            // before running any of it, so a syntax error anywhere executes nothing.
            try
            {
                operations = parser.ParseUpdateSequence();
            }
            catch (SparqlParseException ex)
            {
                return new UpdateResult { Success = false, ErrorMessage = ex.Message, ParseTime = sw.Elapsed };
            }

            var parseTime = sw.Elapsed;
            sw.Restart();

            QueryCancellation.SetToken(ct);
            try
            {
                using var loadExecutor = new LoadExecutor();
                var result = UpdateExecutor.ExecuteSequence(store, sparql.AsSpan(), operations, loadExecutor);

                return new UpdateResult
                {
                    Success = result.Success,
                    AffectedCount = result.AffectedCount,
                    ErrorMessage = result.ErrorMessage,
                    ParseTime = parseTime,
                    ExecutionTime = sw.Elapsed
                };
            }
            finally
            {
                QueryCancellation.ClearToken();
            }
        }
        catch (OperationCanceledException)
        {
            return new UpdateResult { Success = false, ErrorMessage = "Update cancelled", ParseTime = sw.Elapsed };
        }
        catch (Exception ex)
        {
            return new UpdateResult { Success = false, ErrorMessage = ex.Message, ParseTime = sw.Elapsed };
        }
    }

    /// <summary>
    /// Generate a SPARQL EXPLAIN plan for a query.
    /// </summary>
    /// <param name="sparql">The SPARQL query to explain.</param>
    /// <param name="store">Optional store for EXPLAIN ANALYZE (executes the query and collects statistics).</param>
    /// <returns>The formatted explain plan as a string.</returns>
    public static string Explain(string sparql, QuadStore? store = null)
    {
        var parser = new SparqlParser(sparql.AsSpan());
        var query = parser.ParseQuery();

        QueryPlanner? planner = null;
        if (store?.Statistics != null)
        {
            store.AcquireReadLock();
            try
            {
                planner = new QueryPlanner(store.Statistics, store.Atoms);
            }
            finally
            {
                store.ReleaseReadLock();
            }
        }

        var explainer = new SparqlExplainer(sparql.AsSpan(), in query, planner);

        if (store != null)
        {
            var plan = explainer.ExplainAnalyze(store);
            return plan.Format();
        }
        else
        {
            var plan = explainer.Explain();
            return plan.Format();
        }
    }

    /// <summary>
    /// Get all named graph IRIs in the store.
    /// </summary>
    /// <param name="store">The store to query.</param>
    /// <returns>A list of named graph IRI strings.</returns>
    public static IReadOnlyList<string> GetNamedGraphs(QuadStore store)
    {
        var graphs = new List<string>();
        store.AcquireReadLock();
        try
        {
            var enumerator = store.GetNamedGraphs();
            while (enumerator.MoveNext())
                graphs.Add(enumerator.Current.ToString());
        }
        finally
        {
            store.ReleaseReadLock();
        }
        return graphs;
    }

    /// <summary>
    /// Get store statistics (quad count, atom count, storage size, WAL status).
    /// </summary>
    /// <param name="store">The store to query.</param>
    /// <returns>A <see cref="StoreStatistics"/> with the store metrics.</returns>
    public static StoreStatistics GetStatistics(QuadStore store)
    {
        var (quadCount, atomCount, totalBytes) = store.GetStatistics();
        var (walTxId, walCheckpoint, walSize) = store.GetWalStatistics();
        return new StoreStatistics
        {
            Profile = store.Schema.Profile,
            QuadCount = quadCount,
            AtomCount = atomCount,
            TotalBytes = totalBytes,
            WalTxId = walTxId,
            WalCheckpoint = walCheckpoint,
            WalSize = walSize
        };
    }

    #region Private Helpers

    private static QueryResult ExecuteSelect(QueryExecutor executor, string sparql, Query parsed, TimeSpan parseTime, Stopwatch sw)
    {
        var results = executor.Execute();
        var rows = new List<Dictionary<string, string>>();

        // Every row is named by its own bindings: a binding's variable hash maps to the variable's name as written in
        // the query. Rows differ in which variables they bind (OPTIONAL, UNION branches), so names read from one row
        // and applied to the others by position would drop or mislabel values.
        var (nameByHash, namesInSourceOrder) = VariableNamesOf(sparql);

        string[] projectedNames;
        HashSet<string>? projected;
        if (!parsed.SelectClause.SelectAll)
        {
            // Explicit SELECT — expose projected variables AND aggregate aliases. The parser stores regular variables
            // and aggregate expressions in separate inline lists (SelectClause), so the original lexical order is not
            // preserved; projected variables come first and aggregate aliases after. That covers the common
            // `SELECT ?x (COUNT(*) AS ?n)` and pure-aggregate `SELECT (COUNT(*) AS ?n)` shapes.
            var varCount = parsed.SelectClause.ProjectedVariableCount;
            var aggCount = parsed.SelectClause.AggregateCount;
            var names = new List<string>(varCount + aggCount);
            for (int i = 0; i < varCount; i++)
            {
                var (start, length) = parsed.SelectClause.GetProjectedVariable(i);
                names.Add(WithoutSigil(sparql.AsSpan().Slice(start, length)));
            }
            for (int i = 0; i < aggCount; i++)
            {
                var agg = parsed.SelectClause.GetAggregate(i);
                if (agg.AliasLength == 0) continue;
                names.Add(WithoutSigil(sparql.AsSpan().Slice(agg.AliasStart, agg.AliasLength)));
            }
            projectedNames = names.ToArray();
            projected = new HashSet<string>(projectedNames);
        }
        else
        {
            // SELECT * — every variable that some row binds, in the order the query first mentions it. (A variable in
            // scope but bound in no row is not listed; that needs the WHERE's in-scope variables, ADR-055 D7.)
            projectedNames = [];
            projected = null;
        }

        var boundNames = projected == null ? new HashSet<string>() : null;
        try
        {
            long maxResultRows = executor.MaxResultRows; // unbounded-result guard (StorageOptions.MaxResultRows)
            while (results.MoveNext())
            {
                var bindings = results.Current;
                var row = new Dictionary<string, string>();
                for (int i = 0; i < bindings.Count; i++)
                {
                    if (!nameByHash.TryGetValue(bindings.GetVariableHash(i), out var name))
                        continue; // not a query variable (e.g. a blank node's internal binding)
                    if (projected != null && !projected.Contains(name))
                        continue;
                    if (row.TryAdd(name, bindings.GetString(i).ToString()))
                        boundNames?.Add(name);
                }
                rows.Add(row);
                ResultLimitExceededException.ThrowIfExceeded(maxResultRows, rows.Count);
            }
        }
        finally
        {
            results.Dispose();
        }

        if (boundNames != null)
            projectedNames = namesInSourceOrder.Where(boundNames.Contains).ToArray();

        return new QueryResult
        {
            Success = true,
            Kind = ExecutionResultKind.Select,
            Variables = projectedNames,
            Rows = rows,
            ParseTime = parseTime,
            ExecutionTime = sw.Elapsed
        };
    }

    private static QueryResult ExecuteConstruct(QueryExecutor executor, TimeSpan parseTime, Stopwatch sw)
    {
        var triples = new List<(string Subject, string Predicate, string Object)>();
        var results = executor.ExecuteConstruct();
        try
        {
            long maxResultRows = executor.MaxResultRows; // unbounded-result guard
            while (results.MoveNext())
            {
                var triple = results.Current;
                triples.Add((triple.Subject.ToString(), triple.Predicate.ToString(), triple.Object.ToString()));
                ResultLimitExceededException.ThrowIfExceeded(maxResultRows, triples.Count);
            }
        }
        finally
        {
            results.Dispose();
        }

        return new QueryResult
        {
            Success = true,
            Kind = ExecutionResultKind.Construct,
            Triples = triples,
            ParseTime = parseTime,
            ExecutionTime = sw.Elapsed
        };
    }

    private static QueryResult ExecuteTriples(QueryExecutor executor, ExecutionResultKind kind, TimeSpan parseTime, Stopwatch sw)
    {
        var triples = new List<(string Subject, string Predicate, string Object)>();
        var results = executor.Execute();
        try
        {
            long maxResultRows = executor.MaxResultRows; // unbounded-result guard
            while (results.MoveNext())
            {
                var b = results.Current;
                if (b.Count >= 3)
                    triples.Add((b.GetString(0).ToString(), b.GetString(1).ToString(), b.GetString(2).ToString()));
                ResultLimitExceededException.ThrowIfExceeded(maxResultRows, triples.Count);
            }
        }
        finally
        {
            results.Dispose();
        }

        return new QueryResult
        {
            Success = true,
            Kind = kind,
            Triples = triples,
            ParseTime = parseTime,
            ExecutionTime = sw.Elapsed
        };
    }

    /// <summary>
    /// Build a row dictionary mapping projected variable names to their values.
    /// Uses columnNames (hash-matched) to find the correct binding column for each projected variable.
    /// </summary>
    /// <summary>
    /// The query's variables, by the FNV-1a hash the executor gives a binding (of the variable as written, sigil
    /// included), and their names without the sigil in the order the query first mentions them. Both <c>?x</c> and
    /// <c>$x</c> are variables; they name the same variable, so they share one name.
    /// </summary>
    private static (Dictionary<int, string> NameByHash, List<string> NamesInSourceOrder) VariableNamesOf(string source)
    {
        var nameByHash = new Dictionary<int, string>();
        var namesInSourceOrder = new List<string>();
        var span = source.AsSpan();
        for (int i = 0; i < span.Length - 1; i++)
        {
            if (span[i] != '?' && span[i] != '$')
                continue;
            int end = i + 1;
            while (end < span.Length && (char.IsLetterOrDigit(span[end]) || span[end] == '_'))
                end++;
            if (end == i + 1)
                continue;
            var written = span.Slice(i, end - i);
            var name = written.Slice(1).ToString();
            if (nameByHash.TryAdd(Fnv1a.Hash(written), name) && !namesInSourceOrder.Contains(name))
                namesInSourceOrder.Add(name);
            i = end - 1;
        }
        return (nameByHash, namesInSourceOrder);
    }

    private static string WithoutSigil(ReadOnlySpan<char> variable) =>
        (variable.Length > 0 && (variable[0] == '?' || variable[0] == '$') ? variable.Slice(1) : variable).ToString();

    #endregion
}
