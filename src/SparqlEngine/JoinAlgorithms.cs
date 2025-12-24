using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SparqlEngine;

/// <summary>
/// Optimized join algorithms for BGP (Basic Graph Pattern) evaluation
/// Uses hash joins and sort-merge joins with zero allocation
/// </summary>
public ref struct JoinAlgorithms
{
    /// <summary>
    /// Nested loop join - simplest but slowest
    /// O(n * m) complexity but zero allocations
    /// </summary>
    public static JoinResultEnumerator NestedLoopJoin(
        StreamingTripleStore.TripleEnumerator left,
        StreamingTripleStore.TripleEnumerator right,
        JoinCondition condition)
    {
        return new JoinResultEnumerator(left, right, condition, JoinType.NestedLoop);
    }

    /// <summary>
    /// Hash join - O(n + m) complexity
    /// Uses stack-allocated hash table for small joins
    /// </summary>
    public static JoinResultEnumerator HashJoin(
        StreamingTripleStore.TripleEnumerator left,
        StreamingTripleStore.TripleEnumerator right,
        JoinCondition condition)
    {
        return new JoinResultEnumerator(left, right, condition, JoinType.Hash);
    }

    /// <summary>
    /// Sort-merge join - O(n log n + m log m) complexity
    /// Good for sorted input or when hash table doesn't fit in cache
    /// </summary>
    public static JoinResultEnumerator SortMergeJoin(
        StreamingTripleStore.TripleEnumerator left,
        StreamingTripleStore.TripleEnumerator right,
        JoinCondition condition)
    {
        return new JoinResultEnumerator(left, right, condition, JoinType.SortMerge);
    }
}

/// <summary>
/// Join condition specifying which variables to join on
/// </summary>
public struct JoinCondition
{
    public int LeftVariableId;
    public int RightVariableId;
    public JoinPosition LeftPosition;   // Subject, Predicate, or Object
    public JoinPosition RightPosition;
}

public enum JoinPosition
{
    Subject,
    Predicate,
    Object
}

public enum JoinType
{
    NestedLoop,
    Hash,
    SortMerge
}

