#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 50 TARGET — well-behaved CLI: PosixSignalRegistration / SIGTERM
// ====================================================================================
//
// Models the modern .NET (.NET 6+) idiom for handling SIGTERM: explicit
// PosixSignalRegistration.Create(PosixSignal.SIGTERM, handler). This is the
// API the runtime documentation recommends for graceful-shutdown handlers on
// container orchestration platforms (Kubernetes sends SIGTERM, etc.).
//
// On signal:
//   - Print GRACEFUL_CLEANUP_DONE to stdout (probe observes this).
//   - context.Cancel = true (suppress default; we handle exit ourselves).
//   - Environment.Exit(0).

using System;
using System.Runtime.InteropServices;
using System.Threading;

CancellationTokenSource cts = new();

PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
{
    Console.WriteLine("GRACEFUL_CLEANUP_DONE");
    Console.Out.Flush();
    ctx.Cancel = true;
    cts.Cancel();
});

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

try
{
    Task.Delay(Timeout.Infinite, cts.Token).Wait();
}
catch (AggregateException) { /* token cancelled — normal path */ }

Environment.Exit(0);
