// Windows' exit request (ADR-016 6.4): CTRL+BREAK to a target's own process group, sent by this process.
//
// GenerateConsoleCtrlEvent reaches only processes on the sender's console, and a target DrHook launches has a console
// of its own (CREATE_NO_WINDOW, WindowsSpawn). So this process leaves its console, attaches to the target's, sends the
// event to the target's group, leaves that console, and returns to its parent's: for drhook-worker that is
// drhook-mcp's, the console it left (WorkerProcess.Start gives a worker none of its own). It then sets its standard
// handles back. This is finding 117's "worker way": on omega-windows the event reached the target 80 of 80, nothing
// reached the sender or its parent, no standard handle changed, and the sender was off its console 1.12 ms at most.
//
// Not established there: standard handles that are console handles (every one was a pipe), a sender with no console
// or alone on its console, a sender whose console is not its parent's, and a console event from the sender's own
// console while it is away.
//
// BCL + P/Invoke (kernel32) only.

using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SkyOmega.DrHook.Engine.Interop;

/// <summary>Whether CTRL+BREAK was sent, and what did not go as finding 117 measured it (null when everything did).</summary>
internal readonly record struct ConsoleBreakOutcome(bool Sent, string? Deviation);

internal static class WindowsConsoleBreak
{
    private const uint CTRL_BREAK_EVENT = 1;
    private const uint ATTACH_PARENT_PROCESS = unchecked((uint)-1);
    private const int STD_INPUT_HANDLE = -10, STD_OUTPUT_HANDLE = -11, STD_ERROR_HANDLE = -12;

    // A process is on one console at a time, so two sends at once would each take the other off its target's console.
    private static readonly object OneConsoleAtATime = new();

    /// <summary>Send CTRL+BREAK to the process group <paramref name="processGroupId"/>, the PID of a process created with
    /// <c>CREATE_NEW_PROCESS_GROUP</c>, from this process, by way of that process's console. The event is sent only once
    /// this process is on that console. Returns whether it was sent; this process is back on its parent's console
    /// either way, unless <see cref="ConsoleBreakOutcome.Deviation"/> says otherwise.</summary>
    public static ConsoleBreakOutcome SendTo(int processGroupId)
    {
        uint group = (uint)processGroupId;
        lock (OneConsoleAtATime)
        {
            nint stdIn = GetStdHandle(STD_INPUT_HANDLE), stdOut = GetStdHandle(STD_OUTPUT_HANDLE), stdErr = GetStdHandle(STD_ERROR_HANDLE);
            var deviations = new List<string>();
            bool hadConsole = FreeConsole();   // false when this process has no console: then there is none to return to
            bool sent = false;
            if (!AttachConsole(group))
                deviations.Add($"AttachConsole({group.ToString(CultureInfo.InvariantCulture)}) failed, error {Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture)}");
            else
            {
                sent = GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, group);
                if (!sent) deviations.Add($"GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, {group.ToString(CultureInfo.InvariantCulture)}) failed, error {Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture)}");
                if (!FreeConsole()) deviations.Add($"FreeConsole from the target's console failed, error {Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture)}");
            }
            if (hadConsole && !AttachConsole(ATTACH_PARENT_PROCESS))
                deviations.Add($"AttachConsole(ATTACH_PARENT_PROCESS) failed, error {Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture)}: this process is on no console now");
            SetStdHandle(STD_INPUT_HANDLE, stdIn);
            SetStdHandle(STD_OUTPUT_HANDLE, stdOut);
            SetStdHandle(STD_ERROR_HANDLE, stdErr);
            return new ConsoleBreakOutcome(sent, deviations.Count == 0 ? null : string.Join("; ", deviations));
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int stdHandle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int stdHandle, nint handle);
}
