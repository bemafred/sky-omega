// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SkyOmega.Mercury.Abstractions;

namespace SkyOmega.Mercury.Serving;

/// <summary>The options of the REPL's <c>:prune</c>.</summary>
public static class PruneArguments
{
    /// <summary>
    /// Reads <c>--dry-run</c>, <c>--exclude-graph &lt;iri&gt;</c> and <c>--exclude-predicate &lt;iri&gt;</c>, each
    /// exclusion as many times as it is given. Anything else is ignored.
    /// </summary>
    /// <exception cref="ArgumentException"><c>--history</c>, which is gone (ADR-061 D12).</exception>
    public static PruneOptions Parse(string args)
    {
        bool dryRun = false;
        var excludeGraphs = new List<string>();
        var excludePredicates = new List<string>();

        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            switch (parts[i])
            {
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--history":
                    // The history modes are gone (ADR-061 D12). Accepting the option and ignoring it would prune away
                    // the forgotten memories its user asked to keep.
                    throw new ArgumentException(
                        "--history is gone: a prune keeps what is remembered, each memory with the time it was memorized, " +
                        "and nothing that is forgotten (ADR-061 D12). To keep everything, copy the store.");
                case "--exclude-graph" when i + 1 < parts.Length:
                    excludeGraphs.Add(parts[++i]);
                    break;
                case "--exclude-predicate" when i + 1 < parts.Length:
                    excludePredicates.Add(parts[++i]);
                    break;
            }
        }

        return new PruneOptions
        {
            DryRun = dryRun,
            ExcludeGraphs = excludeGraphs.Count > 0 ? excludeGraphs.ToArray() : null,
            ExcludePredicates = excludePredicates.Count > 0 ? excludePredicates.ToArray() : null,
        };
    }
}
