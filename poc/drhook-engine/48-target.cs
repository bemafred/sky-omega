#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 48 TARGET — minimal long-running target
// ============================================================
//
// Each probe-48 cycle spawns ONE fresh instance of this target, attaches, briefly
// observes, disposes. Multiple cycles per probe-host process — the probe tests
// whether the substrate accumulates per-session mscordbi state across DIFFERENT
// targets in the SAME host (MCH-RE-3 characterisation).
//
// Same shape as 47-target.cs (parked sleeper, no callback flood); per-cycle target
// freshness is what distinguishes probe 48 from probe 42 (50 cycles against one
// long-lived target).

using System;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

Thread.Sleep(Timeout.Infinite);
