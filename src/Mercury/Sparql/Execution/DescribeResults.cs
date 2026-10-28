using System;
using System.Buffers;
using System.Collections.Generic;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// The triples a DESCRIBE returns (SPARQL 1.1 §16.4): for each resource, the triples with it as subject, then those with
/// it as object, read from every graph the description covers, as the query's temporal scope sees them. A triple is
/// returned once: one in several graphs is returned once, and one whose subject is also described is returned with that
/// subject, not again as another resource's object. Must be disposed to return its buffer.
/// </summary>
internal ref struct DescribeResults
{
    private readonly QuadStore? _store;
    private readonly List<string>? _resources;
    private readonly HashSet<string>? _described;
    private readonly List<string>? _graphs;
    private readonly TemporalScope _scope;
    private readonly HashSet<string>? _returned;
    private int _resource;
    private int _graph;
    private bool _asSubject;
    private bool _open;
    private TemporalResultEnumerator _currentEnumerator;

    private ConstructedTriple _current;
    private char[]? _outputBuffer;

    public static DescribeResults Empty() => default;

    /// <param name="store">The store to describe from.</param>
    /// <param name="resources">The IRIs and blank nodes to describe, each once, in the order to describe them.</param>
    /// <param name="graphs">The graphs to read, in IRI form; empty is the default graph.</param>
    /// <param name="scope">The query's temporal scope.</param>
    internal DescribeResults(QuadStore store, List<string> resources, List<string> graphs, TemporalScope scope)
    {
        _store = store;
        _resources = resources;
        _described = new HashSet<string>(resources);
        _graphs = graphs;
        _scope = scope;
        _returned = new HashSet<string>();
        _resource = 0;
        _graph = 0;
        _asSubject = true;
        _open = false;
        _currentEnumerator = default;
        _current = default;
        _outputBuffer = ArrayPool<char>.Shared.Rent(2048);
    }

    public readonly ConstructedTriple Current => _current;

    public bool MoveNext()
    {
        if (_store == null || _graphs!.Count == 0)
            return false;

        while (true)
        {
            if (_open)
            {
                while (_currentEnumerator.MoveNext())
                {
                    var triple = _currentEnumerator.Current;

                    // As an object: a triple whose subject is described is that subject's
                    if (!_asSubject && _described!.Contains(triple.Subject.ToString()))
                        continue;
                    if (!_returned!.Add($"{triple.Subject} {triple.Predicate} {triple.Object}"))
                        continue; // already returned from another graph

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
                _open = false;

                // Next graph; after the last, the object side; after that, the next resource
                if (++_graph == _graphs.Count)
                {
                    _graph = 0;
                    if (_asSubject)
                    {
                        _asSubject = false;
                    }
                    else
                    {
                        _asSubject = true;
                        _resource++;
                    }
                }
            }

            if (_resource == _resources!.Count)
                return false;

            var resource = _resources[_resource].AsSpan();
            _currentEnumerator = _asSubject
                ? _scope.Query(_store, resource, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty, _graphs[_graph].AsSpan())
                : _scope.Query(_store, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty, resource, _graphs[_graph].AsSpan());
            _open = true;
        }
    }

    public void Dispose()
    {
        if (_open)
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
