#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
//
// DrHook.Engine probe 101 target — an idle program that ends by itself.
//
// Prints its PID, then sleeps in short steps for the given number of seconds (default 20), writes "done" to the marker
// file if one is named, and exits with code 0. It never breaks into a debugger and writes nothing else to the console,
// so an attached debugger has nothing to do but watch. The marker shows the probe that the target reached its own end.
//
// Arguments: [seconds] [marker file]

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

int seconds = args.Length > 0 ? int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 20;
Console.WriteLine($"PROBE101_TARGET pid={Environment.ProcessId}");
Console.Out.Flush();
var idle = Stopwatch.StartNew();
while (idle.Elapsed < TimeSpan.FromSeconds(seconds))
    Thread.Sleep(100);
if (args.Length > 1) File.WriteAllText(args[1], "done");
return 0;
