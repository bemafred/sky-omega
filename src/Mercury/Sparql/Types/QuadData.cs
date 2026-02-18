namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// Represents a quad (triple + optional graph) for INSERT DATA / DELETE DATA.
/// Uses offsets into source span for zero-allocation parsing.
/// </summary>
internal struct QuadData
{
    public int SubjectStart;
    public int SubjectLength;
    public TermType SubjectType;

    public int PredicateStart;
    public int PredicateLength;
    public TermType PredicateType;

    public int ObjectStart;
    public int ObjectLength;
    public TermType ObjectType;

    // Optional graph (0 length = default graph)
    public int GraphStart;
    public int GraphLength;
}
