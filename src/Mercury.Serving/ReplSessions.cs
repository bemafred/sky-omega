// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SkyOmega.Mercury.Pruning;
using SkyOmega.Mercury.Runtime.IO;

namespace SkyOmega.Mercury.Serving;

/// <summary>The REPL's commands that a tool offers beyond queries, updates, <c>:stats</c>, <c>:graphs</c> and
/// <c>:store</c>, which every session has.</summary>
[Flags]
public enum ReplCommands
{
    /// <summary>None beyond those every session has: what an attached client gets.</summary>
    None = 0,

    /// <summary><c>:prune</c>, a copy-and-switch of the store (<see cref="PruneArguments"/>).</summary>
    Prune = 1,

    /// <summary><c>:attach</c>, the REPL of another process (<see cref="AttachClient"/>).</summary>
    Attach = 2,

    /// <summary><c>:load</c>, a file of RDF into the store.</summary>
    Load = 4,

    /// <summary><c>:convert</c>, a file of RDF into another format, with no store.</summary>
    Convert = 8,

    /// <summary><c>:rebuild-indexes</c>, the secondary indexes from GSPO.</summary>
    RebuildIndexes = 16,
}

/// <summary>The REPL session over a store, for each tool that offers one (ADR-065 D11).</summary>
public static class ReplSessions
{
    /// <summary>A REPL session over <paramref name="store"/>'s active store, with the <paramref name="commands"/>
    /// the tool offers.</summary>
    public static ReplSession Create(StoreSession store, ReplCommands commands = ReplCommands.None)
    {
        var pool = store.Pool;
        return new ReplSession(
            executeQuery: sparql => SparqlEngine.Query(pool.Active, sparql),
            executeUpdate: sparql => SparqlEngine.Update(pool.Active, sparql),
            getStatistics: () => SparqlEngine.GetStatistics(pool.Active),
            getNamedGraphs: () => SparqlEngine.GetNamedGraphs(pool.Active),
            executePrune: commands.HasFlag(ReplCommands.Prune)
                ? args => PruneEngine.Execute(pool, PruneArguments.Parse(args))
                : null,
            getStorePath: () => store.Path,
            executeAttach: commands.HasFlag(ReplCommands.Attach)
                ? async target =>
                {
                    await AttachClient.RunAsync(target, Console.Out);
                    Console.WriteLine($"Detached from {target}.");
                }
                : null,
            // The REPL's :load is a standard load, whatever it is asked for.
            executeLoad: commands.HasFlag(ReplCommands.Load)
                ? (path, _, progress) => RdfEngine.LoadFileAsync(pool.Active, path, onProgress: OnProgress(progress))
                : null,
            executeConvert: commands.HasFlag(ReplCommands.Convert)
                ? (input, output, progress) => RdfEngine.ConvertAsync(input, output, OnProgress(progress))
                : null,
            executeRebuildIndexes: commands.HasFlag(ReplCommands.RebuildIndexes)
                ? progress =>
                {
                    pool.Active.RebuildSecondaryIndexes(progress);
                    return Task.CompletedTask;
                }
                : null);
    }

    private static Action<LoadProgress>? OnProgress(Action<long, TimeSpan>? progress) =>
        progress is null ? null : p => progress(p.TriplesLoaded, p.Elapsed);
}
