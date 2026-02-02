using System;
using System.IO;
using SkyOmega.Mercury.Abstractions;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// Configuration options for storage components (AtomStore, QuadIndex, WriteAheadLog, QuadStore).
/// </summary>
public sealed class StorageOptions
{
    /// <summary>
    /// Default minimum free disk space: 1GB.
    /// </summary>
    public const long DefaultMinimumFreeDiskSpace = 1L << 30; // 1GB

    /// <summary>
    /// Minimum free disk space in bytes that must be maintained on the storage drive.
    /// Storage operations will fail with <see cref="InsufficientDiskSpaceException"/>
    /// if growing would reduce free space below this threshold.
    /// Default: 1GB.
    /// </summary>
    /// <remarks>
    /// This is a safety margin to prevent filling up the disk completely.
    /// Set this based on your system's requirements:
    /// - Development/testing: 512MB - 1GB
    /// - Production with other services: 5GB - 10GB
    /// - Dedicated storage server: 1GB - 2GB
    /// </remarks>
    public long MinimumFreeDiskSpace { get; init; } = DefaultMinimumFreeDiskSpace;

    /// <summary>
    /// Maximum atom size in bytes. Default: 1MB.
    /// Prevents resource exhaustion from oversized values.
    /// </summary>
    public long MaxAtomSize { get; init; } = AtomStore.DefaultMaxAtomSize;

    /// <summary>
    /// Enable full-text search via trigram indexing.
    /// When enabled, object literals are indexed for use with <c>text:match</c> SPARQL function.
    /// Default: false (opt-in for backward compatibility).
    /// </summary>
    /// <remarks>
    /// Full-text search adds overhead to write operations (trigram extraction and indexing).
    /// Only enable if you need the <c>text:match</c> functionality.
    /// </remarks>
    public bool EnableFullTextSearch { get; init; } = false;

    /// <summary>
    /// Initial size for each QuadIndex file (4 indexes: GSPO, GPOS, GOSP, TGSP). Default: 1GB.
    /// For testing, use smaller values (e.g., 64MB) to reduce disk footprint.
    /// Files grow automatically when capacity is exceeded.
    /// </summary>
    public long IndexInitialSizeBytes { get; init; } = 1L << 30;

    /// <summary>
    /// Initial size for AtomStore data file. Default: 1GB.
    /// For testing, use smaller values to reduce disk footprint.
    /// File grows automatically when capacity is exceeded.
    /// </summary>
    public long AtomDataInitialSizeBytes { get; init; } = 1L << 30;

    /// <summary>
    /// Initial capacity for AtomStore offset index (number of atoms). Default: 1M.
    /// File size is this value × 8 bytes (64-bit offsets).
    /// For testing, use smaller values (e.g., 64K) to reduce disk footprint.
    /// </summary>
    public long AtomOffsetInitialCapacity { get; init; } = 1L << 20;

    /// <summary>
    /// Default options with reasonable limits.
    /// </summary>
    public static StorageOptions Default { get; } = new();

    /// <summary>
    /// Options with no disk space enforcement (for testing only).
    /// </summary>
    public static StorageOptions NoEnforcement { get; } = new()
    {
        MinimumFreeDiskSpace = 0
    };

    /// <summary>
    /// Options optimized for testing with minimal disk footprint.
    /// Uses 64MB initial sizes instead of 1GB, reducing per-store footprint from ~5.5GB to ~320MB.
    /// </summary>
    /// <remarks>
    /// Suitable for parallel test execution where many QuadStore instances are created.
    /// Files will grow automatically if tests require more capacity.
    /// </remarks>
    public static StorageOptions ForTesting { get; } = new()
    {
        IndexInitialSizeBytes = 64L << 20,        // 64 MB per index (4 indexes = 256 MB)
        AtomDataInitialSizeBytes = 64L << 20,     // 64 MB atoms
        AtomOffsetInitialCapacity = 64L << 10,    // 64K atoms (~512 KB)
        MinimumFreeDiskSpace = 512L << 20         // 512 MB minimum (relaxed for testing)
    };

    /// <summary>
    /// Creates options with a specific minimum free disk space.
    /// </summary>
    /// <param name="minimumFreeDiskSpace">Minimum free space in bytes.</param>
    public static StorageOptions WithMinimumFreeSpace(long minimumFreeDiskSpace) => new()
    {
        MinimumFreeDiskSpace = minimumFreeDiskSpace
    };
}

/// <summary>
/// Helper for disk space checking used by storage components.
/// </summary>
public static class DiskSpaceChecker
{
    /// <summary>
    /// Checks if there is sufficient disk space for a growth operation.
    /// </summary>
    /// <param name="filePath">Path to the file being grown.</param>
    /// <param name="growthBytes">Number of bytes to grow by.</param>
    /// <param name="minimumFreeSpace">Minimum free space that must remain.</param>
    /// <exception cref="InsufficientDiskSpaceException">
    /// Thrown if the growth would reduce free space below the minimum.
    /// </exception>
    public static void EnsureSufficientSpace(string filePath, long growthBytes, long minimumFreeSpace)
    {
        if (minimumFreeSpace <= 0)
            return; // Enforcement disabled

        var available = GetAvailableSpace(filePath);
        if (available < 0)
            return; // Unable to determine - proceed (fail-open for exotic filesystems)

        var remainingAfterGrowth = available - growthBytes;
        if (remainingAfterGrowth < minimumFreeSpace)
        {
            throw new InsufficientDiskSpaceException(
                filePath,
                growthBytes,
                available,
                minimumFreeSpace);
        }
    }

    /// <summary>
    /// Gets available free space on the drive containing the specified path.
    /// </summary>
    /// <param name="path">Path to check (file or directory).</param>
    /// <returns>Available free space in bytes, or -1 if unable to determine.</returns>
    public static long GetAvailableSpace(string path)
    {
        try
        {
            var fullPath = System.IO.Path.GetFullPath(path);
            var root = System.IO.Path.GetPathRoot(fullPath);

            if (string.IsNullOrEmpty(root))
                return -1;

            var driveInfo = new DriveInfo(root);
            return driveInfo.AvailableFreeSpace;
        }
        catch (Exception)
        {
            return -1;
        }
    }
}
