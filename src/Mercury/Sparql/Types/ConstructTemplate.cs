namespace SkyOmega.Mercury.Sparql.Types;

internal struct ConstructTemplate
{
    public const int MaxPatterns = 16;
    private int _patternCount;

    // Inline storage for up to 16 template triple patterns
    private TriplePattern _p0, _p1, _p2, _p3, _p4, _p5, _p6, _p7;
    private TriplePattern _p8, _p9, _p10, _p11, _p12, _p13, _p14, _p15;

    public readonly int PatternCount => _patternCount;
    public readonly bool HasPatterns => _patternCount > 0;

    public void AddPattern(TriplePattern pattern)
    {
        if (_patternCount >= MaxPatterns)
            throw new SparqlParseException("Too many patterns in CONSTRUCT template (max 16)");

        switch (_patternCount)
        {
            case 0: _p0 = pattern; break;
            case 1: _p1 = pattern; break;
            case 2: _p2 = pattern; break;
            case 3: _p3 = pattern; break;
            case 4: _p4 = pattern; break;
            case 5: _p5 = pattern; break;
            case 6: _p6 = pattern; break;
            case 7: _p7 = pattern; break;
            case 8: _p8 = pattern; break;
            case 9: _p9 = pattern; break;
            case 10: _p10 = pattern; break;
            case 11: _p11 = pattern; break;
            case 12: _p12 = pattern; break;
            case 13: _p13 = pattern; break;
            case 14: _p14 = pattern; break;
            case 15: _p15 = pattern; break;
        }
        _patternCount++;
    }

    public readonly TriplePattern GetPattern(int index)
    {
        return index switch
        {
            0 => _p0,
            1 => _p1,
            2 => _p2,
            3 => _p3,
            4 => _p4,
            5 => _p5,
            6 => _p6,
            7 => _p7,
            8 => _p8,
            9 => _p9,
            10 => _p10,
            11 => _p11,
            12 => _p12,
            13 => _p13,
            14 => _p14,
            15 => _p15,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }
}
