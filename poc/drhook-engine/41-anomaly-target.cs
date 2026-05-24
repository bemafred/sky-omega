#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 41 TARGET — minimal program with a local variable and a marker line
// for the breakpoint. The breakpoint is set at the trailing marker comment; once stopped,
// the probe calls InspectVariablesAsync(depth=999) to force the DepthClamped anomaly to
// surface twice (once from GetLocals, once from GetArguments — both clamp paths exercised).
//
// The local "ignored" exists only to give GetLocals something non-empty to walk; the probe
// asserts on the anomaly surface, not on the local's value.

using System;
using System.Diagnostics;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

for (int i = 0; i < 500 && !Debugger.IsAttached; i++) Thread.Sleep(10);
Debugger.Break();

while (true)
{
    Step();
    Thread.Sleep(20);
}

static void Step()
{
    var ignored = new { Tag = "depth-clamp-target", Count = 1 };
    GC.KeepAlive(ignored);   // ANOMALY_HERE
}
