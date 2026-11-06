#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
#:property TargetFramework=net10.0
//
// DrHook.Engine probe 100 — a read of target memory that the target can never answer (finding 100) =================
//
// Observed (GitHub macOS runner, 2026-10-30, run 41068258696, since deleted; finding 100 quotes its stacks): the DrHook
// integration-test host hung in an attach, in CordbProcess::Init -> ForceDacFlush -> DacDbiInterfaceImpl::FlushCache,
// waiting on the DAC's mutex. Inside the DAC was the previous session's Win32 event thread, reading target memory over
// the debugger transport (DbgTransportSession::ReadMemory -> SendRequestMessageAndWait), for a target that was dead;
// its transport thread was in TwoWayPipe::Connect -> open(), reconnecting to the dead target's pipe. Every later attach
// and launch hung.
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
// ADR-016 Step 4 adds two modes (run only when named; drafted and run 2026-11-05). Together they decide whether
// DrHook can stay dependable with simpler means, or needs its debugger in a worker process it can discard (Step 5):
// a deliberate kill is sometimes needed (a program that won't end), and a crash can happen at any moment.
//   flood-sync-kill — Hypothesis: a kill made while the debugger holds the target synchronized leaves no transport
//             request of the Win32 event thread outstanding, so mscordbi handles the exit and a second attach in the
//             same process succeeds. Construction: the flooding target; Pause (ICorDebugController.Stop) and wait for
//             the Pause stop; on macOS, sample this process and record whether the Win32 event thread is inside
//             DbgTransportSession::SendRequestMessageAndWait (if it is, the run ends as sync-not-idle: the premise
//             failed); SIGKILL; wait for the target to be gone; after 10 s, record the event thread's state and the
//             dead target's pipes; then a second attach, bounded at 30 s. Predicted: sync-kill-attach-ok, N of N.
//   flood-crash — Hypothesis: Environment.FailFast goes through the runtime, which tells the debugger before the
//             process dies, so the Win32 event thread is not mid-request when it dies and a second attach succeeds.
//             Unverified: the runtime's fatal-error path (vm/eepolicy.cpp) was not read. Construction: the target
//             floods and calls FailFast at a random moment 0.5 to 3 s after the attach; a resumer thread continues
//             every stop that surfaces, as an operator would, and records its reason; wait (up to 30 s) for the
//             target to be gone; after 10 s, the same records and second attach as above. Predicted: crash-attach-ok,
//             N of N. Any crash-orphaned-and-attach-hung means no session discipline protects DrHook from a crashing
//             target.
// Both run on macOS and Linux; the Win32 event thread's state is read only on macOS (sample), elsewhere "not checked".
// Observed 2026-11-05, omega (macOS 27.0.1, arm64, SDK 10.0.301), 10 runs each:
//   flood-sync-kill 10/10 sync-kill-attach-ok: the event thread was idle before every kill, the pipes were removed,
//             and the second attach succeeded. Holds on omega.
//   flood-crash 8/10 crash-attach-ok, 2/10 crash-orphaned-and-attach-hung (runs 5 and 6): the debugger got an
//             Exception(Unhandled) stop before every crash, and the resumer continued it; in 2 runs the event thread
//             was still inside a transport request 10 s after the target was gone, and the second attach was pending
//             after 30 s. The dead target's pipes were absent in all 10, unlike flood-kill. Falsified: a crash can
//             leave the debugger unusable even though the runtime tells the debugger first. Which transport request
//             it was is not recorded (the probe checks SendRequestMessageAndWait, not the frame above it).
// Two more modes test acting at the unhandled-exception stop instead of continuing it (drafted 2026-11-05, not yet
// run). Both use the flood-crash target. Every stop before that one is continued; at it the target is synchronized.
// The exception type the debugger reads there is recorded. If no such stop comes within 30 s, the run is no-stop.
// Neither mode covers a crash the debugger is not told about (a native fault; possibly stack overflow).
//   flood-crash-kill — Hypothesis: at the unhandled-exception stop no transport request of the Win32 event thread
//             is outstanding, so a kill there is as clean as flood-sync-kill. Construction: on macOS, sample and
//             record whether the event thread is inside SendRequestMessageAndWait (if it is: crash-not-idle);
//             SIGKILL; wait for the target to be gone; then AfterTheEnd as above. Predicted: crash-kill-attach-ok,
//             N of N.
//   flood-crash-detach — Hypothesis: a Detach at that stop succeeds and removes the process from mscordbi before the
//             target dies, so mscordbi makes no request to the dead target; the target goes on to die (FailFast with
//             no debugger), and a second attach succeeds. Construction: Detach on its own thread, bounded at 30 s
//             (crash-detach-hung); a failed Detach shows as an anomaly line; wait up to 30 s for the target to be gone
//             (crash-detach-no-exit); then AfterTheEnd. Predicted: crash-detach-attach-ok, N of N. Unverified: whether
//             ICorDebug allows a Detach while an unhandled exception is pending, and what the runtime does then.
//   Usage: dotnet run --no-cache 100-orphaned-read-smoke.cs 10 flood-crash-kill flood-crash-detach
// Observed 2026-11-05 18:39-18:44 UTC, omega (macOS 27.0.1, arm64, SDK 10.0.301), 10 runs each, RESULT(0):
//   flood-crash-kill 10/10 crash-kill-attach-ok: an Exception(Unhandled) stop came first every time, the event thread
//             was idle at it, the target was gone within ~30 ms of the kill, the pipes were absent, the second attach
//             succeeded.
//   flood-crash-detach 10/10 crash-detach-attach-ok: Detach returned within ~30 ms each time and the target was gone
//             within 1 ms of it; no anomaly line, so Detach gave S_OK or a gone-target code (which one is not
//             recorded); pipes absent; the second attach succeeded.
//   The exception type read at the stop was System.ExecutionEngineException in all 20 runs: FailFast surfaces to
//   the debugger as an unhandled ExecutionEngineException.
//   Weight: if acting at the stop changed nothing (flood-crash hung 2/10), 10 clean runs in one mode would still
//   come up about 11% of the time (0.8^10), and 20 of 20 across both about 1%. Supported on omega, not proven.
// On omega-ubuntu (both .NET builds), the same day: finding 104.
//
// Three more modes ask what happens when a target dies without the warning that ending at the stop needs (drafted
// 2026-11-05, not yet run). FailFast warns the debugger through EEPolicy::HandleFatalError, which then calls
// TerminateProcess -> PROCAbort -> abort() (omega's crash report of a flood-crash target, 2026-11-05). These three crash
// the same flooding target, at the same random moment after the attach, by means that skip HandleFatalError:
//   flood-stackoverflow — recursion on the crasher thread until its stack is exhausted.
//   flood-segv — a write through address 16 inside libc's memset: a fault in native code, which the runtime does not
//             turn into a managed exception.
//   flood-abort — libc's abort(), called directly.
// Hypotheses, each per mode: (1) no stop surfaces before the end (unwarned), since none of the three passes through
// HandleFatalError; (2) as in flood-crash, the death sometimes lands while the Win32 event thread is mid-request, so
// the second attach hangs in at least one of N runs. Unverified, not read in the runtime source: whether the runtime
// sends the debugger anything on a stack overflow, and which of these runs the runtime's own shutdown work (the
// signal handlers' PROCNotifyProcessShutdown) before the process ends. Construction: flood-crash's (every stop
// continued and recorded, with the exception type when it is one), without assuming a stop comes; a ProcessExited
// stop is not a warning. Result: <kind>-unwarned-... or <kind>-warned-..., then AfterTheEnd as above. Predicted:
// <kind>-unwarned every run, attach-hung in at least one. A warned result falsifies (1), and opens the question of
// ending the session at that stop; ten usable runs falsify (2).
// On macOS each of these crashes leaves a crash report in ~/Library/Logs/DiagnosticReports (flood-crash's did too).
//   Usage: dotnet run --no-cache 100-orphaned-read-smoke.cs 10 flood-stackoverflow flood-segv flood-abort
//
// Observed 2026-10-30, omega (macOS 27, arm64): control 3/3 completed after SIGCONT and kill 3/3 returned within
// ~20 ms of the kill (the dead target's pipes removed), refuting the first form of the hypothesis (any pending read
// never returns). With flood-kill, two batches: control 5/5, kill 5/5, flood-kill 5/5 (the Win32 event thread caught
// in a read on the first or second attempt; still in it 10 s after the kill; pipes left; the second attach pending
// after 30 s). RESULT(0) both times.
//
// Result codes: 0 confirmed: every mode's prediction held over its runs (control-completed; kill-read-returned;
// flood-kill: flood-orphaned-and-attach-hung; flood-sync-kill: sync-kill-attach-ok; flood-crash: crash-attach-ok;
// flood-crash-kill: crash-kill-attach-ok; flood-crash-detach: crash-detach-attach-ok, each every run; the three
// unwarned crashes: <kind>-unwarned every run and attach-hung in at least one); 1 otherwise; 2 target build failed.
// Child results: control-completed, kill-read-returned, kill-orphaned-and-attach-hung, kill-orphaned-attach-ok,
// flood-orphaned-and-attach-hung, flood-orphaned-attach-ok, flood-read-returned, flood-not-caught, sync-kill-attach-ok,
// sync-kill-attach-hung, sync-kill-orphaned-and-attach-hung, sync-not-idle, crash-attach-ok, crash-attach-hung,
// crash-orphaned-and-attach-hung, crash-no-exit, crash-kill-attach-ok, crash-kill-attach-hung,
// crash-kill-orphaned-and-attach-hung, crash-not-idle, crash-detach-attach-ok, crash-detach-attach-hung,
// crash-detach-orphaned-and-attach-hung, crash-detach-hung, crash-detach-no-exit, no-wait, no-stop, setup-failed;
// for kind stackoverflow, segv or abort: <kind>-unwarned- or <kind>-warned- followed by attach-ok, attach-hung or
// orphaned-and-attach-hung, and <kind>-no-exit. "orphaned" means the sample showed the Win32 event thread still in a
// transport request 10 s after the target was gone (macOS only).
//
// Usage (from poc/drhook-engine):  dotnet run --no-cache 100-orphaned-read-smoke.cs [runs] [mode ...]
// With no modes: control, kill, and on macOS flood-kill (the set recorded above). Step 4:
//   dotnet run --no-cache 100-orphaned-read-smoke.cs 10 flood-sync-kill flood-crash

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
if (args.Length == 3 && args[0] == "child")
    return args[1] switch
    {
        "flood-kill" => ChildFlood(args[2]),
        "flood-sync-kill" => ChildSyncKill(args[2]),
        "flood-crash" => ChildCrash(args[2]),
        "flood-crash-kill" => ChildCrashKill(args[2]),
        "flood-crash-detach" => ChildCrashDetach(args[2]),
        "flood-stackoverflow" => ChildUnwarnedCrash(args[2], "flood-stackoverflow", "stackoverflow"),
        "flood-segv" => ChildUnwarnedCrash(args[2], "flood-segv", "segv"),
        "flood-abort" => ChildUnwarnedCrash(args[2], "flood-abort", "abort"),
        _ => Child(args[1], args[2]),
    };

