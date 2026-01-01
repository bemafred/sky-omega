// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Storage;
using ExecutorUpdateResult = SkyOmega.Mercury.Sparql.Execution.UpdateResult;

namespace SkyOmega.Mercury.Adapters;

/// <summary>
/// Adapter to bridge Mercury QuadStore with Mercury.Runtime.IO types.
/// Provides lambda-compatible functions for ReplSession.
/// </summary>
public static class StoreAdapter
{
    /// <summary>
    /// Creates a ReplSession connected to the given store.
    /// </summary>
    public static ReplSession CreateSession(QuadStore store)
    {
        return new ReplSession(
            executeQuery: sparql => ExecuteQuery(store, sparql),
            executeUpdate: sparql => ExecuteUpdate(store, sparql),
            getStatistics: () => GetStatistics(store),
            getNamedGraphs: () => GetNamedGraphs(store)
        );
    }

    /// <summary>
    /// Execute a SPARQL query against a store.
    /// </summary>
    public static QueryResult ExecuteQuery(QuadStore store, string sparql)
    {
        var sw = Stopwatch.StartNew();
        TimeSpan parseTime;

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
                sw.Stop();
                return new QueryResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    ParseTime = sw.Elapsed
                };
            }

            parseTime = sw.Elapsed;
            sw.Restart();

            store.AcquireReadLock();
            try
            {
                var executor = new QueryExecutor(store, sparql.AsSpan(), parsed);

                return parsed.Type switch
                {
                    QueryType.Select => ExecuteSelect(executor, sparql, parsed, parseTime, sw),
                    QueryType.Ask => ExecuteAsk(executor, parseTime, sw),
                    QueryType.Construct => ExecuteConstruct(executor, parseTime, sw),
                    QueryType.Describe => ExecuteDescribe(executor, parseTime, sw),
                    _ => new QueryResult
                    {
                        Success = false,
                        ErrorMessage = $"Unsupported query type: {parsed.Type}",
                        ParseTime = parseTime,
                        ExecutionTime = sw.Elapsed
                    }
                };
            }
            finally
            {
                store.ReleaseReadLock();
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new QueryResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ParseTime = sw.Elapsed
            };
        }
    }

    private static QueryResult ExecuteSelect(QueryExecutor executor, string sparql, Query parsed, TimeSpan parseTime, Stopwatch sw)
    {
        var results = executor.Execute();
        var rows = new List<Dictionary<string, string>>();

        var varNames = ExtractVariableNames(parsed, sparql);
        bool hasExplicitVars = varNames.Length > 0;

        try
        {
            if (!hasExplicitVars && results.MoveNext())
            {
                var bindings = results.Current;
                varNames = ExtractVariablesFromBindings(bindings, sparql);

                var firstRow = new Dictionary<string, string>();
                for (int i = 0; i < bindings.Count; i++)
                {
                    var varName = varNames.Length > i ? varNames[i] : $"?var{i}";
                    firstRow[varName] = bindings.GetString(i).ToString();
                }
                rows.Add(firstRow);
            }

            while (results.MoveNext())
            {
                var bindings = results.Current;
                var row = new Dictionary<string, string>();

                if (hasExplicitVars)
                {
                    foreach (var varName in varNames)
                    {
                        var idx = bindings.FindBinding(varName.AsSpan());
                        row[varName] = idx >= 0 ? bindings.GetString(idx).ToString() : "";
                    }
                }
                else
                {
                    for (int i = 0; i < bindings.Count; i++)
                    {
                        var varName = varNames.Length > i ? varNames[i] : $"?var{i}";
                        row[varName] = bindings.GetString(i).ToString();
                    }
                }
                rows.Add(row);
            }
        }
        finally
        {
            results.Dispose();
        }

        sw.Stop();

        return new QueryResult
        {
            Success = true,
            Kind = ExecutionResultKind.Select,
            Variables = varNames,
            Rows = rows,
            ParseTime = parseTime,
            ExecutionTime = sw.Elapsed
        };
    }

    private static string[] ExtractVariableNames(Query query, string source)
    {
        if (query.SelectClause.SelectAll)
            return [];

        var vars = new List<string>();
        for (int i = 0; i < query.SelectClause.AggregateCount; i++)
        {
            var agg = query.SelectClause.GetAggregate(i);
            if (agg.AliasLength > 0)
            {
                var alias = source.AsSpan().Slice(agg.AliasStart, agg.AliasLength).ToString();
                vars.Add(alias.StartsWith('?') ? alias : "?" + alias);
            }
        }

        return vars.Count > 0 ? vars.ToArray() : [];
    }

    private static string[] ExtractVariablesFromBindings(BindingTable bindings, string source)
    {
        var knownVars = new List<(string Name, int Hash)>();
        var span = source.AsSpan();

        for (int i = 0; i < span.Length - 1; i++)
        {
            if (span[i] == '?')
            {
                int start = i;
                int end = i + 1;
                while (end < span.Length && (char.IsLetterOrDigit(span[end]) || span[end] == '_'))
                    end++;

                if (end > start + 1)
                {
                    var varName = span.Slice(start, end - start).ToString();
                    var hash = ComputeVariableHash(varName.AsSpan());
                    if (!knownVars.Exists(v => v.Hash == hash))
                        knownVars.Add((varName, hash));
                }
            }
        }

        var result = new string[bindings.Count];
        for (int i = 0; i < bindings.Count; i++)
        {
            var bindingHash = bindings.GetVariableHash(i);
            var found = knownVars.Find(v => v.Hash == bindingHash);
            result[i] = found.Name ?? $"?var{i}";
        }

        return result;
    }

    private static int ComputeVariableHash(ReadOnlySpan<char> name)
    {
        uint hash = 2166136261;
        foreach (var ch in name)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return (int)hash;
    }

    private static QueryResult ExecuteAsk(QueryExecutor executor, TimeSpan parseTime, Stopwatch sw)
    {
        var result = executor.ExecuteAsk();
        sw.Stop();

        return new QueryResult
        {
            Success = true,
            Kind = ExecutionResultKind.Ask,
            AskResult = result,
            ParseTime = parseTime,
            ExecutionTime = sw.Elapsed
        };
    }

    private static QueryResult ExecuteConstruct(QueryExecutor executor, TimeSpan parseTime, Stopwatch sw)
    {
        var triples = new List<(string, string, string)>();
        var results = executor.Execute();

        try
        {
            while (results.MoveNext())
            {
                var bindings = results.Current;
                if (bindings.Count >= 3)
                {
                    triples.Add((
                        bindings.GetString(0).ToString(),
                        bindings.GetString(1).ToString(),
                        bindings.GetString(2).ToString()
                    ));
                }
            }
        }
        finally
        {
            results.Dispose();
        }

        sw.Stop();

        return new QueryResult
        {
            Success = true,
            Kind = ExecutionResultKind.Construct,
            Triples = triples,
            ParseTime = parseTime,
            ExecutionTime = sw.Elapsed
        };
    }

    private static QueryResult ExecuteDescribe(QueryExecutor executor, TimeSpan parseTime, Stopwatch sw)
    {
        var triples = new List<(string, string, string)>();
        var results = executor.Execute();

        try
        {
            while (results.MoveNext())
            {
                var bindings = results.Current;
                if (bindings.Count >= 3)
                {
                    triples.Add((
                        bindings.GetString(0).ToString(),
                        bindings.GetString(1).ToString(),
                        bindings.GetString(2).ToString()
                    ));
                }
            }
        }
        finally
        {
            results.Dispose();
        }

        sw.Stop();

        return new QueryResult
        {
            Success = true,
            Kind = ExecutionResultKind.Describe,
            Triples = triples,
            ParseTime = parseTime,
            ExecutionTime = sw.Elapsed
        };
    }

    /// <summary>
    /// Execute a SPARQL update against a store.
    /// </summary>
    public static Runtime.IO.UpdateResult ExecuteUpdate(QuadStore store, string sparql)
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
                sw.Stop();
                return new Runtime.IO.UpdateResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    ParseTime = sw.Elapsed
                };
            }

            var parseTime = sw.Elapsed;
            sw.Restart();

            var executor = new UpdateExecutor(store, sparql.AsSpan(), parsed);
            ExecutorUpdateResult result = executor.Execute();

            sw.Stop();

            return new Runtime.IO.UpdateResult
            {
                Success = result.Success,
                AffectedCount = result.AffectedCount,
                ErrorMessage = result.ErrorMessage,
                ParseTime = parseTime,
                ExecutionTime = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new Runtime.IO.UpdateResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ParseTime = sw.Elapsed
            };
        }
    }

    /// <summary>
    /// Get store statistics.
    /// </summary>
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

    /// <summary>
    /// Get named graphs from the store.
    /// </summary>
    public static IEnumerable<string> GetNamedGraphs(QuadStore store)
    {
        var graphs = new List<string>();

        store.AcquireReadLock();
        try
        {
            var enumerator = store.GetNamedGraphs();
            while (enumerator.MoveNext())
            {
                graphs.Add(enumerator.Current.ToString());
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }

        return graphs;
    }
}
