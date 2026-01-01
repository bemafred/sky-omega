using System;
using System.IO;

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
    /// Creates options with a specific minimum free disk space.
    /// </summary>
    /// <param name="minimumFreeDiskSpace">Minimum free space in bytes.</param>
    public static StorageOptions WithMinimumFreeSpace(long minimumFreeDiskSpace) => new()
    {
        MinimumFreeDiskSpace = minimumFreeDiskSpace
    };
}

/// <summary>
/// Exception thrown when a storage operation would reduce free disk space
/// below the configured minimum threshold.
/// </summary>
public sealed class InsufficientDiskSpaceException : IOException
{
    /// <summary>
    /// Path of the file/directory where the operation was attempted.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Bytes the operation attempted to allocate.
    /// </summary>
    public long RequestedBytes { get; }

    /// <summary>
    /// Current available disk space in bytes.
    /// </summary>
    public long AvailableBytes { get; }

    /// <summary>
    /// Configured minimum free space that must be maintained.
    /// </summary>
    public long MinimumFreeSpace { get; }

    public InsufficientDiskSpaceException(
        string path,
        long requestedBytes,
        long availableBytes,
        long minimumFreeSpace)
        : base(FormatMessage(path, requestedBytes, availableBytes, minimumFreeSpace))
    {
        Path = path;
        RequestedBytes = requestedBytes;
        AvailableBytes = availableBytes;
        MinimumFreeSpace = minimumFreeSpace;
    }

    private static string FormatMessage(
        string path,
        long requestedBytes,
        long availableBytes,
        long minimumFreeSpace)
    {
        var afterGrowth = availableBytes - requestedBytes;
        return $"Storage operation refused: growing '{System.IO.Path.GetFileName(path)}' by {FormatBytes(requestedBytes)} " +
               $"would leave only {FormatBytes(afterGrowth)} free (minimum required: {FormatBytes(minimumFreeSpace)}). " +
               $"Current available: {FormatBytes(availableBytes)}.";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        return bytes switch
        {
            >= 1L << 40 => $"{bytes / (double)(1L << 40):F2} TB",
            >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
            >= 1L << 20 => $"{bytes / (double)(1L << 20):F2} MB",
            >= 1L << 10 => $"{bytes / (double)(1L << 10):F2} KB",
            _ => $"{bytes} bytes"
        };
    }
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
