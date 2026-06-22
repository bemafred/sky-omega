#!/usr/bin/env -S dotnet
//
// ADR-014 fault-reproduction target (BCL-only). Three receiver shapes, each reached via the BYREF
// `this` of an instance method, to localize what aborts mscordbi when drhook_locals expands `this`:
//
//   NormalBox  : a NORMAL struct (int + string)        — marker BOX_BP       (ADR-013 proved this safe)
//   PlainRef   : a REF STRUCT with only int fields      — marker PLAINREF_BP  (ref-struct-ness alone?)
//   Mimic      : a REF STRUCT shaped like BindingTable   — marker FAULT_BP     (the real finale shape)
//                (_bindings: Span<struct>, _stringBuffer: Span<char> live, scalars, char[]?)
//
// The driver breaks at one marker (per run) and expands `this` field-by-field. Each run is its own
// process because the fault is an access violation escalated to a CSE fail-fast (uncatchable). The
// shape whose run dies localizes the boundary. Synthetic of ck:obs-drhook-adr050-finale.

using System;
using System.Buffers;
using System.Diagnostics;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();
for (int i = 0; i < 500 && !Debugger.IsAttached; i++) Thread.Sleep(10);
Debugger.Break();

Span<Cell> bindings = stackalloc Cell[4];                 // span over stack memory: Span<struct>
bindings[0] = new Cell { A = 11, B = 22 };                // live struct element
char[] rented = ArrayPool<char>.Shared.Rent(1024);        // a live backing array on the heap
"hello".AsSpan().CopyTo(rented);

var box = new NormalBox { _n = 42, _tag = "box-tag" };
var plain = new PlainRef(3, 4);
var mimic = new Mimic(bindings, rented.AsSpan(0, 1024));   // _stringBuffer._reference is LIVE into rented
while (true)
{
    box.Touch(7);
    plain.Touch(7);
    mimic.Touch(7);
    Thread.Sleep(50);
}

struct Cell { public long A; public long B; }             // a 16-byte value-type element

struct NormalBox                                          // NORMAL struct — BYREF this → struct (ADR-013 OK)
{
    public int _n;
    public string? _tag;
    public void Touch(int additional)
    {
        int target = _n + additional;
        _n = target + (_tag?.Length ?? 0);                // BOX_BP
    }
}

ref struct PlainRef                                       // REF STRUCT, only int fields — no byref/span field
{
    private int _a;
    private int _b;
    public PlainRef(int a, int b) { _a = a; _b = b; }
    public void Touch(int additional)
    {
        int target = _a + additional;
        _b = target;                                      // PLAINREF_BP
    }
}

ref struct Mimic                                          // REF STRUCT shaped like BindingTable
{
    private Span<Cell> _bindings;
    private int _count;
    private Span<char> _stringBuffer;
    private int _stringOffset;
    private char[]? _grownArray;

    public Mimic(Span<Cell> bindings, Span<char> stringBuffer)
    {
        _bindings = bindings;
        _count = 0;
        _stringBuffer = stringBuffer;
        _stringOffset = 5;
        _grownArray = null;
    }

    public void Touch(int additional)
    {
        int target = _stringOffset + additional;
        _count += target + (_grownArray?.Length ?? 0);   // FAULT_BP
    }
}