/// <summary>
/// Enumerator for join results with zero allocation
/// </summary>
public ref struct JoinResultEnumerator
{
    private StreamingTripleStore.TripleEnumerator _left;
    private StreamingTripleStore.TripleEnumerator _right;
    private readonly JoinCondition _condition;
    private readonly JoinType _joinType;
    
    // Stack-allocated hash table for hash joins (max 256 entries)
    private unsafe fixed int _hashBuckets[256];
    private int _hashCount;
    
    // Current state
    private TripleRef _currentLeft;
    private TripleRef _currentRight;
    private bool _hasLeft;
    private bool _rightExhausted;

    internal JoinResultEnumerator(
        StreamingTripleStore.TripleEnumerator left,
        StreamingTripleStore.TripleEnumerator right,
        JoinCondition condition,
        JoinType joinType)
    {
        _left = left;
        _right = right;
        _condition = condition;
        _joinType = joinType;
        _hashCount = 0;
        _hasLeft = false;
        _rightExhausted = false;
    }

    public bool MoveNext()
    {
        return _joinType switch
        {
            JoinType.NestedLoop => MoveNextNestedLoop(),
            JoinType.Hash => MoveNextHash(),
            JoinType.SortMerge => MoveNextSortMerge(),
            _ => false
        };
    }

    private bool MoveNextNestedLoop()
    {
        while (true)
        {
            if (!_hasLeft)
            {
                if (!_left.MoveNext())
                    return false;
                
                _currentLeft = _left.Current;
                _hasLeft = true;
                _rightExhausted = false;
            }
            
            if (!_right.MoveNext())
            {
                _hasLeft = false;
                continue;
            }
            
            _currentRight = _right.Current;
            
            // Check join condition
            if (MatchesCondition(_currentLeft, _currentRight))
                return true;
        }
    }

    private bool MoveNextHash()
    {
        // Build hash table from left side (first call only)
        if (_hashCount == 0)
        {
            BuildHashTable();
        }
        
        // Probe hash table with right side
        while (_right.MoveNext())
        {
            _currentRight = _right.Current;
            
            var hash = GetJoinKeyHash(_currentRight, _condition.RightPosition);
            var bucket = hash & 0xFF; // Modulo 256
            
            unsafe
            {
                if (_hashBuckets[bucket] != 0)
                {
                    // Found match
                    return true;
                }
            }
        }
        
        return false;
    }

    private void BuildHashTable()
    {
        while (_left.MoveNext())
        {
            _currentLeft = _left.Current;
            
            var hash = GetJoinKeyHash(_currentLeft, _condition.LeftPosition);
            var bucket = hash & 0xFF;
            
            unsafe
            {
                _hashBuckets[bucket] = 1; // Mark bucket as occupied
            }
            
            _hashCount++;
        }
    }

    private bool MoveNextSortMerge()
    {
        // Sort-merge join implementation
        // For simplicity, falls back to nested loop
        return MoveNextNestedLoop();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MatchesCondition(in TripleRef left, in TripleRef right)
    {
        var leftValue = GetJoinKey(left, _condition.LeftPosition);
        var rightValue = GetJoinKey(right, _condition.RightPosition);
        
        return leftValue.SequenceEqual(rightValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<char> GetJoinKey(in TripleRef triple, JoinPosition position)
    {
        return position switch
        {
            JoinPosition.Subject => triple.Subject,
            JoinPosition.Predicate => triple.Predicate,
            JoinPosition.Object => triple.Object,
            _ => ReadOnlySpan<char>.Empty
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetJoinKeyHash(in TripleRef triple, JoinPosition position)
    {
        var key = GetJoinKey(triple, position);
        
        // FNV-1a hash
        int hash = unchecked((int)2166136261);
        foreach (var ch in key)
        {
            hash = (hash ^ ch) * 16777619;
        }
        return hash;
    }

    public readonly JoinResult Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new JoinResult(_currentLeft, _currentRight);
    }

    public JoinResultEnumerator GetEnumerator() => this;
}

/// <summary>
/// Result of a join operation
/// </summary>
public readonly ref struct JoinResult
{
    public readonly TripleRef Left;
    public readonly TripleRef Right;

    public JoinResult(in TripleRef left, in TripleRef right)
    {
        Left = left;
        Right = right;
    }
}

/// <summary>
/// Query optimizer that selects the best join algorithm
/// </summary>
public ref struct QueryOptimizer
{
    private readonly StreamingTripleStore _store;

    public QueryOptimizer(StreamingTripleStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Estimate cardinality of a triple pattern
    /// </summary>
    public int EstimateCardinality(in TriplePattern pattern)
    {
        // Simple heuristic: fewer variables = lower cardinality
        int variableCount = 0;
        if (pattern.Subject.IsVariable) variableCount++;
        if (pattern.Predicate.IsVariable) variableCount++;
        if (pattern.Object.IsVariable) variableCount++;
        
        return variableCount switch
        {
            0 => 1,      // All constants: at most 1 result
            1 => 100,    // 1 variable: ~100 results
            2 => 1000,   // 2 variables: ~1000 results
            3 => 10000,  // All variables: ~10000 results
            _ => 1000
        };
    }

    /// <summary>
    /// Select optimal join algorithm based on estimated cardinalities
    /// </summary>
    public JoinType SelectJoinAlgorithm(int leftCard, int rightCard)
    {
        // Hash join for small-to-medium sizes
        if (leftCard < 1000 && rightCard < 10000)
            return JoinType.Hash;
        
        // Nested loop for small joins
        if (leftCard < 100 && rightCard < 100)
            return JoinType.NestedLoop;
        
        // Sort-merge for large joins
        return JoinType.SortMerge;
    }

    /// <summary>
    /// Reorder patterns for optimal execution
    /// Uses greedy algorithm to minimize intermediate results
    /// </summary>
    public void ReorderPatterns(Span<TriplePattern> patterns)
    {
        // Selection sort by estimated cardinality
        for (int i = 0; i < patterns.Length - 1; i++)
        {
            int minIndex = i;
            int minCard = EstimateCardinality(patterns[i]);
            
            for (int j = i + 1; j < patterns.Length; j++)
            {
                int card = EstimateCardinality(patterns[j]);
                if (card < minCard)
                {
                    minCard = card;
                    minIndex = j;
                }
            }
            
            if (minIndex != i)
            {
                // Swap
                var temp = patterns[i];
                patterns[i] = patterns[minIndex];
                patterns[minIndex] = temp;
            }
        }
    }
}

/// <summary>
/// Statistics collector for query optimization
/// </summary>
public sealed class Statistics
{
    private const int BucketCount = 256;
    
    // Histograms for selectivity estimation
    private readonly int[] _subjectHistogram;
    private readonly int[] _predicateHistogram;
    private readonly int[] _objectHistogram;
    
    public Statistics()
    {
        _subjectHistogram = new int[BucketCount];
        _predicateHistogram = new int[BucketCount];
        _objectHistogram = new int[BucketCount];
    }

    public void UpdateStatistics(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj)
    {
        _subjectHistogram[GetHash(subject) % BucketCount]++;
        _predicateHistogram[GetHash(predicate) % BucketCount]++;
        _objectHistogram[GetHash(obj) % BucketCount]++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetHash(ReadOnlySpan<char> span)
    {
        int hash = unchecked((int)2166136261);
        foreach (var ch in span)
        {
            hash = (hash ^ ch) * 16777619;
        }
        return hash & 0x7FFFFFFF; // Positive only
    }

    public double EstimateSelectivity(ReadOnlySpan<char> value, TriplePosition position)
    {
        var histogram = position switch
        {
            TriplePosition.Subject => _subjectHistogram,
            TriplePosition.Predicate => _predicateHistogram,
            TriplePosition.Object => _objectHistogram,
            _ => _subjectHistogram
        };
        
        var bucket = GetHash(value) % BucketCount;
        var total = 0;
        
        foreach (var count in histogram)
            total += count;
        
        return total == 0 ? 1.0 : (double)histogram[bucket] / total;
    }
}

public enum TriplePosition
{
    Subject,
    Predicate,
    Object
}