// Each mode's prediction, in words, and the test it puts to the mode's outcomes. Most predict one outcome every run;
// the unwarned crashes predict a pattern over the runs.
var predicted = new Dictionary<string, (string Text, Func<List<string>, bool> Holds)>
{
    ["control"] = ("control-completed every run", rs => rs.All(r => r == "control-completed")),
    ["kill"] = ("kill-read-returned every run", rs => rs.All(r => r == "kill-read-returned")),
    ["flood-kill"] = ("flood-orphaned-and-attach-hung every run", rs => rs.All(r => r == "flood-orphaned-and-attach-hung")),
    ["flood-sync-kill"] = ("sync-kill-attach-ok every run", rs => rs.All(r => r == "sync-kill-attach-ok")),
    ["flood-crash"] = ("crash-attach-ok every run", rs => rs.All(r => r == "crash-attach-ok")),
    ["flood-crash-kill"] = ("crash-kill-attach-ok every run", rs => rs.All(r => r == "crash-kill-attach-ok")),
    ["flood-crash-detach"] = ("crash-detach-attach-ok every run", rs => rs.All(r => r == "crash-detach-attach-ok")),
    ["flood-stackoverflow"] = ("stackoverflow-unwarned every run, attach-hung in at least one",
        rs => rs.All(r => r.StartsWith("stackoverflow-unwarned-", StringComparison.Ordinal)) && rs.Any(r => r.EndsWith("attach-hung", StringComparison.Ordinal))),
    ["flood-segv"] = ("segv-unwarned every run, attach-hung in at least one",
        rs => rs.All(r => r.StartsWith("segv-unwarned-", StringComparison.Ordinal)) && rs.Any(r => r.EndsWith("attach-hung", StringComparison.Ordinal))),
    ["flood-abort"] = ("abort-unwarned every run, attach-hung in at least one",
        rs => rs.All(r => r.StartsWith("abort-unwarned-", StringComparison.Ordinal)) && rs.Any(r => r.EndsWith("attach-hung", StringComparison.Ordinal))),
};
int runs = args.Length > 0 ? int.Parse(args[0]) : 3;
string[] modes = args.Length > 1 ? args[1..] : OperatingSystem.IsMacOS() ? ["control", "kill", "flood-kill"] : ["control", "kill"];
foreach (string mode in modes)
{
    if (!predicted.ContainsKey(mode)) { Console.WriteLine($"unknown mode '{mode}'; modes: {string.Join(", ", predicted.Keys)}"); return 1; }
    if (mode == "flood-kill" && !OperatingSystem.IsMacOS()) { Console.WriteLine("flood-kill needs macOS (it catches the event thread with sample)"); return 1; }
}
string? dll = BuildTarget(Path.GetFullPath("100-orphaned-read-target.cs"));
if (dll is null) { Console.WriteLine("FAIL(2): could not build 100-orphaned-read-target.cs"); return 2; }
Console.WriteLine($"platform: {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}");

