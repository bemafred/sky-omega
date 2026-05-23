#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 37 TARGET — direct ProbeException : Exception with NO same-module
// intermediate. ex.Message can only be resolved by CROSSING the module boundary into CoreLib
// (where System.Exception declares get_Message). Distinct from probe 36, which has an in-module
// TitledException intermediate that proved within-module walking suffices for Title.

using System;
using System.Diagnostics;
using System.Threading;

const string Expected = "hello message";

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

for (int i = 0; i < 500 && !Debugger.IsAttached; i++) Thread.Sleep(10);
Debugger.Break();

while (true)
{
    try { throw new ProbeException(Expected); }
    catch (ProbeException) { /* swallow */ }
    Thread.Sleep(20);
}

sealed class ProbeException : Exception
{
    public ProbeException(string message) : base(message) { }
}
