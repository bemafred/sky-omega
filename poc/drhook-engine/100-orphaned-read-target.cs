#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
//
// DrHook.Engine probe 100 target — a process with managed frames and locals to read while it is frozen.
//
// Prints its PID, then runs for up to 60 s and exits by itself. With no argument it loops, and once a debugger is
// attached breaks into it each round, so the stop has a thread with managed frames and live locals to read. With
// "flood" it starts and joins threads without pause, so the debugger is always dispatching CreateThread and
// ExitThread events (and reading target memory to do it). "flood-crash" floods the same way, and a second thread
// calls Environment.FailFast at a random moment 0.5 to 3 s after a debugger has attached: the target ends itself
// abruptly, at a moment nobody outside it chose.

using System;
using System.Diagnostics;
using System.Threading;

Console.WriteLine($"PROBE100_TARGET pid={Environment.ProcessId}");
Console.Out.Flush();
if (args.Length > 0 && args[0] == "flood-crash")
{
    new Thread(static () =>
    {
        while (!Debugger.IsAttached) Thread.Sleep(10);
        Thread.Sleep(Random.Shared.Next(500, 3000));
        Environment.FailFast("probe100 flood-crash: the target ends itself");
    }) { IsBackground = true, Name = "probe100-crasher" }.Start();
}
if (args.Length > 0 && (args[0] == "flood" || args[0] == "flood-crash"))
{
    var flooding = Stopwatch.StartNew();
    while (flooding.Elapsed < TimeSpan.FromSeconds(60))
    {
        Thread t = new(static () => { });
        t.Start();
        t.Join();
    }
    return;
}
string label = "probe100";
long total = 0;
for (int i = 0; i < 1200; i++)
{
    for (int j = 0; j < 1000; j++) total += i ^ j;
    if (Debugger.IsAttached) Debugger.Break();
    Thread.Sleep(50);
}
GC.KeepAlive(label);
Console.WriteLine(total);
