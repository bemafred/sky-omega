#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 53 TARGET — no signal handler (default CoreCLR behavior)
// ==============================================================================
//
// No Console.CancelKeyPress, no PosixSignalRegistration. Parked in Task.Delay so
// any reasonable signal delivery has an obvious moment to wake the process up.
// Probes CoreCLR's default SIGINT and SIGTERM disposition separately (two runs
// of the probe, one per signal).

using System;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

Task.Delay(Timeout.Infinite).Wait();
