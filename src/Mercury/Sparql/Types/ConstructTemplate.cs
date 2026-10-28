using System;

namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// [73] ConstructTemplate: the triple patterns a CONSTRUCT instantiates per solution, in order and with no size limit.
/// Read once per query at parse time, so its storage is an ordinary array that grows as triples are added.
/// </summary>
internal struct ConstructTemplate
{
    private TriplePattern[]? _patterns;
    private int _patternCount;

    public readonly int PatternCount => _patternCount;
    public readonly bool HasPatterns => _patternCount > 0;

    public void AddPattern(TriplePattern pattern)
    {
        if (_patterns == null || _patternCount == _patterns.Length)
            Array.Resize(ref _patterns, Math.Max(8, 2 * _patternCount));
        _patterns[_patternCount++] = pattern;
    }

    public readonly TriplePattern GetPattern(int index)
    {
        if ((uint)index >= (uint)_patternCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _patterns![index];
    }
}
