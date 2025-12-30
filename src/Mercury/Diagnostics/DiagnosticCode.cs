// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace SkyOmega.Mercury.Diagnostics;

/// <summary>
/// Diagnostic error codes organized by category.
/// </summary>
/// <remarks>
/// Code ranges:
/// - E1xxx: Lexical/parse errors
/// - E2xxx: Semantic errors
/// - E3xxx: Runtime/execution errors
/// - E4xxx: Storage errors
/// - W1xxx: Warnings (add 10000 to code)
/// - I1xxx: Info/hints (add 20000 to code)
/// </remarks>
public static class DiagnosticCode
{
    // ========================================================================
    // E1xxx - Lexical/Parse Errors
    // ========================================================================

    /// <summary>Unexpected character in input.</summary>
    public const int UnexpectedCharacter = 1001;

    /// <summary>String literal not terminated before end of input.</summary>
    public const int UnterminatedString = 1002;

    /// <summary>IRI reference not terminated (missing '&gt;').</summary>
    public const int UnterminatedIri = 1003;

    /// <summary>Invalid escape sequence in string or IRI.</summary>
    public const int InvalidEscape = 1004;

    /// <summary>Unexpected end of input.</summary>
    public const int UnexpectedEndOfInput = 1005;

    /// <summary>Unexpected token encountered.</summary>
    public const int UnexpectedToken = 1010;

    /// <summary>Expected a specific token but found something else.</summary>
    public const int ExpectedToken = 1011;

    /// <summary>Expected an expression.</summary>
    public const int ExpectedExpression = 1012;

    /// <summary>Expected a pattern.</summary>
    public const int ExpectedPattern = 1013;

    /// <summary>Malformed numeric literal.</summary>
    public const int MalformedNumber = 1020;

    /// <summary>Malformed datetime literal.</summary>
    public const int MalformedDateTime = 1021;

    /// <summary>Malformed boolean literal.</summary>
    public const int MalformedBoolean = 1022;

    /// <summary>Invalid prefixed name (bad local part).</summary>
    public const int InvalidPrefixedName = 1030;

    /// <summary>Invalid blank node identifier.</summary>
    public const int InvalidBlankNode = 1031;

    /// <summary>Unclosed parenthesis or brace.</summary>
    public const int UnclosedDelimiter = 1040;

    /// <summary>Mismatched delimiter (e.g., opened with '{', closed with ')').</summary>
    public const int MismatchedDelimiter = 1041;

    // ========================================================================
    // E2xxx - Semantic Errors
    // ========================================================================

    /// <summary>Prefix used but not declared.</summary>
    public const int UndefinedPrefix = 2001;

    /// <summary>Variable in SELECT projection not bound in WHERE clause.</summary>
    public const int UnboundVariable = 2002;

    /// <summary>Aggregate function used in non-aggregate query.</summary>
    public const int AggregateInNonAggregate = 2003;

    /// <summary>Non-grouped variable used in aggregate query projection.</summary>
    public const int NonGroupedVariable = 2004;

    /// <summary>Invalid property path syntax.</summary>
    public const int InvalidPropertyPath = 2005;

    /// <summary>Duplicate variable binding.</summary>
    public const int DuplicateVariable = 2006;

    /// <summary>Unknown function name.</summary>
    public const int UnknownFunction = 2010;

    /// <summary>Wrong number of arguments to function.</summary>
    public const int WrongArgumentCount = 2011;

    /// <summary>Type mismatch in expression (e.g., adding string to number).</summary>
    public const int TypeMismatch = 2012;

    /// <summary>Invalid argument type for function.</summary>
    public const int InvalidArgumentType = 2013;

    /// <summary>GRAPH clause with invalid target.</summary>
    public const int InvalidGraphTarget = 2020;

    /// <summary>SERVICE clause with invalid endpoint.</summary>
    public const int InvalidServiceEndpoint = 2021;

    /// <summary>Subquery projects variable that conflicts with outer query.</summary>
    public const int SubqueryVariableConflict = 2030;

    /// <summary>Recursive subquery detected.</summary>
    public const int RecursiveSubquery = 2031;

    // ========================================================================
    // E3xxx - Runtime/Execution Errors
    // ========================================================================

    /// <summary>Query execution exceeded time limit.</summary>
    public const int QueryTimeout = 3001;

    /// <summary>Query execution exceeded memory limit.</summary>
    public const int MemoryLimitExceeded = 3002;

    /// <summary>SERVICE endpoint could not be reached.</summary>
    public const int ServiceUnreachable = 3003;

