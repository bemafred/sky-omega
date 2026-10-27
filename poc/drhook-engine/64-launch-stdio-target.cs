#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
//
// DrHook.Engine probe 64 target — a launched debuggee that touches all three standard streams.
//
// Writes a marker to stdout and to stderr, then tries for 1.5 s to read one line from stdin. It records what it
// saw in a report file (args[0]) as well, so the result does not depend on the streams under test.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

string report = args.Length > 0 ? args[0] : "probe64-report.txt";

Console.WriteLine($"PROBE64_TARGET_STDOUT pid={Environment.ProcessId}");
Console.Out.Flush();
Console.Error.WriteLine("PROBE64_TARGET_STDERR");
Console.Error.Flush();

Task<string?> read = Task.Run(() => { try { return Console.In.ReadLine(); } catch (Exception ex) { return "EXCEPTION " + ex.GetType().Name; } });
string stdinResult = read.Wait(1500) ? (read.Result ?? "EOF") : "NOTHING_WITHIN_1500MS";

File.WriteAllText(report,
    $"stdin={stdinResult}\n" +
    $"isInputRedirected={Console.IsInputRedirected}\n" +
    $"isOutputRedirected={Console.IsOutputRedirected}\n" +
    $"isErrorRedirected={Console.IsErrorRedirected}\n");

Console.WriteLine("PROBE64_TARGET_DONE");
Console.Out.Flush();