var results = new Dictionary<string, List<string>>();
foreach (string mode in modes)
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
foreach ((string mode, List<string> outcomes) in results)
    Console.WriteLine($"{mode}: predicted {predicted[mode].Text}: {(predicted[mode].Holds(outcomes) ? "holds" : "does not hold")} over {outcomes.Count} runs");
bool confirmed = results.All(kv => predicted[kv.Key].Holds(kv.Value));
Console.WriteLine(confirmed
    ? "RESULT(0): confirmed — every mode run gave its predicted outcome every time (see the header for each hypothesis)"
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
        caught = Win32EventThreadIn("DbgTransportSession::ReadMemory");
        if (!caught) kill(pid, SIGCONT);
    }
    if (!caught) { kill(pid, SIGKILL); Say("the Win32 event thread was never caught in a read"); return Result("flood-not-caught"); }
    Say($"attempt {attempt}: froze the target with mscordbi's Win32 event thread in a transport read");

    kill(pid, SIGKILL);
    Say("target killed (SIGKILL)");
    Thread.Sleep(10_000);
    bool stillInRead = Win32EventThreadIn("DbgTransportSession::ReadMemory");
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

// flood-sync-kill: synchronize the flooding target through the debugger, then kill it.
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
static int ChildSyncKill(string dll)
{
    var clock = Stopwatch.StartNew();
    void Say(string text) { Console.WriteLine($"child: {text} [{clock.ElapsedMilliseconds} ms]"); Console.Out.Flush(); }
    int Result(string outcome) { Console.WriteLine($"child: result={outcome}"); Console.Out.Flush(); return 0; }

    int pid = StartTarget(dll, "flood");
    Console.WriteLine($"child: target={pid}");
    Console.Out.Flush();
    DebugSession session;
    try { session = DebugSession.AttachAndOwn(pid, new Sink()); }
    catch (Exception e) { Say($"attach failed: {e.Message}"); return Result("setup-failed"); }
    Say("attached; the target floods thread events");
    Thread.Sleep(1000);

    session.Pause();
    StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(10));
    Say($"stop: {stop?.Reason.ToString() ?? "none"}");
    if (stop?.Reason != StopReason.Pause) { kill(pid, SIGKILL); return Result("no-stop"); }

    if (OperatingSystem.IsMacOS())
    {
        bool busy = Win32EventThreadIn("DbgTransportSession::SendRequestMessageAndWait");
        Say(busy ? "synchronized, but the Win32 event thread is inside a transport request" : "synchronized; the Win32 event thread is not inside a transport request");
        if (busy) { kill(pid, SIGKILL); return Result("sync-not-idle"); }
    }
    else Say("the Win32 event thread's state before the kill: not checked (macOS only)");

    kill(pid, SIGKILL);
    Say("target killed (SIGKILL) while synchronized");
    Say(WaitUntilGone(pid, TimeSpan.FromSeconds(10)) ? "target gone" : "target still present 10 s after the kill");
    return AfterTheEnd("sync-kill", pid, dll, Say, Result);
}

