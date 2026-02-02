// InsufficientDiskSpaceException.cs
// Exception for disk space constraint violations.
// No external dependencies, only BCL.
// .NET 10 / C# 14

using System.IO;

namespace SkyOmega.Mercury.Abstractions;

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
    /// Configured minimum free space that must be maintained (0 if not applicable).
    /// </summary>
    public long MinimumFreeSpace { get; }

    /// <summary>
    /// Name of the operation that was attempted (null if not applicable).
    /// </summary>
    public string? OperationName { get; }

    /// <summary>
    /// Creates an exception for storage operations with minimum free space enforcement.
    /// </summary>
    public InsufficientDiskSpaceException(
        string path,
        long requestedBytes,
        long availableBytes,
        long minimumFreeSpace)
        : base(FormatStorageMessage(path, requestedBytes, availableBytes, minimumFreeSpace))
    {
        Path = path;
        RequestedBytes = requestedBytes;
        AvailableBytes = availableBytes;
        MinimumFreeSpace = minimumFreeSpace;
        OperationName = null;
    }

    /// <summary>
    /// Creates an exception for named operations with required space.
    /// </summary>
    public InsufficientDiskSpaceException(
        string path,
        long requiredBytes,
        long availableBytes,
        string operationName)
        : base(FormatOperationMessage(path, requiredBytes, availableBytes, operationName))
    {
        Path = path;
        RequestedBytes = requiredBytes;
        AvailableBytes = availableBytes;
        MinimumFreeSpace = 0;
        OperationName = operationName;
    }

    private static string FormatStorageMessage(
        string path,
        long requestedBytes,
        long availableBytes,
        long minimumFreeSpace)
    {
        var afterGrowth = availableBytes - requestedBytes;
        return $"Storage operation refused: growing '{System.IO.Path.GetFileName(path)}' by {ByteFormatter.Format(requestedBytes)} " +
               $"would leave only {ByteFormatter.Format(afterGrowth)} free (minimum required: {ByteFormatter.Format(minimumFreeSpace)}). " +
               $"Current available: {ByteFormatter.Format(availableBytes)}.";
    }

    private static string FormatOperationMessage(
        string path,
        long required,
        long available,
        string operation)
    {
        return $"Insufficient disk space for {operation}. " +
               $"Required: {ByteFormatter.Format(required)}, Available: {ByteFormatter.Format(available)}. " +
               $"Path: {path}";
    }
}
