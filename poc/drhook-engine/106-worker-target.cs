#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
//
// DrHook.Engine probe 106 target — a program that ends by itself and shows from outside whether it is progressing.
//
// Usage: 106-worker-target <beat file> <rounds>. Each round adds to a total, writes the round's number to the beat
// file, breaks into the debugger once one is attached (a stop with managed frames and live locals: round, total,
// label), and sleeps 50 ms. After the last round it writes "done <total>" to the beat file and ends. The beat file lets the
// supervisor tell, without a debugger, whether the target is running (the number advances), held (it does not), or
// finished; it is used because a launched target's console belongs to its debugger, which may be gone.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

Console.WriteLine($"PROBE106_TARGET pid={Environment.ProcessId}");
Console.Out.Flush();
string beat = args[0];
int rounds = int.Parse(args[1], CultureInfo.InvariantCulture);
string label = "probe106";
long total = 0;
for (int round = 1; round <= rounds; round++)
{
    for (int j = 0; j < 1000; j++) total += round ^ j;
    File.WriteAllText(beat, round.ToString(CultureInfo.InvariantCulture));
    if (Debugger.IsAttached) Debugger.Break();
    Thread.Sleep(50);
}
// The console is written only at the start: a launched target's console is its debugger's pipe, which has no reader
// once the debugger is gone, and a write to it then would end the target with an IOException instead of naturally.
File.WriteAllText(beat, $"done {total.ToString(CultureInfo.InvariantCulture)}");
GC.KeepAlive(label);