// flood-crash: the flooding target calls Environment.FailFast at a moment of its own; every stop is continued.
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
static int ChildCrash(string dll)
{
    var clock = Stopwatch.StartNew();
    void Say(string text) { Console.WriteLine($"child: {text} [{clock.ElapsedMilliseconds} ms]"); Console.Out.Flush(); }
    int Result(string outcome) { Console.WriteLine($"child: result={outcome}"); Console.Out.Flush(); return 0; }

    int pid = StartTarget(dll, "flood-crash");
    Console.WriteLine($"child: target={pid}");
    Console.Out.Flush();
    DebugSession session;
    try { session = DebugSession.AttachAndOwn(pid, new Sink()); }
    catch (Exception e) { Say($"attach failed: {e.Message}"); return Result("setup-failed"); }
    Say("attached; the target floods thread events and will call FailFast within 0.5 to 3 s");

    // An operator who continues every stop: the crash then proceeds whatever the debugger is told about it.
    var stops = new List<string>();
    bool resuming = true;
    var resumer = new Thread(() =>
    {
        while (Volatile.Read(ref resuming))
        {
            try
            {
                StopInfo? s = session.WaitForStop(TimeSpan.FromMilliseconds(200));
                if (s is null) continue;
                lock (stops) stops.Add(s.Reason == StopReason.Exception ? $"Exception({s.ExceptionKind})" : s.Reason.ToString());
                session.Resume();
            }
            catch (Exception e) { lock (stops) stops.Add($"threw {e.GetType().Name}"); return; }
        }
    }) { IsBackground = true, Name = "probe100-resumer" };
    resumer.Start();

    bool gone = WaitUntilGone(pid, TimeSpan.FromSeconds(30));
    Volatile.Write(ref resuming, false);
    lock (stops) Say($"stops seen before the end: {(stops.Count == 0 ? "none" : string.Join(", ", stops))}");
    if (!gone) { kill(pid, SIGKILL); Say("target still present 30 s after the attach"); return Result("crash-no-exit"); }
    Say("target gone (FailFast)");
    resumer.Join(TimeSpan.FromSeconds(2));
    return AfterTheEnd("crash", pid, dll, Say, Result);
}

