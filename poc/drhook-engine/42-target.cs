#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 42 TARGET — informational-only callback flood (BOUNDED, ADR-008 Increment 2)
// ==================================================================================================
//
// Generates ONLY CallbackKind.Informational mscordbi events (CreateThread + ExitThread)
// via a Thread.Start/Join loop. NO exceptions, NO breakpoints, NO module dynamics —
// nothing that would produce STOPPING callbacks (Exception/Breakpoint/Step).
//
// Substrate consequence (CallbackPump.cs:148–215): the pump worker takes each Informational
// event and calls _resumeHandler!(Continue, 0) ⟶ controller.Continue(0) directly. The worker
// never enters the STOPPING branch, never pushes to _stops, never parks at _resume.Take().
// Worker is ALWAYS either consuming the next event or inside _resumeHandler's COM call.
//
// LIFECYCLE DISCIPLINE (ADR-008 / finding 67 / Increment 2): bounded iteration count for
// natural exit. Previously `while (true)` — Layer 1 discipline violator. Now bounded to
// 500,000 iterations. Empirically calibrated: probe 42 produces ~3000 callbacks/sec under
// continuous attach (1500 iterations/sec). Native (un-attached) rate is higher (~6000
// iterations/sec). Probe 42's 50-cycle run takes ~27s during which substrate is attached
// ~50% of the time. 500,000 iterations gives ≥60s natural runtime with comfortable margin
// for slower machines / probe expansion. If probe takes longer than natural lifetime,
// target exits naturally and substrate handles via finding 66.
//
// Prints "READY <pid>" once the loop has begun. The harness attaches to THIS pid.

using System;
using System.Threading;

const int Iterations = 500_000;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

for (int i = 0; i < Iterations; i++)
{
    Thread t = new(static () => { }) { IsBackground = true };
    t.Start();   // -> CreateThread (Informational)
    t.Join();    // -> ExitThread when the thread body returns (Informational)
}
// Falls through to natural exit (Main returns) — ADR-008 Layer 1 discipline.
