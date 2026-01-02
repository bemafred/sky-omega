using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// SPARQL query planner that determines optimal pattern execution order
/// using predicate cardinality statistics.
/// </summary>
/// <remarks>
/// Internal constructor: AtomStore requires external synchronization via QuadStore's
/// read/write locks. QueryPlanner is created by QueryExecutor which holds appropriate locks.
/// </remarks>
public sealed class QueryPlanner
{
    private readonly StatisticsStore _statistics;
    private readonly AtomStore _atoms;

    // Default cardinality estimates when no statistics are available
    private const double DefaultFullScanCardinality = 10000.0;
    private const double DefaultSelectiveScanCardinality = 100.0;
    private const double DefaultPointLookupCardinality = 1.0;

    internal QueryPlanner(StatisticsStore statistics, AtomStore atoms)
    {
        _statistics = statistics ?? throw new ArgumentNullException(nameof(statistics));
        _atoms = atoms ?? throw new ArgumentNullException(nameof(atoms));
    }

    /// <summary>
    /// Estimate cardinality for a triple pattern given currently bound variables.
    /// </summary>
    /// <param name="pattern">The triple pattern to estimate.</param>
    /// <param name="source">The source SPARQL query text.</param>
    /// <param name="boundVariableHashes">Hashes of already-bound variables.</param>
    /// <returns>Estimated number of results.</returns>
    public double EstimateCardinality(
        in TriplePattern pattern,
        ReadOnlySpan<char> source,
        List<int> boundVariableHashes)
    {
        // If predicate has a property path, use heuristics
        if (pattern.HasPropertyPath)
        {
            return EstimatePropertyPathCardinality(pattern, source, boundVariableHashes);
        }

        // Check if predicate is a constant (not a variable)
        if (!pattern.Predicate.IsVariable)
        {
            var predicateSpan = source.Slice(pattern.Predicate.Start, pattern.Predicate.Length);
            var predicateAtom = _atoms.GetAtomId(predicateSpan);

            if (predicateAtom > 0)
            {
                var stats = _statistics.GetStats(predicateAtom);
                if (stats.HasValue)
                {
                    return EstimateWithStats(pattern, stats.Value, source, boundVariableHashes);
                }
            }
        }

        // No statistics available - use heuristics
        return EstimateWithHeuristics(pattern, source, boundVariableHashes);
    }

    /// <summary>
    /// Estimate cardinality using predicate statistics.
    /// </summary>
    private double EstimateWithStats(
        in TriplePattern pattern,
        PredicateStats stats,
        ReadOnlySpan<char> source,
        List<int> boundVariableHashes)
    {
        bool subjectBound = !pattern.Subject.IsVariable ||
            IsVariableBound(pattern.Subject, source, boundVariableHashes);
        bool objectBound = !pattern.Object.IsVariable ||
            IsVariableBound(pattern.Object, source, boundVariableHashes);

        if (subjectBound && objectBound)
        {
            // Point lookup - usually 0 or 1 result
            return DefaultPointLookupCardinality;
        }

        if (subjectBound)
        {
            // Subject bound: avg objects per subject
            return stats.EstimateWithBoundSubject();
        }

        if (objectBound)
        {
            // Object bound: avg subjects per object
            return stats.EstimateWithBoundObject();
        }

        // Neither bound: return total count for predicate
        return stats.TripleCount;
    }

    /// <summary>
    /// Estimate cardinality using heuristics when no statistics are available.
    /// </summary>
    private double EstimateWithHeuristics(
        in TriplePattern pattern,
        ReadOnlySpan<char> source,
        List<int> boundVariableHashes)
    {
        int boundCount = 0;

        // Subject bound?
        if (!pattern.Subject.IsVariable ||
            IsVariableBound(pattern.Subject, source, boundVariableHashes))
        {
            boundCount++;
        }

        // Predicate bound?
        if (!pattern.Predicate.IsVariable)
        {
            boundCount++;
        }

        // Object bound?
        if (!pattern.Object.IsVariable ||
            IsVariableBound(pattern.Object, source, boundVariableHashes))
        {
            boundCount++;
        }

        return boundCount switch
        {
            3 => DefaultPointLookupCardinality,           // Point lookup
            2 => DefaultSelectiveScanCardinality,         // Selective scan
            1 => DefaultFullScanCardinality / 10,         // Moderate scan
            _ => DefaultFullScanCardinality               // Full scan
        };
    }

