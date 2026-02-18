using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Execution.Operators;

namespace SkyOmega.Mercury.Sparql.Execution.Expressions;

/// <summary>
/// Analyzes FILTER expressions to determine which variables they reference.
/// Used for predicate pushdown optimization - pushing filter evaluation
/// earlier in query execution.
/// </summary>
/// <remarks>
/// <para><strong>INTERNAL USE ONLY:</strong> This class is internal because it is an
/// implementation detail of predicate pushdown optimization. Filter analysis is
/// transparent to users - it happens automatically during query execution.</para>
/// </remarks>
internal static class FilterAnalyzer
{
    /// <summary>
    /// Maximum number of variables that can be tracked per filter.
    /// </summary>
    public const int MaxVariablesPerFilter = 8;

    /// <summary>
    /// Extract variable hashes referenced in a filter expression.
    /// Variables are identified by ?name patterns in the expression.
    /// </summary>
    /// <param name="filter">The filter expression (offset into source).</param>
    /// <param name="source">The source SPARQL query text.</param>
    /// <param name="variableHashes">Output list of unique variable name hashes.</param>
    public static void GetFilterVariables(
        FilterExpr filter,
        ReadOnlySpan<char> source,
        List<int> variableHashes)
    {
        if (filter.Length <= 0)
            return;

        var expr = source.Slice(filter.Start, filter.Length);
        ExtractVariables(expr, variableHashes);
    }

