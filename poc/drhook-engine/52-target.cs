#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 52 TARGET — CPU-bound tight-loop target
// =============================================================
//
// Models the "stuck in compute" pattern: target runs a pure CPU-bound loop with NO
// I/O, NO async, NO Thread.Sleep, NO signal handlers. Tests whether the .NET CoreCLR
// runtime can deliver signals during pure user-code execution — does the GC/safepoint
// machinery yield often enough that SIGTERM gets a chance to run via default handler?
// Or does the loop continue uninterrupted until SIGKILL?
//
// No explicit handler — observes CoreCLR's default signal disposition under CPU pressure.

using System;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

long counter = 0;
while (true)
{
    counter++;
    // Prevent dead-code elimination so the loop stays hot.
    if (counter == long.MaxValue) Console.WriteLine(counter);
}
