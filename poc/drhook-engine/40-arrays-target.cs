#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 40 TARGET — three SZARRAY locals in scope at a breakpoint:
//   numbers : int[]   primitives.
//   names   : string[] strings (each rendered via StringValue).
//   items   : Item[]  recursive — each Item is an object with an int N field.
// Validates ArrayInspector at depth=1 (elements named "[i]"), and at depth=2 nested through
// arrays into object fields. Marker token kept OFF this comment so FindMarker resolves to
// the actual code line (see probe 32/36 lesson).

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
    int[] numbers = new[] { 1, 2, 3, 5, 8 };
    string[] names = new[] { "alpha", "beta", "gamma" };
    Item[] items = new[] { new Item(10), new Item(20), new Item(30) };
    GC.KeepAlive((numbers, names, items));   // ARRAYS_HERE
}

sealed class Item
{
    public int N;
    public Item(int n) { N = n; }
}
