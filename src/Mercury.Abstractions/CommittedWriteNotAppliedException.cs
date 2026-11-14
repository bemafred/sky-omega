// CommittedWriteNotAppliedException.cs
// Exception thrown when a store could not apply a write its log has committed.
// No external dependencies, only BCL.
// .NET 10 / C# 14

using System;

namespace SkyOmega.Mercury.Abstractions;

/// <summary>
/// An internal error of the store. A write was committed to the log, and applying it to the indexes failed
/// (ADR-061 M7). The write is not lost: the log holds it, and opening the store again applies it.
/// </summary>
/// <remarks>
/// <para>
/// The store that met the failure stops. It answers no read, takes no write and makes no checkpoint, and it closes
/// without one, so the log keeps the write. Every later call on that store throws this exception again, with the
/// first failure as <see cref="Exception.InnerException"/>.
/// </para>
/// <para>
/// Any other exception from a write means the write was not committed and nothing changed. After this one the write
/// holds once the store has been opened again. Repeating the write is safe in both cases.
/// </para>
/// <para>
/// A store whose log cannot be applied when it is opened throws this exception from its constructor, and the log is
/// kept as it is.
/// </para>
/// </remarks>
public sealed class CommittedWriteNotAppliedException : Exception
{
    /// <summary>
    /// Path of the store directory.
    /// </summary>
    public string StorePath { get; }

    public CommittedWriteNotAppliedException(string storePath, Exception cause)
        : base(FormatMessage(storePath, cause), cause)
    {
        StorePath = storePath;
    }

    private static string FormatMessage(string storePath, Exception cause) =>
        $"Internal error in the Mercury store at '{storePath}': a write was committed to the log, but applying it to " +
        $"the indexes failed with {cause.GetType().Name}: {cause.Message} " +
        "The write is not lost: the log holds it, and it is applied when the store is opened again. " +
        "Until then this store answers no read, takes no write and makes no checkpoint. " +
        "Close the store and open it again. If opening fails in the same way, the log is kept and the cause must be fixed.";
}
