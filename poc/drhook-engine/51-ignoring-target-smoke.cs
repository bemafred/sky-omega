#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 51 — IGNORING TARGET: soft signals catchable + ignorable; SIGKILL not ===
//
// ADR-008 Phase 0 (process lifecycle ground truth).
//
// HYPOTHESIS: "Soft signals (SIGINT, SIGTERM) can be intercepted and ignored by a
// target's handler. Only hard kill (SIGKILL on Unix / TerminateProcess on Windows) is
// non-catchable by the target — that's the kernel-mandated escape hatch."
//
// CONSTRUCTION: spawn 51-target.cs (catches both, ignores both, runs forever).
//   Phase A: send SIGINT, observe SIGINT_INTERCEPTED_AND_IGNORED, validate target still alive after 1s.
//   Phase B: send SIGTERM, observe SIGTERM_INTERCEPTED_AND_IGNORED, validate target still alive after 1s.
//   Phase C: send SIGKILL, validate target IS dead within 1s (cannot intercept).
//
// Falsification:
//   2 usage; 3 no READY; 4 kill syscall failed; 5 soft signal not intercepted (handler didn't run);
//   6 target died on soft signal (didn't actually ignore); 7 target survived SIGKILL (impossible — kernel violated);
//   0 PASS.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

return IgnoringTargetP51.Run(args);

static class Posix
{
    [DllImport("libc", SetLastError = true)]
    public static extern int kill(int pid, int sig);
    public const int SIGINT = 2;
    public const int SIGTERM = 15;
    public const int SIGKILL = 9;
}

static class IgnoringTargetP51
{
    const int InterceptObserveMs = 500;
    const int PostSoftSignalAliveCheckMs = 1000;
    const int KillExitWindowMs = 1000;

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 51-ignoring-target-smoke.cs <path-to-51-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"os-arch    : {RuntimeInformation.RuntimeIdentifier}");
        Console.WriteLine($"plan       : Phase A SIGINT-ignored, Phase B SIGTERM-ignored, Phase C SIGKILL-effective");

        using Process proc = new()
        {
            StartInfo = new ProcessStartInfo("dotnet", $"\"{args[0]}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };
        proc.Start();

        int realPid = -1;
        bool sigintIntercepted = false;
        bool sigtermIntercepted = false;
        ManualResetEventSlim ready = new(false);
        ManualResetEventSlim sigintCaught = new(false);
        ManualResetEventSlim sigtermCaught = new(false);
        Thread reader = new(() =>
        {
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) is not null)
            {
                Match m = Regex.Match(line, @"READY (\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
                {
                    Volatile.Write(ref realPid, pid);
                    ready.Set();
                }
                else if (line.Contains("SIGINT_INTERCEPTED_AND_IGNORED"))
                {
                    sigintIntercepted = true;
                    sigintCaught.Set();
                }
                else if (line.Contains("SIGTERM_INTERCEPTED_AND_IGNORED"))
                {
                    sigtermIntercepted = true;
                    sigtermCaught.Set();
                }
            }
        }) { IsBackground = true, Name = "target-stdout" };
        reader.Start();
        Thread errDrain = new(() => { while (proc.StandardError.ReadLine() is not null) { } })
        { IsBackground = true, Name = "target-stderr" };
        errDrain.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(30)))
        {
            Console.Error.WriteLine("FALSIFIED (no READY).");
            try { proc.Kill(entireProcessTree: true); } catch { }
            return 3;
        }
        realPid = Volatile.Read(ref realPid);
        Console.WriteLine($"target pid : {realPid}");

        Thread.Sleep(200);

        // ── Phase A: SIGINT intercepted + ignored ───────────────────────────────────────
        Console.WriteLine("phase A    : sending SIGINT...");
        if (Posix.kill(realPid, Posix.SIGINT) != 0) { Console.Error.WriteLine($"FALSIFIED (Phase A kill failed): errno {Marshal.GetLastWin32Error()}."); try { proc.Kill(entireProcessTree: true); } catch { } return 4; }
        sigintCaught.Wait(InterceptObserveMs);
        Thread.Sleep(PostSoftSignalAliveCheckMs);
        if (!sigintIntercepted) { Console.Error.WriteLine("FALSIFIED Phase A: target did not intercept SIGINT (handler didn't run)."); try { proc.Kill(entireProcessTree: true); } catch { } return 5; }
        if (proc.HasExited) { Console.Error.WriteLine("FALSIFIED Phase A: target exited despite intercepting SIGINT — handler didn't actually ignore."); return 6; }
        Console.WriteLine($"phase A    : SIGINT intercepted + ignored; target alive {PostSoftSignalAliveCheckMs}ms after signal ✓");

        // ── Phase B: SIGTERM intercepted + ignored ──────────────────────────────────────
        Console.WriteLine("phase B    : sending SIGTERM...");
        if (Posix.kill(realPid, Posix.SIGTERM) != 0) { Console.Error.WriteLine($"FALSIFIED (Phase B kill failed): errno {Marshal.GetLastWin32Error()}."); try { proc.Kill(entireProcessTree: true); } catch { } return 4; }
        sigtermCaught.Wait(InterceptObserveMs);
        Thread.Sleep(PostSoftSignalAliveCheckMs);
        if (!sigtermIntercepted) { Console.Error.WriteLine("FALSIFIED Phase B: target did not intercept SIGTERM."); try { proc.Kill(entireProcessTree: true); } catch { } return 5; }
        if (proc.HasExited) { Console.Error.WriteLine("FALSIFIED Phase B: target exited despite intercepting SIGTERM."); return 6; }
        Console.WriteLine($"phase B    : SIGTERM intercepted + ignored; target alive {PostSoftSignalAliveCheckMs}ms after signal ✓");

        // ── Phase C: SIGKILL not catchable ──────────────────────────────────────────────
        Console.WriteLine("phase C    : sending SIGKILL...");
        Stopwatch sw = Stopwatch.StartNew();
        if (Posix.kill(realPid, Posix.SIGKILL) != 0) { Console.Error.WriteLine($"FALSIFIED (Phase C kill failed): errno {Marshal.GetLastWin32Error()}."); return 4; }
        bool exitedFromKill = proc.WaitForExit(KillExitWindowMs);
        sw.Stop();
        if (!exitedFromKill) { Console.Error.WriteLine($"FALSIFIED Phase C: target survived SIGKILL beyond {KillExitWindowMs}ms — kernel-enforced kill failed (impossible)."); return 7; }
        Console.WriteLine($"phase C    : SIGKILL effective; target dead in {sw.ElapsedMilliseconds}ms (exit code {proc.ExitCode}) ✓");

        Console.WriteLine();
        Console.WriteLine($"PROBE 51 PASSED — soft signals (SIGINT, SIGTERM) are intercepted + ignored by misbehaving target; SIGKILL ({sw.ElapsedMilliseconds}ms) is the kernel-mandated non-catchable escape hatch. Layer 2 discipline empirical foundation: substrate's forced-kill is the ONLY mechanism guaranteed against violators.");
        return 0;
    }
}
