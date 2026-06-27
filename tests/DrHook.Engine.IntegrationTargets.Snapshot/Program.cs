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
const string alphabet = "abcdefghij";
for (int beat = 1; beat <= 600; beat++)
{
    worker.Compute(beat, "tick");
    worker.Scan(alphabet.AsSpan(0, (beat % 7) + 1));  // span length cycles 2..7 — gates `value.Length > 5`
    Thread.Sleep(50);
}

sealed class Worker
{
    public long Total;
    public int Scanned;

    public long Compute(int n, string label)
    {
        int doubled = n * 2;
        long contribution = doubled + label.Length;
        var tags = new List<string> { label };  // a generic local — exercises runtime generic type-name rendering
        Total += contribution;   // SNAPSHOT_HERE
        GC.KeepAlive(tags);
        return Total;
    }

    // `value` is a ReadOnlySpan<char> argument — a ref struct. The ADR-013 D3 test arms a conditional
    // breakpoint `value.Length > 5` here: the substrate reads value.Length DIRECTLY from the span's
    // `_length` field (a ref struct cannot be func-eval'd), so the condition evaluates instead of faulting.
    public void Scan(ReadOnlySpan<char> value)
    {
        Scanned += value.Length;   // SPAN_HERE
    }
}
