#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
//
// Probe 107's target. Built once to a dll and run as `dotnet <dll> <mode> [lingerMs]`, so the process the probe
// spawns is the runtime that runs this code (`dotnet run x.cs` would put the SDK's own .NET processes in front).
//
// Every mark is "<name> <UTC ticks>" on its own line, flushed, so the probe can order it against its own marks.
//   modinit  - [ModuleInitializer]: the first managed code of this assembly (Q2: must come after the probe's resume)
//   main     - top of Main
//   env      - this process's view of DOTNET_DiagnosticPorts (Q3: is it consumed or unset by the runtime?)
// Modes:
//   plain [lingerMs]  - marks, then stays alive lingerMs (default 0) and exits 0. The linger keeps the runtime loaded
//                       when hold-control registers 2 s after the spawn, so dbgshim sees a live, started runtime.
//   spawn-child       - marks, then waits for one line on stdin (the probe's go marker), then runs
//                       `dotnet <this dll> plain` as its child, relays the child's stdout prefixed "child: ", waits for
//                       it (at most 90 s) and exits 0.

using System.Diagnostics;
using System.Runtime.CompilerServices;

string mode = args.Length > 0 ? args[0] : "plain";
Marks.Say("main");
Console.WriteLine($"env DOTNET_DiagnosticPorts={Environment.GetEnvironmentVariable("DOTNET_DiagnosticPorts") ?? "(unset)"}");
Console.Out.Flush();

if (mode == "plain")
{
    int linger = args.Length > 1 ? int.Parse(args[1]) : 0;
    if (linger > 0) Thread.Sleep(linger);
    Marks.Say("exit");
    return 0;
}

if (mode == "spawn-child")
{
    string? go = Console.ReadLine();
    Marks.Say($"go({go ?? "eof"})");
    var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, UseShellExecute = false };
    psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "107-launch-hold-target.dll"));
    psi.ArgumentList.Add("plain");
    using var child = Process.Start(psi)!;
    Console.WriteLine($"child-pid {child.Id}");
    Console.Out.Flush();
    child.OutputDataReceived += (_, e) =>
    {
        if (e.Data is null) return;
        Console.WriteLine("child: " + e.Data);
        Console.Out.Flush();
    };
    child.BeginOutputReadLine();
    if (!child.WaitForExit(90_000))
    {
        Marks.Say("child-timeout");
        return 3;
    }
    child.WaitForExit();                                   // drains the relayed output
    Marks.Say($"child-exit({child.ExitCode})");
    return 0;
}

Console.WriteLine($"unknown mode {mode}");
return 2;

static class Marks
{
    public static void Say(string name)
    {
        Console.WriteLine($"{name} {DateTime.UtcNow.Ticks}");
        Console.Out.Flush();
    }
}

static class ModuleInit
{
    [ModuleInitializer]
    internal static void Run() => Marks.Say("modinit");
}
