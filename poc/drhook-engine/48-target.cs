#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 48 TARGET — brief observable work + natural exit (BOUNDED, ADR-008 Increment 2)
// =====================================================================================================
//
// Each probe-48 cycle spawns ONE fresh instance of this target, attaches, briefly observes,
// disposes. Multiple cycles per probe-host process — the probe tests whether the substrate
// accumulates per-session mscordbi state across DIFFERENT targets in the SAME host.
//
// LIFECYCLE DISCIPLINE (ADR-008 / finding 67 / Increment 2): brief observable work then
// natural exit. Previously `Thread.Sleep(Timeout.Infinite)` — Layer 1 discipline violator.
// Now: 10 iterations of Thread.Start/Join with 450ms pauses ≈ 5s total natural runtime.
// During probe 48's ~200ms per-cycle observation window, target generates 1-2 observable
// CreateThread/ExitThread callbacks; substrate Disposes; target's remaining iterations
// complete naturally before exit. 5s margin per cycle covers probe-host spawn overhead +
// substrate's SIGTERM grace + ExitWorkSettle.

using System;
using System.Threading;

const int Iterations = 10;
const int PauseMs = 450;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

for (int i = 0; i < Iterations; i++)
{
    Thread t = new(static () => Thread.Sleep(50)) { IsBackground = true };
    t.Start();
    t.Join();
    Thread.Sleep(PauseMs);
}
// Falls through to natural exit (Main returns) — ADR-008 Layer 1 discipline.
