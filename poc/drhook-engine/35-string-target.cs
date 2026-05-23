#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 35 TARGET — throws a custom exception carrying a STRING property in a
// caught loop. The probe func-evals the string-returning getter on the in-flight exception and
// expects the rendered string content to come back in ArgumentValue.StringValue (the new
// reference-string rendering path through ICorDebugStringValue).
//
// Description is declared on ProbeException (not inherited) so the existing MetadataResolver
// .FindMethodInType finds get_Description directly — subclass walking is a future axis.

using System;
using System.Diagnostics;
using System.Threading;

const string Expected = "hello string";

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
    public string Description { get; }
    public ProbeException(string description) : base(description) => Description = description;
}