    /// <summary>SERVICE endpoint returned an error.</summary>
    public const int ServiceError = 3004;

    /// <summary>LOAD failed to fetch URL.</summary>
    public const int LoadFetchFailed = 3005;

    /// <summary>LOAD failed to parse fetched content.</summary>
    public const int LoadParseFailed = 3006;

    /// <summary>Division by zero in expression.</summary>
    public const int DivisionByZero = 3010;

    /// <summary>Invalid type cast.</summary>
    public const int InvalidCast = 3011;

    /// <summary>Regex pattern is invalid.</summary>
    public const int InvalidRegex = 3012;

    /// <summary>Result set too large.</summary>
    public const int ResultSetTooLarge = 3020;

    // ========================================================================
    // E4xxx - Storage Errors
    // ========================================================================

    /// <summary>Store directory not found.</summary>
    public const int StoreNotFound = 4001;

    /// <summary>Store is locked by another process.</summary>
    public const int StoreLocked = 4002;

    /// <summary>Write-ahead log is corrupted.</summary>
    public const int WalCorrupted = 4003;

    /// <summary>Checkpoint operation failed.</summary>
    public const int CheckpointFailed = 4004;

    /// <summary>Atom store is corrupted.</summary>
    public const int AtomStoreCorrupted = 4005;

    /// <summary>Index is corrupted.</summary>
    public const int IndexCorrupted = 4006;

    // ========================================================================
    // W1xxx - Warnings (base + 10000)
    // ========================================================================

    private const int WarningBase = 10000;

    /// <summary>Query produces Cartesian product (no shared variables between patterns).</summary>
    public const int CartesianProduct = WarningBase + 1001;

    /// <summary>Variable in FILTER is never bound (condition always false).</summary>
    public const int UnboundFilterVariable = WarningBase + 1002;

    /// <summary>DISTINCT is redundant (results already unique).</summary>
    public const int RedundantDistinct = WarningBase + 1003;

    /// <summary>Large OFFSET without LIMIT may be slow.</summary>
    public const int LargeOffsetWithoutLimit = WarningBase + 1004;

    /// <summary>Empty pattern group matches everything.</summary>
    public const int EmptyPatternGroup = WarningBase + 1005;

    /// <summary>Deprecated syntax used.</summary>
    public const int DeprecatedSyntax = WarningBase + 1010;

    /// <summary>SERVICE SILENT may hide errors.</summary>
    public const int ServiceSilent = WarningBase + 1020;

    /// <summary>Potential performance issue detected.</summary>
    public const int PerformanceWarning = WarningBase + 1030;

    // ========================================================================
    // I1xxx - Info/Hints (base + 20000)
    // ========================================================================

    private const int InfoBase = 20000;

    /// <summary>Consider adding an index for this pattern.</summary>
    public const int SuggestIndex = InfoBase + 1001;

    /// <summary>Query plan is available (use EXPLAIN).</summary>
    public const int QueryPlanAvailable = InfoBase + 1002;

    /// <summary>Consider rewriting pattern for better performance.</summary>
    public const int SuggestRewrite = InfoBase + 1003;

    /// <summary>Prefix was auto-registered.</summary>
    public const int PrefixAutoRegistered = InfoBase + 1010;

    // ========================================================================
    // Helper Methods
    // ========================================================================

    /// <summary>
    /// Gets the severity for a diagnostic code.
    /// </summary>
    public static DiagnosticSeverity GetSeverity(int code)
    {
        return code switch
        {
            >= InfoBase => DiagnosticSeverity.Hint,
            >= WarningBase => DiagnosticSeverity.Warning,
            _ => DiagnosticSeverity.Error
        };
    }

    /// <summary>
    /// Formats a code as a string (e.g., "E1001", "W1003", "I1001").
    /// </summary>
    public static string FormatCode(int code)
    {
        return code switch
        {
            >= InfoBase => $"I{code - InfoBase:D4}",
            >= WarningBase => $"W{code - WarningBase:D4}",
            _ => $"E{code:D4}"
        };
    }

    /// <summary>
    /// Returns true if the code represents an error (not warning or info).
    /// </summary>
    public static bool IsError(int code) => code < WarningBase;

    /// <summary>
    /// Returns true if the code represents a warning.
    /// </summary>
    public static bool IsWarning(int code) => code >= WarningBase && code < InfoBase;

    /// <summary>
    /// Returns true if the code represents info/hint.
    /// </summary>
    public static bool IsInfo(int code) => code >= InfoBase;
}
