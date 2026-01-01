// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Storage;
using ReplUpdateResult = SkyOmega.Mercury.Runtime.IO.UpdateResult;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Test helper for creating ReplSession instances.
/// </summary>
internal static class TestSessionHelper
{
    public static ReplSession CreateSession(QuadStore store)
    {
        return new ReplSession(
            executeQuery: sparql => ExecuteQuery(store, sparql),
            executeUpdate: sparql => ExecuteUpdate(store, sparql),
            getStatistics: () => GetStatistics(store),
            getNamedGraphs: () => GetNamedGraphs(store));
    }

    private static QueryResult ExecuteQuery(QuadStore store, string sparql)
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
                return new QueryResult { Success = false, ErrorMessage = ex.Message, ParseTime = sw.Elapsed };
            }

            var parseTime = sw.Elapsed;
            sw.Restart();

            store.AcquireReadLock();
            try
            {
                var executor = new QueryExecutor(store, sparql.AsSpan(), parsed);

                return parsed.Type switch
                {
                    QueryType.Select => ExecuteSelect(executor, sparql, parseTime, sw),
                    QueryType.Ask => new QueryResult
                    {
                        Success = true,
                        Kind = ExecutionResultKind.Ask,
                        AskResult = executor.ExecuteAsk(),
                        ParseTime = parseTime,
                        ExecutionTime = sw.Elapsed
                    },
                    QueryType.Construct or QueryType.Describe => ExecuteTriples(executor, parsed.Type, parseTime, sw),
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
            return new QueryResult { Success = false, ErrorMessage = ex.Message, ParseTime = sw.Elapsed };
        }
    }

    private static QueryResult ExecuteSelect(QueryExecutor executor, string sparql, TimeSpan parseTime, Stopwatch sw)
    {
        var results = executor.Execute();
        var rows = new List<Dictionary<string, string>>();
        string[] varNames = [];

        try
        {
            if (results.MoveNext())
            {
                var bindings = results.Current;
                varNames = ExtractVariableNames(bindings, sparql);
                rows.Add(BindingsToRow(bindings, varNames));
            }

            while (results.MoveNext())
                rows.Add(BindingsToRow(results.Current, varNames));
        }
        finally
        {
            results.Dispose();
        }

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

    private static Dictionary<string, string> BindingsToRow(BindingTable bindings, string[] varNames)
    {
        var row = new Dictionary<string, string>();
        for (int i = 0; i < bindings.Count; i++)
            row[varNames.Length > i ? varNames[i] : $"?var{i}"] = bindings.GetString(i).ToString();
        return row;
    }

    private static string[] ExtractVariableNames(BindingTable bindings, string source)
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

    private static QueryResult ExecuteTriples(QueryExecutor executor, QueryType type, TimeSpan parseTime, Stopwatch sw)
    {
        var triples = new List<(string, string, string)>();
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
            Kind = type == QueryType.Construct ? ExecutionResultKind.Construct : ExecutionResultKind.Describe,
            Triples = triples,
            ParseTime = parseTime,
            ExecutionTime = sw.Elapsed
        };
    }

    private static ReplUpdateResult ExecuteUpdate(QuadStore store, string sparql)
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
                return new ReplUpdateResult { Success = false, ErrorMessage = ex.Message, ParseTime = sw.Elapsed };
            }

            var parseTime = sw.Elapsed;
            sw.Restart();

            var executor = new UpdateExecutor(store, sparql.AsSpan(), parsed);
            var result = executor.Execute();

            return new ReplUpdateResult
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
            return new ReplUpdateResult { Success = false, ErrorMessage = ex.Message, ParseTime = sw.Elapsed };
        }
    }

    private static StoreStatistics GetStatistics(QuadStore store)
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

    private static IEnumerable<string> GetNamedGraphs(QuadStore store)
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
}
