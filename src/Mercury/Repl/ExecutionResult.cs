// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SkyOmega.Mercury.Diagnostics;

namespace SkyOmega.Mercury.Repl;

/// <summary>
/// The type of result from executing a REPL command.
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
/// Result of executing a query or command in the REPL.
/// </summary>
public sealed class ExecutionResult
{
    /// <summary>
    /// The kind of result.
    /// </summary>
    public ExecutionResultKind Kind { get; init; }

    /// <summary>
    /// True if execution completed successfully (may still have warnings).
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Materialized diagnostics from parsing and execution.
    /// </summary>
    public List<MaterializedDiagnostic>? Diagnostics { get; init; }

    /// <summary>
    /// Returns true if there are any diagnostics.
    /// </summary>
    public bool HasDiagnostics => Diagnostics != null && Diagnostics.Count > 0;

    /// <summary>
    /// Returns true if there are any errors in diagnostics.
    /// </summary>
    public bool HasErrors => Diagnostics?.Any(d => d.IsError) ?? false;

    /// <summary>
    /// Time spent parsing the input.
    /// </summary>
    public TimeSpan ParseTime { get; init; }

    /// <summary>
    /// Time spent executing the query/update.
    /// </summary>
    public TimeSpan ExecutionTime { get; init; }

    /// <summary>
    /// Total time (parse + execution).
    /// </summary>
    public TimeSpan TotalTime => ParseTime + ExecutionTime;

    /// <summary>
    /// Number of result rows (for SELECT) or triples (for CONSTRUCT/DESCRIBE).
    /// </summary>
    public int RowCount { get; init; }

    /// <summary>
    /// Column names for SELECT results.
    /// </summary>
    public string[]? Variables { get; init; }

    /// <summary>
    /// Materialized rows for SELECT results.
    /// Each row is a dictionary of variable name to value string.
    /// </summary>
    public List<Dictionary<string, string>>? Rows { get; init; }

    /// <summary>
    /// Boolean result for ASK queries.
    /// </summary>
    public bool? AskResult { get; init; }

    /// <summary>
    /// Triples for CONSTRUCT/DESCRIBE results.
    /// </summary>
    public List<(string Subject, string Predicate, string Object)>? Triples { get; init; }

    /// <summary>
    /// Number of affected triples for UPDATE operations.
    /// </summary>
    public int AffectedCount { get; init; }

    /// <summary>
    /// Message for command results or errors.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Creates an empty result.
    /// </summary>
    public static ExecutionResult Empty() => new()
    {
        Kind = ExecutionResultKind.Empty,
        Success = true
    };

    /// <summary>
    /// Creates an error result.
    /// </summary>
    public static ExecutionResult Error(string message, List<MaterializedDiagnostic>? diagnostics = null) => new()
    {
        Kind = ExecutionResultKind.Error,
        Success = false,
        Message = message,
        Diagnostics = diagnostics
    };

    /// <summary>
    /// Creates a command result.
    /// </summary>
    public static ExecutionResult Command(string message) => new()
    {
        Kind = ExecutionResultKind.Command,
        Success = true,
        Message = message
    };

    /// <summary>
    /// Creates a prefix registration result.
    /// </summary>
    public static ExecutionResult PrefixRegistered(string prefix, string iri) => new()
    {
        Kind = ExecutionResultKind.PrefixRegistered,
        Success = true,
        Message = $"Prefix '{prefix}:' registered as <{iri}>"
    };

    /// <summary>
    /// Creates a base IRI result.
    /// </summary>
    public static ExecutionResult BaseSet(string iri) => new()
    {
        Kind = ExecutionResultKind.BaseSet,
        Success = true,
        Message = $"Base IRI set to <{iri}>"
    };
}
