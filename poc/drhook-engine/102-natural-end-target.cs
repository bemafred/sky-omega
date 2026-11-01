#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
//
// DrHook.Engine probe 102 target — a program that ends by itself, under a debugger or just after one leaves.
//
// Prints its PID, waits up to 30 s for a debugger to attach (Debugger.IsAttached), then does what its kind says and
// returns 0. It writes "done" to the marker file just before it returns, so the probe can tell its own end from any
// other. It never breaks into a debugger. Whatever happens, it returns within 60 s of starting, unless it is held
// stopped by a debugger.
//
// The flood is probe 100's: a thread starts and joins one empty thread after another, so an attached debugger is
// always dispatching CreateThread and ExitThread events, and reading this program's memory to do it.
//
// Arguments: <kind> [marker file]. Kinds:
//   idle             no flood; returns 3 s after the attach. The probe's final attach check.
//   quiet            floods for 1.5 s, stops the flood, and returns 300 ms later: an end with nothing in flight.
//   mid-flood        returns 1.5 s after the attach while a background thread still floods, so the process ends in
//                    the middle of the flood and of the debugger's dispatch of it.
//   detach-then-end  floods until the debugger has left, then 1 s more, stops the flood and returns.
//   exit-at-detach   floods while the debugger is attached and returns as soon as it has left (only the marker is
//                    written in between): finding 63's case, without the kill.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

var started = Stopwatch.StartNew();
TimeSpan cap = TimeSpan.FromSeconds(60);
string kind = args.Length > 0 ? args[0] : "idle";
string? marker = args.Length > 1 ? args[1] : null;
Console.WriteLine($"PROBE102_TARGET pid={Environment.ProcessId}");
Console.Out.Flush();

while (!Debugger.IsAttached && started.Elapsed < TimeSpan.FromSeconds(30))
    Thread.Sleep(5);

Thread? flooder = null;
if (kind != "idle")
{
    flooder = new Thread(() =>
    {
        while (!Flood.Stop && started.Elapsed < cap)
        {
            Thread t = new(static () => { });
            t.Start();
            t.Join();
        }
    }) { IsBackground = true, Name = "flood" };
    flooder.Start();
}

switch (kind)
{
    case "idle":
        Idle(TimeSpan.FromSeconds(3));
        break;
    case "quiet":
        Idle(TimeSpan.FromSeconds(1.5));
        Flood.Stop = true;
        flooder!.Join();
        Thread.Sleep(300);
        break;
    case "mid-flood":
        Idle(TimeSpan.FromSeconds(1.5));
        break;
    case "detach-then-end":
        while (Debugger.IsAttached && started.Elapsed < cap) Thread.Sleep(5);
        Idle(TimeSpan.FromSeconds(1));
        Flood.Stop = true;
        flooder!.Join();
        break;
    case "exit-at-detach":
        while (Debugger.IsAttached && started.Elapsed < cap) Thread.Yield();
        break;
    default:
        Console.Error.WriteLine($"unknown kind: {kind}");
        return 2;
}
if (marker is not null) File.WriteAllText(marker, "done");
return 0;

// Waits for the span, or until the 60 s cap.
void Idle(TimeSpan span)
{
    var waited = Stopwatch.StartNew();
    while (waited.Elapsed < span && started.Elapsed < cap)
        Thread.Sleep(10);
}

static class Flood
{
    public static volatile bool Stop;
}
