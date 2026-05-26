#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 51 TARGET — INTENTIONAL LAYER-1-VIOLATOR for ignoring-handler scenario testing
// =====================================================================================================
//
// Models the "lifecycle violator" pattern (Claude Chat App lens): target catches
// both SIGINT and SIGTERM via .NET signal handlers, marks Cancel = true on both,
// and continues running. Designed to validate Layer 2 discipline — soft signals
// can be ignored by misbehaving targets; only SIGKILL forces termination.
//
// LIFECYCLE DISCIPLINE NOTE (ADR-008 / finding 67 / Increment 2):
// This target INTENTIONALLY remains a Layer 1 violator (while-true loop, Cancel=true
// signal handlers that suppress default termination). The probe's substrate-correctness
// hypothesis is "what does the substrate do when a target catches AND ignores soft
// signals?" — that scenario REQUIRES the target to catch SIGINT/SIGTERM and refuse
// to exit. The target's "violator" shape IS the test variable.
//
// Also used by probe 55 (substrate's two-stage SIGTERM-then-SIGKILL escalation
// validation) for the same reason: substrate's Stage 1 must time out, and that
// requires the target to ignore SIGTERM.
//
// Other probe targets (07, 42, 48 per Increment 2) were redesigned to natural exit.
// This target's purpose requires it to remain a violator; the redesign discipline
// does not apply here.

using System;
using System.Runtime.InteropServices;
using System.Threading;

Console.CancelKeyPress += (sender, args) =>
{
    Console.WriteLine("SIGINT_INTERCEPTED_AND_IGNORED");
    Console.Out.Flush();
    args.Cancel = true;
};

PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
{
    Console.WriteLine("SIGTERM_INTERCEPTED_AND_IGNORED");
    Console.Out.Flush();
    ctx.Cancel = true;
});

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

while (true) Thread.Sleep(100);