    /// <summary>
    /// Estimate cardinality for property path patterns.
    /// </summary>
    private double EstimatePropertyPathCardinality(
        in TriplePattern pattern,
        ReadOnlySpan<char> source,
        List<int> boundVariableHashes)
    {
        bool subjectBound = !pattern.Subject.IsVariable ||
            IsVariableBound(pattern.Subject, source, boundVariableHashes);
        bool objectBound = !pattern.Object.IsVariable ||
            IsVariableBound(pattern.Object, source, boundVariableHashes);

        // Property paths can expand significantly
        var pathMultiplier = pattern.Path.Type switch
        {
            PathType.Inverse => 1.0,       // Same as forward
            PathType.ZeroOrOne => 2.0,     // At most 2x base
            PathType.ZeroOrMore => 100.0,  // Could be large
            PathType.OneOrMore => 50.0,    // Could be large
            PathType.Sequence => 10.0,     // Product of two
            PathType.Alternative => 2.0,   // Sum of two
            _ => 1.0
        };

        var baseCardinality = EstimateWithHeuristics(pattern, source, boundVariableHashes);
        return baseCardinality * pathMultiplier;
    }

    /// <summary>
    /// Reorder patterns by estimated cardinality (lowest first).
    /// Respects variable dependencies: a pattern using a variable must come after
    /// the pattern that binds it.
    /// </summary>
    /// <param name="pattern">The graph pattern containing triple patterns.</param>
    /// <param name="source">The source SPARQL query text.</param>
    /// <returns>Array of pattern indices in optimized order.</returns>
    public int[] OptimizePatternOrder(in GraphPattern pattern, ReadOnlySpan<char> source)
    {
        var patternCount = pattern.RequiredPatternCount;

        // Nothing to optimize for 0 or 1 patterns
        if (patternCount <= 1)
        {
            return patternCount == 0 ? Array.Empty<int>() : new[] { 0 };
        }

        var order = new int[patternCount];
        var used = new bool[patternCount];
        var boundVars = new List<int>();

        // Build mapping from pattern index (including optionals) to required-only index
        var requiredIndices = BuildRequiredPatternIndices(pattern);

        for (int i = 0; i < patternCount; i++)
        {
            // Find pattern with lowest cardinality that can execute given bound vars
            int bestIdx = -1;
            double bestCost = double.MaxValue;

            for (int j = 0; j < patternCount; j++)
            {
                if (used[j]) continue;

                var patternIdx = requiredIndices[j];
                var tp = pattern.GetPattern(patternIdx);

                if (!CanExecute(tp, source, boundVars))
                    continue;

                var cost = EstimateCardinality(tp, source, boundVars);

                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestIdx = j;
                }
            }

            // If no valid pattern found (shouldn't happen), take next unused
            if (bestIdx < 0)
            {
                for (int j = 0; j < patternCount; j++)
                {
                    if (!used[j])
                    {
                        bestIdx = j;
                        break;
                    }
                }
            }

            order[i] = requiredIndices[bestIdx];
            used[bestIdx] = true;

            // Add variables bound by this pattern to our bound set
            var selectedPattern = pattern.GetPattern(requiredIndices[bestIdx]);
            AddBoundVariables(selectedPattern, source, boundVars);
        }

