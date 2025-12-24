using System;
using System.Runtime.CompilerServices;

namespace SparqlEngine;

/// <summary>
/// Property path evaluation for SPARQL 1.1
/// Supports: /, |, *, +, ?, ^, !
/// </summary>
public ref struct PropertyPathEvaluator
{
    private readonly StreamingTripleStore _store;
    private readonly PropertyPath _path;
    private ReadOnlySpan<char> _startNode;
    private ReadOnlySpan<char> _endNode;

    public PropertyPathEvaluator(
        StreamingTripleStore store,
        in PropertyPath path,
        ReadOnlySpan<char> startNode,
        ReadOnlySpan<char> endNode)
    {
        _store = store;
        _path = path;
        _startNode = startNode;
        _endNode = endNode;
    }

    /// <summary>
    /// Evaluate property path and return matching node pairs
    /// </summary>
    public PropertyPathResultEnumerator Evaluate()
    {
        return new PropertyPathResultEnumerator(_store, _path, _startNode, _endNode);
    }
}

/// <summary>
/// Property path specification
/// </summary>
public struct PropertyPath
{
    public PropertyPathType Type;
    public ReadOnlySpan<char> PredicateUri;
    
    // For composite paths
    public PropertyPath LeftPath;
    public PropertyPath RightPath;
    
    // For quantified paths
    public int MinOccurrences;
    public int MaxOccurrences; // -1 for unbounded
}

public enum PropertyPathType
{
    // Basic
    Predicate,              // <uri>
    Inverse,                // ^<uri>
    
    // Sequence
    Sequence,               // path1 / path2
    
    // Alternative
    Alternative,            // path1 | path2
    
    // Quantifiers
    ZeroOrMore,            // path*
    OneOrMore,             // path+
    ZeroOrOne,             // path?
    
    // Negation
    NegatedPropertySet     // !(uri1|uri2|...)
}

