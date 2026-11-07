#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
#:property TargetFramework=net10.0
//
// DrHook.Engine probe 106 worker — the debugger in a process of its own (ADR-016 Step 5).
//
// The only process of probe 106 that loads DrHook.Engine, and with it dbgshim, mscordbi and the DAC. It runs one
// debugging session for its supervisor (106-supervisor.cs) and is discarded afterwards, or when its debugger is lost.
// The supervisor builds it and starts it as `dotnet <dll> serve`.
//
// Protocol: one JSON object per line. Commands on stdin, events on stdout; every event carries "ev" and "t" (ms since
// the worker started). Nothing else is written to stdout: a launched target's console goes to DrHook's own pipes
// (ADR-011 D2), and engine anomalies are sent as events.
//   {"cmd":"launch","args":[...]}    DebugSession.Launch("dotnet", args)    -> started {pid, how:"launched"}
//   {"cmd":"attach","pid":N}         DebugSession.Attach (borrowed)         -> started {pid, how:"attached"}
//   {"cmd":"wait-break","ms":N}      resume every stop until a Break stop, and hold it   -> stop {reason} or no-stop
//   {"cmd":"locals"}                 GetLocals at the current stop          -> locals {values}
//   {"cmd":"run-to-end","detect":b,"exitedMs":N}
//         resume the held stop, if any, and every later one, until the target is gone (the engine's TargetHasExited,
//         read from the OS, not from mscordbi). Facts: target-gone {at}, then exited {afterGoneMs} when a
//         ProcessExited stop came within exitedMs of the death, or no-exited {waitedMs} when it did not. With detect,
//         no ProcessExited is also reported as the verdict debugger-stuck: finding 104 measured ProcessExited within
//         175 ms of the death in every usable session and never in a stuck one. Ends with end {outcome}.
//   {"cmd":"close"}                  Dispose (a borrowed target is detached and left running)   -> closed
//   {"cmd":"exit"}                   -> bye, then the worker ends
// A command that throws is answered with error {cmd, type, message}; a lost debugger with debugger-lost.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using SkyOmega.DrHook.Engine;

var clock = Stopwatch.StartNew();
object outLock = new();
void Emit(JsonObject ev)
{
    ev["t"] = clock.ElapsedMilliseconds;
    string line = ev.ToJsonString();
    lock (outLock) { Console.Out.WriteLine(line); Console.Out.Flush(); }
}

if (args.Length != 1 || args[0] != "serve") { Console.Error.WriteLine("usage: 106-worker serve"); return 2; }

var sink = new EventSink(Emit);
DebugSession? session = null;
int targetPid = 0;
bool atStop = false; // wait-break returned with the target held at a stop
Emit(new JsonObject { ["ev"] = "ready", ["pid"] = Environment.ProcessId, ["runtime"] = RuntimeInformation.FrameworkDescription });

