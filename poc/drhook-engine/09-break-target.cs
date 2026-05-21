#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 09 TARGET — calls Debugger.Break() in a loop.
// De-risk question: does Debugger.Break() fire the ICorDebugManagedCallback::Break callback
// (a STOPPING event) under our ICorDebug attach? If yes, it lets us validate the stopping-
// event model without breakpoint-setting machinery. Run through the probe-07 harness first
// (which auto-continues + records a histogram): a non-zero "Break" count confirms it fires.

using System;
using System.Diagnostics;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

while (true)
{
    Debugger.Break(); // -> ICorDebugManagedCallback::Break (slot 5), a stopping event
    Thread.Sleep(20);
}