        return order;
    }

    /// <summary>
    /// Build array of indices for required (non-optional) patterns only.
    /// </summary>
    private static int[] BuildRequiredPatternIndices(in GraphPattern pattern)
    {
        var count = pattern.RequiredPatternCount;
        var indices = new int[count];
        var limit = pattern.HasUnion ? pattern.FirstBranchPatternCount : pattern.PatternCount;

        int j = 0;
        for (int i = 0; i < limit && j < count; i++)
        {
            if (!pattern.IsOptional(i))
            {
                indices[j++] = i;
            }
        }

        return indices;
    }

    /// <summary>
    /// Check if a pattern can execute given currently bound variables.
    /// A pattern can execute if at least one of S/P/O is either:
    /// 1. A constant (not a variable), or
    /// 2. A variable that's already bound
    /// </summary>
    private bool CanExecute(
        in TriplePattern pattern,
        ReadOnlySpan<char> source,
        List<int> boundVars)
    {
        // If subject is constant or bound variable
        if (!pattern.Subject.IsVariable ||
            IsVariableBound(pattern.Subject, source, boundVars))
        {
            return true;
        }

        // If predicate is constant (for simple predicates, not paths)
        if (!pattern.HasPropertyPath && !pattern.Predicate.IsVariable)
        {
            return true;
        }

        // If predicate path has an IRI
        if (pattern.HasPropertyPath && pattern.Path.Iri.Start > 0)
        {
            return true;
        }

        // If object is constant or bound variable
        if (!pattern.Object.IsVariable ||
            IsVariableBound(pattern.Object, source, boundVars))
        {
            return true;
        }

        // All positions are unbound variables - this would be a full scan
        // Allow it (we have to start somewhere), but it will have high cost
        return true;
    }

    /// <summary>
    /// Add variables bound by a pattern to the bound variable set.
    /// </summary>
    private void AddBoundVariables(
        in TriplePattern pattern,
        ReadOnlySpan<char> source,
        List<int> boundVars)
    {
        if (pattern.Subject.IsVariable)
        {
            var hash = ComputeVariableHash(pattern.Subject, source);
            if (!boundVars.Contains(hash))
                boundVars.Add(hash);
        }

        if (!pattern.HasPropertyPath && pattern.Predicate.IsVariable)
        {
            var hash = ComputeVariableHash(pattern.Predicate, source);
            if (!boundVars.Contains(hash))
                boundVars.Add(hash);
        }

        if (pattern.Object.IsVariable)
        {
            var hash = ComputeVariableHash(pattern.Object, source);
            if (!boundVars.Contains(hash))
                boundVars.Add(hash);
        }
    }

    /// <summary>
    /// Check if a variable term is in the bound variables set.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsVariableBound(
        in Term term,
        ReadOnlySpan<char> source,
        List<int> boundVars)
    {
        if (!term.IsVariable)
            return false;

        var hash = ComputeVariableHash(term, source);
        return boundVars.Contains(hash);
    }

    /// <summary>
    /// Compute FNV-1a hash for a variable name.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeVariableHash(in Term term, ReadOnlySpan<char> source)
    {
        var name = source.Slice(term.Start, term.Length);
        unchecked
        {
            int hash = (int)2166136261;
            foreach (var c in name)
            {
                hash = (hash ^ c) * 16777619;
            }
            return hash;
        }
    }

    // Default cardinality estimate for SERVICE clauses (remote endpoints)
    // Conservative estimate: SERVICE calls are expensive, assume moderate result size
    private const double DefaultServiceCardinality = 500.0;

    // Network call overhead - penalize strategies that make multiple SERVICE calls
    private const double NetworkCallPenalty = 10.0;

    /// <summary>
    /// Determine the optimal join strategy for SERVICE + local pattern execution.
    /// </summary>
    /// <param name="localPattern">The graph pattern containing local triple patterns.</param>
    /// <param name="serviceClause">The SERVICE clause to join with.</param>
    /// <param name="source">The source SPARQL query text.</param>
    /// <returns>
    /// True for LocalFirst strategy (execute local patterns first, then SERVICE for each result).
    /// False for ServiceFirst strategy (execute SERVICE first, then local patterns for each result).
    /// </returns>
    /// <remarks>
    /// Strategy selection considers:
    /// - Estimated cardinality of local patterns vs SERVICE
    /// - Network overhead (SERVICE calls are expensive)
    /// - Join variable selectivity
    ///
    /// LocalFirst is preferred when:
    /// - Local patterns are highly selective (few results)
    /// - The estimated local cardinality * network penalty < SERVICE cardinality
    ///
    /// ServiceFirst is preferred when:
    /// - SERVICE is highly selective (constrained by bound variables)
    /// - Local patterns would produce many results (full scans)
    /// </remarks>
    public bool ShouldUseLocalFirstStrategy(
        in GraphPattern localPattern,
        in ServiceClause serviceClause,
        ReadOnlySpan<char> source)
    {
        // Estimate local pattern cardinality
        var localCardinality = EstimateLocalPatternCardinality(localPattern, source);

        // Estimate SERVICE cardinality (heuristic since we can't query remote statistics)
        var serviceCardinality = EstimateServiceCardinality(serviceClause, source);

        // LocalFirst means N SERVICE calls where N = local results
        // ServiceFirst means N local scans where N = SERVICE results
        // Factor in network penalty for SERVICE calls

        var localFirstCost = localCardinality * NetworkCallPenalty + localCardinality * serviceCardinality;
        var serviceFirstCost = serviceCardinality + serviceCardinality * localCardinality;

        // Prefer LocalFirst when local patterns are very selective
        // This avoids fetching potentially large SERVICE results when only a few will match
        if (localCardinality <= 10)
            return true;

        // Prefer ServiceFirst when SERVICE has bound variables (likely selective)
        if (HasBoundVariablesInService(serviceClause, source))
            return false;

        // Default to cost-based decision
        return localFirstCost < serviceFirstCost;
    }

    /// <summary>
    /// Estimate total cardinality for all local patterns in a graph pattern.
    /// Uses product of individual pattern cardinalities with join selectivity.
    /// </summary>
    private double EstimateLocalPatternCardinality(in GraphPattern pattern, ReadOnlySpan<char> source)
    {
        var patternCount = pattern.RequiredPatternCount;
        if (patternCount == 0)
            return 1.0; // No local patterns

        var boundVars = new List<int>();
        double totalCardinality = 1.0;

        // Estimate cardinality considering join selectivity
        for (int i = 0; i < patternCount; i++)
        {
            var tp = pattern.GetPattern(i);
            var patternCard = EstimateCardinality(tp, source, boundVars);

            // Apply join selectivity factor for subsequent patterns
            if (i > 0)
            {
                // Shared variables reduce cardinality significantly
                var sharedVars = CountSharedVariables(tp, source, boundVars);
                var selectivity = sharedVars > 0 ? 0.1 / sharedVars : 1.0;
                patternCard *= selectivity;
            }

            totalCardinality *= patternCard;
            AddBoundVariables(tp, source, boundVars);
        }

        return Math.Max(1.0, totalCardinality);
    }

    /// <summary>
    /// Estimate SERVICE cardinality using heuristics.
    /// </summary>
    private double EstimateServiceCardinality(in ServiceClause serviceClause, ReadOnlySpan<char> source)
    {
        // We can't query remote statistics, so use heuristics based on pattern structure
        var patternCount = serviceClause.PatternCount;

        if (patternCount == 0)
            return 1.0;

        // Count bound positions across all SERVICE patterns
        int totalBoundPositions = 0;
        for (int i = 0; i < patternCount; i++)
        {
            var tp = serviceClause.GetPattern(i);
            if (!tp.Subject.IsVariable) totalBoundPositions++;
            if (!tp.Predicate.IsVariable) totalBoundPositions++;
            if (!tp.Object.IsVariable) totalBoundPositions++;
        }

        // More bound positions = more selective
        var avgBoundPerPattern = (double)totalBoundPositions / patternCount;

        return avgBoundPerPattern switch
        {
            >= 2.5 => DefaultServiceCardinality / 100,  // Very selective
            >= 2.0 => DefaultServiceCardinality / 10,   // Selective
            >= 1.5 => DefaultServiceCardinality / 2,    // Moderate
            >= 1.0 => DefaultServiceCardinality,        // Low selectivity
            _ => DefaultServiceCardinality * 10         // Nearly full scan
        };
    }

    /// <summary>
    /// Check if SERVICE clause patterns have variables that would be bound by local patterns.
    /// </summary>
    private static bool HasBoundVariablesInService(in ServiceClause serviceClause, ReadOnlySpan<char> source)
    {
        // This is a heuristic - we don't know at planning time which variables
        // will be bound. Return true if SERVICE uses variables that typically
        // appear in local patterns (like ?s subject variables).
        // For now, return false to prefer cardinality-based decision.
        return false;
    }

    /// <summary>
    /// Count how many variables in a pattern are already bound.
    /// </summary>
    private static int CountSharedVariables(in TriplePattern pattern, ReadOnlySpan<char> source, List<int> boundVars)
    {
        int count = 0;
        if (pattern.Subject.IsVariable && IsVariableBound(pattern.Subject, source, boundVars))
            count++;
        if (!pattern.HasPropertyPath && pattern.Predicate.IsVariable && IsVariableBound(pattern.Predicate, source, boundVars))
            count++;
        if (pattern.Object.IsVariable && IsVariableBound(pattern.Object, source, boundVars))
            count++;
        return count;
    }
}
