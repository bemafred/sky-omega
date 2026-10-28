using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// SPARQL query planner that determines optimal pattern execution order
/// using predicate cardinality statistics.
/// </summary>
/// <remarks>
/// Internal constructor: IAtomStore requires external synchronization via QuadStore's
/// read/write locks. QueryPlanner is created by QueryExecutor; the caller of
/// QueryExecutor must hold appropriate locks (see ADR-020, ADR-021).
/// </remarks>
internal sealed class QueryPlanner
{
    private readonly StatisticsStore _statistics;
    private readonly IAtomStore _atoms;

    // Default cardinality estimates when no statistics are available
    private const double DefaultFullScanCardinality = 10000.0;
    private const double DefaultSelectiveScanCardinality = 100.0;
    private const double DefaultPointLookupCardinality = 1.0;

    internal QueryPlanner(StatisticsStore statistics, IAtomStore atoms)
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
            // A synthetic predicate (rdf:first/rest/nil from RDF-collection expansion, etc.) carries a negative
            // marker offset, not a source position — resolve it to its IRI so the stats lookup still works.
            var predicateSpan = Operators.SyntheticTerms.IsSynthetic(pattern.Predicate.Start)
                ? Operators.SyntheticTerms.GetSyntheticIri(pattern.Predicate.Start)
                : source.Slice(pattern.Predicate.Start, pattern.Predicate.Length);
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
    /// <param name="patterns">The triple patterns of one basic graph pattern.</param>
    /// <param name="source">The source SPARQL query text.</param>
    /// <returns>Array of pattern indices in optimized order.</returns>
    public int[] OptimizePatternOrder(ReadOnlySpan<TriplePattern> patterns, ReadOnlySpan<char> source)
    {
        var patternCount = patterns.Length;

        // Nothing to optimize for 0 or 1 patterns
        if (patternCount <= 1)
        {
            return patternCount == 0 ? Array.Empty<int>() : new[] { 0 };
        }

        var order = new int[patternCount];
        var used = new bool[patternCount];
        var boundVars = new List<int>();

        for (int i = 0; i < patternCount; i++)
        {
            // Find pattern with lowest cardinality that can execute given bound vars
            int bestIdx = -1;
            double bestCost = double.MaxValue;

            for (int j = 0; j < patternCount; j++)
            {
                if (used[j]) continue;

                var tp = patterns[j];

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

            order[i] = bestIdx;
            used[bestIdx] = true;

            // Add variables bound by this pattern to our bound set
            AddBoundVariables(patterns[bestIdx], source, boundVars);
        }

        return order;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeVariableHash(in Term term, ReadOnlySpan<char> source)
    {
        // Synthetic variables produced by SequencePath expansion (and similar internal
        // rewrites) carry negative Term.Start values as a marker — there is no source-text
        // slice to hash. Use the negative Start as a stable hash; collisions across
        // different synthetic variable kinds are avoided by the +200 / +400 offsets the
        // parser applies per kind.
        if (term.Start < 0)
            return term.Start;
        return VariableName.Hash(source.Slice(term.Start, term.Length));
    }
}
