#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 19 TARGET — a static method to func-eval.
// Probe.Answer() returns 42. The probe attaches, stops at Debugger.Break(), and func-evals
// Probe.Answer() in this process. The method need not be called by the target — func-eval JITs
// and runs it on demand. Loops Debugger.Break() so a stop is always available after attach.

using System;
using System.Diagnostics;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

for (int i = 0; i < 500 && !Debugger.IsAttached; i++) Thread.Sleep(10);

while (true)
{
    Debugger.Break();
    Thread.Sleep(50);
}

static class Probe
{
    public static int Answer() => 42;
}
