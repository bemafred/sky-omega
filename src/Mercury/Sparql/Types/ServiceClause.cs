namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// A SERVICE clause: SERVICE [SILENT] &lt;uri&gt; { patterns } or SERVICE [SILENT] ?var { patterns }
/// Stores the endpoint term and patterns to be sent to a remote SPARQL endpoint.
/// </summary>
public struct ServiceClause
{
    public const int MaxPatterns = 8;

    public bool Silent;           // SILENT modifier - ignore failures
    public bool IsOptional;       // Inside OPTIONAL block - preserve outer bindings on no match
    public int UnionBranch;       // 0 = not in UNION or first branch, 1 = second branch
    public Term Endpoint;         // The endpoint IRI or variable
    private int _patternCount;

    // Inline storage for up to 8 triple patterns
    private TriplePattern _p0, _p1, _p2, _p3, _p4, _p5, _p6, _p7;

    public readonly int PatternCount => _patternCount;
    public readonly bool HasPatterns => _patternCount > 0;
    public readonly bool IsVariable => Endpoint.IsVariable;

    public void AddPattern(TriplePattern pattern)
    {
        if (_patternCount >= MaxPatterns) return;
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
            _ => default
        };
    }
}
