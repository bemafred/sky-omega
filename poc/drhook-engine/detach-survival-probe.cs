#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// F-010-2 probe — does a debugged CoreCLR CHILD survive the debugger's RUDE exit on macOS?
//
// Tests the inferred CoreCLR "Rude-Detach" risk directly: launch a long-running target as a
// debugged child (the current posix_spawn-as-child launch), let it heartbeat to a file, then
// EXIT the debugger WITHOUT detaching or disposing (the worst case — debugger vanishes uncleanly).
//
// If the child keeps heartbeating after we vanish, macOS does NOT kill debugged children on
// debugger exit -> a CLEAN ICorDebug Detach certainly leaves it running -> F-010-2 (Owned
// detach-leave-running) is trivially easy, no reparenting. If the child DIES, the clean-detach
// variant is the next probe (and reparenting may be required).
//
// No engine code is touched — uses only DebugSession.Launch + Environment.Exit.
//
// Usage:  dotnet run --no-cache detach-survival-probe.cs   (the harness captures RUNNER_PID and
//         checks survival after this process exits)

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SkyOmega.DrHook.Engine;

string runnerDll = "/tmp/drhook-detach-probe/Runner/bin/Debug/net10.0/Runner.dll";
if (!File.Exists(runnerDll)) { Console.Error.WriteLine($"FALSIFIED: runner not built at {runnerDll}"); return 2; }

string runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
string? rootDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir)));
string dotnet = rootDir is not null && File.Exists(Path.Combine(rootDir, "dotnet")) ? Path.Combine(rootDir, "dotnet") : "dotnet";

DebugSession session;
try { session = DebugSession.Launch(dotnet, new[] { "exec", runnerDll }, Path.GetDirectoryName(runnerDll), new NullSink()); }
catch (Exception ex) { Console.Error.WriteLine($"FALSIFIED (Launch): {ex.GetType().Name}: {ex.Message}"); return 3; }

Console.Error.WriteLine($"RUNNER_PID={session.ProcessId}");
Thread.Sleep(2500); // let the runner heartbeat a few times while debugged
Console.Error.WriteLine($"[probe] RUDE exit now — abandoning the debug session (no detach, no dispose); runner pid={session.ProcessId}");
Console.Error.Flush();
Environment.Exit(0); // worst case: the debugger vanishes uncleanly. Does the debugged child survive?
return 0;

sealed class NullSink : IDebugEventSink { public void OnEvent(string n) { } }
