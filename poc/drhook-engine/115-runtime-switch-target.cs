#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
//
// DrHook.Engine probe 115 target — a program that runs on whichever runtime it was built for, and gives a line to stop
// at. Probe 115 builds it twice: as here, for .NET 10, and with -p:TargetFramework=net9.0.
//
// Usage: 115-runtime-switch-target. It loops for at most 60 s; a session ends it long before.

using System;
using System.Threading;

string runtime = Environment.Version.ToString();
for (int beat = 1; beat <= 1200; beat++)
{
    int doubled = beat * 2;   // PROBE115_STOP
    GC.KeepAlive(runtime);
    Thread.Sleep(50 + doubled % 2);
}
