// eval-arg-receiver-target — validates member-access on an ARGUMENT receiver in a condition.
// Describe(Widget w): w is an argument (not a local); the smoke arms a conditional breakpoint whose
// condition calls a PROPERTY getter on that argument (w.Area). Two calls with different areas prove
// the condition both EVALUATES (func-eval with the receiver resolved from an argument slot) and GATES
// correctly — it must skip the Area=25 call and stop on the Area=200 call.
//
// Built to a DLL by the smoke (build-first), launched under DebugSession via `dotnet exec`.

using System;
using System.Diagnostics;

Debugger.Break();                                    // setup stop — smoke arms the conditional breakpoint
Console.WriteLine(Describe(new Widget(5, 5)));       // Area=25  — condition w.Area==200 FALSE → no stop
Console.WriteLine(Describe(new Widget(10, 20)));     // Area=200 — condition TRUE → stop on THIS call

static string Describe(Widget w)
{
    string label = "computed";                       // MEMBER_MARK — w is an ARGUMENT; condition is w.Area==200
    return label + w.Width;
}

sealed class Widget
{
    public readonly int Width;                       // explicit fields → inspected by real name
    public readonly int Height;
    public Widget(int width, int height) { Width = width; Height = height; }
    public int Area => Width * Height;               // computed property getter — the member the condition calls
}
