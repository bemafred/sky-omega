// DrHook.Engine probe 33 TARGET (compiled). DebugSession.Launch attaches BEFORE Main runs;
// Debugger.Break is the first stop, after which the probe sets a line breakpoint at the loop
// body and resumes to it. Marker tokens are kept out of comments so FindMarker resolves to the
// actual code line.

using System;
using System.Diagnostics;
using System.Threading;

Console.WriteLine("launched");
Debugger.Break();

for (int i = 0; i < 100; i++)
{
    int v = i * 2;
    GC.KeepAlive(v);   // PROBE_BREAK
    Thread.Sleep(20);
}
