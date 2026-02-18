using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using SkyOmega.Mercury.Abstractions;
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
    {
        var sw = Stopwatch.StartNew();

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
                return new QueryResult { Success = false, Kind = ExecutionResultKind.Error, ErrorMessage = ex.Message, ParseTime = sw.Elapsed };
            }

            var parseTime = sw.Elapsed;
            sw.Restart();

            QueryCancellation.SetToken(ct);
            store.AcquireReadLock();
            try
            {
                var planner = store.Statistics != null
                    ? new QueryPlanner(store.Statistics, store.Atoms)
                    : null;
                using var executor = new QueryExecutor(store, sparql.AsSpan(), parsed, null, planner);

                return parsed.Type switch
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
            return new QueryResult { Success = false, Kind = ExecutionResultKind.Error, ErrorMessage = "Query cancelled", ParseTime = sw.Elapsed };
        }
        catch (Exception ex)
        {
            return new QueryResult { Success = false, Kind = ExecutionResultKind.Error, ErrorMessage = ex.Message, ParseTime = sw.Elapsed };
        }
    }

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
            UpdateOperation parsed;

            try
            {
                parsed = parser.ParseUpdate();
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
                var executor = new UpdateExecutor(store, sparql.AsSpan(), parsed, loadExecutor);
                var result = executor.Execute();

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
        string[] columnNames = []; // maps binding column index → variable name (via hash)
        string[] projectedNames = []; // the variable names to expose in the result

        try
        {
            if (results.MoveNext())
            {
                var bindings = results.Current;

                // Always use hash matching to correctly map binding columns to variable names
                columnNames = ExtractColumnNames(bindings, sparql);

                if (!parsed.SelectClause.SelectAll)
                {
                    // Explicit SELECT — expose only projected variables in SELECT clause order
                    projectedNames = new string[parsed.SelectClause.ProjectedVariableCount];
                    for (int i = 0; i < parsed.SelectClause.ProjectedVariableCount; i++)
                    {
                        var (start, length) = parsed.SelectClause.GetProjectedVariable(i);
                        var varSpan = sparql.AsSpan().Slice(start, length);
                        if (varSpan.Length > 0 && (varSpan[0] == '?' || varSpan[0] == '$'))
                            varSpan = varSpan.Slice(1);
                        projectedNames[i] = varSpan.ToString();
                    }
                }
                else
                {
                    // SELECT * — expose all bound variables
                    projectedNames = columnNames;
                }

                rows.Add(BindingsToRow(bindings, columnNames, projectedNames));
            }

            while (results.MoveNext())
                rows.Add(BindingsToRow(results.Current, columnNames, projectedNames));
        }
        finally
        {
            results.Dispose();
        }

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
            while (results.MoveNext())
            {
                var triple = results.Current;
                triples.Add((triple.Subject.ToString(), triple.Predicate.ToString(), triple.Object.ToString()));
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
            while (results.MoveNext())
            {
                var b = results.Current;
                if (b.Count >= 3)
                    triples.Add((b.GetString(0).ToString(), b.GetString(1).ToString(), b.GetString(2).ToString()));
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
    private static Dictionary<string, string> BindingsToRow(BindingTable bindings, string[] columnNames, string[] projectedNames)
    {
        var row = new Dictionary<string, string>();
        foreach (var name in projectedNames)
        {
            // Find the binding column for this projected variable
            var colIndex = Array.IndexOf(columnNames, name);
            if (colIndex >= 0 && colIndex < bindings.Count)
                row[name] = bindings.GetString(colIndex).ToString();
        }
        return row;
    }

    /// <summary>
    /// Extract variable names for each binding column using FNV-1a hash matching.
    /// Returns names without the ? prefix.
    /// </summary>
    private static string[] ExtractColumnNames(BindingTable bindings, string source)
    {
        var names = ExtractVariableNamesByHash(bindings, source);
        // Strip ? prefix
        for (int i = 0; i < names.Length; i++)
        {
            if (names[i].StartsWith("?") || names[i].StartsWith("$"))
                names[i] = names[i].Substring(1);
        }
        return names;
    }

    /// <summary>
    /// FNV-1a hash-based variable name extraction for SELECT *.
    /// Scans the SPARQL source for ?varName tokens, computes their FNV-1a hash,
    /// and matches against binding hashes from the query executor.
    /// </summary>
    private static string[] ExtractVariableNamesByHash(BindingTable bindings, string source)
    {
        var knownVars = new List<(string Name, int Hash)>();
        var span = source.AsSpan();

        for (int i = 0; i < span.Length - 1; i++)
        {
            if (span[i] == '?')
            {
                int end = i + 1;
                while (end < span.Length && (char.IsLetterOrDigit(span[end]) || span[end] == '_'))
                    end++;

                if (end > i + 1)
                {
                    var varName = span.Slice(i, end - i).ToString();
                    uint hash = 2166136261;
                    foreach (var ch in varName) { hash ^= ch; hash *= 16777619; }
                    if (!knownVars.Exists(v => v.Hash == (int)hash))
                        knownVars.Add((varName, (int)hash));
                }
            }
        }

        var result = new string[bindings.Count];
        for (int i = 0; i < bindings.Count; i++)
        {
            var bindingHash = bindings.GetVariableHash(i);
            string? foundName = null;
            foreach (var (name, hash) in knownVars)
            {
                if (hash == bindingHash) { foundName = name; break; }
            }
            result[i] = foundName ?? $"?var{i}";
        }
        return result;
    }

    #endregion
}
