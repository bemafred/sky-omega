#!/usr/bin/env -S dotnet
//
// ADR-013 validation target (BCL-only). A struct instance method receives `this` as a managed ref
// (BYREF, 0x10), and the ReadOnlySpan<char> arg is a VALUETYPE (0x11). The driver breaks at the
// marker and expands both — the two element types the lazy-inspection first increment couldn't open.

using System;
using System.Diagnostics;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();
for (int i = 0; i < 500 && !Debugger.IsAttached; i++) Thread.Sleep(10);
Debugger.Break();

var box = new Box { N = 42, Tag = "box-tag" };
while (true)
{
    box.Inspect("hello-span".AsSpan());   // s.Length == 10
    Thread.Sleep(50);
}

struct Box
{
    public int N;
    public string? Tag;
    // this = ref Box (BYREF 0x10); s = ReadOnlySpan<char> (VALUETYPE 0x11).
    public void Inspect(ReadOnlySpan<char> s)
    {
        int len = s.Length;
        N += len;   // VT_BP
    }
}
