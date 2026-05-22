#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 24 TARGET — a user-defined object with a property, for GENERAL member
// resolution. Worker.Inspect has a local `box` (a Box with Size = 42); the probe resolves
// box.Size on box's RUNTIME type (no hardcoded type/module) and func-evals get_Size, expecting 42.
// Box is in this target's own module (not CoreLib). Marker token kept off the header comment.

using System;
using System.Diagnostics;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

for (int i = 0; i < 500 && !Debugger.IsAttached; i++) Thread.Sleep(10);
Debugger.Break();

var worker = new Worker();
while (true)
{
    worker.Inspect();
    Thread.Sleep(20);
}

sealed class Box
{
    public int Size { get; }
    public Box(int size) => Size = size;
}

sealed class Worker
{
    public void Inspect()
    {
        var box = new Box(42);
        GC.KeepAlive(box);   // MEMBER_HERE
    }
}
