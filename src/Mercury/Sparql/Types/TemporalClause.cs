namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// Temporal clause parsed from SPARQL query.
/// Supports: AS OF, DURING, ALL VERSIONS
/// </summary>
internal struct TemporalClause
{
    public TemporalQueryMode Mode;

    // For AS OF: single timestamp (TimeStart only)
    // For DURING: range [TimeStart, TimeEnd]
    public int TimeStartStart;   // Offset into source for start time literal
    public int TimeStartLength;
    public int TimeEndStart;     // Offset for end time (DURING only)
    public int TimeEndLength;

    public readonly bool HasTemporal => Mode != TemporalQueryMode.Current;
}
