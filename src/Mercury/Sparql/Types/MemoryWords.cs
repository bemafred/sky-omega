namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// The words of Cognitive SPARQL after one triple pattern (ADR-061 D6, D10), as offsets into the query's source:
/// <c>MEMORIZED</c> and <c>FORGOTTEN</c>, each with its time variable and its bounds. The parser reads them with
/// <c>SparqlParser.ParseMemoryWords</c>; the slot of the pattern keeps their span, and the executor reads them again
/// from it, with the same reader.
/// </summary>
internal struct MemoryWords
{
    public MemoryWord Memorized;
    public MemoryWord Forgotten;
}

/// <summary>
/// One word: whether it is written, the variable it binds, and the expressions of its <c>SINCE</c> and
/// <c>BEFORE</c>. A length of 0 is absent.
/// </summary>
internal struct MemoryWord
{
    public bool IsWritten;
    public int VariableStart, VariableLength;
    public int SinceStart, SinceLength;
    public int BeforeStart, BeforeLength;
}
