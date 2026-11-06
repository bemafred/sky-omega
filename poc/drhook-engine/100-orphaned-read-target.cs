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
// abruptly, at a moment nobody outside it chose. Three more crash the same way, at the same moments, by means that
// skip EEPolicy::HandleFatalError (the path by which FailFast tells the debugger, then calls PROCAbort):
// "flood-stackoverflow" recurses on that thread until its stack is exhausted; "flood-segv" writes through a bad
// address inside libc's memset, a fault in native code; "flood-abort" calls libc's abort() directly. On Windows the
// last two use ucrtbase's memset (an access violation) and abort().

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

Console.WriteLine($"PROBE100_TARGET pid={Environment.ProcessId}");
Console.Out.Flush();
string mode = args.Length > 0 ? args[0] : "";
if (mode is "flood-crash" or "flood-stackoverflow" or "flood-segv" or "flood-abort")
{
    new Thread(() =>
    {
        while (!Debugger.IsAttached) Thread.Sleep(10);
        Thread.Sleep(Random.Shared.Next(500, 3000));
        switch (mode)
        {
            case "flood-crash": Environment.FailFast("probe100 flood-crash: the target ends itself"); break;
            case "flood-stackoverflow": Console.WriteLine(Recurse(0)); break;
            case "flood-segv" when OperatingSystem.IsWindows(): ucrt_memset(16, 0, 1); break;
            case "flood-segv": memset(16, 0, 1); break;
            case "flood-abort" when OperatingSystem.IsWindows(): ucrt_abort(); break;
            case "flood-abort": abort(); break;
        }
    }) { IsBackground = true, Name = "probe100-crasher" }.Start();
}
if (mode is "flood" or "flood-crash" or "flood-stackoverflow" or "flood-segv" or "flood-abort")
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
return;

// Not inlined, and the addition after the call keeps it from being a tail call, so every level takes a frame.
[MethodImpl(MethodImplOptions.NoInlining)]
static long Recurse(long depth)
{
    Span<byte> frame = stackalloc byte[256];
    frame[0] = (byte)depth;
    return Recurse(depth + 1) + frame[0];
}

[DllImport("libc")]
static extern nint memset(nint destination, int value, nuint count);

[DllImport("libc")]
static extern void abort();

// Windows has no libc; the C runtime's memset and abort live in ucrtbase.
[DllImport("ucrtbase", EntryPoint = "memset")]
static extern nint ucrt_memset(nint destination, int value, nuint count);

[DllImport("ucrtbase", EntryPoint = "abort")]
static extern void ucrt_abort();
