#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 07 TARGET — continuous managed-event generator
// ===================================================================
//
// Modern CoreCLR replays NO catch-up Create/Load events on attach (probe 05 finding): a
// parked target yields zero ICorDebug callbacks, so probe 05 attached cleanly yet observed
// nothing. This target instead churns managed threads and throws/catches in a tight loop, so
// that once the engine attaches every iteration produces synchronized stops — CreateThread +
// ExitThread per spawned thread, plus a first-chance Exception — for the continue-loop to
// drain. Each stop requires the debugger to Continue; a working loop sees a stream, a broken
// one wedges after the first. Runs until the harness kills it.
//
// Prints "READY <pid>" once the CLR is up and the loop has begun. The harness attaches to
// THIS pid (Environment.ProcessId — the real managed process), which can differ from the
// launched `dotnet` pid, and starts observing only after the sentinel.

using System;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

while (true)
{
    Thread t = new(static () => { }); // -> CreateThread, then ExitThread on completion
    t.Start();
    t.Join();

    try { throw new InvalidOperationException("drhook-smoke"); }
    catch { /* first-chance Exception callback */ }

    Thread.Sleep(20);
}
