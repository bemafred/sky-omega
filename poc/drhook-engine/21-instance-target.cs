#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 21 TARGET — an instance method/property to func-eval on a local.
// Worker.Inspect has a string local s = "hello"; the probe breaks at the marked line (s in scope)
// and func-evals s.Length (String.get_Length with this=s), expecting 5. Marker token kept off the
// header comment so the probe matches only the code line.

using System;
using System.Diagnostics;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

for (int i = 0; i < 500 && !Debugger.IsAttached; i++) Thread.Sleep(10);
Debugger.Break();

var worker = new Worker();
while (true)
{
    worker.Inspect();
    Thread.Sleep(20);
}

sealed class Worker
{
    public void Inspect()
    {
        string s = "hello";
        GC.KeepAlive(s);   // INSTANCE_HERE
    }
}
