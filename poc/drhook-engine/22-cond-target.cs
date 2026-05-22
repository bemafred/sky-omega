#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 22 TARGET — a loop with a varying local for a CONDITIONAL breakpoint.
// Worker.Step(n) copies n into a local `value` that cycles 0..6; the probe sets an unconditional
// breakpoint at the marked line and a condition `value == 3`, and must stop only on the iteration
// where value == 3. Marker token kept off the header comment.

using System;
using System.Diagnostics;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

for (int i = 0; i < 500 && !Debugger.IsAttached; i++) Thread.Sleep(10);
Debugger.Break();

var worker = new Worker();
int n = 0;
while (true)
{
    worker.Step(n);
    n = (n + 1) % 7;
    Thread.Sleep(20);
}

sealed class Worker
{
    public void Step(int n)
    {
        int value = n;
        GC.KeepAlive(value);   // COND_HERE
    }
}
