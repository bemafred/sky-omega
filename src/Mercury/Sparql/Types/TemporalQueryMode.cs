namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// Temporal query mode for bitemporal queries.
/// </summary>
internal enum TemporalQueryMode
{
    Current,      // Default: valid at UtcNow
    AsOf,         // Point-in-time: valid at specific time
    During,       // Range: changed during period
    AllVersions   // Evolution: all versions ever
}
