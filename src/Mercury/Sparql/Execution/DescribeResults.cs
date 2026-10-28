using System;
using System.Buffers;
using System.Collections.Generic;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// The triples a DESCRIBE returns (SPARQL 1.1 §16.4): for each resource, the current triples with it as subject, then
/// those with it as object. A triple is returned once: one whose subject is also described is returned with that
/// subject, not again as another resource's object. Must be disposed to return its buffer.
/// </summary>
internal ref struct DescribeResults
{
    private readonly QuadStore? _store;
    private readonly List<string>? _resources;
    private readonly HashSet<string>? _described;
    private int _next;
    private string? _currentResource;
    private bool _queryingAsSubject;
    private TemporalResultEnumerator _currentEnumerator;

    private ConstructedTriple _current;
    private char[]? _outputBuffer;

    public static DescribeResults Empty() => default;

    /// <param name="store">The store to describe from.</param>
    /// <param name="resources">The IRIs and blank nodes to describe, each once, in the order to describe them.</param>
    internal DescribeResults(QuadStore store, List<string> resources)
    {
        _store = store;
        _resources = resources;
        _described = new HashSet<string>(resources);
        _next = 0;
        _currentResource = null;
        _queryingAsSubject = true;
        _currentEnumerator = default;
        _current = default;
        _outputBuffer = ArrayPool<char>.Shared.Rent(2048);
    }

    public readonly ConstructedTriple Current => _current;

    public bool MoveNext()
    {
        if (_store == null)
            return false;

        while (true)
        {
            if (_currentResource != null)
            {
                while (_currentEnumerator.MoveNext())
                {
                    var triple = _currentEnumerator.Current;

                    // As an object: a triple whose subject is described is that subject's
                    if (!_queryingAsSubject && _described!.Contains(triple.Subject.ToString()))
                        continue;

                    int length = triple.Subject.Length + triple.Predicate.Length + triple.Object.Length;
                    if (_outputBuffer!.Length < length)
                    {
                        ArrayPool<char>.Shared.Return(_outputBuffer);
                        _outputBuffer = ArrayPool<char>.Shared.Rent(length);
                    }
                    var output = _outputBuffer.AsSpan();
                    triple.Subject.CopyTo(output);
                    triple.Predicate.CopyTo(output.Slice(triple.Subject.Length));
                    triple.Object.CopyTo(output.Slice(triple.Subject.Length + triple.Predicate.Length));

                    _current = new ConstructedTriple(
                        output.Slice(0, triple.Subject.Length),
                        output.Slice(triple.Subject.Length, triple.Predicate.Length),
                        output.Slice(triple.Subject.Length + triple.Predicate.Length, triple.Object.Length));
                    return true;
                }

                _currentEnumerator.Dispose();

                if (_queryingAsSubject)
                {
                    _queryingAsSubject = false;
                    _currentEnumerator = _store.QueryCurrent(
                        ReadOnlySpan<char>.Empty,
                        ReadOnlySpan<char>.Empty,
                        _currentResource.AsSpan());
                    continue;
                }

                _currentResource = null;
                _queryingAsSubject = true;
            }

            if (_next == _resources!.Count)
                return false;

            _currentResource = _resources[_next++];
            _currentEnumerator = _store.QueryCurrent(
                _currentResource.AsSpan(),
                ReadOnlySpan<char>.Empty,
                ReadOnlySpan<char>.Empty);
        }
    }

    public void Dispose()
    {
        _currentEnumerator.Dispose();
        if (_outputBuffer != null)
        {
            ArrayPool<char>.Shared.Return(_outputBuffer);
            _outputBuffer = null;
        }
    }
}

/// <summary>
/// Scans a single triple pattern against the store.
/// Binds matching values to variables.
/// Supports property paths: inverse (^), zero-or-more (*), one-or-more (+), zero-or-one (?).
/// </summary>