// flood-crash-kill: the same crashing target; every stop before the unhandled-exception stop is continued, and at that
// stop, while the target is synchronized, it is killed.
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
static int ChildCrashKill(string dll)
{
    var clock = Stopwatch.StartNew();
    void Say(string text) { Console.WriteLine($"child: {text} [{clock.ElapsedMilliseconds} ms]"); Console.Out.Flush(); }
    int Result(string outcome) { Console.WriteLine($"child: result={outcome}"); Console.Out.Flush(); return 0; }

    int pid = StartTarget(dll, "flood-crash");
    Console.WriteLine($"child: target={pid}");
    Console.Out.Flush();
    DebugSession session;
    try { session = DebugSession.AttachAndOwn(pid, new Sink()); }
    catch (Exception e) { Say($"attach failed: {e.Message}"); return Result("setup-failed"); }
    Say("attached; the target floods thread events and will call FailFast within 0.5 to 3 s");

    var stops = new List<string>();
    var waiting = Stopwatch.StartNew();
    StopInfo? unhandled = null;
    while (unhandled is null && waiting.Elapsed < TimeSpan.FromSeconds(30))
    {
        StopInfo? s = session.WaitForStop(TimeSpan.FromMilliseconds(200));
        if (s is null) continue;
        stops.Add(s.Reason == StopReason.Exception ? $"Exception({s.ExceptionKind})" : s.Reason.ToString());
        if (s.Reason == StopReason.Exception && s.ExceptionKind == ExceptionStopKind.Unhandled) unhandled = s;
        else if (s.Reason == StopReason.ProcessExited) break;
        else session.Resume();
    }
    Say($"stops seen: {(stops.Count == 0 ? "none" : string.Join(", ", stops))}");
    if (unhandled is null) { kill(pid, SIGKILL); Say("no unhandled-exception stop within 30 s of the attach"); return Result("no-stop"); }
    Say($"at the unhandled-exception stop; exception type: {session.GetCurrentExceptionTypeName() ?? "none read"}");

    if (OperatingSystem.IsMacOS())
    {
        bool busy = Win32EventThreadIn("DbgTransportSession::SendRequestMessageAndWait");
        Say(busy ? "synchronized, but the Win32 event thread is inside a transport request" : "synchronized; the Win32 event thread is not inside a transport request");
        if (busy) { kill(pid, SIGKILL); return Result("crash-not-idle"); }
    }
    else Say("the Win32 event thread's state before the kill: not checked (macOS only)");

    kill(pid, SIGKILL);
    Say("target killed (SIGKILL) at the unhandled-exception stop");
    Say(WaitUntilGone(pid, TimeSpan.FromSeconds(10)) ? "target gone" : "target still present 10 s after the kill");
    return AfterTheEnd("crash-kill", pid, dll, Say, Result);
}

