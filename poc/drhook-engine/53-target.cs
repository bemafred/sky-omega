#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 53 TARGET — INTENTIONAL LAYER-1-VIOLATOR for default-disposition baseline
// ================================================================================================
//
// No Console.CancelKeyPress, no PosixSignalRegistration. Parked in Task.Delay so
// any reasonable signal delivery has an obvious moment to wake the process up.
// Probes CoreCLR's default SIGINT and SIGTERM disposition separately (two runs
// of the probe, one per signal).
//
// LIFECYCLE DISCIPLINE NOTE (ADR-008 / finding 67 / Increment 2):
// This target INTENTIONALLY remains a Layer 1 violator (Task.Delay(Timeout.Infinite),
// no signal handlers). The probe's hypothesis is "what is CoreCLR's default signal
// disposition for a target that registers NO explicit handlers?" — that scenario
// REQUIRES the target to have no handlers and be parked, so any termination comes
// from CoreCLR's default behavior, not user code. The "no-handler" shape IS the
// test variable.

using System;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

Task.Delay(Timeout.Infinite).Wait();
