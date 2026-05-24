#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 47 TARGET — long-running observation target (no flood)
// ===========================================================================
//
// Minimal long-running process: print READY + park. Generates NO mscordbi callbacks
// (no thread creation, no exceptions, no module dynamics) — the probe tests the
// substrate's external-death-detection path, not callback-flood interaction.
//
// Prints "READY <pid>" once the loop has begun (Environment.ProcessId — the real
// managed process). Sleeps until the probe kills it.

using System;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

Thread.Sleep(Timeout.Infinite);
