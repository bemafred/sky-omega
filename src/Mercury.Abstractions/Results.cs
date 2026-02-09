// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace SkyOmega.Mercury.Abstractions;

/// <summary>
/// The type of result from executing a SPARQL query or command.
/// </summary>
public enum ExecutionResultKind
{
    /// <summary>No result (empty input, comment only).</summary>
    Empty,

    /// <summary>SELECT query with bindings.</summary>
    Select,

    /// <summary>ASK query with boolean result.</summary>
    Ask,

    /// <summary>CONSTRUCT query with triples.</summary>
    Construct,

    /// <summary>DESCRIBE query with triples.</summary>
    Describe,

    /// <summary>SPARQL Update operation.</summary>
    Update,

    /// <summary>PREFIX declaration registered.</summary>
    PrefixRegistered,

    /// <summary>BASE declaration set.</summary>
    BaseSet,

    /// <summary>REPL command executed (e.g., :help, :clear).</summary>
    Command,

    /// <summary>Parse or execution error.</summary>
    Error
}

/// <summary>
/// Result of a SPARQL query execution.
/// </summary>
public sealed class QueryResult
{
    public bool Success { get; init; }
    public ExecutionResultKind Kind { get; init; }
    public string[]? Variables { get; init; }
    public List<Dictionary<string, string>>? Rows { get; init; }
    public bool? AskResult { get; init; }
    public List<(string Subject, string Predicate, string Object)>? Triples { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan ParseTime { get; init; }
    public TimeSpan ExecutionTime { get; init; }
}

/// <summary>
/// Result of a SPARQL update execution.
/// </summary>
public sealed class UpdateResult
{
    public bool Success { get; init; }
    public int AffectedCount { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan ParseTime { get; init; }
    public TimeSpan ExecutionTime { get; init; }
}

/// <summary>
/// Result of a store pruning operation.
/// </summary>
public sealed class PruneResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public long QuadsScanned { get; init; }
    public long QuadsWritten { get; init; }
    public long BytesSaved { get; init; }
    public TimeSpan Duration { get; init; }
    public bool DryRun { get; init; }
}

/// <summary>
/// Store statistics for the :stats command.
/// </summary>
public sealed class StoreStatistics
{
    public long QuadCount { get; init; }
    public long AtomCount { get; init; }
    public long TotalBytes { get; init; }
    public long WalTxId { get; init; }
    public long WalCheckpoint { get; init; }
    public long WalSize { get; init; }
}
