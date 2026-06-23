// argname-fidelity-target — a FILE-BASED (single-file) app target for the argument-name +
// this/field-inspection probe. A top-level program (so its helpers compile as STATIC methods — the
// case that mislabelled argument 0 as "this") plus an INSTANCE method whose receiver (this) has a
// field AND whose parameter is an OBJECT with fields, so the smoke can inspect "this" and a non-this
// object argument and their fields — not just their names.
//
// Built to a DLL by the smoke (build-first; `dotnet run` would delay the attach window), then
// launched under DebugSession via `dotnet exec`.

using System;
using System.Diagnostics;

Debugger.Break();                                        // setup stop — smoke arms breakpoints here
Console.WriteLine(StaticScale(7, 3L));                   // -> static method: args (seed, factor), NO "this"
Console.WriteLine(new Box(100).Shift(new Delta(5, 2)));  // -> instance method: args (this[Box], d[Delta])

static long StaticScale(int seed, long factor)
{
    long product = seed * factor;                        // STATIC_MARK
    return product;
}

sealed class Delta
{
    public int By;
    public int Times;
    public Delta(int by, int times) { By = by; Times = times; }
}

sealed class Box
{
    private readonly int _base;
    public Box(int b) { _base = b; }

    public int Shift(Delta d)
    {
        int moved = _base + d.By * d.Times;              // INSTANCE_MARK
        return moved;
    }
}
