using System;
using System.IO;
using SkyOmega.Mercury.Abstractions;

namespace SkyOmega.Mercury.Runtime;

/// <summary>
/// Provides disk space checking utilities to prevent filling up disks during tests and operations.
/// </summary>
/// <remarks>
/// <para>
/// Use this guard before operations that may consume significant disk space, such as:
/// - Bulk loading RDF data
/// - Creating large indexes
/// - Running stress tests
/// </para>
/// <para>
/// The guard checks available space on the drive containing the specified path
/// and throws if space falls below the minimum threshold.
/// </para>
/// </remarks>
public static class DiskSpaceGuard
{
    /// <summary>
    /// Default minimum free space: 1GB.
    /// </summary>
    public const long DefaultMinFreeBytes = 1L << 30; // 1GB

    /// <summary>
    /// Minimum free space for stress tests: 512MB.
    /// Stress tests are expected to consume space but should not fill the disk.
    /// </summary>
    public const long StressTestMinFreeBytes = 512L << 20; // 512MB

    /// <summary>
    /// Gets available free space on the drive containing the specified path.
    /// </summary>
    /// <param name="path">Path to check (file or directory).</param>
    /// <returns>Available free space in bytes, or -1 if unable to determine.</returns>
    public static long GetAvailableSpace(string path)
    {
        try
        {
            // Get the root of the path
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);

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

    /// <summary>
    /// Checks if there is sufficient disk space and throws if not.
    /// </summary>
    /// <param name="path">Path to check (file or directory).</param>
    /// <param name="requiredBytes">Minimum required free space in bytes.</param>
    /// <param name="operationName">Name of the operation for error messages.</param>
    /// <exception cref="InsufficientDiskSpaceException">Thrown when disk space is below threshold.</exception>
    public static void EnsureSufficientSpace(string path, long requiredBytes, string operationName = "operation")
    {
        var available = GetAvailableSpace(path);

        if (available < 0)
        {
            // Unable to determine - proceed with warning
            return;
        }

        if (available < requiredBytes)
        {
            throw new InsufficientDiskSpaceException(
                path,
                requiredBytes,
                available,
                operationName);
        }
    }

    /// <summary>
    /// Checks if there is sufficient disk space using the default minimum (1GB).
    /// </summary>
    /// <param name="path">Path to check.</param>
    /// <param name="operationName">Name of the operation for error messages.</param>
    public static void EnsureSufficientSpace(string path, string operationName = "operation")
    {
        EnsureSufficientSpace(path, DefaultMinFreeBytes, operationName);
    }

    /// <summary>
    /// Checks if there is sufficient disk space for stress testing (512MB minimum).
    /// </summary>
    /// <param name="path">Path to check.</param>
    /// <param name="operationName">Name of the operation for error messages.</param>
    public static void EnsureSufficientSpaceForStressTest(string path, string operationName = "stress test")
    {
        EnsureSufficientSpace(path, StressTestMinFreeBytes, operationName);
    }

    /// <summary>
    /// Returns true if there is at least the specified amount of free space.
    /// </summary>
    /// <param name="path">Path to check.</param>
    /// <param name="requiredBytes">Minimum required free space in bytes.</param>
    /// <returns>True if sufficient space is available or unable to determine.</returns>
    public static bool HasSufficientSpace(string path, long requiredBytes)
    {
        var available = GetAvailableSpace(path);
        return available < 0 || available >= requiredBytes;
    }

    /// <summary>
    /// Creates a space-limited scope that tracks disk usage and enforces a maximum.
    /// </summary>
    /// <param name="path">Path to monitor.</param>
    /// <param name="maxUsageBytes">Maximum bytes this operation should consume.</param>
    /// <param name="operationName">Name of the operation.</param>
    /// <returns>A disposable scope that tracks usage.</returns>
    public static SpaceLimitedScope CreateScope(string path, long maxUsageBytes, string operationName = "operation")
    {
        return new SpaceLimitedScope(path, maxUsageBytes, operationName);
    }
}

/// <summary>
/// Exception thrown when disk space is insufficient for an operation.
/// </summary>
public sealed class InsufficientDiskSpaceException : IOException
{
    public string Path { get; }
    public long RequiredBytes { get; }
    public long AvailableBytes { get; }
    public string OperationName { get; }

    public InsufficientDiskSpaceException(string path, long requiredBytes, long availableBytes, string operationName)
        : base(FormatMessage(path, requiredBytes, availableBytes, operationName))
    {
        Path = path;
        RequiredBytes = requiredBytes;
        AvailableBytes = availableBytes;
        OperationName = operationName;
    }

    private static string FormatMessage(string path, long required, long available, string operation)
    {
        return $"Insufficient disk space for {operation}. " +
               $"Required: {ByteFormatter.Format(required)}, Available: {ByteFormatter.Format(available)}. " +
               $"Path: {path}";
    }
}

/// <summary>
/// A scope that tracks disk usage and can enforce limits during an operation.
/// </summary>
public sealed class SpaceLimitedScope : IDisposable
{
    private readonly string _path;
    private readonly long _maxUsageBytes;
    private readonly string _operationName;
    private readonly long _startingFreeSpace;
    private bool _disposed;

    internal SpaceLimitedScope(string path, long maxUsageBytes, string operationName)
    {
        _path = path;
        _maxUsageBytes = maxUsageBytes;
        _operationName = operationName;
        _startingFreeSpace = DiskSpaceGuard.GetAvailableSpace(path);
    }

    /// <summary>
    /// Gets the estimated bytes consumed since scope creation.
    /// </summary>
    public long EstimatedBytesConsumed
    {
        get
        {
            var current = DiskSpaceGuard.GetAvailableSpace(_path);
            if (_startingFreeSpace < 0 || current < 0)
                return 0;
            return Math.Max(0, _startingFreeSpace - current);
        }
    }

    /// <summary>
    /// Gets the remaining bytes allowed in this scope.
    /// </summary>
    public long RemainingBytes => Math.Max(0, _maxUsageBytes - EstimatedBytesConsumed);

    /// <summary>
    /// Checks if the limit has been exceeded and throws if so.
    /// Call this periodically during long-running operations.
    /// </summary>
    /// <exception cref="DiskSpaceLimitExceededException">Thrown when the limit is exceeded.</exception>
    public void CheckLimit()
    {
        var consumed = EstimatedBytesConsumed;
        if (consumed > _maxUsageBytes)
        {
            throw new DiskSpaceLimitExceededException(
                _operationName,
                _maxUsageBytes,
                consumed);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // Just marks the scope as ended - no cleanup needed
    }
}

/// <summary>
/// Exception thrown when a disk space limit is exceeded during an operation.
/// </summary>
public sealed class DiskSpaceLimitExceededException : IOException
{
    public string OperationName { get; }
    public long LimitBytes { get; }
    public long ConsumedBytes { get; }

    public DiskSpaceLimitExceededException(string operationName, long limitBytes, long consumedBytes)
        : base($"Disk space limit exceeded for {operationName}. " +
               $"Limit: {ByteFormatter.Format(limitBytes)}, Consumed: {ByteFormatter.Format(consumedBytes)}")
    {
        OperationName = operationName;
        LimitBytes = limitBytes;
        ConsumedBytes = consumedBytes;
    }
}
