using System.Runtime.CompilerServices;
using System.Threading;

namespace SkyOmega.Mercury.Sparql.Execution.Operators;

/// <summary>
/// Thread-static cancellation token for query execution.
/// Allows operators to check for cancellation without passing token through all constructors.
/// </summary>
internal static class QueryCancellation
{
    [ThreadStatic]
    private static CancellationToken _token;

    /// <summary>
    /// Set the cancellation token for the current thread's query execution.
    /// </summary>
    public static void SetToken(CancellationToken token) => _token = token;

    /// <summary>
    /// Clear the cancellation token after query execution completes.
    /// </summary>
    public static void ClearToken() => _token = default;

    /// <summary>
    /// The current thread's query token, for a call that waits outside the query: a SERVICE call (ADR-066 D4).
    /// </summary>
    public static CancellationToken Token => _token;

    /// <summary>
    /// Throw OperationCanceledException if cancellation has been requested.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ThrowIfCancellationRequested() => _token.ThrowIfCancellationRequested();
}
