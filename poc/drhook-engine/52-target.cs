#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 52 TARGET — INTENTIONAL LAYER-1-VIOLATOR for tight-CPU-loop scenario testing
// ===================================================================================================
//
// Models the "stuck in compute" pattern: target runs a pure CPU-bound loop with NO
// I/O, NO async, NO Thread.Sleep, NO signal handlers. Tests whether the .NET CoreCLR
// runtime can deliver signals during pure user-code execution — does the GC/safepoint
// machinery yield often enough that SIGTERM gets a chance to run via default handler?
// Or does the loop continue uninterrupted until SIGKILL?
//
// LIFECYCLE DISCIPLINE NOTE (ADR-008 / finding 67 / Increment 2):
// This target INTENTIONALLY remains a Layer 1 violator (while-true tight CPU loop,
// no signal handlers). The probe's hypothesis is "what does CoreCLR's default signal
// disposition do for a target that has no async safepoints?" — that scenario REQUIRES
// the target to be in a tight CPU-bound loop. The "violator" shape IS the test variable.
//
// Finding 68 empirically confirmed CoreCLR's signal-delivery layer IS robust against
// this — SIGTERM exits the target in ~16ms even under tight user code. The target's
// violator shape is what made that finding observable.

using System;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

long counter = 0;
while (true)
{
    counter++;
    // Prevent dead-code elimination so the loop stays hot.
    if (counter == long.MaxValue) Console.WriteLine(counter);
}
