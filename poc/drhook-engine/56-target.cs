#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 56 TARGET — Debugger.Break() halt target
// =============================================================
//
// Bare-bones target that:
//   1. Prints "READY <pid>" + flushes (so probe can extract PID + Attach)
//   2. Waits up to 30 s for Debugger.IsAttached (set by substrate's Attach)
//   3. Calls Debugger.Break() — halts at this line, surfaces to substrate
//      as StopReason.Break with a valid _stopThread.
//   4. If we ever resume past Break(), print "DONE" and exit naturally (bounded
//      via 60-s safety timeout to avoid orphan if probe doesn't resume us).
//
// Purpose: probe 56 substrate-correctness investigation. Exercises the scenario
// where substrate's Dispose runs against a target halted at Break stop —
// reproduced by AnomalyInjectionTest's failure during Phase 8b. Validates
// whether substrate's SIGTERM-then-SIGKILL escalation (ADR-008 Increment 1)
// terminates Break-halted targets cleanly.

using System;
using System.Diagnostics;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

// Wait for substrate Attach (Debugger.IsAttached transitions to true when mscordbi's
// CreateProcess event sees us). Bounded by a safety timeout so a no-attach scenario
// doesn't orphan this process.
Stopwatch sw = Stopwatch.StartNew();
while (!Debugger.IsAttached && sw.Elapsed < TimeSpan.FromSeconds(30))
{
    Thread.Sleep(50);
}

if (!Debugger.IsAttached)
{
    Console.Error.WriteLine("TARGET: no debugger attached within 30s — exiting naturally.");
    Environment.Exit(2);
}

Console.WriteLine("BREAKING");
Console.Out.Flush();
Debugger.Break();
Console.WriteLine("DONE");
Console.Out.Flush();
