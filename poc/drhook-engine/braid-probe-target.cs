// braid-probe-target — debuggee for the braid-visualization legibility probe (ADR-012 Phase 3, D4).
// Describe(label) has locals the probe predicts — some correctly, one deliberately WRONG then corrected —
// and then observes LIVE, so the rendered braid interleaves stated hypotheses with REAL runtime state.
//
// At the marked line: doubled=14 (7*2), span=ReadOnlySpan<char> over "hello" (_length=5), total=19,
// label="hello", this=Widget{_seed=7}. Built to a DLL by the smoke; launched via `dotnet exec`.
// (Marker token kept OFF this header so the smoke's text search matches only the code line — probes 17/18.)

using System;
using System.Diagnostics;

Debugger.Break();                                    // setup stop — the smoke arms the marked code line
var w = new Widget(7);
Console.WriteLine(w.Describe("hello"));

sealed class Widget
{
    readonly int _seed;
    public Widget(int seed) { _seed = seed; }

    public string Describe(string label)
    {
        int doubled = _seed * 2;                     // probe predicts doubled = 14 (correct)
        ReadOnlySpan<char> span = label.AsSpan();    // probe predicts span.Length WRONG (8), then corrects to 5
        int total = doubled + span.Length;           // BRAID_MARK — doubled / span / total / label / this all live
        return label + ":" + total;
    }
}
