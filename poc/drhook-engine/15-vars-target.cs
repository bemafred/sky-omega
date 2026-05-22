#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 15 TARGET — a method with known argument values to read.
// Worker.Compute(int n, long total) is called with (7, 100) in a loop. At a breakpoint on
// Compute's entry the arguments are live (this, n=7, total=100) while locals are not yet set —
// so reading ARGUMENTS at entry is the clean v1 case. Waits for the debugger before the setup
// break so it can't race attach (see finding 18).

using System;
using System.Diagnostics;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

for (int i = 0; i < 500 && !Debugger.IsAttached; i++) Thread.Sleep(10);
Debugger.Break(); // setup stop — resolve Worker.Compute / set the breakpoint here

var worker = new Worker();
while (true)
{
    worker.Compute(7, 100L);
    Thread.Sleep(20);
}

sealed class Worker
{
    public long Acc;
    public void Compute(int n, long total) => Acc += n + total; // args: this(0), n(1)=7, total(2)=100
}
