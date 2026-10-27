// Which thread names a target process has — how the substrate tells that a target's runtime debugger has started,
// the precondition for attaching on the debug-transport platforms (finding 99).
//
// On Linux and macOS mscordbi queues its managed attach unconditionally (Cordb::DebugActiveProcessCommon). If the
// target's debug transport is up but its debugger is not yet marked initialized (Debugger::Startup, between
// DbgTransportSession::Init and RaiseStartupNotification), CordbProcess::Init opens no IPC channel, and the queued
// attach later sends on it: SIGSEGV of the DEBUGGER host. The runtime starts its debugger helper thread right after
// setting that mark (DebuggerRCThread::Start, on the same startup thread), and the helper names itself
// ".NET Debugger". A thread of that name therefore proves the target is past the window.
//
//   Linux:  /proc/<pid>/task/<tid>/comm (the kernel keeps 15 bytes of a name; ".NET Debugger" is 13).
//   macOS:  proc_pidinfo(PROC_PIDLISTTHREADS), then proc_pidinfo(PROC_PIDTHREADINFO) per thread — unentitled for a
//           same-user target, like MappedFiles' region walk.
//
// Layout basis (verified 2026-10-27 against the macOS 27 SDK <sys/proc_info.h> by compiling a probe):
// PROC_PIDLISTTHREADS = 6, PROC_PIDTHREADINFO = 5, sizeof(struct proc_threadinfo) = 112, pth_name @ 48 (char[64]).

using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class TargetThreads
{
    /// <summary>The name the runtime's debugger helper thread gives itself (rcthread.cpp, ThreadProcStatic).</summary>
    public const string DebuggerHelperThreadName = ".NET Debugger";

    private const int PROC_PIDLISTTHREADS = 6;
    private const int PROC_PIDTHREADINFO = 5;
    private const int ThreadInfoSize = 112;
    private const int ThreadNameOffset = 48;
    private const int ThreadNameLength = 64;

    /// <summary>Upper bound on threads listed per lookup. A process with more is read up to this many; a runtime
    /// starts its debugger helper among its first dozen threads.</summary>
    private const int MaxThreads = 4096;

    /// <summary>True if a thread of <paramref name="pid"/> is named exactly <paramref name="name"/>; false if none
    /// is, the target is gone, or its threads cannot be read. Linux and macOS only.</summary>
    public static bool HasThreadNamed(int pid, string name)
    {
        if (OperatingSystem.IsMacOS()) return HasThreadNamedMacOS(pid, name);
        if (OperatingSystem.IsLinux()) return HasThreadNamedLinux(pid, name);
        throw new PlatformNotSupportedException("TargetThreads reads thread names on Linux and macOS only.");
    }

    private static bool HasThreadNamedLinux(int pid, string name)
    {
        try
        {
            foreach (string task in Directory.EnumerateDirectories($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/task"))
            {
                try
                {
                    if (File.ReadAllText(Path.Combine(task, "comm")).TrimEnd('\n') == name) return true;
                }
                catch (IOException) { /* thread exited */ }
            }
        }
        catch (IOException) { /* target exited */ }
        catch (UnauthorizedAccessException) { /* not ours to read */ }
        return false;
    }

    private static bool HasThreadNamedMacOS(int pid, string name)
    {
        ulong[] handles = new ulong[MaxThreads];
        int bytes;
        fixed (ulong* list = handles)
            bytes = LibProc.proc_pidinfo(pid, PROC_PIDLISTTHREADS, 0, (byte*)list, handles.Length * sizeof(ulong));
        if (bytes <= 0) return false;

        byte* info = stackalloc byte[ThreadInfoSize];
        for (int i = 0; i < bytes / sizeof(ulong); i++)
        {
            if (LibProc.proc_pidinfo(pid, PROC_PIDTHREADINFO, handles[i], info, ThreadInfoSize) < ThreadInfoSize) continue;
            var threadName = new ReadOnlySpan<byte>(info + ThreadNameOffset, ThreadNameLength);
            int length = threadName.IndexOf((byte)0);
            if (length < 0) length = ThreadNameLength;
            if (Encoding.UTF8.GetString(threadName[..length]) == name) return true;
        }
        return false;
    }
}
