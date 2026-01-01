using System.Runtime.CompilerServices;

namespace SkyOmega.Mercury.Pruning.Filters;

/// <summary>
/// Filter that accepts all quads. Singleton pattern for zero allocation.
/// </summary>
public sealed class AllPassFilter : IPruningFilter
{
    /// <summary>
    /// Singleton instance.
    /// </summary>
    public static readonly AllPassFilter Instance = new();

    private AllPassFilter() { }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ShouldInclude(
        ReadOnlySpan<char> graph,
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj,
        DateTimeOffset validFrom,
        DateTimeOffset validTo) => true;
}
