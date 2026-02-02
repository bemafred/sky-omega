// QuadStorePoolOptions.cs
// Configuration options for QuadStorePool.
// No external dependencies, only BCL.
// .NET 10 / C# 14

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// Configuration options for <see cref="QuadStorePool"/>.
/// </summary>
public sealed class QuadStorePoolOptions
{
    /// <summary>
    /// Maximum disk space in bytes for all stores combined.
    /// 0 means use automatic calculation based on <see cref="DiskBudgetFraction"/>.
    /// </summary>
    public long MaxDiskBytes { get; init; }

    /// <summary>
    /// Maximum number of anonymous pooled stores for <see cref="QuadStorePool.Rent"/>/<see cref="QuadStorePool.Return"/> API.
    /// Default: 8.
    /// </summary>
    public int MaxPooledStores { get; init; } = 8;

    /// <summary>
    /// Storage options for created stores.
    /// Default: <see cref="StorageOptions.Default"/>.
    /// </summary>
    public StorageOptions StorageOptions { get; init; } = StorageOptions.Default;

    /// <summary>
    /// Enable cross-process coordination via <see cref="SkyOmega.Mercury.Runtime.CrossProcessStoreGate"/>.
    /// When enabled, store creation blocks until a global slot is available across ALL processes.
    /// Useful for parallel test execution to prevent disk exhaustion.
    /// Default: false.
    /// </summary>
    public bool UseCrossProcessGate { get; init; }

    /// <summary>
    /// Fraction of available disk space to use as budget (0.0-1.0).
    /// Only used when <see cref="MaxDiskBytes"/> is 0.
    /// Default: 0.33 (33%).
    /// </summary>
    public double DiskBudgetFraction { get; init; } = QuadStorePool.DefaultDiskBudgetFraction;

    /// <summary>
    /// Default options suitable for production use.
    /// </summary>
    public static QuadStorePoolOptions Default { get; } = new();

    /// <summary>
    /// Options optimized for testing with minimal disk footprint.
    /// Uses <see cref="StorageOptions.ForTesting"/> and enables cross-process gate.
    /// </summary>
    public static QuadStorePoolOptions ForTesting { get; } = new()
    {
        StorageOptions = StorageOptions.ForTesting,
        UseCrossProcessGate = true,
        MaxPooledStores = 4
    };
}
