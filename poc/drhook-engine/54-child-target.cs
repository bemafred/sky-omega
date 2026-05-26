#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 54 CHILD TARGET — INTENTIONAL LAYER-1-VIOLATOR (see 54-target.cs note)
// Spawned by 54-target; reports its own PID. Parked until probe terminates the tree.

using System;
using System.Threading;

Console.WriteLine($"CHILD_READY {Environment.ProcessId}");
Console.Out.Flush();

Task.Delay(Timeout.Infinite).Wait();
