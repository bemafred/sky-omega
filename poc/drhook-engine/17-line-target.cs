#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 17 TARGET — a multi-statement method for a SOURCE-LINE breakpoint.
// Worker.Step has several statements; the probe reads this file, finds the marker-comment line
// (on the middle statement), and sets a breakpoint at THAT line (a non-entry IL offset) to prove
// file:line breakpoints bind mid-method. Waits for the debugger before the setup break (finding 18).

using System;
using System.Diagnostics;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

for (int i = 0; i < 500 && !Debugger.IsAttached; i++) Thread.Sleep(10);
Debugger.Break(); // setup stop — set the line breakpoint here

var worker = new Worker();
while (true)
{
    worker.Step(5);
    Thread.Sleep(20);
}

sealed class Worker
{
    public long Acc;
    public void Step(int n)
    {
        int a = n + 1;
        int b = a * 2;   // BREAK_HERE
        Acc += b;
    }
}
