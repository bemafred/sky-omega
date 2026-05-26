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
}
