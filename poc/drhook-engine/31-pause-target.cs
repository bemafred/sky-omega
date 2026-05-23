#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 31 TARGET — a tight loop with no breakpoints, no exceptions, no Debugger.Break
// after the initial attach sentinel. The probe attaches at the startup Break, resumes, lets the
// target run for a moment, then calls DebugSession.Pause to AsyncBreak it.

using System;
using System.Diagnostics;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

for (int i = 0; i < 500 && !Debugger.IsAttached; i++) Thread.Sleep(10);
Debugger.Break();

long n = 0;
while (true)
{
    n++;
    if ((n & 0xFFFFF) == 0) Thread.Yield();   // be a touch friendly to the scheduler
}
