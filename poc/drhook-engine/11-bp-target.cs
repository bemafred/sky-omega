#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 11/12 TARGET — a known method to resolve + break on.
// Calls Debugger.Break() once at startup to give the debugger a synchronized window to resolve
// the method token (probe 11) and set a breakpoint (probe 12) before the loop runs. Then calls
// Worker.Tick() in a loop — the method probe 12 breaks on. Tick increments a field so its body
// is real work (not elided). Runs until killed.

using System;
using System.Diagnostics;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

// Wait for the debugger to attach before the single setup break, so it can't race ahead of
// attach (probe 09 looped Break; this target breaks once). Capped so it never hangs forever.
for (int i = 0; i < 500 && !Debugger.IsAttached; i++) Thread.Sleep(10);
Debugger.Break(); // setup stop — resolve Worker.Tick / set the breakpoint here

var worker = new Worker();
while (true)
{
    worker.Tick();
    Thread.Sleep(20);
}

sealed class Worker
{
    public long Ticks;
    public void Tick() => Ticks++; // breakpoint target
}
