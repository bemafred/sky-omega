#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 47 TARGET — INTENTIONAL LAYER-1-VIOLATOR for external-death-scenario testing
// ===================================================================================================
//
// Minimal long-running process: print READY + park. Generates NO mscordbi callbacks
// (no thread creation, no exceptions, no module dynamics) — the probe tests the
// substrate's external-death-detection path, not callback-flood interaction.
//
// LIFECYCLE DISCIPLINE NOTE (ADR-008 / finding 67 / Increment 2):
// This target INTENTIONALLY remains a Layer 1 violator (Thread.Sleep(Timeout.Infinite),
// no signal handlers, no natural exit). Probe 47's substrate-correctness hypothesis is
// "what happens when the OS or external code terminates a target we are observing as
// Borrowed?" That scenario requires the target to be ALIVE when the probe sends the
// external kill — i.e., not exit naturally first. The substrate's Layer 2 guard
// (finding 66 death-detection + ADR-008 Stage-2 SIGKILL fallback) handles the
// violator scenario cleanly. The target's "violator" shape IS the test variable.
//
// Other probe targets (07, 42, 48 per Increment 2) were redesigned to natural exit
// because their probes have no test-purpose reason to need infinite duration. This
// target's purpose requires it; the redesign discipline does not apply here.
//
// Prints "READY <pid>" once the loop has begun (Environment.ProcessId — the real
// managed process). Sleeps until the probe kills it (which is the test).

using System;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

Thread.Sleep(Timeout.Infinite);
