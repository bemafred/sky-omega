// ToolResult.cs
// Shared result type for CLI tool operations

namespace SkyOmega.Mercury.Turtle.Tool;

/// <summary>
/// Result of a tool operation.
/// </summary>
public readonly struct ToolResult
{
    /// <summary>
    /// Exit code (0 for success, non-zero for failure).
    /// </summary>
    public int ExitCode { get; }

    /// <summary>
    /// Error message if the operation failed.
    /// </summary>
    public string? ErrorMessage { get; }

    /// <summary>
    /// Whether the operation succeeded.
    /// </summary>
    public bool Success => ExitCode == 0;

    private ToolResult(int exitCode, string? errorMessage = null)
    {
        ExitCode = exitCode;
        ErrorMessage = errorMessage;
    }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static ToolResult Ok() => new(0);

    /// <summary>
    /// Creates a failure result with the specified exit code and error message.
    /// </summary>
    public static ToolResult Fail(string errorMessage, int exitCode = 1) => new(exitCode, errorMessage);

    /// <summary>
    /// Creates a failure result from an exception.
    /// </summary>
    public static ToolResult FromException(Exception ex, int exitCode = 1) => new(exitCode, ex.Message);
}
