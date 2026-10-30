#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
#:property TargetFramework=net10.0
//
// DrHook.Engine probe 100 — a read of target memory that the target can never answer (finding 100) =================
//
// Observed (GitHub macOS runner, 2026-10-30, run 41068258696): the DrHook integration-test host hung in an attach, in
// CordbProcess::Init -> ForceDacFlush -> DacDbiInterfaceImpl::FlushCache, waiting on the DAC's mutex. Inside the DAC
// was the previous session's Win32 event thread, reading target memory over the debugger transport
// (DbgTransportSession::ReadMemory -> SendRequestMessageAndWait), for a target that was dead; its transport thread was
// in TwoWayPipe::Connect -> open(), reconnecting to the dead target's pipe. Every later attach and launch hung.
//
// Runtime source (release/10.0): on macOS a read goes over the transport when task_for_pid fails. The read waits with
// no timeout for the reply or the session's process-exit handle.
//
// Hypothesis: a read of target memory over the transport waits until the target answers or the debugger handles the
// target's exit. mscordbi's Win32 event thread both dispatches the target's events (reading its memory to do it) and
// handles its exit. So a read made by any other thread is released when the target dies, but a read made by the Win32
// event thread itself, mid-dispatch, never returns: nothing else handles the exit. It holds the DAC's lock, so a later
// attach in the same process hangs, and the dead target's pipes are never removed.
//
// Construction: one child process per attempt, so a hung child is ended from outside. The child starts the target
// under bash (not as its own child; finding 87), attaches with DebugSession.AttachAndOwn, waits for the Break the
// target raises each round once a debugger is attached, and freezes the target with SIGSTOP: its runtime cannot answer
// a read then. A second thread reads the stopped thread's stack and locals. Modes:
//   control — after 3 s, SIGCONT: the read must then complete. Shows the read waits on the target, not on
//             anything else.
//   kill    — after 3 s, SIGKILL: does the read ever return? Then start a second target and attach to it.
//   flood-kill (macOS) — the target starts and joins threads without pause, so the Win32 event thread is always
//             dispatching CreateThread and ExitThread. The child freezes the target with SIGSTOP at random moments and
//             samples itself (sample) until the Win32 event thread is caught inside DbgTransportSession::ReadMemory,
//             then kills the target: does that thread ever leave the read, are the pipes removed, does a second
//             attach hang?
// A read that returns while the target is frozen did not go over the transport (task_for_pid succeeded: direct
// reads), and the mode's outcome is no-wait.
//
// Observed 2026-10-30, omega (macOS 27, arm64): control 3/3 completed after SIGCONT and kill 3/3 returned within
// ~20 ms of the kill (the dead target's pipes removed), refuting the first form of the hypothesis (any pending read
// never returns). With flood-kill, two batches: control 5/5, kill 5/5, flood-kill 5/5 (the Win32 event thread caught
// in a read on the first or second attempt; still in it 10 s after the kill; pipes left; the second attach pending
// after 30 s). RESULT(0) both times.
//
// Result codes: 0 confirmed (control completed; kill returned; flood-kill: the Win32 event thread stayed in the read,
// the pipes stayed, the second attach hung); 1 otherwise; 2 target build failed.
// Child results: control-completed, kill-read-returned, kill-orphaned-and-attach-hung, kill-orphaned-attach-ok,
// flood-orphaned-and-attach-hung, flood-orphaned-attach-ok, flood-read-returned, flood-not-caught, no-wait, no-stop,
// setup-failed.
//
// Usage (from poc/drhook-engine):  dotnet run --no-cache 100-orphaned-read-smoke.cs [runs]

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

if (OperatingSystem.IsWindows()) { Console.WriteLine("SKIP: Windows has no debug transport pipes; reads are direct."); return 0; }
if (args.Length == 3 && args[0] == "child") return args[1] == "flood-kill" ? ChildFlood(args[2]) : Child(args[1], args[2]);

int runs = args.Length > 0 ? int.Parse(args[0]) : 3;
string? dll = BuildTarget(Path.GetFullPath("100-orphaned-read-target.cs"));
if (dll is null) { Console.WriteLine("FAIL(2): could not build 100-orphaned-read-target.cs"); return 2; }
Console.WriteLine($"platform: {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}");

