#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 42 TARGET — informational-only callback flood
// ==================================================================
//
// Generates ONLY CallbackKind.Informational mscordbi events (CreateThread + ExitThread)
// via a tight Thread.Start/Join loop. NO exceptions, NO breakpoints, NO module dynamics —
// nothing that would produce STOPPING callbacks (Exception/Breakpoint/Step).
//
// Substrate consequence (CallbackPump.cs:148–215): the pump worker takes each Informational
// event and calls _resumeHandler!(Continue, 0) ⟶ controller.Continue(0) directly. The worker
// never enters the STOPPING branch, never pushes to _stops, never parks at _resume.Take().
// Worker is ALWAYS either consuming the next event or inside _resumeHandler's COM call.
//
// This is the target shape probe 42's stated hypothesis requires: "Dispose during the
// worker's _resumeHandler(...) call." The original probe 42 used 07-target.cs whose
// throw/catch loop produces Exception (STOPPING) callbacks — those park the worker at
// _resume.Take, not inside _resumeHandler. The replacement target restores hypothesis-
// construction alignment.
//
// Prints "READY <pid>" once the loop has begun (Environment.ProcessId — the real managed
// process). Runs until the harness kills it.

using System;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

while (true)
{
    Thread t = new(static () => { }) { IsBackground = true };
    t.Start();   // -> CreateThread (Informational)
    t.Join();    // -> ExitThread when the thread body returns (Informational)
}
