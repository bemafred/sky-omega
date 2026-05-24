#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 44 TARGET — minimal idle loop. NO Debugger.Break (so re-attach works
// across cycles without a one-time setup stop), NO exception stream (so Pause-stop arrives
// without an Exception ahead of it in the pump's _events queue). Just READY + while-true-sleep.

using System;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

while (true) Thread.Sleep(100);
