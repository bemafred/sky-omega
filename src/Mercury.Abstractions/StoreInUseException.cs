// StoreInUseException.cs
// Exception thrown when a store is already locked by another process.
// No external dependencies, only BCL.
// .NET 10 / C# 14

using System.IO;

namespace SkyOmega.Mercury.Abstractions;

/// <summary>
/// Exception thrown when a store directory is already locked by another process.
/// </summary>
public sealed class StoreInUseException : IOException
{
    /// <summary>
    /// Path of the store directory that is locked.
    /// </summary>
    public string StorePath { get; }

    /// <summary>
    /// Process ID of the owner, or null if it could not be determined.
    /// </summary>
    public int? OwnerProcessId { get; }

    public StoreInUseException(string storePath, int? ownerProcessId)
        : base(FormatMessage(storePath, ownerProcessId))
    {
        StorePath = storePath;
        OwnerProcessId = ownerProcessId;
    }

    private static string FormatMessage(string storePath, int? ownerPid)
    {
        var pidInfo = ownerPid.HasValue ? $" (PID {ownerPid.Value})" : "";
        return $"Store is in use by another process{pidInfo}: {storePath}";
    }
}
