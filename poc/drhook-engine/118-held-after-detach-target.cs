#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
//
// DrHook.Engine probe 118 target — a program that floods thread events while a debugger is attached, and ends by
// itself once the debugger has left. Probe 102's detach-then-end target, with the phases it reaches written down.
//
// Prints its PID, waits up to 30 s for a debugger to attach (Debugger.IsAttached), then floods: one thread starts and
// joins one empty thread after another, so an attached debugger is always dispatching CreateThread and ExitThread
// events. Once the debugger has left it floods 1 s more, stops the flood, joins the flood thread and returns 0.
// Whatever happens, it returns within 60 s of starting, unless something holds it.
//
// Each phase it reaches is appended to the phase file, one line each, flushed: attached, detached, stop, joined, done.
// A held target's last line says how far its main thread got. The flood thread writes nothing: a write per thread it
// starts would change the flood.
//
// Argument: <phase file>.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

var started = Stopwatch.StartNew();
TimeSpan cap = TimeSpan.FromSeconds(60);
string phases = args[0];
Console.WriteLine($"PROBE118_TARGET pid={Environment.ProcessId}");
Console.Out.Flush();

while (!Debugger.IsAttached && started.Elapsed < TimeSpan.FromSeconds(30))
    Thread.Sleep(5);
Phase("attached");

var flooder = new Thread(() =>
{
    while (!Flood.Stop && started.Elapsed < cap)
    {
        Thread t = new(static () => { });
        t.Start();
        t.Join();
    }
}) { IsBackground = true, Name = "flood" };
flooder.Start();

while (Debugger.IsAttached && started.Elapsed < cap) Thread.Sleep(5);
Phase("detached");
var waited = Stopwatch.StartNew();
while (waited.Elapsed < TimeSpan.FromSeconds(1) && started.Elapsed < cap) Thread.Sleep(10);
Flood.Stop = true;
Phase("stop");
flooder.Join();
Phase("joined");
Phase("done");
return 0;

void Phase(string name)
{
    using var file = new FileStream(phases, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
    byte[] line = System.Text.Encoding.UTF8.GetBytes($"{name} {started.ElapsedMilliseconds}\n");
    file.Write(line);
    file.Flush(flushToDisk: true);
}

static class Flood
{
    public static volatile bool Stop;
}
