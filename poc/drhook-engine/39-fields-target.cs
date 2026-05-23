#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 39 TARGET — a Counter object with PRIMITIVE + STRING + BOOL + NESTED-OBJECT
// fields, in scope at a line breakpoint. Validates GetLocals(depth>=1) populates Fields with the
// instance fields, and at depth>=2 the nested Inner is also expanded. Plain public fields keep the
// metadata names readable (auto-properties would produce backing fields like <Count>k__BackingField).

using System;
using System.Diagnostics;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

for (int i = 0; i < 500 && !Debugger.IsAttached; i++) Thread.Sleep(10);
Debugger.Break();

while (true)
{
    Step();
    Thread.Sleep(20);
}

static void Step()
{
    var counter = new Counter(42, "hello", true, new Inner(99));
    GC.KeepAlive(counter);   // FIELDS_HERE
}

sealed class Inner
{
    public int X;
    public Inner(int x) { X = x; }
}

sealed class Counter
{
    public int Count;
    public string Label;
    public bool Active;
    public Inner Nested;
    public Counter(int count, string label, bool active, Inner nested)
    {
        Count = count; Label = label; Active = active; Nested = nested;
    }
}
