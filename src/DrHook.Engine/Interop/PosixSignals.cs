// Minimal Posix signal-send primitive for substrate RequestExit (ADR-008 / finding 68).
// Sends SIGTERM (or any signal) to a target by PID via libc.kill. macOS + Linux.
// Windows path is NotImplemented today — Phase 0.1 / ADR-007 Phase 9 will add
// GenerateConsoleCtrlEvent-based equivalent.

using System;
using System.Runtime.InteropServices;

namespace SkyOmega.DrHook.Engine.Interop;

internal static class PosixSignals
{
    /// <summary>POSIX SIGTERM — soft (catchable, ignorable) termination request.
    /// Standard graceful-shutdown signal used by docker stop, systemd stop, etc.</summary>
    public const int SIGTERM = 15;

    /// <summary>POSIX SIGINT — Ctrl+C equivalent. Catchable via .NET
    /// <c>Console.CancelKeyPress</c>.</summary>
    public const int SIGINT = 2;

    /// <summary>POSIX SIGKILL — non-catchable, kernel-mandated termination.
    /// Cannot be intercepted by user code. Process.Kill on Unix uses this.</summary>
    public const int SIGKILL = 9;

    /// <summary>Send a POSIX signal to a target by PID. Returns 0 on success, -1 on
    /// failure (caller can read <see cref="Marshal.GetLastWin32Error"/> for errno).
    /// Throws <see cref="PlatformNotSupportedException"/> on Windows — Windows uses
    /// a different mechanism (GenerateConsoleCtrlEvent for soft-signal-equivalent,
    /// TerminateProcess for hard kill, which Process.Kill already wraps).
    /// Substrate consumers (RequestExit on Unix) check
    /// <see cref="RuntimeInformation.IsOSPlatform"/> before calling.</summary>
    public static int Kill(int pid, int signal)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "PosixSignals.Kill is Unix-only. Windows substrate path is deferred to " +
                "ADR-007 Phase 9 (Windows uses GenerateConsoleCtrlEvent for soft signals " +
                "and Process.Kill / TerminateProcess for hard kill).");
        }
        return kill(pid, signal);
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static extern int kill(int pid, int sig);

    /// <summary>POSIX EINTR — a blocking syscall was interrupted by a signal and should be retried.
    /// Same value (4) on Linux and macOS.</summary>
    private const int EINTR = 4;

    /// <summary>Block until child <paramref name="pid"/> terminates, reaping it via <c>waitpid</c> so
    /// it does not linger as a zombie. Needed for a target the substrate launched then DETACHED and
    /// left running (F-010-2): detaching the debugger does not change the OS parent-child link, and
    /// the target was acquired via <see cref="System.Diagnostics.Process.GetProcessById(int)"/> (not
    /// <c>Process.Start</c>), so the runtime's child reaper does not track it — without this an exited
    /// detached target zombies under the (long-lived) debugger process (dogfood finding 2026-06-03).
    /// Intended to run on a dedicated background thread; returns when the target exits (reaped here)
    /// or was already reaped elsewhere (<c>ECHILD</c>) — immediate return if the target is already
    /// gone. Unix-only — Windows has no zombie/waitpid model (closing the process handle suffices).</summary>
    public static void ReapChild(int pid)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException(
                "PosixSignals.ReapChild is Unix-only (Windows has no zombie/waitpid model).");

        while (true)
        {
            int rc = waitpid(pid, out int _, 0);
            if (rc >= 0) return;                                 // reaped the corpse (rc == pid) — no zombie
            if (Marshal.GetLastWin32Error() == EINTR) continue;  // interrupted by a signal — re-enter the wait
            return;                                              // ECHILD (already reaped) or other — done, best-effort
        }
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "waitpid")]
    private static extern int waitpid(int pid, out int status, int options);
}
