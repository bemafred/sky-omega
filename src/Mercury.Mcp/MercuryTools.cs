// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Mcp;

[McpServerToolType]
public sealed class MercuryTools
{
    private readonly QuadStorePool _pool;

    public MercuryTools(QuadStorePool pool)
    {
        _pool = pool;
    }

    [McpServerTool(Name = "mercury_query"), Description("Execute a SPARQL SELECT, ASK, CONSTRUCT, or DESCRIBE query against the Mercury triple store")]
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

    [McpServerTool(Name = "mercury_prune"), Description("Compact the Mercury store by removing soft-deleted data and optionally filtering graphs or predicates")]
    public string Prune(
        [Description("Preview without writing (default: false)")] bool dryRun = false,
        [Description("History mode: 'flatten' (default), 'preserve', or 'all'")] string historyMode = "flatten",
        [Description("Comma-separated graph IRIs to exclude")] string? excludeGraphs = null,
        [Description("Comma-separated predicate IRIs to exclude")] string? excludePredicates = null)
    {
        try
        {
            var mode = historyMode.ToLowerInvariant() switch
            {
                "preserve" => HistoryMode.PreserveVersions,
                "all" => HistoryMode.PreserveAll,
                _ => HistoryMode.FlattenToCurrent
            };

            string[]? graphIris = null;
            if (!string.IsNullOrWhiteSpace(excludeGraphs))
            {
                graphIris = excludeGraphs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (graphIris.Length == 0) graphIris = null;
            }

            string[]? predIris = null;
            if (!string.IsNullOrWhiteSpace(excludePredicates))
            {
                predIris = excludePredicates.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (predIris.Length == 0) predIris = null;
            }

            var options = new PruneOptions
            {
                DryRun = dryRun,
                HistoryMode = mode,
                ExcludeGraphs = graphIris,
                ExcludePredicates = predIris
            };

            var result = PruneEngine.Execute(_pool, options);

            if (!result.Success)
                return $"Prune failed: {result.ErrorMessage}";

            var sb = new StringBuilder();
            sb.AppendLine(dryRun ? "Prune dry-run complete:" : "Prune complete:");
            sb.AppendLine($"  Quads scanned: {result.QuadsScanned:N0}");
            sb.AppendLine($"  Quads written: {result.QuadsWritten:N0}");
            if (!dryRun)
                sb.AppendLine($"  Space saved: {ByteFormatter.FormatCompact(result.BytesSaved)}");
            sb.AppendLine($"  Duration: {result.Duration.TotalMilliseconds:N0}ms");
            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}
