using System;
using System.IO;
using System.Threading;

namespace SkyOmega.DrHook.Engine;

/// <summary>The observable half of a UI-liveness probe: a temp file the debugger creates and the debuggee's UI
/// thread deletes — <see cref="DebugSession.TryEvalPostLivenessJob"/> queues <c>File.Delete(Path)</c> on the UI
/// dispatcher. The file's disappearance means the UI thread drained its queue (alive); its persistence past the
/// deadline means it did not (hung — in managed or native code alike).
///
/// Why a file, not a runtime event: the verdict has to cover the window AFTER detach (when the 1-in-41
/// post-capture hang appeared), and an EventPipe observer cannot be opened while the debugger holds the target
/// suspended — the session start blocks until the target runs (observed 2026-10-03, probe 88), and opening it after
/// resume races the job. A file needs no observer: it persists until deleted, so nothing can be missed.
/// Same machine + same user by construction (the transport is local); a sandboxed debuggee that cannot see the
/// debugger's temp directory would read as hung — a false negative, never a false "alive".</summary>
public sealed class DispatcherDrainSentinel : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private DispatcherDrainSentinel(string path) => Path = path;

    /// <summary>Full path of the sentinel file — hand it to <see cref="DebugSession.TryEvalPostLivenessJob"/>.</summary>
    public string Path { get; }

    /// <summary>Create the sentinel for target <paramref name="pid"/> in the user's temp directory.</summary>
    public static DispatcherDrainSentinel Create(int pid)
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"drhook-ui-liveness-{pid}-{Guid.NewGuid():N}");
        File.WriteAllBytes(path, []);
        return new DispatcherDrainSentinel(path);
    }

    /// <summary>True once the debuggee has deleted the sentinel — now, or within <paramref name="timeout"/>.</summary>
    public bool WaitForDrain(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (File.Exists(Path))
        {
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(PollInterval);
        }
        return true;
    }

    /// <summary>Remove the sentinel if the debuggee never did (the hung case), so no temp file is left behind.</summary>
    public void Dispose()
    {
        try { File.Delete(Path); } catch (IOException) { /* best effort */ } catch (UnauthorizedAccessException) { /* best effort */ }
    }
}
