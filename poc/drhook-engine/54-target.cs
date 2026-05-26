#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 54 PARENT TARGET — spawns 2 children, then parks.
// Children print CHILD_READY <pid>; parent prints PARENT_READY <pid> and CHILDREN_PID <p1> <p2>.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: 54-target.cs <path-to-54-child-target.cs>");
    Environment.Exit(2);
}

string childTargetPath = args[0];

Process child1 = new()
{
    StartInfo = new ProcessStartInfo("dotnet", $"\"{childTargetPath}\"")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    }
};
child1.Start();

Process child2 = new()
{
    StartInfo = new ProcessStartInfo("dotnet", $"\"{childTargetPath}\"")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    }
};
child2.Start();

// Forward children stdout lines (CHILD_READY) up to our stdout so the probe parses
// children's PIDs via our stdout.
Thread c1 = new(() => { string? l; while ((l = child1.StandardOutput.ReadLine()) is not null) Console.WriteLine(l); }) { IsBackground = true };
Thread c2 = new(() => { string? l; while ((l = child2.StandardOutput.ReadLine()) is not null) Console.WriteLine(l); }) { IsBackground = true };
c1.Start(); c2.Start();

Console.WriteLine($"PARENT_READY {Environment.ProcessId}");
Console.Out.Flush();

Task.Delay(Timeout.Infinite).Wait();
