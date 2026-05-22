#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 26 TARGET — throws a custom exception (caught) in a loop, so the attached
// debugger receives ICorDebugManagedCallback2::Exception (first-chance) for it. ProbeException
// lives in this target's OWN module, so resolving its type name from the live exception exercises
// user-module metadata (like Box in probe 24), not just CoreLib. The catch swallows it; the
// first-chance callback has already fired at the throw.

using System;
using System.Diagnostics;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

for (int i = 0; i < 500 && !Debugger.IsAttached; i++) Thread.Sleep(10);
Debugger.Break();

while (true)
{
    try
    {
        throw new ProbeException("probe 26 first-chance");
    }
    catch (ProbeException)
    {
        // swallow — the first-chance Exception callback already fired at the throw site
    }
    Thread.Sleep(20);
}

sealed class ProbeException : Exception
{
    public ProbeException(string message) : base(message) { }
}
