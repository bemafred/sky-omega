namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// An EXISTS or NOT EXISTS filter: FILTER [NOT] EXISTS { pattern }
/// Stores the pattern for later evaluation against the store.
/// </summary>
public struct ExistsFilter
{
    public const int MaxPatterns = 4;

    public bool Negated;         // true for NOT EXISTS, false for EXISTS
    private int _patternCount;

    // Inline storage for up to 4 triple patterns
    private TriplePattern _p0, _p1, _p2, _p3;

    // Graph context for patterns inside GRAPH clause
    // If HasGraph is true, all patterns should be evaluated against this graph
    public bool HasGraph;
    public Term GraphTerm;  // The graph IRI or variable

    public readonly int PatternCount => _patternCount;
    public readonly bool HasPatterns => _patternCount > 0;

    public void AddPattern(TriplePattern pattern)
    {
        if (_patternCount >= MaxPatterns) return;
        switch (_patternCount)
        {
            case 0: _p0 = pattern; break;
            case 1: _p1 = pattern; break;
            case 2: _p2 = pattern; break;
            case 3: _p3 = pattern; break;
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
            _ => default
        };
    }

    public void SetGraphContext(Term graphTerm)
    {
        HasGraph = true;
        GraphTerm = graphTerm;
    }
}
