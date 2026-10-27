#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
//
// DrHook.Engine probe 66 target — an idle process to attach to as early as possible.
//
// Prints its PID, then sleeps in short steps for up to 20 s so a Pause has a running thread to stop. Exits by itself.

using System;
using System.Threading;

Console.WriteLine($"PROBE66_TARGET pid={Environment.ProcessId}");
Console.Out.Flush();
for (int i = 0; i < 400; i++) Thread.Sleep(50);
