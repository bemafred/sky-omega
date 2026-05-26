#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 51 TARGET — IGNORING target: catches SIGINT/SIGTERM and refuses to exit
// =============================================================================================
//
// Models the "lifecycle violator" pattern (Claude Chat App lens): target catches
// both SIGINT and SIGTERM via .NET signal handlers, marks Cancel = true on both,
// and continues running. Designed to validate Layer 2 discipline — soft signals
// can be ignored by misbehaving targets; only SIGKILL forces termination.

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
