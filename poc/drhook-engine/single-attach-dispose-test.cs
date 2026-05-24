#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
// Minimal regression probe: spawn 07-target, single Attach + Dispose, check exit code.

using System;
using System.Diagnostics;
using System.Threading;
using SkyOmega.DrHook.Engine;

return Run();

static int Run()
{
    Process proc = new()
    {
        StartInfo = new ProcessStartInfo("dotnet", "/Users/bemafred/src/repos/sky-omega/poc/drhook-engine/07-target.cs")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        }
    };
    proc.Start();
    int pid = -1;
    ManualResetEventSlim ready = new(false);
    new Thread(() =>
    {
        string? line;
        while ((line = proc.StandardOutput.ReadLine()) is not null)
        {
            if (line.StartsWith("READY "))
            {
                pid = int.Parse(line.AsSpan(6));
                ready.Set();
            }
        }
    })
    { IsBackground = true }.Start();
    if (!ready.Wait(30000)) { Console.Error.WriteLine("no READY"); proc.Kill(true); return 1; }

    Console.WriteLine($"pid={pid}, attaching...");
    DebugSession session = DebugSession.Attach(pid, new Sink());
    Console.WriteLine("attached.");
    Thread.Sleep(500);
    Console.WriteLine("disposing...");
    session.Dispose();
    Console.WriteLine("disposed.");
    Thread.Sleep(200);
    Console.WriteLine("cleanup...");
    try { if (!proc.HasExited) proc.Kill(true); } catch { }
    Console.WriteLine("PASS.");
    return 0;
}

sealed class Sink : IDebugEventSink { public void OnEvent(string n) { } }