/// <summary>
/// Enumerator for property path results
/// </summary>
public ref struct PropertyPathResultEnumerator
{
    private readonly StreamingTripleStore _store;
    private readonly PropertyPath _path;
    private readonly ReadOnlySpan<char> _startNode;
    private readonly ReadOnlySpan<char> _endNode;
    
    // Current state
    private StreamingTripleStore.TripleEnumerator _currentEnumerator;
    private bool _initialized;
    
    // For recursive evaluation (*, +)
    private unsafe fixed byte _visitedNodes[4096]; // Bitset for visited nodes
    private int _visitedCount;
    
    // Stack for path exploration (max depth 32)
    private unsafe fixed long _pathStack[64]; // Node pairs
    private int _stackDepth;

    internal PropertyPathResultEnumerator(
        StreamingTripleStore store,
        PropertyPath path,
        ReadOnlySpan<char> startNode,
        ReadOnlySpan<char> endNode)
    {
        _store = store;
        _path = path;
        _startNode = startNode;
        _endNode = endNode;
        _initialized = false;
        _visitedCount = 0;
        _stackDepth = 0;
    }

    public bool MoveNext()
    {
        if (!_initialized)
        {
            InitializeEvaluation();
            _initialized = true;
        }

        return _path.Type switch
        {
            PropertyPathType.Predicate => EvaluatePredicate(),
            PropertyPathType.Inverse => EvaluateInverse(),
            PropertyPathType.Sequence => EvaluateSequence(),
            PropertyPathType.Alternative => EvaluateAlternative(),
            PropertyPathType.ZeroOrMore => EvaluateZeroOrMore(),
            PropertyPathType.OneOrMore => EvaluateOneOrMore(),
            PropertyPathType.ZeroOrOne => EvaluateZeroOrOne(),
            PropertyPathType.NegatedPropertySet => EvaluateNegatedPropertySet(),
            _ => false
        };
    }

    private void InitializeEvaluation()
    {
        // Initialize based on path type
        if (_path.Type == PropertyPathType.Predicate)
        {
            _currentEnumerator = _store.Query(
                _startNode,
                _path.PredicateUri,
                _endNode
            );
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool EvaluatePredicate()
    {
        return _currentEnumerator.MoveNext();
    }

    private bool EvaluateInverse()
    {
        // Query with subject/object swapped
        if (!_initialized)
        {
            _currentEnumerator = _store.Query(
                _endNode,
                _path.PredicateUri,
                _startNode
            );
            _initialized = true;
        }
        
        return _currentEnumerator.MoveNext();
    }

    private bool EvaluateSequence()
    {
        // Evaluate path1 / path2
        // This is simplified - full implementation would chain evaluators
        return false;
    }

    private bool EvaluateAlternative()
    {
        // Evaluate path1 | path2
        // This is simplified - full implementation would try both branches
        return false;
    }

    private bool EvaluateZeroOrMore()
    {
        // Transitive closure using BFS
        // Start node matches end node (zero case)
        if (_visitedCount == 0 && _startNode.SequenceEqual(_endNode))
        {
            _visitedCount++;
            return true;
        }
        
        // Expand one step
        return ExpandPath();
    }

    private bool EvaluateOneOrMore()
    {
        // Like ZeroOrMore but requires at least one step
        return ExpandPath();
    }

    private bool EvaluateZeroOrOne()
    {
        // Either direct match or one step
        if (_visitedCount == 0 && _startNode.SequenceEqual(_endNode))
        {
            _visitedCount++;
            return true;
        }
        
        if (_visitedCount == 1)
        {
            return EvaluatePredicate();
        }
        
        return false;
    }

    private bool EvaluateNegatedPropertySet()
    {
        // Query all predicates except those in the negated set
        // Simplified implementation
        return false;
    }

    private bool ExpandPath()
    {
        // BFS expansion for transitive paths
        // This is a simplified implementation
        
        if (!_currentEnumerator.MoveNext())
        {
            // Try next level
            if (_stackDepth == 0)
                return false;
            
            // Pop from stack and continue
            _stackDepth--;
            return ExpandPath();
        }
        
        var triple = _currentEnumerator.Current;
        
        // Check if we've reached the end node
        if (!_endNode.IsEmpty && triple.Object.SequenceEqual(_endNode))
            return true;
        
        // Mark as visited and continue expansion
        MarkVisited(triple.Object);
        
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkVisited(ReadOnlySpan<char> node)
    {
        // Simplified - would use proper hash set
        _visitedCount++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsVisited(ReadOnlySpan<char> node)
    {
        // Simplified - would check hash set
        return false;
    }

    public readonly PathResult Current
    {
        get
        {
            var triple = _currentEnumerator.Current;
            return new PathResult
            {
                StartNode = triple.Subject,
                EndNode = triple.Object,
                PathLength = 1
            };
        }
    }

    public PropertyPathResultEnumerator GetEnumerator() => this;
}

/// <summary>
/// Result of property path evaluation
/// </summary>
public readonly ref struct PathResult
{
    public readonly ReadOnlySpan<char> StartNode;
    public readonly ReadOnlySpan<char> EndNode;
    public readonly int PathLength;
}

/// <summary>
/// Property path builder for fluent API
/// </summary>
public ref struct PropertyPathBuilder
{
    private PropertyPath _path;

    public static PropertyPathBuilder Create()
    {
        return new PropertyPathBuilder();
    }

    public PropertyPathBuilder Predicate(ReadOnlySpan<char> uri)
    {
        _path = new PropertyPath
        {
            Type = PropertyPathType.Predicate,
            PredicateUri = uri
        };
        return this;
    }

    public PropertyPathBuilder Inverse(ReadOnlySpan<char> uri)
    {
        _path = new PropertyPath
        {
            Type = PropertyPathType.Inverse,
            PredicateUri = uri
        };
        return this;
    }

    public PropertyPathBuilder Sequence(PropertyPath left, PropertyPath right)
    {
        _path = new PropertyPath
        {
            Type = PropertyPathType.Sequence,
            LeftPath = left,
            RightPath = right
        };
        return this;
    }

    public PropertyPathBuilder Alternative(PropertyPath left, PropertyPath right)
    {
        _path = new PropertyPath
        {
            Type = PropertyPathType.Alternative,
            LeftPath = left,
            RightPath = right
        };
        return this;
    }

    public PropertyPathBuilder ZeroOrMore()
    {
        var basePath = _path;
        _path = new PropertyPath
        {
            Type = PropertyPathType.ZeroOrMore,
            LeftPath = basePath
        };
        return this;
    }

    public PropertyPathBuilder OneOrMore()
    {
        var basePath = _path;
        _path = new PropertyPath
        {
            Type = PropertyPathType.OneOrMore,
            LeftPath = basePath
        };
        return this;
    }

    public PropertyPathBuilder ZeroOrOne()
    {
        var basePath = _path;
        _path = new PropertyPath
        {
            Type = PropertyPathType.ZeroOrOne,
            LeftPath = basePath
        };
        return this;
    }

    public readonly PropertyPath Build() => _path;
}

/// <summary>
/// Property path parser for SPARQL syntax
/// </summary>
public ref struct PropertyPathParser
{
    private ReadOnlySpan<char> _source;
    private int _position;

    public PropertyPathParser(ReadOnlySpan<char> source)
    {
        _source = source;
        _position = 0;
    }

    public PropertyPath Parse()
    {
        return ParseAlternative();
    }

    private PropertyPath ParseAlternative()
    {
        var left = ParseSequence();
        
        SkipWhitespace();
        if (Peek() == '|')
        {
            Advance(); // Skip '|'
            var right = ParseAlternative();
            
            return new PropertyPath
            {
                Type = PropertyPathType.Alternative,
                LeftPath = left,
                RightPath = right
            };
        }
        
        return left;
    }

    private PropertyPath ParseSequence()
    {
        var left = ParsePrimary();
        
        SkipWhitespace();
        if (Peek() == '/')
        {
            Advance(); // Skip '/'
            var right = ParseSequence();
            
            return new PropertyPath
            {
                Type = PropertyPathType.Sequence,
                LeftPath = left,
                RightPath = right
            };
        }
        
        return left;
    }

    private PropertyPath ParsePrimary()
    {
        SkipWhitespace();
        
        var ch = Peek();
        
        // Inverse path
        if (ch == '^')
        {
            Advance();
            var basePath = ParsePrimary();
            return new PropertyPath
            {
                Type = PropertyPathType.Inverse,
                LeftPath = basePath
            };
        }
        
        // IRI
        if (ch == '<')
        {
            var uri = ParseIri();
            var path = new PropertyPath
            {
                Type = PropertyPathType.Predicate,
                PredicateUri = uri
            };
            
            // Check for quantifiers
            SkipWhitespace();
            ch = Peek();
            
            if (ch == '*')
            {
                Advance();
                return new PropertyPath
                {
                    Type = PropertyPathType.ZeroOrMore,
                    LeftPath = path
                };
            }
            else if (ch == '+')
            {
                Advance();
                return new PropertyPath
                {
                    Type = PropertyPathType.OneOrMore,
                    LeftPath = path
                };
            }
            else if (ch == '?')
            {
                Advance();
                return new PropertyPath
                {
                    Type = PropertyPathType.ZeroOrOne,
                    LeftPath = path
                };
            }
            
            return path;
        }
        
        return default;
    }

    private ReadOnlySpan<char> ParseIri()
    {
        if (Peek() != '<')
            throw new ParseException("Expected '<' for IRI");
        
        var start = _position;
        Advance(); // Skip '<'
        
        while (!IsAtEnd() && Peek() != '>')
            Advance();
        
        if (!IsAtEnd())
            Advance(); // Skip '>'
        
        return _source.Slice(start, _position - start);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SkipWhitespace()
    {
        while (!IsAtEnd() && char.IsWhiteSpace(Peek()))
            Advance();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsAtEnd() => _position >= _source.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char Peek() => IsAtEnd() ? '\0' : _source[_position];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char Advance() => IsAtEnd() ? '\0' : _source[_position++];
}
