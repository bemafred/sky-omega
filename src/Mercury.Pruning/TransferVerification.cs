namespace SkyOmega.Mercury.Pruning;

/// <summary>
/// Result of post-transfer verification.
/// </summary>
public readonly struct TransferVerification
{
    /// <summary>Whether verification passed successfully.</summary>
    public bool Passed { get; init; }

    /// <summary>Quads in source matching filter (re-enumerated after transfer).</summary>
    public long SourceCount { get; init; }

    /// <summary>Quads in target after transfer.</summary>
    public long TargetCount { get; init; }

    /// <summary>SourceCount - TargetCount (if > 0, data was lost).</summary>
    public long MissingCount { get; init; }

    /// <summary>Source checksum (if ComputeChecksum was enabled).</summary>
    public ulong? SourceChecksum { get; init; }

    /// <summary>Target checksum (if ComputeChecksum was enabled).</summary>
    public ulong? TargetChecksum { get; init; }

    /// <summary>Error message if verification failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Creates a successful verification result.
    /// </summary>
    public static TransferVerification Success(long sourceCount, long targetCount, ulong? sourceChecksum = null, ulong? targetChecksum = null) =>
        new()
        {
            Passed = true,
            SourceCount = sourceCount,
            TargetCount = targetCount,
            MissingCount = 0,
            SourceChecksum = sourceChecksum,
            TargetChecksum = targetChecksum
        };

    /// <summary>
    /// Creates a failed verification result.
    /// </summary>
    public static TransferVerification Failure(long sourceCount, long targetCount, string errorMessage, ulong? sourceChecksum = null, ulong? targetChecksum = null) =>
        new()
        {
            Passed = false,
            SourceCount = sourceCount,
            TargetCount = targetCount,
            MissingCount = sourceCount - targetCount,
            SourceChecksum = sourceChecksum,
            TargetChecksum = targetChecksum,
            ErrorMessage = errorMessage
        };
}
