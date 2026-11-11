namespace SkyOmega.Mercury.Abstractions;

/// <summary>
/// How to handle temporal history during transfer.
/// </summary>
public enum HistoryMode
{
    /// <summary>
    /// Only transfer current facts (valid at UtcNow).
    /// Results in a single version per quad with MemorizedAt=now, ForgottenAt=MaxValue.
    /// Most compact output - discards all history.
    /// </summary>
    FlattenToCurrent,

    /// <summary>
    /// Transfer all versions including historical ones.
    /// Preserves MemorizedAt/ForgottenAt exactly as stored.
    /// Excludes soft-deleted entries.
    /// </summary>
    PreserveVersions,

    /// <summary>
    /// Transfer all versions including soft-deleted entries.
    /// Full audit trail preservation.
    /// </summary>
    PreserveAll
}
