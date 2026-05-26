#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 49 TARGET — well-behaved CLI: Console.CancelKeyPress handler
// =================================================================================
//
// Models the canonical "well-behaved CLI tool" pattern: target installs a signal
// handler via .NET's Console.CancelKeyPress event, which catches SIGINT (Unix) /
// CTRL_C_EVENT (Windows). On signal, target performs observable cleanup work and
// exits cleanly with exit code 0.
//
// Validates Layer 1 discipline (finding 67, ADR-008): well-implemented processes
// respond to soft termination requests with graceful cleanup.
//
// Behavior:
//   1. Print "READY <pid>" + flush.
//   2. Install Console.CancelKeyPress handler. When signal arrives, the handler:
//      - Writes "GRACEFUL_CLEANUP_DONE" to stdout (probe observes this).
//      - Sets the cancellation token + Environment.Exit(0).
//   3. Wait indefinitely (Thread.Sleep loop) until cancel token fires.
//
// Probe expects: target exits cleanly within a brief window of signal arrival,
// stdout shows GRACEFUL_CLEANUP_DONE marker, exit code = 0.

using System;
using System.Threading;

CancellationTokenSource cts = new();

Console.CancelKeyPress += (sender, args) =>
{
    Console.WriteLine("GRACEFUL_CLEANUP_DONE");
    Console.Out.Flush();
    args.Cancel = true;  // suppress default terminate; we handle exit ourselves
    cts.Cancel();
};

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

try
{
    Task.Delay(Timeout.Infinite, cts.Token).Wait();
}
catch (AggregateException) { /* token cancelled — normal path */ }

// Reaching here means signal handler fired cts.Cancel().
Environment.Exit(0);