var results = new Dictionary<string, List<string>>();
foreach (string mode in OperatingSystem.IsMacOS() ? new[] { "control", "kill", "flood-kill" } : new[] { "control", "kill" })
{
    for (int i = 1; i <= runs; i++)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("child");
        psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add(dll);
        using Process child = Process.Start(psi)!;
        child.ErrorDataReceived += (_, _) => { };
        child.BeginErrorReadLine();

        // Read the child's lines until it reports its result; a child whose threads are hung may not exit by itself.
        var lines = new List<string>();
        string result = "no-result";
        var reader = new Thread(() =>
        {
            string? line;
            while ((line = child.StandardOutput.ReadLine()) is not null)
            {
                lock (lines) lines.Add(line);
                if (line.StartsWith("child: result=", StringComparison.Ordinal)) { result = line["child: result=".Length..].Trim(); return; }
            }
        }) { IsBackground = true };
        reader.Start();
        reader.Join(TimeSpan.FromSeconds(120));
        if (!child.WaitForExit(5000)) child.Kill(entireProcessTree: true);
        lock (lines)
        {
            foreach (string line in lines.Where(l => l.StartsWith("child: target=", StringComparison.Ordinal) || l.StartsWith("child: second=", StringComparison.Ordinal)))
                try { Process.GetProcessById(int.Parse(line.Split('=')[1].Trim())).Kill(); } catch (ArgumentException) { }
            Console.WriteLine($"{mode} run {i}: {result}");
            foreach (string line in lines.Where(l => l.StartsWith("child:", StringComparison.Ordinal) && !l.StartsWith("child: result=", StringComparison.Ordinal)))
                Console.WriteLine($"    {line["child: ".Length..]}");
        }
        if (!results.TryGetValue(mode, out List<string>? list)) results[mode] = list = [];
        list.Add(result);
    }
}

Console.WriteLine(string.Join("; ", results.SelectMany(kv => kv.Value.GroupBy(r => r).Select(g => $"{kv.Key} {g.Key}: {g.Count()}"))));
bool confirmed = results["control"].All(r => r == "control-completed") && results["kill"].All(r => r == "kill-read-returned")
    && (!results.ContainsKey("flood-kill") || results["flood-kill"].All(r => r == "flood-orphaned-and-attach-hung"));
Console.WriteLine(confirmed
    ? "RESULT(0): confirmed — a read waits for the target; another thread's read is released when the target dies, but the Win32 event thread's own read never returns, and a later attach in the same process hangs"
    : "RESULT(1): not confirmed as stated (see the outcomes above)");
return confirmed ? 0 : 1;