    /// <summary>
    /// Check if a filter contains EXISTS or NOT EXISTS subpatterns.
    /// Such filters cannot be safely pushed down because they require
    /// the full pattern match context.
    /// </summary>
    /// <param name="filter">The filter expression.</param>
    /// <param name="source">The source SPARQL query text.</param>
    /// <returns>True if the filter contains EXISTS patterns.</returns>
    public static bool ContainsExists(FilterExpr filter, ReadOnlySpan<char> source)
    {
        if (filter.Length <= 0)
            return false;

        var expr = source.Slice(filter.Start, filter.Length);

        // Scan for EXISTS keyword (case-insensitive)
        for (int i = 0; i <= expr.Length - 6; i++)
        {
            if (MatchKeyword(expr.Slice(i), "EXISTS"))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Determine the earliest pattern index where a filter can be applied.
    /// A filter can be applied after a pattern if all its required variables
    /// are bound by that pattern or earlier patterns.
    /// </summary>
    /// <param name="filter">The filter expression.</param>
    /// <param name="source">The source SPARQL query text.</param>
    /// <param name="patterns">The triple patterns in execution order.</param>
    /// <param name="patternOrder">Optional pattern order mapping.</param>
    /// <returns>
    /// The pattern index (0-based) after which the filter can be applied,
    /// or -1 if the filter cannot be pushed (e.g., uses no variables or contains EXISTS).
    /// </returns>
    public static int GetEarliestApplicablePattern(
        FilterExpr filter,
        ReadOnlySpan<char> source,
        in GraphPattern patterns,
        int[]? patternOrder = null)
    {
        // Don't push filters containing EXISTS
        if (ContainsExists(filter, source))
            return -1;

        // Extract variables from filter
        var filterVars = new List<int>(MaxVariablesPerFilter);
        GetFilterVariables(filter, source, filterVars);

        // No variables = constant filter, can't push (or push to level 0?)
        if (filterVars.Count == 0)
            return -1;

        // Track which patterns bind which variables
        var boundVars = new List<int>();
        int patternCount = patterns.RequiredPatternCount;

        for (int level = 0; level < patternCount; level++)
        {
            int patternIdx = patternOrder != null && level < patternOrder.Length
                ? patternOrder[level]
                : level;

            var pattern = patterns.GetPattern(patternIdx);

            // Add variables bound by this pattern
            AddPatternVariables(pattern, source, boundVars);

            // Check if all filter variables are now bound
            if (AllVariablesBound(filterVars, boundVars))
                return level;
        }

        // Variables not bound by any pattern - don't push
        return -1;
    }

    /// <summary>
    /// Analyze all filters in a graph pattern and determine pushdown assignments.
    /// </summary>
    /// <param name="pattern">The graph pattern containing filters.</param>
    /// <param name="source">The source SPARQL query text.</param>
    /// <param name="patternOrder">Optional pattern order mapping.</param>
    /// <returns>Array of filter assignments, indexed by filter index.</returns>
    public static FilterAssignment[] AnalyzeFilters(
        in GraphPattern pattern,
        ReadOnlySpan<char> source,
        int[]? patternOrder = null)
    {
        int filterCount = pattern.FilterCount;
        if (filterCount == 0)
            return Array.Empty<FilterAssignment>();

        var assignments = new FilterAssignment[filterCount];

        for (int i = 0; i < filterCount; i++)
        {
            var filter = pattern.GetFilter(i);
            var level = GetEarliestApplicablePattern(filter, source, pattern, patternOrder);

            assignments[i] = new FilterAssignment
            {
                FilterIndex = i,
                PatternLevel = level,
                CanPush = level >= 0
            };
        }

        return assignments;
    }

    /// <summary>
    /// Build per-level filter lists for MultiPatternScan integration.
    /// </summary>
    /// <param name="pattern">The graph pattern containing filters.</param>
    /// <param name="source">The source SPARQL query text.</param>
    /// <param name="patternCount">Number of patterns.</param>
    /// <param name="patternOrder">Optional pattern order mapping.</param>
    /// <returns>Array where index is pattern level, value is list of filter indices to apply at that level.</returns>
    public static List<int>[] BuildLevelFilters(
        in GraphPattern pattern,
        ReadOnlySpan<char> source,
        int patternCount,
        int[]? patternOrder = null)
    {
        var levelFilters = new List<int>[patternCount];
        for (int i = 0; i < patternCount; i++)
            levelFilters[i] = new List<int>();

        var assignments = AnalyzeFilters(pattern, source, patternOrder);

        foreach (var assignment in assignments)
        {
            if (assignment.CanPush && assignment.PatternLevel >= 0 && assignment.PatternLevel < patternCount)
            {
                levelFilters[assignment.PatternLevel].Add(assignment.FilterIndex);
            }
        }

        return levelFilters;
    }

    /// <summary>
    /// Get indices of filters that cannot be pushed and must be evaluated at the end.
    /// </summary>
    public static List<int> GetUnpushableFilters(in GraphPattern pattern, ReadOnlySpan<char> source, int[]? patternOrder = null)
    {
        var unpushable = new List<int>();
        var assignments = AnalyzeFilters(pattern, source, patternOrder);

        foreach (var assignment in assignments)
        {
            if (!assignment.CanPush)
                unpushable.Add(assignment.FilterIndex);
        }

        return unpushable;
    }

    #region Private Helpers

    /// <summary>
    /// Extract variable name hashes from an expression.
    /// </summary>
    private static void ExtractVariables(ReadOnlySpan<char> expr, List<int> variableHashes)
    {
        int pos = 0;
        while (pos < expr.Length)
        {
            if (expr[pos] == '?')
            {
                // Found variable start
                int start = pos;
                pos++; // Skip '?'

                // Read variable name
                while (pos < expr.Length && IsVarChar(expr[pos]))
                    pos++;

                if (pos > start + 1) // Has at least one char after '?'
                {
                    var varSpan = expr.Slice(start, pos - start);
                    var hash = ComputeHash(varSpan);

                    // Add if not already present
                    if (!variableHashes.Contains(hash))
                        variableHashes.Add(hash);
                }
            }
            else if (expr[pos] == '"')
            {
                // Skip string literal to avoid ?var inside strings
                pos++;
                while (pos < expr.Length && expr[pos] != '"')
                {
                    if (expr[pos] == '\\' && pos + 1 < expr.Length)
                        pos++; // Skip escape
                    pos++;
                }
                if (pos < expr.Length)
                    pos++; // Skip closing quote
            }
            else
            {
                pos++;
            }
        }
    }

    /// <summary>
    /// Add variables bound by a triple pattern to the list.
    /// </summary>
    private static void AddPatternVariables(in TriplePattern pattern, ReadOnlySpan<char> source, List<int> boundVars)
    {
        if (pattern.Subject.IsVariable)
        {
            var hash = ComputeHash(source.Slice(pattern.Subject.Start, pattern.Subject.Length));
            if (!boundVars.Contains(hash))
                boundVars.Add(hash);
        }

        if (!pattern.HasPropertyPath && pattern.Predicate.IsVariable)
        {
            var hash = ComputeHash(source.Slice(pattern.Predicate.Start, pattern.Predicate.Length));
            if (!boundVars.Contains(hash))
                boundVars.Add(hash);
        }

        if (pattern.Object.IsVariable)
        {
            var hash = ComputeHash(source.Slice(pattern.Object.Start, pattern.Object.Length));
            if (!boundVars.Contains(hash))
                boundVars.Add(hash);
        }
    }

    /// <summary>
    /// Check if all required variables are in the bound set.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AllVariablesBound(List<int> required, List<int> bound)
    {
        foreach (var hash in required)
        {
            if (!bound.Contains(hash))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Match a keyword at the start of a span (case-insensitive).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MatchKeyword(ReadOnlySpan<char> span, string keyword)
    {
        if (span.Length < keyword.Length)
            return false;

        var slice = span.Slice(0, keyword.Length);
        if (!slice.Equals(keyword.AsSpan(), StringComparison.OrdinalIgnoreCase))
            return false;

        // Must not be followed by letter/digit
        if (span.Length > keyword.Length)
        {
            var next = span[keyword.Length];
            if (IsVarChar(next))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Check if character is valid in a variable name.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsVarChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
        (c >= '0' && c <= '9') || c == '_';

    /// <summary>
    /// Compute FNV-1a hash for a variable name span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeHash(ReadOnlySpan<char> value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var ch in value)
            {
                hash ^= ch;
                hash *= 16777619;
            }
            return (int)hash;
        }
    }

    #endregion
}

/// <summary>
/// Describes where a filter should be applied in the pattern execution.
/// </summary>
internal struct FilterAssignment
{
    /// <summary>Index of the filter in the graph pattern.</summary>
    public int FilterIndex;

    /// <summary>
    /// Pattern level (0-based) after which this filter should be applied.
    /// -1 means filter cannot be pushed.
    /// </summary>
    public int PatternLevel;

    /// <summary>True if this filter can be pushed down.</summary>
    public bool CanPush;
}
