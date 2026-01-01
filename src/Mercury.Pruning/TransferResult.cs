namespace SkyOmega.Mercury.Pruning;

/// <summary>
/// Result of a completed transfer operation.
/// </summary>
public readonly struct TransferResult
{
    /// <summary>Whether the transfer completed successfully.</summary>
    public bool Success { get; init; }

    /// <summary>Total quads scanned from source.</summary>
    public long TotalScanned { get; init; }

    /// <summary>Quads that passed the filter.</summary>
    public long TotalMatched { get; init; }

    /// <summary>Quads written to target.</summary>
    public long TotalWritten { get; init; }

    /// <summary>Atoms in target store after transfer.</summary>
    public long TargetAtomCount { get; init; }

    /// <summary>Total elapsed time.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Error message if Success is false.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Storage savings (source bytes - target bytes).
    /// Positive = space saved, negative = space increased.
    /// </summary>
    public long BytesSaved { get; init; }

    /// <summary>
    /// XxHash64 of all transferred quad content (if ComputeChecksum was enabled).
    /// </summary>
    public ulong? ContentChecksum { get; init; }

    /// <summary>
    /// Verification result (if VerifyAfterTransfer was enabled).
    /// </summary>
    public TransferVerification? Verification { get; init; }

    /// <summary>
    /// Throughput in quads per second.
    /// </summary>
    public double QuadsPerSecond => Duration.TotalSeconds > 0
        ? TotalWritten / Duration.TotalSeconds : 0;
}