// flood-crash-detach: the same crashing target; every stop before the unhandled-exception stop is continued, and at
// that stop the debugger detaches. The target then goes on to its death with no debugger attached.
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
static int ChildCrashDetach(string dll)
{
    var clock = Stopwatch.StartNew();
    void Say(string text) { Console.WriteLine($"child: {text} [{clock.ElapsedMilliseconds} ms]"); Console.Out.Flush(); }
    int Result(string outcome) { Console.WriteLine($"child: result={outcome}"); Console.Out.Flush(); return 0; }

    int pid = StartTarget(dll, "flood-crash");
    Console.WriteLine($"child: target={pid}");
    Console.Out.Flush();
    DebugSession session;
    try { session = DebugSession.AttachAndOwn(pid, new Sink()); }
    catch (Exception e) { Say($"attach failed: {e.Message}"); return Result("setup-failed"); }
    Say("attached; the target floods thread events and will call FailFast within 0.5 to 3 s");

    var stops = new List<string>();
    var waiting = Stopwatch.StartNew();
    StopInfo? unhandled = null;
    while (unhandled is null && waiting.Elapsed < TimeSpan.FromSeconds(30))
    {
        StopInfo? s = session.WaitForStop(TimeSpan.FromMilliseconds(200));
        if (s is null) continue;
        stops.Add(s.Reason == StopReason.Exception ? $"Exception({s.ExceptionKind})" : s.Reason.ToString());
        if (s.Reason == StopReason.Exception && s.ExceptionKind == ExceptionStopKind.Unhandled) unhandled = s;
        else if (s.Reason == StopReason.ProcessExited) break;
        else session.Resume();
    }
    Say($"stops seen: {(stops.Count == 0 ? "none" : string.Join(", ", stops))}");
    if (unhandled is null) { kill(pid, SIGKILL); Say("no unhandled-exception stop within 30 s of the attach"); return Result("no-stop"); }
    Say($"at the unhandled-exception stop; exception type: {session.GetCurrentExceptionTypeName() ?? "none read"}");

    // Detach reports a failure only as an anomaly; the Sink prints it as a child line.
    var detachDone = new ManualResetEventSlim();
    new Thread(() => { session.Detach(); detachDone.Set(); }) { IsBackground = true, Name = "probe100-detach" }.Start();
    if (!detachDone.Wait(TimeSpan.FromSeconds(30))) { kill(pid, SIGKILL); Say("Detach still pending after 30 s"); return Result("crash-detach-hung"); }
    Say("detached at the unhandled-exception stop");
    if (!WaitUntilGone(pid, TimeSpan.FromSeconds(30))) { kill(pid, SIGKILL); Say("target still present 30 s after the detach"); return Result("crash-detach-no-exit"); }
    Say("target gone (FailFast, no debugger attached)");
    return AfterTheEnd("crash-detach", pid, dll, Say, Result);
}

