namespace SkyOmega.Mercury.Pruning;

/// <summary>
/// Progress information for transfer operations.
/// </summary>
internal readonly struct TransferProgress
{
    /// <summary>Total quads scanned from source.</summary>
    public long QuadsScanned { get; init; }

    /// <summary>Quads that passed the filter.</summary>
    public long QuadsMatched { get; init; }

    /// <summary>Quads written to target.</summary>
    public long QuadsWritten { get; init; }

    /// <summary>Elapsed time since transfer started.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>Current throughput in quads/second.</summary>
    public double QuadsPerSecond => Elapsed.TotalSeconds > 0
        ? QuadsScanned / Elapsed.TotalSeconds : 0;

    /// <summary>Current graph being processed (if applicable).</summary>
    public string? CurrentGraph { get; init; }
}

/// <summary>
/// Callback delegate for progress reporting.
/// </summary>
internal delegate void TransferProgressCallback(in TransferProgress progress);
