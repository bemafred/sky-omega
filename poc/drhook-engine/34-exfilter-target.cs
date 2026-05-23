#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 34 TARGET — throws TWO custom exception types ALTERNATELY (caught), so the
// probe can validate that ArmExceptionFilter persists across waits: with a filter on
// ProbeException the WaitForStop call surfaces only ProbeException; with the filter removed,
// both types surface.

using System;
using System.Diagnostics;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

for (int i = 0; i < 500 && !Debugger.IsAttached; i++) Thread.Sleep(10);
Debugger.Break();

while (true)
{
    try { throw new ProbeException("matches"); }
    catch (ProbeException) { /* swallow */ }

    try { throw new OtherException("doesn't match"); }
    catch (OtherException) { /* swallow */ }

    Thread.Sleep(20);
}

sealed class ProbeException : Exception
{
    public ProbeException(string message) : base(message) { }
}

sealed class OtherException : Exception
{
    public OtherException(string message) : base(message) { }
}
