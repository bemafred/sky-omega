#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 25 TARGET — member-access CONDITIONAL breakpoint. Worker.Inspect builds a
// Box whose Size cycles 40..44 across iterations; the probe sets an unconditional breakpoint here
// and drives it with a Roslyn-parsed condition "box.Size == 42", so only the Size==42 iteration
// should surface. `box` is a real LOCAL (TryEvalMemberCall resolves the operand from locals).
// Box lives in this target's own module. Marker token kept off the header comment.

using System;
using System.Diagnostics;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

for (int i = 0; i < 500 && !Debugger.IsAttached; i++) Thread.Sleep(10);
Debugger.Break();

var worker = new Worker();
int n = 0;
while (true)
{
    worker.Inspect(40 + (n++ % 5));   // sizes cycle 40,41,42,43,44,40,…
    Thread.Sleep(20);
}

sealed class Box
{
    public int Size { get; }
    public Box(int size) => Size = size;
}

sealed class Worker
{
    public void Inspect(int size)
    {
        var box = new Box(size);
        GC.KeepAlive(box);   // MEMBER_HERE
    }
}
