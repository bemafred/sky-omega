#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe 11 — metadata resolution (ADR-006 Phase 2, breakpoint setting 4b)
// =====================================================================================
//
// To set a breakpoint we need the method's mdMethodDef token. This probe validates resolving
// one by name: at a stop, DebugSession.ResolveMethodToken walks to the target module (4a), gets
// IMetaDataImport, FindTypeDefByName("Worker") → EnumMethodsWithName("Tick") → token.
//
// Checks: the token is non-zero, is an mdMethodDef (high byte 0x06), and a bogus method name
// resolves to 0 (no false positive).
//
// Falsification: 2 usage; 3 no READY; 4 attach; 5 no Break stop; 6 token 0 (resolution failed);
//   7 token not an mdMethodDef (resolved the wrong thing — bad slot/GUID); 8 bogus name resolved
//   non-zero (false positive); 0 PASS.
//
// Usage:  DBGSHIM_PATH=<libdbgshim> dotnet 11-metadata-smoke.cs <path-to-11-bp-target.cs>

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using SkyOmega.DrHook.Engine;

return Metadata11.Run(args);

sealed class NullSink : IDebugEventSink
{
    public void OnEvent(string name) { }
}

static class Metadata11
{
    const string ModuleSubstr = "11-bp-target";
    const string TypeName = "Worker";
    const string MethodName = "Tick";

    public static int Run(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet 11-metadata-smoke.cs <path-to-11-bp-target.cs>");
            return 2;
        }

        Console.WriteLine($"runtime    : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"dbgshim    : {Environment.GetEnvironmentVariable("DBGSHIM_PATH") ?? "(resolver default)"}");

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

        int code = 0;
        uint token = 0;
        StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(10));
        if (stop is null || stop.Reason != StopReason.Break)
        {
            Console.Error.WriteLine($"FALSIFIED (no stop): got {(stop is null ? "timeout" : stop.Reason.ToString())}, expected Break.");
            code = 5;
        }
        else
        {
            Console.WriteLine($"stopped    : {stop.Reason} — resolving {TypeName}.{MethodName} in {ModuleSubstr}");
            token = session.ResolveMethodToken(ModuleSubstr, TypeName, MethodName);
            uint bogus = session.ResolveMethodToken(ModuleSubstr, TypeName, "NoSuchMethod");
            Console.WriteLine($"token      : 0x{token:X8}  (table 0x{token >> 24:X2}, rid {token & 0x00FFFFFF})");
            Console.WriteLine($"bogus      : 0x{bogus:X8}");

            if (token == 0) { Console.Error.WriteLine("FALSIFIED: token 0 — could not resolve Worker.Tick."); code = 6; }
            else if ((token >> 24) != 0x06) { Console.Error.WriteLine($"FALSIFIED: token table 0x{token >> 24:X2} != 0x06 (mdMethodDef)."); code = 7; }
            else if (bogus != 0) { Console.Error.WriteLine("FALSIFIED: a non-existent method resolved to a non-zero token."); code = 8; }

            session.Resume();
        }

        if (code == 0)
            Console.WriteLine($"\nPROBE 11 PASSED — resolved {TypeName}.{MethodName} → mdMethodDef 0x{token:X8}; bogus name → 0.");

        WriteFixture(realPid, token, code);
        session.Dispose();
        KillTree(proc);
        return code;
    }

    static void KillTree(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    static void WriteFixture(int pid, uint token, int code)
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "fixtures");
        Directory.CreateDirectory(dir);
        string rid = RuntimeInformation.RuntimeIdentifier;
        string ts = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"11-metadata-{rid}-{ts}.txt");
        string body =
            "# DrHook.Engine probe 11 fixture — metadata resolution (ADR-006 Phase 2, 4b)\n" +
            $"timestamp        = {DateTime.UtcNow:O}\n" +
            $"runtime          = {RuntimeInformation.FrameworkDescription}\n" +
            $"os-arch          = {rid}\n" +
            $"target-pid       = {pid}\n" +
            $"resolved         = {TypeName}.{MethodName}\n" +
            $"token            = 0x{token:X8}\n" +
            $"is-mdMethodDef   = {(token >> 24) == 0x06}\n" +
            $"verdict          = {(code == 0 ? "PASSED" : $"FALSIFIED-{code}")}\n";
        File.WriteAllText(path, body);
        Console.WriteLine($"fixture    : {path}");
    }
}
