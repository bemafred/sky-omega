#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 58 TARGET — increments a local `counter` in a loop, so an attached debugger
// can attach a logpoint breakpoint (Suspend.None) whose LogMessage template interpolates `{counter}`.
// Each iteration is one hit; the substrate's template compiler renders the message and the
// IDebugEventSink.OnLog flow emits a structured LogRecord without surfacing a stop. The probe
// asserts N well-formed log lines accumulate via RecordingSink.

using System;
using System.Diagnostics;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

for (int i = 0; i < 500 && !Debugger.IsAttached; i++) Thread.Sleep(10);
Debugger.Break();

int counter = 0;
while (true)
{
    Probe(counter); // LOGPOINT_HERE  — probe sets the breakpoint at this line
    counter++;
    Thread.Sleep(20);
}

static void Probe(int counter) { /* substrate observation point */ }