[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
static int Child(string mode, string dll)
{
    var clock = Stopwatch.StartNew();
    void Say(string text) { Console.WriteLine($"child: {text} [{clock.ElapsedMilliseconds} ms]"); Console.Out.Flush(); }
    int Result(string outcome) { Console.WriteLine($"child: result={outcome}"); Console.Out.Flush(); return 0; }

    int pid = StartTarget(dll);
    Console.WriteLine($"child: target={pid}");
    Console.Out.Flush();

    DebugSession session;
    try { session = DebugSession.AttachAndOwn(pid, new Sink()); }
    catch (Exception e) { Say($"attach failed: {e.Message}"); return Result("setup-failed"); }
    Say("attached");
    // A Pause stop has no stopped thread to read. The target breaks into the debugger each round once one is attached;
    // that Break's thread has managed frames and live locals. A Pause queued ahead of it is resumed.
    session.Pause();
    StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(5));
    while (stop?.Reason == StopReason.Pause)
    {
        session.Resume();
        stop = session.WaitForStop(TimeSpan.FromSeconds(5));
    }
    Say($"stop: {stop?.Reason.ToString() ?? "none"}");
    if (stop?.Reason != StopReason.Break) return Result("no-stop");

    kill(pid, SIGSTOP);
    Say("target frozen (SIGSTOP)");
    Thread.Sleep(200);

    string? readOutcome = null;
    var readDone = new ManualResetEventSlim();
    long readStartedAt = clock.ElapsedMilliseconds;
    new Thread(() =>
    {
        try
        {
            IReadOnlyList<string> frames = session.GetStackFrames();
            IReadOnlyList<LocalValue> locals = session.GetLocals(depth: 2);
            readOutcome = $"returned {frames.Count} frames, {locals.Count} locals";
        }
        catch (Exception e) { readOutcome = $"threw {e.GetType().Name}: {e.Message}"; }
        readDone.Set();
    }) { IsBackground = true, Name = "probe100-reader" }.Start();

    if (readDone.Wait(TimeSpan.FromSeconds(3)))
    {
        Say($"read finished while the target was frozen: {readOutcome}");
        kill(pid, SIGKILL);
        return Result("no-wait");
    }
    Say("read pending 3 s with the target frozen");

    if (mode == "control")
    {
        kill(pid, SIGCONT);
        Say("target resumed (SIGCONT)");
        bool done = readDone.Wait(TimeSpan.FromSeconds(10));
        Say(done ? $"read finished {clock.ElapsedMilliseconds - readStartedAt} ms after it started: {readOutcome}" : "read still pending 10 s after SIGCONT");
        kill(pid, SIGKILL);
        return Result(done ? "control-completed" : "control-still-pending");
    }

    kill(pid, SIGKILL);
    Say("target killed (SIGKILL)");
    bool returned = readDone.Wait(TimeSpan.FromSeconds(15));
    Say(returned ? $"read finished after the kill: {readOutcome}" : "read still pending 15 s after the kill");
    Say($"the dead target's pipes: {(PipesExist(pid) ? "present" : "absent")}");
    if (returned) return Result("kill-read-returned");

    int second = StartTarget(dll);
    Console.WriteLine($"child: second={second}");
    Console.Out.Flush();
    string? attachOutcome = null;
    var attachDone = new ManualResetEventSlim();
    new Thread(() =>
    {
        try { DebugSession.AttachAndOwn(second, new Sink()); attachOutcome = "attached"; }
        catch (Exception e) { attachOutcome = $"threw {e.GetType().Name}: {e.Message}"; }
        attachDone.Set();
    }) { IsBackground = true, Name = "probe100-second-attach" }.Start();
    bool attached = attachDone.Wait(TimeSpan.FromSeconds(30));
    Say(attached ? $"second attach finished: {attachOutcome}" : "second attach still pending after 30 s");
    return Result(attached ? "kill-orphaned-attach-ok" : "kill-orphaned-and-attach-hung");
}

// flood-kill: catch mscordbi's Win32 event thread inside a transport read, then kill the target.
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
static int ChildFlood(string dll)
{
    var clock = Stopwatch.StartNew();
    void Say(string text) { Console.WriteLine($"child: {text} [{clock.ElapsedMilliseconds} ms]"); Console.Out.Flush(); }
    int Result(string outcome) { Console.WriteLine($"child: result={outcome}"); Console.Out.Flush(); return 0; }

    int pid = StartTarget(dll, "flood");
    Console.WriteLine($"child: target={pid}");
    Console.Out.Flush();
    try { _ = DebugSession.AttachAndOwn(pid, new Sink()); }
    catch (Exception e) { Say($"attach failed: {e.Message}"); return Result("setup-failed"); }
    Say("attached; the target floods thread events");
    Thread.Sleep(1000);

    var random = new Random();
    int attempt = 0;
    bool caught = false;
    while (!caught && ++attempt <= 30)
    {
        Thread.Sleep(random.Next(20, 200));
        kill(pid, SIGSTOP);
        Thread.Sleep(300);
        caught = Win32EventThreadInRead();
        if (!caught) kill(pid, SIGCONT);
    }
    if (!caught) { kill(pid, SIGKILL); Say("the Win32 event thread was never caught in a read"); return Result("flood-not-caught"); }
    Say($"attempt {attempt}: froze the target with mscordbi's Win32 event thread in a transport read");

    kill(pid, SIGKILL);
    Say("target killed (SIGKILL)");
    Thread.Sleep(10_000);
    bool stillInRead = Win32EventThreadInRead();
    Say(stillInRead ? "10 s after the kill the Win32 event thread is still in the read" : "10 s after the kill the Win32 event thread has left the read");
    Say($"the dead target's pipes: {(PipesExist(pid) ? "present" : "absent")}");
    if (!stillInRead) return Result("flood-read-returned");

    int second = StartTarget(dll);
    Console.WriteLine($"child: second={second}");
    Console.Out.Flush();
    string? attachOutcome = null;
    var attachDone = new ManualResetEventSlim();
    new Thread(() =>
    {
        try { DebugSession.AttachAndOwn(second, new Sink()); attachOutcome = "attached"; }
        catch (Exception e) { attachOutcome = $"threw {e.GetType().Name}: {e.Message}"; }
        attachDone.Set();
    }) { IsBackground = true, Name = "probe100-second-attach" }.Start();
    bool attached = attachDone.Wait(TimeSpan.FromSeconds(30));
    Say(attached ? $"second attach finished: {attachOutcome}" : "second attach still pending after 30 s");
    return Result(attached ? "flood-orphaned-attach-ok" : "flood-orphaned-and-attach-hung");
}

