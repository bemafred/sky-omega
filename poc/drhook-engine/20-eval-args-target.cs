#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 20 TARGET — a static method taking an argument.
// Probe.Doubled(21) returns 42. The probe func-evals it with the argument 21 built as an eval
// value, validating CallFunction WITH arguments. Loops Debugger.Break() for a stop.

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
    public static int Doubled(int x) => x * 2;
}
