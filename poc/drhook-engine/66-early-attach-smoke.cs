#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
#:property TargetFramework=net10.0
//
// DrHook.Engine probe 66 — attaching before the target's runtime debugger has started (finding 99) =============
//
// Observed (omega-ubuntu, Linux arm64, 2026-10-27): the integration test host died with SIGSEGV in 3 cores, all in
// VSTest-testhost attach tests, which attach as soon as vstest.console prints the testhost PID (finding 91: before
// the testhost is up). Each core: mscordbi's RC event thread runs SendAttachProcessWorkItem::Do, and
// CordbRCEventThread::SendIPCEvent dereferences CordbProcess::m_pEventChannel = NULL. The process is not detached,
// terminated or neutered; its DAC is initialized; m_initialized = 0.
//
// Runtime source (v10.0.0): on transport platforms (Linux, macOS) Cordb::DebugActiveProcessCommon calls
// QueueManagedAttach UNCONDITIONALLY after the attach; elsewhere the attach is queued only when CordbProcess::Init
// found the left side initialized and opened the IPC channel. Debugger::Startup marks the left side initialized
// (the inlined RaiseStartupNotification) on the instruction right after DbgTransportSession::Init. If the attach's
// transport session opens in between, Init opens no channel and the queued attach sends on a null channel.
//
// Hypothesis: an attach whose transport session opens while the target's left side is not yet initialized kills
// the debugger host with SIGSEGV; DrHook's readiness guard (wait for the ".NET Debugger" helper thread, started
// right after the mark) refuses such an attach while the target stays there, and lets it through once it moves on.
//
// Construction: the probe runs itself as a child per attempt, so a host crash is an exit code. The child starts the
// target (not as its own child; finding 87), attaches with DebugSession.AttachAndOwn, Pauses, waits 5 s for the
// stop, and disposes. Modes:
//   unready  — the target's TMPDIR is read-only: its transport cannot create its FIFOs, so Debugger::Startup returns
//              before the mark and the runtime runs on with no debugger. (No session can open: mscordbi itself
//              times out with CORDBG_E_TIMEOUT, so this mode shows the guard's refusal, not the crash.)
//   held     — Linux with gdb: the target runs under gdb in non-stop mode with a breakpoint on the marking
//              instruction, so ONLY the startup thread stops, after the transport is up; the transport's own thread
//              keeps running and the session can open. PROBE66_OFFSET is that instruction's offset in libcoreclr
//              (default 0x29d9a8: Ubuntu 26.04's arm64 10.0.13 build, from its debug symbols; another build needs
//              its own). The attach lands with the left side not initialized for as long as the thread is held.
//   released — as held, but the startup thread is let go 2 s into the attach.
//   ready    — a normal target, attached 1 s after start.
//
// Observed 2026-10-27, omega-ubuntu: earlier constructions missed the state. Attaching by timing from spawn found
// the FIFOs already there (the window is ~1 ms after libcoreclr loads, shorter than a cold attach), and freezing the
// whole target with SIGSTOP stopped its transport thread too, so DebugActiveProcess timed out instead.
// Without the guard: held 3/3 and released 3/3 SIGSEGV of the host, unready 3/3 CORDBG_E_TIMEOUT, ready 3/3 stopped.
// With the guard: held 3/3 refused, released 3/3 stopped, unready 3/3 refused, ready 3/3 stopped, no crash.
//
// Result codes: 0 guarded (no host crash; unready and held refused; released and ready stopped); 1 otherwise;
// 2 target build failed. Child exit codes: 0 stopped, 3 no stop, 4 setup failed, 7 attach refused, 139 SIGSEGV.
//
// Usage (from poc/drhook-engine):  dotnet run --no-cache 66-early-attach-smoke.cs [runs]

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SkyOmega.DrHook.Engine;

if (OperatingSystem.IsWindows()) { Console.WriteLine("SKIP: Windows has no debug transport FIFOs (the attach is not queued unconditionally there)."); return 0; }
if (args.Length == 3 && args[0] == "child")
    return args[1] is "held" or "released" ? ChildHeld(args[1] == "released", args[2]) : Child(args[1] == "unready", args[2]);

int runs = args.Length > 0 ? int.Parse(args[0]) : 5;
string? dll = BuildTarget(Path.GetFullPath("66-early-attach-target.cs"));
if (dll is null) { Console.WriteLine("FAIL(2): could not build 66-early-attach-target.cs"); return 2; }
Console.WriteLine($"platform: {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}");

