// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Reflection;
using System.Text;
using ModelContextProtocol.Server;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Mcp;

/// <summary>
/// Holds the resolved store path for DI injection.
/// </summary>
public sealed class StorePathHolder(string path)
{
    public string Path { get; } = path;
}

[McpServerToolType]
public sealed class MercuryTools
{
    private readonly QuadStorePool _pool;
    private readonly StorePathHolder _storePath;

    public MercuryTools(QuadStorePool pool, StorePathHolder storePath)
    {
        _pool = pool;
        _storePath = storePath;
    }

    [McpServerTool(Name = "mercury_query"), Description("Execute a SPARQL SELECT, ASK, CONSTRUCT, or DESCRIBE query against the Mercury triple store. Supports text:match(?var, \"term\") in FILTER clauses for case-insensitive full-text search.")]
    public string Query([Description("The SPARQL query to execute")] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Error: Query is required";

        try
        {
            var result = SparqlEngine.Query(_pool.Active, query);
            if (!result.Success)
                return $"Error: {result.ErrorMessage}";

            var sb = new StringBuilder();

            switch (result.Kind)
            {
                case ExecutionResultKind.Select:
                    var variables = result.Variables ?? [];
                    var rows = result.Rows ?? [];
                    if (variables.Length > 0 && rows.Count > 0)
                    {
                        sb.AppendLine(string.Join("\t", variables));
                        foreach (var row in rows)
                        {
                            var values = new string[variables.Length];
                            for (int i = 0; i < variables.Length; i++)
                                values[i] = row.TryGetValue(variables[i], out var v) ? v : "";
                            sb.AppendLine(string.Join("\t", values));
                        }
                        sb.AppendLine($"\n{rows.Count} result(s)");
                    }
                    else
                    {
                        sb.AppendLine("No results");
                    }
                    break;

                case ExecutionResultKind.Ask:
                    sb.AppendLine(result.AskResult == true ? "true" : "false");
                    break;

                case ExecutionResultKind.Construct:
                case ExecutionResultKind.Describe:
                    var count = 0;
                    foreach (var (s, p, o) in result.Triples ?? [])
                    {
                        sb.AppendLine($"{s} {p} {o} .");
                        count++;
                    }
                    sb.AppendLine($"\n{count} triple(s)");
                    break;

                default:
                    return "Error: Unsupported query type";
            }

            return sb.ToString().Trim();
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
            var result = SparqlEngine.Update(_pool.Active, update);
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
        var stats = SparqlEngine.GetStatistics(_pool.Active);

        var sb = new StringBuilder();
        sb.AppendLine("Mercury Store Statistics:");
        sb.AppendLine($"  Quads: {stats.QuadCount:N0}");
        sb.AppendLine($"  Atoms: {stats.AtomCount:N0}");
        sb.AppendLine($"  Storage: {ByteFormatter.FormatCompact(stats.TotalBytes)}");
        sb.AppendLine($"  WAL TxId: {stats.WalTxId:N0}");
        sb.AppendLine($"  WAL Checkpoint: {stats.WalCheckpoint:N0}");
        sb.AppendLine($"  WAL Size: {ByteFormatter.FormatCompact(stats.WalSize)}");

        return sb.ToString().Trim();
    }

    [McpServerTool(Name = "mercury_graphs"), Description("List all named graphs in the Mercury triple store")]
    public string Graphs()
    {
        var graphs = SparqlEngine.GetNamedGraphs(_pool.Active);

        if (graphs.Count == 0)
            return "No named graphs. Only the default graph exists.";

        var sb = new StringBuilder();
        sb.AppendLine($"Named graphs ({graphs.Count}):");
        foreach (var g in graphs.OrderBy(g => g))
        {
            if (g.StartsWith('<') && g.EndsWith('>'))
                sb.AppendLine($"  {g}");
            else
                sb.AppendLine($"  <{g}>");
        }

        return sb.ToString().Trim();
    }

    [McpServerTool(Name = "mercury_store"), Description("Get the filesystem path of the Mercury store folder")]
    public string Store()
    {
        return _storePath.Path;
    }

    [McpServerTool(Name = "mercury_version"), Description("Get the Mercury MCP server version")]
    public string Version()
    {
        var version = typeof(MercuryTools).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
        return $"mercury-mcp {version}";
    }

    // Pruning is intentionally NOT exposed through MCP. Per ADR-006 (MCP Surface Discipline),
    // destructive operations whose effects an AI shouldn't initiate autonomously are CLI-only.
    // To prune a Mercury store, run the `mercury prune` CLI directly — the human's shell history
    // is the audit trail for the operation.
    //
    // For Reference profile stores, see also ADR-007 (Sealed Substrate Immutability):
    // pruning a Reference store is rejected at plan time; the alternative is to bulk-load
    // the source data with the desired filters into a new Reference store.
}
