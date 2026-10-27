#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
#:property AllowUnsafeBlocks=true
//
// DrHook.Engine probe 65 — does unloading libdbgshim crash a thread that exits afterwards? ==================
//
// Observed (omega-ubuntu, Linux arm64, 2026-10-26): the DrHook integration test host died with SIGSEGV (exit 139)
// in about 1 of 8 runs, ~40 s after its last test, on a NATIVE thread in glibc's thread-exit path
// (start_thread -> __nptl_deallocate_tsd). In the core, one pthread TLS key's destructor pointed into unmapped
// memory at the same offset as live keys owned by libdbgshim.so: an earlier copy of libdbgshim, freed by
// DbgShim.Dispose (NativeLibrary.Free), had left its key registered.
//
// Hypothesis: libdbgshim's embedded PAL registers pthread keys with destructors inside the library and gives any
// thread that calls into it a value for them. If the library is unloaded and such a thread exits afterwards, glibc
// calls a destructor in unmapped memory: SIGSEGV. Keeping the library loaded removes the crash.
//
// Construction: the probe runs itself as a child twice, once with "free" and once with "keep". In the child, a
// worker thread calls one dbgshim export (CreateVersionStringFromModule on a file that is not a runtime — the
// result is irrelevant, only that the PAL ran on that thread), then parks. The main thread frees the library
// ("free") or not ("keep"), then lets the worker exit and waits for it.
//
// Prediction: "free" dies with signal 11 (exit 139) every time on Linux; "keep" exits 0 every time.
// Result codes: 0 hypothesis confirmed; 1 refuted (free survived, or keep crashed); 2 dbgshim not found.
//
// Usage (from poc/drhook-engine):  dotnet run --no-cache 65-dbgshim-unload-tls-smoke.cs [runs]

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

if (args.Length == 2 && args[0] == "child") return Child(args[1] == "free");

int runs = args.Length > 0 ? int.Parse(args[0]) : 5;
string? shim = FindDbgShim();
if (shim is null) { Console.WriteLine("FAIL(2): libdbgshim not found; build the DrHook tests first."); return 2; }
Console.WriteLine($"dbgshim: {shim}\nplatform: {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}");

int freeCrashes = 0, keepCrashes = 0;
foreach (string mode in new[] { "free", "keep" })
{
    for (int i = 1; i <= runs; i++)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("child");
        psi.ArgumentList.Add(mode);
        psi.Environment["PROBE65_DBGSHIM"] = shim;
        psi.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        using Process child = Process.Start(psi)!;
        string output = child.StandardOutput.ReadToEnd() + child.StandardError.ReadToEnd();
        child.WaitForExit();
        bool crashed = child.ExitCode != 0;
        if (crashed && mode == "free") freeCrashes++;
        if (crashed && mode == "keep") keepCrashes++;
        Console.WriteLine($"{mode} run {i}: exit {child.ExitCode} {output.Trim().Replace('\n', ' ')}");
    }
}
Console.WriteLine($"free: {freeCrashes}/{runs} crashed; keep: {keepCrashes}/{runs} crashed");
bool confirmed = freeCrashes == runs && keepCrashes == 0;
Console.WriteLine(confirmed ? "RESULT(0): confirmed — unloading libdbgshim crashes a thread that called into it when it exits"
                            : "RESULT(1): refuted or partial — see the counts");
return confirmed ? 0 : 1;

static unsafe int Child(bool free)
{
    nint lib = NativeLibrary.Load(Environment.GetEnvironmentVariable("PROBE65_DBGSHIM")!);
    var createVersionString = (delegate* unmanaged[Cdecl]<uint, char*, char*, uint, uint*, int>)NativeLibrary.GetExport(lib, "CreateVersionStringFromModule");

    using var called = new ManualResetEventSlim();
    using var mayExit = new ManualResetEventSlim();
    int hr = 0;
    var worker = new Thread(() =>
    {
        fixed (char* module = Environment.ProcessPath!)
        {
            uint needed = 0;
            hr = createVersionString((uint)Environment.ProcessId, module, null, 0, &needed);
        }
        called.Set();
        mayExit.Wait();
    }) { IsBackground = false, Name = "probe65-dbgshim-caller" };
    worker.Start();
    called.Wait();

    if (free) NativeLibrary.Free(lib);
    mayExit.Set();
    worker.Join();
    Console.WriteLine($"child({(free ? "free" : "keep")}): worker exited cleanly, hr=0x{hr:X8}");
    return 0;
}

static string? FindDbgShim()
{
    string rid = (OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsWindows() ? "win" : "linux") + "-" +
                 RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    string lib = OperatingSystem.IsWindows() ? "dbgshim.dll" : OperatingSystem.IsMacOS() ? "libdbgshim.dylib" : "libdbgshim.so";
    string candidate = Path.GetFullPath(Path.Combine("..", "..", "tests", "DrHook.Engine.IntegrationTests", "bin", "Debug", "net10.0", "runtimes", rid, "native", lib));
    return File.Exists(candidate) ? candidate : null;
}
