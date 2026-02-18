using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Diagnostics;
using SkyOmega.Mercury.Pruning.Filters;
using SkyOmega.Mercury.Runtime.Buffers;

namespace SkyOmega.Mercury.Pruning;

/// <summary>
/// Configuration options for quad transfer.
/// </summary>
internal sealed class TransferOptions
{
    /// <summary>
    /// How to handle temporal history. Default: FlattenToCurrent.
    /// </summary>
    public HistoryMode HistoryMode { get; init; } = HistoryMode.FlattenToCurrent;

    /// <summary>
    /// Filter to apply during transfer. Default: AllPassFilter (include everything).
    /// </summary>
    public IPruningFilter Filter { get; init; } = AllPassFilter.Instance;

    /// <summary>
    /// Batch size for writes to target store. Default: 10,000 quads per batch.
    /// Higher values = better throughput, more memory.
    /// </summary>
    public int BatchSize { get; init; } = 10_000;

    /// <summary>
    /// Progress callback interval (number of quads between reports). Default: 100,000.
    /// Set to 0 to disable progress reporting.
    /// </summary>
    public int ProgressInterval { get; init; } = 100_000;

    /// <summary>
    /// Cancellation token for long-running transfers.
    /// </summary>
    public CancellationToken CancellationToken { get; init; } = CancellationToken.None;

    /// <summary>
    /// Logger for diagnostics. Default: NullLogger.
    /// </summary>
    public ILogger Logger { get; init; } = NullLogger.Instance;

    /// <summary>
    /// Buffer manager for pooled allocations. Default: PooledBufferManager.Shared.
    /// </summary>
    public IBufferManager BufferManager { get; init; } = PooledBufferManager.Shared;

    /// <summary>
    /// If true, enumerate and filter without writing to target.
    /// Returns what WOULD be transferred. Default: false.
    /// </summary>
    public bool DryRun { get; init; } = false;

    /// <summary>
    /// If true, perform post-transfer verification by re-enumerating source
    /// and checking each quad exists in target. Default: false.
    /// </summary>
    public bool VerifyAfterTransfer { get; init; } = false;

    /// <summary>
    /// Compute content hash during transfer for verification.
    /// Uses XxHash64 for speed. Default: false.
    /// </summary>
    public bool ComputeChecksum { get; init; } = false;

    /// <summary>
    /// If set, writes filtered-out quads to this file in N-Quads format.
    /// Enables recovery of pruned data if needed. Default: null (disabled).
    /// </summary>
    public string? AuditLogPath { get; init; }

    /// <summary>
    /// Default options: FlattenToCurrent, no filter, 10K batch size.
    /// </summary>
    public static TransferOptions Default { get; } = new();
}