// flood-stackoverflow, flood-segv, flood-abort: the flooding target crashes by a means that skips HandleFatalError.
// flood-crash's construction (every stop continued, its reason recorded), with no assumption that a stop comes: the
// result is <kind>-unwarned-... when no stop surfaced before the end, <kind>-warned-... when one did.
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
static int ChildUnwarnedCrash(string dll, string targetMode, string kind)
{
    var clock = Stopwatch.StartNew();
    void Say(string text) { Console.WriteLine($"child: {text} [{clock.ElapsedMilliseconds} ms]"); Console.Out.Flush(); }
    int Result(string outcome) { Console.WriteLine($"child: result={outcome}"); Console.Out.Flush(); return 0; }

    int pid = StartTarget(dll, targetMode);
    Console.WriteLine($"child: target={pid}");
    Console.Out.Flush();
    DebugSession session;
    try { session = DebugSession.AttachAndOwn(pid, new Sink()); }
    catch (Exception e) { Say($"attach failed: {e.Message}"); return Result("setup-failed"); }
    Say($"attached; the target floods thread events and will crash ({kind}) within 0.5 to 3 s");

    var stops = new List<string>();
    bool resuming = true;
    var resumer = new Thread(() =>
    {
        while (Volatile.Read(ref resuming))
        {
            try
            {
                StopInfo? s = session.WaitForStop(TimeSpan.FromMilliseconds(200));
                if (s is null) continue;
                string reason = s.Reason == StopReason.Exception ? $"Exception({s.ExceptionKind}, {session.GetCurrentExceptionTypeName() ?? "type not read"})" : s.Reason.ToString();
                lock (stops) stops.Add(reason);
                if (s.Reason == StopReason.ProcessExited) return;
                session.Resume();
            }
            catch (Exception e) { lock (stops) stops.Add($"threw {e.GetType().Name}"); return; }
        }
    }) { IsBackground = true, Name = "probe100-resumer" };
    resumer.Start();

    bool gone = WaitUntilGone(pid, TimeSpan.FromSeconds(30));
    Volatile.Write(ref resuming, false);
    resumer.Join(TimeSpan.FromSeconds(2));
    // A ProcessExited stop is the debugger noticing the end, not a warning before it.
    bool warned;
    lock (stops)
    {
        Say($"stops seen before the end: {(stops.Count == 0 ? "none" : string.Join(", ", stops))}");
        warned = stops.Any(s => s != nameof(StopReason.ProcessExited));
    }
    if (!gone) { kill(pid, SIGKILL); Say("target still present 30 s after the attach"); return Result($"{kind}-no-exit"); }
    Say($"target gone ({kind})");
    return AfterTheEnd($"{kind}-{(warned ? "warned" : "unwarned")}", pid, dll, Say, Result);
}

// After the target is gone: let mscordbi handle the exit, record the event thread and the pipes, then attach again.
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
static int AfterTheEnd(string prefix, int pid, string dll, Action<string> say, Func<string, int> result)
{
    Thread.Sleep(10_000);
    bool? stuck = OperatingSystem.IsMacOS() ? Win32EventThreadIn("DbgTransportSession::SendRequestMessageAndWait") : null;
    say(stuck switch
    {
        true => "10 s after the end the Win32 event thread is inside a transport request",
        false => "10 s after the end the Win32 event thread is not inside a transport request",
        null => "10 s after the end the Win32 event thread's state: not checked (macOS only)",
    });
    say($"the dead target's pipes: {(PipesExist(pid) ? "present" : "absent")}");

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
    say(attached ? $"second attach finished: {attachOutcome}" : "second attach still pending after 30 s");
    if (attached && attachOutcome == "attached") return result($"{prefix}-attach-ok");
    if (attached) return result("setup-failed");
    return result(stuck == true ? $"{prefix}-orphaned-and-attach-hung" : $"{prefix}-attach-hung");
}

// Polls with signal 0 until the process is gone (bash, its parent, reaps it). True once kill(pid, 0) fails.
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
static bool WaitUntilGone(int pid, TimeSpan bound)
{
    var waited = Stopwatch.StartNew();
    while (waited.Elapsed < bound)
    {
        if (kill(pid, 0) != 0) return true;
        Thread.Sleep(20);
    }
    return false;
}

// macOS: samples this process for a second and reports whether one thread is both in mscordbi's Win32 event loop and
// inside the given frame (a transport read, or any transport request). Only the per-thread call graph is read:
// sample's summary totals frames across threads.
static bool Win32EventThreadIn(string frame)
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
               && t.Contains(frame, StringComparison.Ordinal));
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
