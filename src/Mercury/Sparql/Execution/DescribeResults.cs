using System;
using System.Buffers;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Execution;

public ref struct DescribeResults
{
    private readonly TripleStore _store;
    private QueryResults _queryResults;
    private Binding[]? _bindings;
    private char[]? _stringBuffer;
    private bool _isEmpty;
    private bool _describeAll;

    // State for iterating through resources and their triples
    private HashSet<string> _describedResources;
    private Queue<string> _pendingResources;
    private TemporalResultEnumerator _currentEnumerator;
    private bool _queryingAsSubject;  // true = querying as subject, false = querying as object
    private string? _currentResource;
    private bool _initialized;

    // Current triple
    private ConstructedTriple _current;
    private char[] _outputBuffer;

    public static DescribeResults Empty()
    {
        var result = new DescribeResults();
        result._isEmpty = true;
        return result;
    }

    internal DescribeResults(TripleStore store, QueryResults queryResults,
        Binding[] bindings, char[] stringBuffer, bool describeAll)
    {
        _store = store;
        _queryResults = queryResults;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _isEmpty = false;
        _describeAll = describeAll;

        _describedResources = new HashSet<string>();
        _pendingResources = new Queue<string>();
        _currentEnumerator = default;
        _queryingAsSubject = true;
        _currentResource = null;
        _initialized = false;
        _current = default;
        _outputBuffer = ArrayPool<char>.Shared.Rent(2048);
    }

    public readonly ConstructedTriple Current => _current;

    public bool MoveNext()
    {
        if (_isEmpty)
            return false;

        while (true)
        {
            // Try to get next triple from current enumerator
            if (_currentResource != null)
            {
                while (_currentEnumerator.MoveNext())
                {
                    var triple = _currentEnumerator.Current;

                    // Copy triple to output buffer
                    int pos = 0;
                    var subjectStart = pos;
                    triple.Subject.CopyTo(_outputBuffer.AsSpan(pos));
                    pos += triple.Subject.Length;
                    var subjectLen = triple.Subject.Length;

                    var predicateStart = pos;
                    triple.Predicate.CopyTo(_outputBuffer.AsSpan(pos));
                    pos += triple.Predicate.Length;
                    var predicateLen = triple.Predicate.Length;

                    var objectStart = pos;
                    triple.Object.CopyTo(_outputBuffer.AsSpan(pos));
                    var objectLen = triple.Object.Length;

                    _current = new ConstructedTriple(
                        _outputBuffer.AsSpan(subjectStart, subjectLen),
                        _outputBuffer.AsSpan(predicateStart, predicateLen),
                        _outputBuffer.AsSpan(objectStart, objectLen));

                    return true;
                }

                _currentEnumerator.Dispose();

                // Switch from subject query to object query
                if (_queryingAsSubject)
                {
                    _queryingAsSubject = false;
                    _currentEnumerator = _store.QueryCurrent(
                        ReadOnlySpan<char>.Empty,
                        ReadOnlySpan<char>.Empty,
                        _currentResource.AsSpan());
                    continue;
                }

                // Done with this resource, move to next
                _currentResource = null;
                _queryingAsSubject = true;
            }

            // Get next resource to describe
            if (_pendingResources.Count > 0)
            {
                _currentResource = _pendingResources.Dequeue();
                _currentEnumerator = _store.QueryCurrent(
                    _currentResource.AsSpan(),
                    ReadOnlySpan<char>.Empty,
                    ReadOnlySpan<char>.Empty);
                continue;
            }

            // Get more resources from query results
            if (!_initialized || _queryResults.MoveNext())
            {
                _initialized = true;
                var currentBindings = _queryResults.Current;

                // Collect all IRI/blank node values from bindings
                for (int i = 0; i < currentBindings.Count; i++)
                {
                    var value = currentBindings.GetString(i);
                    var valueStr = value.ToString();

                    // Only describe IRIs and blank nodes (skip literals)
                    if (value.Length > 0 && (value[0] == '<' || value[0] == '_'))
                    {
                        if (!_describedResources.Contains(valueStr))
                        {
                            _describedResources.Add(valueStr);
                            _pendingResources.Enqueue(valueStr);
                        }
                    }
                }

                continue;
            }

            // No more results
            return false;
        }
    }

    public void Dispose()
    {
        _queryResults.Dispose();
        _currentEnumerator.Dispose();
        if (_outputBuffer != null)
        {
            ArrayPool<char>.Shared.Return(_outputBuffer);
            _outputBuffer = null!;
        }
    }
}

/// <summary>
/// Scans a single triple pattern against the store.
/// Binds matching values to variables.
/// Supports property paths: inverse (^), zero-or-more (*), one-or-more (+), zero-or-one (?).
/// </summary>
