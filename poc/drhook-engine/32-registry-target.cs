#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 32 TARGET — a worker with two marked code lines and a method
// (Worker.Step) used as a function-entry breakpoint location. The probe exercises the registry
// (set / list / remove / clear) without ever HITTING a breakpoint — the target just runs the loop
// and is killed at the end. Marker tokens are kept OFF the header comment so the marker finder
// resolves them to actual code lines (see probe 17 first-run lesson).

using System;
using System.Diagnostics;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

for (int i = 0; i < 500 && !Debugger.IsAttached; i++) Thread.Sleep(10);
Debugger.Break();

var w = new Worker();
int n = 0;
while (true)
{
    w.Step(n++ % 7);
    Thread.Sleep(20);
}

sealed class Worker
{
    public void Step(int v)
    {
        int a = v;
        int b = v * 2;       // BREAK_A
        int c = v + 10;      // BREAK_B
        GC.KeepAlive((a, b, c));
    }
}
