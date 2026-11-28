#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
#:property PublishAot=false
//
// DrHook.Engine probe 119 target — which of its handles a Windows target loses when the debugger detaches.
//
// Arguments: <debugger pid> <result file> <scanned marker>. Prints its PID, waits up to 30 s for a debugger to attach,
// and then, while it is debugged, looks for the handle mscordbi duplicated into it: the debugger's process handle, SYNCHRONIZE only
// (LocalEventChannel::Init, release/10.0). It compares every handle value from 4 to 0x10000 with a handle of its own to
// the debugger's process (CompareObjectHandles, which is true when two handles name one kernel object), and then writes
// the scanned marker, so the debugger detaches only after the scan.
//
// The moment the debugger has gone (Debugger.IsAttached false), it creates 64 events, one after another, and records
// each one's handle value. 300 ms later it asks Windows which of them are still valid (GetHandleInformation), and
// whether the debugger's handle still is. It writes one JSON object to the result file and returns 0. It never closes
// any of those handles itself.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;

var started = Stopwatch.StartNew();
int debuggerPid = int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
string resultFile = args[1];
string scannedMarker = args[2];
Console.WriteLine($"PROBE119_TARGET pid={Environment.ProcessId}");
Console.Out.Flush();

while (!Debugger.IsAttached && started.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(5);
var result = new JsonObject { ["attached"] = Debugger.IsAttached };

// While debugged: which handle values name the debugger's process.
const uint SYNCHRONIZE = 0x00100000;
IntPtr mine = OpenProcess(SYNCHRONIZE, false, (uint)debuggerPid);
var debuggerHandles = new JsonArray();
if (mine != IntPtr.Zero)
{
    for (long value = 4; value <= 0x10000; value += 4)
        if (value != (long)mine && CompareObjectHandles((IntPtr)value, mine)) debuggerHandles.Add($"0x{value:X}");
}
result["debuggerHandlesWhileAttached"] = debuggerHandles;
result["debuggerHandlesFoundAtMs"] = started.ElapsedMilliseconds;
File.WriteAllText(scannedMarker, "scanned");

while (Debugger.IsAttached && started.Elapsed < TimeSpan.FromSeconds(60)) Thread.Yield();
long detachedAt = started.ElapsedMilliseconds;
result["detachedAtMs"] = detachedAt;

// The moment the debugger has gone: 64 new handles, one after another.
var events = new ManualResetEvent[64];
var values = new long[events.Length];
for (int i = 0; i < events.Length; i++)
{
    events[i] = new ManualResetEvent(false);
    values[i] = (long)events[i].SafeWaitHandle.DangerousGetHandle();
}
result["eventsCreatedInMs"] = started.ElapsedMilliseconds - detachedAt;
result["eventHandles"] = new JsonArray(values.Select(v => (JsonNode?)$"0x{v:X}").ToArray());

Thread.Sleep(300);

var invalid = new JsonArray();
for (int i = 0; i < values.Length; i++)
    if (!GetHandleInformation((IntPtr)values[i], out _)) invalid.Add(new JsonObject { ["index"] = i, ["handle"] = $"0x{values[i]:X}", ["error"] = Marshal.GetLastPInvokeError() });
result["invalidEvents"] = invalid;
var debuggerHandlesAfter = new JsonArray();
foreach (JsonNode? h in debuggerHandles)
{
    long value = Convert.ToInt64(h!.GetValue<string>()[2..], 16);
    debuggerHandlesAfter.Add(new JsonObject
    {
        ["handle"] = h.GetValue<string>(),
        ["valid"] = GetHandleInformation((IntPtr)value, out _),
        ["stillTheDebugger"] = mine != IntPtr.Zero && CompareObjectHandles((IntPtr)value, mine),
    });
}
result["debuggerHandlesAfter"] = debuggerHandlesAfter;
result["mine"] = $"0x{(long)mine:X}";

File.WriteAllText(resultFile, result.ToJsonString());
GC.KeepAlive(events);
return 0;

[DllImport("kernel32", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
[DllImport("kernelbase")] static extern bool CompareObjectHandles(IntPtr first, IntPtr second);
[DllImport("kernel32", SetLastError = true)] static extern bool GetHandleInformation(IntPtr handle, out uint flags);
