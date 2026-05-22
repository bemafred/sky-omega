#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 27 TARGET — throws a custom exception (caught) carrying a known int property,
// so the probe can FUNC-EVAL a getter on the IN-FLIGHT exception at the exception stop. ProbeException
// lives in this target's own module; Code is set to 42 at the throw site. The catch swallows it (the
// first-chance Exception callback already fired at the throw).

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
        throw new ProbeException("probe 27 func-eval at exception stop", 42);
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
