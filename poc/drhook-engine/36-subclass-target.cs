#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 36 TARGET — exception with a TWO-LEVEL hierarchy in the target module:
//   ProbeException : TitledException : Exception
// where TitledException declares the `Title` property. The probe validates within-module
// subclass-walking by func-evaling ex.Title on a ProbeException — the resolver walks from
// ProbeException up to TitledException to find get_Title. It also confirms the scope boundary:
// ex.Message (declared on Exception in CoreLib) is NOT resolved by this slice.

using System;
using System.Diagnostics;
using System.Threading;

const string Expected = "hello title";

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

abstract class TitledException : Exception
{
    public string Title { get; }
    protected TitledException(string title) : base(title) => Title = title;
}

sealed class ProbeException : TitledException
{
    public ProbeException(string title) : base(title) { }
}