// macOS: samples this process for a second and reports whether one thread is both in mscordbi's Win32 event loop and
// inside a transport read. Only the per-thread call graph is read: sample's summary totals frames across threads.
static bool Win32EventThreadInRead()
{
    string file = Path.Combine(Path.GetTempPath(), $"probe100-sample-{Environment.ProcessId}.txt");
    var psi = new ProcessStartInfo("sample") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string a in new[] { Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "1", "-file", file }) psi.ArgumentList.Add(a);
    using (Process sampler = Process.Start(psi)!) { sampler.StandardOutput.ReadToEnd(); sampler.StandardError.ReadToEnd(); sampler.WaitForExit(); }
    if (!File.Exists(file)) return false;
    string text = File.ReadAllText(file);
    File.Delete(file);
    int start = text.IndexOf("Call graph:", StringComparison.Ordinal);
    int end = text.IndexOf("Total number in stack", StringComparison.Ordinal);
    if (start < 0) return false;
    string graph = end > start ? text[start..end] : text[start..];
    return Regex.Split(graph, @"\n(?=\s+\d+ Thread_)")
        .Any(t => t.Contains("CordbWin32EventThread::Win32EventLoop", StringComparison.Ordinal)
               && t.Contains("DbgTransportSession::ReadMemory", StringComparison.Ordinal));
}

// The target runs under bash, so it is bash's child and not this process's (finding 87: an attached child of the
// debugger host has two reapers). bash prints the target's PID and waits for it.
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
static int StartTarget(string dll, string? targetArgument = null)
{
    var psi = new ProcessStartInfo("bash") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    psi.ArgumentList.Add("-c");
    psi.ArgumentList.Add("dotnet \"$0\" \"$@\" >/dev/null 2>&1 </dev/null & echo $!; wait");
    psi.ArgumentList.Add(dll);
    if (targetArgument is not null) psi.ArgumentList.Add(targetArgument);
    Process shell = Process.Start(psi)!;
    int pid = int.Parse(shell.StandardOutput.ReadLine()!.Trim());
    Thread.Sleep(1000); // the attach waits for the target's debugger itself (finding 99); this only lets main start
    return pid;
}

// The runtime names its debug transport pipes clr-debug-pipe-<pid>-<disambiguation key>-in/out in its temp directory.
static bool PipesExist(int pid)
{
    string prefix = $"clr-debug-pipe-{pid}-";
    string tmp = Path.GetTempPath();
    return Directory.EnumerateFileSystemEntries(tmp, prefix + "*-in").Any() && Directory.EnumerateFileSystemEntries(tmp, prefix + "*-out").Any();
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

[DllImport("libc", SetLastError = true)]
static extern int kill(int pid, int sig);

partial class Program
{
    static int SIGKILL => 9;
    static int SIGSTOP => OperatingSystem.IsMacOS() ? 17 : 19;
    static int SIGCONT => OperatingSystem.IsMacOS() ? 19 : 18;
}

sealed class Sink : IDebugEventSink
{
    public void OnEvent(string name) { }
    public void OnAnomaly(EngineAnomaly a) { Console.WriteLine($"child: anomaly {a.Kind} in {a.Operation}: {a.Observed}"); Console.Out.Flush(); }
}