string? line;
while ((line = Console.In.ReadLine()) is not null)
{
    JsonNode command = JsonNode.Parse(line)!;
    string name = (string)command["cmd"]!;
    try
    {
        switch (name)
        {
            case "launch":
            {
                List<string> launchArgs = command["args"]!.AsArray().Select(a => (string)a!).ToList();
                session = DebugSession.Launch("dotnet", launchArgs, null, sink);
                targetPid = session.ProcessId;
                Emit(new JsonObject { ["ev"] = "started", ["pid"] = targetPid, ["how"] = "launched" });
                break;
            }
            case "attach":
            {
                targetPid = (int)command["pid"]!;
                session = DebugSession.Attach(targetPid, sink);
                Emit(new JsonObject { ["ev"] = "started", ["pid"] = targetPid, ["how"] = "attached" });
                break;
            }
            case "wait-break":
            {
                var waiting = Stopwatch.StartNew();
                var bound = TimeSpan.FromMilliseconds((int)command["ms"]!);
                StopInfo? breakStop = null;
                while (waiting.Elapsed < bound)
                {
                    StopInfo? stop = session!.WaitForStop(TimeSpan.FromMilliseconds(200));
                    if (stop is null) continue;
                    if (stop.Reason == StopReason.Break) { breakStop = stop; break; }
                    if (stop.EndsSession) { breakStop = stop; break; }
                    session.Resume();
                }
                atStop = breakStop is not null && !breakStop.EndsSession;
                Emit(breakStop is null
                    ? new JsonObject { ["ev"] = "no-stop", ["waitedMs"] = waiting.ElapsedMilliseconds }
                    : new JsonObject { ["ev"] = "stop", ["reason"] = breakStop.Reason.ToString() });
                break;
            }
            case "locals":
            {
                var values = new JsonObject();
                foreach (LocalValue local in session!.GetLocals())
                    values[local.Name] = local.StringValue ?? Convert.ToString(local.RawValue, CultureInfo.InvariantCulture);
                Emit(new JsonObject { ["ev"] = "locals", ["values"] = values });
                break;
            }
            case "run-to-end":
            {
                bool detect = (bool)command["detect"]!;
                int exitedMs = (int)command["exitedMs"]!;
                long goneAt = -1, exitedAt = -1;
                if (atStop) { session!.Resume(); atStop = false; }
                var running = Stopwatch.StartNew();
                while (running.Elapsed < TimeSpan.FromSeconds(30))
                {
                    StopInfo? stop = session!.WaitForStop(TimeSpan.FromMilliseconds(20));
                    if (stop is not null)
                    {
                        if (stop.Reason == StopReason.ProcessExited) { exitedAt = clock.ElapsedMilliseconds; break; }
                        if (stop.Reason == StopReason.DebuggerLost) break;
                        if (stop.Reason == StopReason.Exception)
                            Emit(new JsonObject { ["ev"] = "stop", ["reason"] = $"Exception({stop.ExceptionKind})" });
                        session.Resume();
                    }
                    if (goneAt < 0 && session.TargetHasExited)
                    {
                        goneAt = clock.ElapsedMilliseconds;
                        Emit(new JsonObject { ["ev"] = "target-gone", ["pid"] = targetPid });
                        break;
                    }
                }
                if (goneAt >= 0 && exitedAt < 0)
                {
                    // The death was seen first: give ProcessExited its bound.
                    var waitingForExit = Stopwatch.StartNew();
                    while (waitingForExit.ElapsedMilliseconds < exitedMs)
                    {
                        StopInfo? stop = session!.WaitForStop(TimeSpan.FromMilliseconds(20));
                        if (stop?.Reason == StopReason.ProcessExited) { exitedAt = clock.ElapsedMilliseconds; break; }
                        if (stop is not null && !stop.EndsSession) session.Resume();
                    }
                }
                else if (exitedAt >= 0 && goneAt < 0)
                {
                    goneAt = exitedAt;
                    Emit(new JsonObject { ["ev"] = "target-gone", ["pid"] = targetPid, ["seenBy"] = "ProcessExited" });
                }
                string outcome;
                if (session!.IsDebuggerLost) { outcome = "debugger-lost"; Emit(new JsonObject { ["ev"] = "debugger-lost" }); }
                else if (exitedAt >= 0) { outcome = "exited"; Emit(new JsonObject { ["ev"] = "exited", ["afterGoneMs"] = exitedAt - goneAt }); }
                else if (goneAt >= 0)
                {
                    outcome = "no-exited";
                    Emit(new JsonObject { ["ev"] = "no-exited", ["waitedMs"] = exitedMs });
                    if (detect) Emit(new JsonObject { ["ev"] = "debugger-stuck", ["basis"] = "target gone, no ProcessExited within the bound" });
                }
                else outcome = "target-still-running";
                Emit(new JsonObject { ["ev"] = "end", ["outcome"] = outcome });
                break;
            }
            case "close":
                session?.Dispose();
                session = null;
                Emit(new JsonObject { ["ev"] = "closed" });
                break;
            case "exit":
                Emit(new JsonObject { ["ev"] = "bye" });
                return 0;
            default:
                Emit(new JsonObject { ["ev"] = "error", ["cmd"] = name, ["type"] = "UnknownCommand", ["message"] = name });
                break;
        }
    }
    catch (DebuggerLostException e)
    {
        Emit(new JsonObject { ["ev"] = "debugger-lost", ["cmd"] = name, ["errorHR"] = e.Error.ErrorHResult, ["errorCode"] = e.Error.ErrorCode });
    }
    catch (Exception e)
    {
        Emit(new JsonObject { ["ev"] = "error", ["cmd"] = name, ["type"] = e.GetType().Name, ["message"] = e.Message });
    }
}
return 0;

sealed class EventSink(Action<JsonObject> emit) : IDebugEventSink
{
    public void OnEvent(string name) { }
    public void OnAnomaly(EngineAnomaly a) =>
        emit(new JsonObject { ["ev"] = "anomaly", ["kind"] = a.Kind.ToString(), ["operation"] = a.Operation, ["observed"] = a.Observed });
}
