// DrHook.Engine — ADR-012 Phase 1 CaptureState snapshot probe TARGET (compiled, net10).
// A two-frame call stack with NAMED arguments (n, label) and NAMED locals (doubled, contribution) at the
// marked line, plus a file heartbeat so the probe knows the loop is live. The probe launches via the
// entry-module hold-gate (no Debugger.Break crutch), arms a line breakpoint at the marked line, and
// captures the FIRST synchronized stop (beat=1: n=1, label="tick", doubled=2, contribution=6) — proving
// the unified snapshot renders the full picture (session, position, stack, locals, args, breakpoints,
// stream tails) from one call. The marker token is kept OFF this header comment so the probe's text search
// matches only the code line below (lesson from probe 17/18).

using System;
using System.Globalization;
using System.IO;
using System.Threading;

if (args.Length < 1) { Console.Error.WriteLine("usage: DebugStateTarget <heartbeat-file>"); return 2; }
string beatFile = args[0];

var worker = new Worker();
for (int beat = 1; beat <= 600; beat++)
{
    long total = worker.Compute(beat, "tick");
    Console.WriteLine($"beat {beat} total {total}");
    File.WriteAllText(beatFile, beat.ToString(CultureInfo.InvariantCulture));
    Thread.Sleep(100);
}
return 0;

sealed class Worker
{
    public long Total;

    public long Compute(int n, string label)
    {
        int doubled = n * 2;
        long contribution = doubled + label.Length;
        Total += contribution;   // SNAPSHOT_HERE
        return Total;
    }
}
