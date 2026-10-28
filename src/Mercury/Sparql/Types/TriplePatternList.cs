using System;
using System.Buffers;

namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// The triple patterns an expansion produces (a collection, a blank-node property list, a quoted triple or a sequence
/// path, written as several triples), in order and with no size limit. Its storage is a pooled array, rented on the first
/// triple and grown as needed; must be disposed.
/// </summary>
internal struct TriplePatternList : IDisposable
{
    private TriplePattern[]? _patterns;
    private int _count;

    public readonly int Count => _count;

    public readonly TriplePattern this[int index] => _patterns![index];

    public void Add(TriplePattern pattern)
    {
        if (_patterns == null || _count == _patterns.Length)
        {
            var grown = ArrayPool<TriplePattern>.Shared.Rent(Math.Max(16, 2 * _count));
            if (_patterns != null)
            {
                _patterns.AsSpan(0, _count).CopyTo(grown);
                ArrayPool<TriplePattern>.Shared.Return(_patterns);
            }
            _patterns = grown;
        }
        _patterns[_count++] = pattern;
    }

    public void Dispose()
    {
        if (_patterns != null)
        {
            ArrayPool<TriplePattern>.Shared.Return(_patterns);
            _patterns = null;
        }
        _count = 0;
    }
}
