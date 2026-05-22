#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 18 TARGET — a method with named locals at known values.
// Worker.Step(5) computes a = 6 (int) and b = 60 (long); the probe breaks at the marked line
// (where both are assigned) and reads the named locals back. Marker token kept off the header
// comment so the probe's text search matches only the code line (lesson from probe 17).

using System;
using System.Diagnostics;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

for (int i = 0; i < 500 && !Debugger.IsAttached; i++) Thread.Sleep(10);
Debugger.Break();

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
        long b = a * 10L;
        Acc += b;   // LOCALS_READY
    }
}
