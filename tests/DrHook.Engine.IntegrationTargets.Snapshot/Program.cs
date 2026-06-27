// DrHook.Engine — Layer 3 LAUNCHABLE INTEGRATION TARGET for the ADR-012 Phase 1 CaptureState snapshot
// test. A plain net10 console app the substrate LAUNCHES under the debugger (DebugSession.Launch +
// entry-module hold-gate — no Debugger.Break crutch, unlike the attach-shaped MTP/VSTest targets).
// Worker.Compute(n, label) gives a two-frame call stack with named arguments and named locals at the
// marked code line, so the integration test can CaptureState a rich, self-contained snapshot. The marker
// token is kept OFF this header comment so the test's text search matches only the code line below
// (lesson from probes 17/18). Bounded loop — the test disposes the Owned session long before it ends.

using System;
using System.Collections.Generic;
using System.Threading;

var worker = new Worker();
for (int beat = 1; beat <= 600; beat++)
{
    worker.Compute(beat, "tick");
    Thread.Sleep(50);
}

sealed class Worker
{
    public long Total;

    public long Compute(int n, string label)
    {
        int doubled = n * 2;
        long contribution = doubled + label.Length;
        var tags = new List<string> { label };  // a generic local — exercises runtime generic type-name rendering
        Total += contribution;   // SNAPSHOT_HERE
        GC.KeepAlive(tags);
        return Total;
    }
}
