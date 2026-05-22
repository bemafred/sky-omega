#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 26 — exception breakpoints: does ICorDebugManagedCallback2::Exception fire
// with rich info on macOS/ARM64 CoreCLR? ====================================================
//
// The probe-gated unknown from finding 35. The Exception2 thunk + the v2 vtable + QI for
// IID_ICorDebugManagedCallback2 were already wired; this probe proves the runtime actually INVOKES
// that callback here, classified as a STOPPING event carrying the CorDebugExceptionCallbackType, and
// that the thrown type is resolvable from the live exception object (ICorDebugThread.GetCurrentException
// → runtime class → metadata name). No breakpoint is set: the EXCEPTION is the stop.
//
// The target throws `ProbeException` (its own module) in a loop. The probe resumes, then on each
// Exception stop reports the phase + resolved type name; it PASSES on the first FirstChance stop whose
// type is ProbeException. Stray first-chance exceptions (BCL/JIT) are resumed past, bounded.
//
// Falsification: 2 usage; 3 no READY; 4 attach; 5 no setup Break; 7 no exception stop within budget
//   (the callback never fired / needs enabling — the real unknown); 8 budget exhausted without ever
//   matching ProbeException+FirstChance; 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 26-exception-smoke.cs <path-to-26-exception-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return Exception26.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class Exception26
{
    const string ExpectedType = "ProbeException";
    const int Budget = 40;   // bounded resumes past stray first-chance exceptions

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 26-exception-smoke.cs <path-to-26-exception-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");
        Console.WriteLine($"expecting  : ICorDebugManagedCallback2::Exception (FirstChance) for {ExpectedType}");

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
        ManualResetEventSlim ready = new(false);
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
            }
        }) { IsBackground = true, Name = "target-stdout" };
        reader.Start();
        Thread errDrain = new(() => { while (proc.StandardError.ReadLine() is not null) { } })
        { IsBackground = true, Name = "target-stderr" };
        errDrain.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(90)))
        {
            Console.Error.WriteLine("FALSIFIED (target): no READY sentinel within 90s.");
            KillTree(proc);
            return 3;
        }
        realPid = Volatile.Read(ref realPid);
        Console.WriteLine($"target pid : {realPid}");

        DebugSession session;
        try { session = DebugSession.Attach(realPid, new NullSink()); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FALSIFIED (Attach): {ex.GetType().Name}: {ex.Message}");
            KillTree(proc);
            return 4;
        }
        Console.WriteLine("attached   : DebugSession established");

        int code = Drive(session);

        WriteFixture(realPid, code);
        KillTree(proc);
        Thread.Sleep(TimeSpan.FromMilliseconds(300));
        try { session.Dispose(); } catch { /* best effort */ }
        return code;
    }

    static int Drive(DebugSession session)
    {
        StopInfo? setup = session.WaitForStop(TimeSpan.FromSeconds(10));
        if (setup is null || setup.Reason != StopReason.Break)
        {
            Console.Error.WriteLine($"FALSIFIED (no setup stop): {(setup is null ? "timeout" : setup.Reason.ToString())}.");
            return 5;
        }
        Console.WriteLine("running    : resuming; waiting for an exception callback …");
        session.Resume();

        bool sawAnyException = false;
        for (int i = 0; i < Budget; i++)
        {
            StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(10));
            if (stop is null)
            {
                Console.Error.WriteLine(sawAnyException
                    ? "FALSIFIED: no further stop (exhausted before matching ProbeException+FirstChance)."
                    : "FALSIFIED: no exception stop within budget — ManagedCallback2::Exception never fired.");
                return sawAnyException ? 8 : 7;
            }
            if (stop.Reason != StopReason.Exception)
            {
                session.Resume();
                continue;
            }

            sawAnyException = true;
            string? type = session.GetCurrentExceptionTypeName();
            Console.WriteLine($"exception  : phase={stop.ExceptionKind}  type={type ?? "(unresolved)"}");

            if (stop.ExceptionKind == ExceptionStopKind.FirstChance && type == ExpectedType)
            {
                session.Resume();
                Console.WriteLine($"\nPROBE 26 PASSED — ManagedCallback2::Exception fires with rich info: {ExpectedType} at FirstChance, type resolved from the live exception (no hardcoding).");
                return 0;
            }
            session.Resume();
        }

        Console.Error.WriteLine($"FALSIFIED: {Budget} stops seen, never matched {ExpectedType}+FirstChance.");
        return 8;
    }

    static void KillTree(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    static void WriteFixture(int pid, int code)
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"26-exception-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 26 fixture — ICorDebugManagedCallback2::Exception delivery\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"expected         = {ExpectedType} at FirstChance, type resolved from live exception\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
