#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 54 CHILD TARGET — spawned by 54-target; reports its own PID.

using System;
using System.Threading;

Console.WriteLine($"CHILD_READY {Environment.ProcessId}");
Console.Out.Flush();

Task.Delay(Timeout.Infinite).Wait();
