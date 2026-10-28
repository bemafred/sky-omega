using System;
using System.Buffers;

namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// The in-scope variables of a pattern (SPARQL 1.1 §18.2.1), each once, in the order the query first mentions them
/// (the tree's order differs where a blank-node property list expands), as spans of the query source with their sigils. Filled by <see cref="Parsing.SparqlParser.AddInScopeVariables"/>.
/// Its storage is a pooled array, rented on the first variable; must be disposed.
/// </summary>
internal ref struct InScopeVariables
{
    private readonly ReadOnlySpan<char> _source;
    private int[]? _spans; // start, length pairs
    private int _count;

    /// <param name="source">The query source the variables' offsets index.</param>
    public InScopeVariables(ReadOnlySpan<char> source)
    {
        _source = source;
        _spans = null;
        _count = 0;
    }

    public readonly int Count => _count;

    /// <summary>The variable at <paramref name="index"/>, as written (with its sigil).</summary>
    public readonly ReadOnlySpan<char> this[int index] => _source.Slice(_spans![2 * index], _spans[2 * index + 1]);

    /// <summary>True when <paramref name="name"/> (with or without a sigil) is one of the variables.</summary>
    public readonly bool Contains(ReadOnlySpan<char> name) => IndexOf(name) >= 0;

    private readonly int IndexOf(ReadOnlySpan<char> name)
    {
        for (int i = 0; i < _count; i++)
        {
            if (VariableName.Same(this[i], name))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Adds the variable written at <paramref name="start"/>. A variable already here moves to this mention when it
    /// comes earlier in the source.
    /// </summary>
    public void Add(int start, int length)
    {
        int existing = IndexOf(_source.Slice(start, length));
        if (existing >= 0)
        {
            if (start >= _spans![2 * existing])
                return;
            // Remove it, then insert it at this earlier mention
            _spans.AsSpan(2 * existing + 2, 2 * (_count - existing - 1)).CopyTo(_spans.AsSpan(2 * existing));
            _count--;
        }
        if (_spans == null || 2 * _count + 2 > _spans.Length)
        {
            var grown = ArrayPool<int>.Shared.Rent(Math.Max(32, 2 * (_spans?.Length ?? 0)));
            if (_spans != null)
            {
                _spans.AsSpan(0, 2 * _count).CopyTo(grown);
                ArrayPool<int>.Shared.Return(_spans);
            }
            _spans = grown;
        }
        int at = _count;
        while (at > 0 && _spans[2 * (at - 1)] > start)
            at--;
        _spans.AsSpan(2 * at, 2 * (_count - at)).CopyTo(_spans.AsSpan(2 * at + 2));
        _spans[2 * at] = start;
        _spans[2 * at + 1] = length;
        _count++;
    }

    public void Dispose()
    {
        if (_spans != null)
        {
            ArrayPool<int>.Shared.Return(_spans);
            _spans = null;
        }
    }
}
