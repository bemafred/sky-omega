#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 28 TARGET — a tight loop with a breakpoint location where a NAMED LOCAL `v`
// cycles 0..6. The probe drives ONE target+breakpoint with FOUR different BreakpointPolicy configs
// (conditional / logpoint / hit-count-gated / fault) to prove the unification end-to-end.

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
        GC.KeepAlive(v);   // POLICY_HERE
    }
}
