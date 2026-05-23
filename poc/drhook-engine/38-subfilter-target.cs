#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 38 TARGET — throws TWO distinct exception types alternately, both deriving
// DIRECTLY from System.Exception (no intermediate). A filter on "System.Exception" must match
// both via subclass-aware filtering (probe 38); a filter on "ProbeException" must match only
// ProbeException (exact-match still works for non-base filters).

using System;
using System.Diagnostics;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

for (int i = 0; i < 500 && !Debugger.IsAttached; i++) Thread.Sleep(10);
Debugger.Break();

while (true)
{
    try { throw new ProbeException("probe"); } catch (ProbeException) { /* swallow */ }
    try { throw new OtherException("other"); } catch (OtherException) { /* swallow */ }
    Thread.Sleep(20);
}

sealed class ProbeException : Exception { public ProbeException(string m) : base(m) { } }
sealed class OtherException : Exception { public OtherException(string m) : base(m) { } }
