// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Mcp;

[McpServerToolType]
public sealed class MercuryTools
{
    private readonly QuadStore _store;

    public MercuryTools(QuadStore store)
    {
        _store = store;
    }

    [McpServerTool(Name = "mercury_query"), Description("Execute a SPARQL SELECT, ASK, CONSTRUCT, or DESCRIBE query against the Mercury triple store")]
    public string Query([Description("The SPARQL query to execute")] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Error: Query is required";

        try
        {
            var parser = new SparqlParser(query.AsSpan());
            Query parsed;

            try
            {
                parsed = parser.ParseQuery();
            }
            catch (SparqlParseException ex)
            {
                return $"Error: {ex.Message}";
            }

            _store.AcquireReadLock();
            try
            {
                var executor = new QueryExecutor(_store, query.AsSpan(), parsed);
                var sb = new StringBuilder();

                switch (parsed.Type)
                {
                    case QueryType.Select:
                        var results = executor.Execute();
                        var rows = new List<string[]>();
                        string[]? varNames = null;

                        try
                        {
                            while (results.MoveNext())
                            {
                                var bindings = results.Current;
                                varNames ??= ExtractVariableNames(bindings, query);
                                var row = new string[bindings.Count];
                                for (int i = 0; i < bindings.Count; i++)
                                    row[i] = bindings.GetString(i).ToString();
                                rows.Add(row);
                            }
                        }
                        finally
                        {
                            results.Dispose();
                        }

                        if (varNames != null && rows.Count > 0)
                        {
                            sb.AppendLine(string.Join("\t", varNames));
                            foreach (var row in rows)
                                sb.AppendLine(string.Join("\t", row));
                            sb.AppendLine($"\n{rows.Count} result(s)");
                        }
                        else
                        {
                            sb.AppendLine("No results");
                        }
                        break;

                    case QueryType.Ask:
                        sb.AppendLine(executor.ExecuteAsk() ? "true" : "false");
                        break;

                    case QueryType.Construct:
                    case QueryType.Describe:
                        var tripleResults = executor.Execute();
                        var count = 0;
                        try
                        {
                            while (tripleResults.MoveNext())
                            {
                                var bindings = tripleResults.Current;
                                if (bindings.Count >= 3)
                                {
                                    sb.AppendLine($"{bindings.GetString(0)} {bindings.GetString(1)} {bindings.GetString(2)} .");
                                    count++;
                                }
                            }
                        }
                        finally
                        {
                            tripleResults.Dispose();
                        }
                        sb.AppendLine($"\n{count} triple(s)");
                        break;

                    default:
                        return $"Error: Unsupported query type: {parsed.Type}";
                }

                return sb.ToString().Trim();
            }
            finally
            {
                _store.ReleaseReadLock();
            }
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "mercury_update"), Description("Execute a SPARQL UPDATE (INSERT, DELETE, LOAD, CLEAR, etc.) to modify the triple store")]
    public string Update([Description("The SPARQL UPDATE statement to execute")] string update)
    {
        if (string.IsNullOrWhiteSpace(update))
            return "Error: Update statement is required";

        try
        {
            var parser = new SparqlParser(update.AsSpan());
            UpdateOperation parsed;

            try
            {
                parsed = parser.ParseUpdate();
            }
            catch (SparqlParseException ex)
            {
                return $"Error: {ex.Message}";
            }

            var executor = new UpdateExecutor(_store, update.AsSpan(), parsed);
            var result = executor.Execute();

            if (!result.Success)
                return $"Error: {result.ErrorMessage}";

            return $"OK - {result.AffectedCount} triple(s) affected";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "mercury_stats"), Description("Get Mercury store statistics (quad count, atoms, storage size, WAL status)")]
    public string Stats()
    {
        var (quadCount, atomCount, totalBytes) = _store.GetStatistics();
        var (walTxId, walCheckpoint, walSize) = _store.GetWalStatistics();

        var sb = new StringBuilder();
        sb.AppendLine("Mercury Store Statistics:");
        sb.AppendLine($"  Quads: {quadCount:N0}");
        sb.AppendLine($"  Atoms: {atomCount:N0}");
        sb.AppendLine($"  Storage: {ByteFormatter.FormatCompact(totalBytes)}");
        sb.AppendLine($"  WAL TxId: {walTxId:N0}");
        sb.AppendLine($"  WAL Checkpoint: {walCheckpoint:N0}");
        sb.AppendLine($"  WAL Size: {ByteFormatter.FormatCompact(walSize)}");

        return sb.ToString().Trim();
    }

    [McpServerTool(Name = "mercury_graphs"), Description("List all named graphs in the Mercury triple store")]
    public string Graphs()
    {
        var graphs = new List<string>();

        _store.AcquireReadLock();
        try
        {
            var enumerator = _store.GetNamedGraphs();
            while (enumerator.MoveNext())
                graphs.Add(enumerator.Current.ToString());
        }
        finally
        {
            _store.ReleaseReadLock();
        }

        if (graphs.Count == 0)
            return "No named graphs. Only the default graph exists.";

        var sb = new StringBuilder();
        sb.AppendLine($"Named graphs ({graphs.Count}):");
        foreach (var g in graphs.OrderBy(g => g))
        {
            sb.AppendLine($"  <{g}>");
        }

        return sb.ToString().Trim();
    }

    private static string[] ExtractVariableNames(BindingTable bindings, string source)
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
                    uint hash = 2166136261;
                    foreach (var ch in varName)
                    {
                        hash ^= ch;
                        hash *= 16777619;
                    }
                    if (!knownVars.Exists(v => v.Hash == (int)hash))
                        knownVars.Add((varName, (int)hash));
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
}
