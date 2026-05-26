#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 07 TARGET — managed-event generator (BOUNDED per ADR-008 Increment 2)
// ============================================================================================
//
// Modern CoreCLR replays NO catch-up Create/Load events on attach (probe 05 finding): a
// parked target yields zero ICorDebug callbacks, so probe 05 attached cleanly yet observed
// nothing. This target instead churns managed threads and throws/catches in a tight loop, so
// that once the engine attaches every iteration produces synchronized stops — CreateThread +
// ExitThread per spawned thread, plus a first-chance Exception — for the continue-loop to
// drain. Each stop requires the debugger to Continue; a working loop sees a stream, a broken
// one wedges after the first.
//
// LIFECYCLE DISCIPLINE (ADR-008 / finding 67 / Increment 2): bounded iteration count for
// natural exit. Previously `while (true)` — Layer 1 discipline violator. Now bounded to
// 3000 iterations × ~30ms per iteration ≈ 90s natural runtime. Substrate-correctness probes
// (07, 43, 44, 45) all complete in well under that window; if a probe ever takes longer than
// 90s, target exits naturally and substrate handles it via finding 66 death-detection routing.
// No more `while (true)`; no more dependency on substrate kill for shutdown.
//
// Prints "READY <pid>" once the CLR is up and the loop has begun. The harness attaches to
// THIS pid (Environment.ProcessId — the real managed process), which can differ from the
// launched `dotnet` pid, and starts observing only after the sentinel.

using System;
using System.Threading;

const int Iterations = 3000;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

for (int i = 0; i < Iterations; i++)
{
    Thread t = new(static () => { }); // -> CreateThread, then ExitThread on completion
    t.Start();
    t.Join();

    try { throw new InvalidOperationException("drhook-smoke"); }
    catch { /* first-chance Exception callback */ }

    Thread.Sleep(20);
}
// Falls through to natural exit (Main returns) — ADR-008 Layer 1 discipline.
