#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 59 target — minimal CLR program that prints a marker to stdout.
//
// Spawned SUSPENDED by 59-spawn-stdio-smoke.cs with stdout/stderr dup2'd to a probe-owned pipe,
// then resumed via SIGCONT. If the redirection worked, the marker lands in the probe's pipe —
// NOT the probe process's own stdout. The brief sleep gives the runtime-startup callback time.

using System;
using System.Threading;

Console.WriteLine($"PROBE59_TARGET_STDOUT pid={Environment.ProcessId}");
Console.Out.Flush();
Thread.Sleep(300);
Console.Error.WriteLine("PROBE59_TARGET_STDERR");
Console.Error.Flush();
Console.WriteLine("PROBE59_TARGET_DONE");
Console.Out.Flush();
