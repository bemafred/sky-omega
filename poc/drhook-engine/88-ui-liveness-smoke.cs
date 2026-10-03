#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
#:property TargetFramework=net10.0
//
// DrHook.Engine probe 88 — UI liveness by dispatcher drain (the capture-hang guard's mechanism)
// =============================================================================================
//
// Hypothesis: queueing File.Delete(sentinel) on the UI dispatcher by func-eval at a UI-thread stop, then detaching,
// makes the sentinel file disappear within 3 s IFF the UI thread is alive after detach:
//   idle    → sentinel deleted     (alive)
//   managed → none                 (UI thread deadlocked on Monitor.Enter, managed frames on top)
//   native  → none                 (UI thread blocked in pthread_mutex_lock, a native frame on top)
// Falsified by: sentinel still present for idle, or deleted in either hung mode.
//
// Fixture: poc/avalonia-hang-target (build it first). The target hangs itself in Beat() when it finds
// $TMPDIR/drhook-hang-<pid>; we write that file only once STOPPED at Beat's first line, so the hang starts on
// resume — after the job is queued and before the dispatcher could drain it.
//
// Launch: via a throwaway .app bundle + `open -n -g` so it reaches the Aqua session from any shell (a Background
// launchd session cannot start a GUI — Avalonia RenderTimer -6661), after `caffeinate -u` wakes the display.
// `open` also keeps the target from being OUR child (finding 87 — no dual reaper).
//
// Run from poc/drhook-engine/:  dotnet run --no-cache 88-ui-liveness-smoke.cs

using System.Diagnostics;
using SkyOmega.DrHook.Engine;

if (!OperatingSystem.IsMacOS()) { Console.Error.WriteLine("probe 88 is macOS-only (app bundle + open)"); return 2; }
string fixture = Path.GetFullPath("../avalonia-hang-target");
string apphost = Path.Combine(fixture, "bin/Debug/net10.0/HangTarget");
string source = Path.Combine(fixture, "Program.cs");
const int BeatFirstLine = 37; // `_beat++;` — before Beat() reads the trigger file
if (!File.Exists(apphost)) { Console.Error.WriteLine($"build the fixture first: {apphost}"); return 2; }

string bundle = Path.Combine(Path.GetTempPath(), $"drhook-probe88-{Environment.ProcessId}", "HangTarget.app");
Directory.CreateDirectory(Path.Combine(bundle, "Contents/MacOS"));
File.WriteAllText(Path.Combine(bundle, "Contents/Info.plist"),
    "<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><dict>" +
    "<key>CFBundleExecutable</key><string>launch</string><key>CFBundleIdentifier</key><string>dev.sky-omega.drhook.probe88</string>" +
    "<key>CFBundlePackageType</key><string>APPL</string></dict></plist>");
string launcher = Path.Combine(bundle, "Contents/MacOS/launch");
File.WriteAllText(launcher, $"#!/bin/sh\nexec \"{apphost}\"\n");
File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

int failures = 0;
foreach (string mode in new[] { "idle", "managed", "native" })
{
    int pid = LaunchFixture();
    Console.WriteLine($"── {mode}: target pid {pid}");
    try
    {
        var sink = new BoundedAnomalySink(64);
        using DispatcherDrainSentinel sentinel = DispatcherDrainSentinel.Create(pid);
        using (DebugSession session = DebugSession.Attach(pid, sink))
        {
            StopInfo? setup = session.WaitForStop(TimeSpan.FromSeconds(5));
            if (setup is null) { session.Pause(); setup = session.WaitForStop(TimeSpan.FromSeconds(5)); }
            if (setup is null) throw new InvalidOperationException("no synchronized stop after attach");

            session.SetBreakpointAtLine(source, BeatFirstLine);
            session.Resume();
            StopInfo? beat = session.WaitForStop(TimeSpan.FromSeconds(5));
            if (beat?.Reason != StopReason.Breakpoint) throw new InvalidOperationException($"expected Beat breakpoint, got {beat?.Reason}");

            if (mode != "idle") File.WriteAllText(Path.Combine(Path.GetTempPath(), $"drhook-hang-{pid}"), mode);

            EvalStatus posted = session.TryEvalPostLivenessJob(UiLivenessPlan.Avalonia, sentinel.Path, TimeSpan.FromSeconds(10), out string trace);
            Console.WriteLine($"   post: {posted} (trace={trace})");
            if (posted != EvalStatus.Completed) throw new InvalidOperationException("liveness job not queued");

            session.ClearBreakpoints();
        }                                                     // Borrowed Dispose → detach, target runs on

        var clock = Stopwatch.StartNew();
        bool alive = sentinel.WaitForDrain(TimeSpan.FromSeconds(3));
        bool expected = mode == "idle";
        string verdict = alive == expected ? "PASS" : "FAIL";
        if (alive != expected) failures++;
        Console.WriteLine($"   sentinel drained: {alive} after {clock.ElapsedMilliseconds} ms — expected {expected} → {verdict}");
    }
    catch (Exception ex) { failures++; Console.WriteLine($"   FAIL: {ex.GetType().Name}: {ex.Message}"); }
    finally
    {
        try { Process.GetProcessById(pid).Kill(); } catch { /* already gone */ }
    }
}
Console.WriteLine(failures == 0 ? "PROBE 88: CONFIRMED" : $"PROBE 88: FALSIFIED ({failures} failure(s))");
return failures == 0 ? 0 : 1;

int LaunchFixture()
{
    HashSet<int> before = HangTargetPids();
    Run("caffeinate", "-u", "-t", "1");
    Run("open", "-n", "-g", bundle);
    for (int i = 0; i < 100; i++)
    {
        int[] fresh = HangTargetPids().Except(before).ToArray();
        if (fresh.Length == 1) { Thread.Sleep(2000); return fresh[0]; } // window up + first beats
        Thread.Sleep(100);
    }
    throw new InvalidOperationException("fixture did not start (display asleep? Background session?)");
}

HashSet<int> HangTargetPids() => Process.GetProcesses()
    .Where(p => { try { return p.MainModule?.FileName == apphost; } catch { return false; } })
    .Select(p => p.Id).ToHashSet();

void Run(string file, params string[] args)
{
    var psi = new ProcessStartInfo(file) { UseShellExecute = false };
    foreach (string a in args) psi.ArgumentList.Add(a);
    using Process p = Process.Start(psi)!;
    p.WaitForExit();
}
