#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 30 TARGET — throws a custom exception (caught) carrying a known int property
// in a loop, so an attached debugger can drive an EXCEPTION-LOCATION BreakpointPolicy. ProbeException
// lives in this target's own module; Code = 42 at the throw site (same shape as probe 27, but
// driven via WaitForExceptionPolicyStop here rather than TryEvalCurrentExceptionMember directly).

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
        throw new ProbeException("probe 30 exception-through-policy", 42);
    }
    catch (ProbeException)
    {
        // swallow — the first-chance Exception callback already fired at the throw site
    }
    Thread.Sleep(20);
}

sealed class ProbeException : Exception
{
    public int Code { get; }
    public ProbeException(string message, int code) : base(message) => Code = code;
}
