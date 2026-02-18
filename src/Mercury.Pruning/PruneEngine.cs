using System;
using System.Collections.Generic;
using System.Diagnostics;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Pruning;
using SkyOmega.Mercury.Pruning.Filters;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury;

/// <summary>
/// Facade for store pruning operations.
/// Encapsulates the dual-instance copy-and-switch workflow with optional
/// graph and predicate filtering.
/// </summary>
public static class PruneEngine
{
    /// <summary>
    /// Execute a prune operation on the pool's active store.
    /// Transfers quads from primary to secondary with optional filtering,
    /// then switches primary to the pruned copy.
    /// </summary>
    /// <param name="pool">The store pool with named stores ("primary" and "secondary").</param>
    /// <param name="options">Pruning options (filters, history mode, dry run).</param>
    /// <returns>A <see cref="PruneResult"/> with operation metrics.</returns>
    public static PruneResult Execute(QuadStorePool pool, PruneOptions? options = null)
    {
        options ??= PruneOptions.Default;
        var sw = Stopwatch.StartNew();

        try
        {
            // Build filter from options
            var filters = new List<IPruningFilter>();

            if (options.ExcludeGraphs is { Length: > 0 })
                filters.Add(GraphFilter.Exclude(options.ExcludeGraphs));

            if (options.ExcludePredicates is { Length: > 0 })
                filters.Add(PredicateFilter.Exclude(options.ExcludePredicates));

            IPruningFilter? filter = filters.Count switch
            {
                0 => null,
                1 => filters[0],
                _ => CompositeFilter.All(filters.ToArray())
            };

            // Build transfer options
            var transferOptions = new TransferOptions
            {
                HistoryMode = options.HistoryMode,
                DryRun = options.DryRun
            };

            if (filter != null)
            {
                transferOptions = new TransferOptions
                {
                    HistoryMode = options.HistoryMode,
                    DryRun = options.DryRun,
                    Filter = filter
                };
            }

            // Execute pruning workflow
            pool.Clear("secondary");
            var transfer = new PruningTransfer(pool["primary"], pool["secondary"], transferOptions);
            var result = transfer.Execute();

            if (!result.Success)
            {
                return new PruneResult
                {
                    Success = false,
                    ErrorMessage = result.ErrorMessage,
                    QuadsScanned = result.TotalScanned,
                    QuadsWritten = result.TotalWritten,
                    Duration = sw.Elapsed,
                    DryRun = options.DryRun
                };
            }

            if (!options.DryRun)
            {
                pool.Switch("primary", "secondary");
                pool.Clear("secondary");
            }

            return new PruneResult
            {
                Success = true,
                QuadsScanned = result.TotalScanned,
                QuadsWritten = result.TotalWritten,
                BytesSaved = result.BytesSaved,
                Duration = sw.Elapsed,
                DryRun = options.DryRun
            };
        }
        catch (Exception ex)
        {
            return new PruneResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Duration = sw.Elapsed,
                DryRun = options.DryRun
            };
        }
    }
}