var tally = new Dictionary<string, int>();
string[] modes = OperatingSystem.IsLinux() && File.Exists("/usr/bin/gdb") ? ["unready", "held", "released", "ready"] : ["unready", "ready"];
var codes = new Dictionary<string, List<int>>();
foreach (string mode in modes)
{
    for (int i = 1; i <= runs; i++)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("child");
        psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add(dll);
        psi.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        using Process child = Process.Start(psi)!;
        string output = child.StandardOutput.ReadToEnd() + child.StandardError.ReadToEnd();
        child.WaitForExit();
        // A crashed child cannot end its target; do it here.
        foreach (string line in output.Split('\n').Where(l => l.StartsWith("child: target=", StringComparison.Ordinal)))
            try { Process.GetProcessById(int.Parse(line["child: target=".Length..].Trim())).Kill(); } catch (ArgumentException) { }
        string outcome = child.ExitCode switch { 0 => "stopped", 3 => "NO-STOP", 4 => "setup-failed", 7 => "refused", 139 => "SIGSEGV", int c => $"exit-{c}" };
        if (!codes.TryGetValue(mode, out List<int>? list)) codes[mode] = list = [];
        list.Add(child.ExitCode);
        string key = $"{mode} {outcome}";
        tally[key] = tally.GetValueOrDefault(key) + 1;
        Console.WriteLine($"{mode} run {i}: {outcome} | {string.Join(" ", output.Split('\n').Where(l => l.StartsWith("child:", StringComparison.Ordinal) || l.Contains("Exception", StringComparison.Ordinal)).Select(l => l.Trim()))}");
    }
}
Console.WriteLine(string.Join("; ", tally.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}: {kv.Value}")));
// With the readiness guard (finding 99) nothing crashes: an attach to a target whose debugger is not started is
// refused (unready, held), and one whose debugger starts during the wait goes through (released, ready).
bool All(string mode, int code) => !codes.ContainsKey(mode) || codes[mode].All(c => c == code);
bool crashed = codes.Values.Any(l => l.Contains(139));
bool confirmed = !crashed && All("unready", 7) && All("held", 7) && All("released", 0) && All("ready", 0);
Console.WriteLine(confirmed
    ? "RESULT(0): guarded — no host crash; unready and held attaches refused, released and ready attaches stopped"
    : $"RESULT(1): {(crashed ? "HOST CRASH observed — the attach landed before the target's debugger started" : "unexpected outcome")} (see the tally)");
return confirmed ? 0 : 1;

[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
static int Child(bool unready, string dll)
{
    var psi = new ProcessStartInfo("bash") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    psi.ArgumentList.Add("-c");
    psi.ArgumentList.Add("dotnet \"$0\" >/dev/null 2>&1 </dev/null & echo $!; wait");
    psi.ArgumentList.Add(dll);
    if (unready)
    {
        string readOnly = Path.Combine(Path.GetTempPath(), "probe66-readonly");
        Directory.CreateDirectory(readOnly);
        File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        psi.Environment["TMPDIR"] = readOnly;
    }
    using Process shell = Process.Start(psi)!;
    int pid = int.Parse(shell.StandardOutput.ReadLine()!.Trim());
    Console.WriteLine($"child: target={pid}");
    Console.Out.Flush();
    Thread.Sleep(1000);
    Console.WriteLine($"child: fifos={(PipesExist(pid) ? "present" : "absent")}");
    Console.Out.Flush();

    DebugSession session;
    try { session = DebugSession.AttachAndOwn(pid, new Sink()); }
    catch (DebugEngineException e) { Console.WriteLine($"child: attach refused: {e.Message}"); return 7; }
    Console.WriteLine("child: attached");
    Console.Out.Flush();
    using (session)
    {
        session.Pause();
        StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(5));
        Console.WriteLine($"child: stop={stop?.Reason.ToString() ?? "none"}");
        Console.Out.Flush();
        return stop is null ? 3 : 0;
    }
}

// "held": the target runs under gdb in non-stop mode. When libcoreclr loads, a breakpoint is set on the instruction in
// Debugger::Startup that sets m_fLeftSideInitialized (the inlined RaiseStartupNotification), right after the debug
// transport is up. Only the startup thread stops there; the transport's own thread keeps running, so the debugger's
// handshake can complete while the left side still reads as not initialized. The attach then lands in that state.
// PROBE66_OFFSET is that instruction's offset in this libcoreclr build (from its debug symbols; see finding 99).
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
static int ChildHeld(bool release, string dll)
{
    string offset = Environment.GetEnvironmentVariable("PROBE66_OFFSET") ?? "0x29d9a8";
    string mark = Path.Combine(Path.GetTempPath(), $"probe66-held-{Environment.ProcessId}");
    string script = Path.Combine(Path.GetTempPath(), $"probe66-hold-{Environment.ProcessId}.py");
    File.Delete(mark);
    File.WriteAllText(script, $$"""
        import gdb
        OFF = {{offset}}
        armed = [False]
        def arm(base):
            armed[0] = True
            gdb.execute("break *%#x" % (base + OFF))
            gdb.execute("set stop-on-solib-events 0")
            gdb.execute("continue &")
        def on_stop(ev):
            if isinstance(ev, gdb.BreakpointEvent) and armed[0]:
                with open("{{mark}}", "w") as f:
                    f.write("%d %d\n" % (gdb.selected_inferior().pid, gdb.selected_thread().ptid[1]))
                return
            if armed[0]:
                return
            pid = gdb.selected_inferior().pid
            for line in open("/proc/%d/maps" % pid):
                parts = line.split()
                if len(parts) >= 6 and parts[5].endswith("/libcoreclr.so") and parts[2] == "00000000":
                    base = int(parts[0].split("-")[0], 16)
                    gdb.post_event(lambda: arm(base))
                    return
            gdb.post_event(lambda: gdb.execute("continue &"))
        gdb.events.stop.connect(on_stop)
        """);
    var psi = new ProcessStartInfo("gdb") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string a in new[] { "-q", "-nx", "dotnet" }) psi.ArgumentList.Add(a);
    using Process gdb = Process.Start(psi)!;
    gdb.OutputDataReceived += (_, _) => { };
    gdb.ErrorDataReceived += (_, _) => { };
    gdb.BeginOutputReadLine();
    gdb.BeginErrorReadLine();
    gdb.StandardInput.Write($"set pagination off\nset confirm off\nset non-stop on\nhandle all nostop noprint pass\nset stop-on-solib-events 1\nsource {script}\nrun \"{dll}\" >/dev/null 2>&1 </dev/null &\n");
    gdb.StandardInput.Flush();
    Console.WriteLine($"child: gdb={gdb.Id}");
    Console.Out.Flush();

    var waited = Stopwatch.StartNew();
    while (!File.Exists(mark) || File.ReadAllText(mark).Length == 0)
    {
        if (waited.Elapsed > TimeSpan.FromSeconds(30)) { Console.WriteLine("child: breakpoint never hit"); gdb.Kill(); return 4; }
        Thread.Sleep(20);
    }
    string[] held = File.ReadAllText(mark).Split(' ', StringSplitOptions.TrimEntries);
    int pid = int.Parse(held[0]);
    Console.WriteLine($"child: target={pid}");
    Console.WriteLine($"child: held startup thread {held[1]}; fifos={(PipesExist(pid) ? "present" : "absent")}");
    Console.Out.Flush();

    if (release)
    {
        // Let the startup thread go 2 s into the attach: a guarded attach is still waiting, and must then proceed.
        new Thread(() =>
        {
            Thread.Sleep(2000);
            lock (gdb) { gdb.StandardInput.Write("delete\ncontinue -a &\n"); gdb.StandardInput.Flush(); }
            Console.WriteLine("child: released the startup thread");
            Console.Out.Flush();
        }) { IsBackground = true }.Start();
    }

    int code;
    try
    {
        DebugSession session = DebugSession.AttachAndOwn(pid, new Sink());
        Console.WriteLine("child: attached");
        Console.Out.Flush();
        using (session)
        {
            session.Pause();
            StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(5));
            Console.WriteLine($"child: stop={stop?.Reason.ToString() ?? "none"}");
            Console.Out.Flush();
            code = stop is null ? 3 : 0;
        }
    }
    catch (DebugEngineException e) { Console.WriteLine($"child: attach refused: {e.Message}"); code = 7; }
    lock (gdb) { gdb.StandardInput.Write("kill\nquit\n"); gdb.StandardInput.Flush(); }
    if (!gdb.WaitForExit(5000)) gdb.Kill();
    File.Delete(mark);
    File.Delete(script);
    return code;
}

// The runtime names its debug transport FIFOs clr-debug-pipe-<pid>-<disambiguation key>-in/out in its temp directory
// (the default one here; an unready target's TMPDIR is the read-only directory, where none can exist).
static bool PipesExist(int pid)
{
    string prefix = $"clr-debug-pipe-{pid}-";
    string tmp = Path.GetTempPath();
    return Directory.EnumerateFiles(tmp, prefix + "*-in").Any() && Directory.EnumerateFiles(tmp, prefix + "*-out").Any();
}

static string? BuildTarget(string csPath)
{
    var psi = new ProcessStartInfo("dotnet", $"build \"{csPath}\" -c Debug")
    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    using var p = Process.Start(psi)!;
    string outp = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0) { Console.Error.WriteLine(outp); return null; }
    foreach (var line in outp.Split('\n'))
    {
        int arrow = line.IndexOf("-> ", StringComparison.Ordinal);
        if (arrow >= 0)
        {
            string path = line[(arrow + 3)..].Trim();
            if (path.EndsWith(".dll", StringComparison.Ordinal) && File.Exists(path)) return path;
        }
    }
    return null;
}

sealed class Sink : IDebugEventSink
{
    public void OnEvent(string name) { }
}
