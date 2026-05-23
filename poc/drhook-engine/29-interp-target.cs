#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 29 TARGET — a tight loop with a named local `v` cycling 0..6, used by a
// logpoint whose message comes from a ROSLYN-PARSED interpolated string ($"v={v} doubled={2*v}")
// rather than a hand-written renderer lambda. Same target shape as probe 28; this probe is about
// the front end, not the engine.

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
    w.Step(n++ % 7);     // v will cycle 0..6
    Thread.Sleep(20);
}

sealed class Worker
{
    public void Step(int value)
    {
        int v = value;
        GC.KeepAlive(v);   // INTERP_HERE
    }
}
