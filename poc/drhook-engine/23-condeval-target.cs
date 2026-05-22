#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 23 TARGET — a string local whose length cycles 1..4.
// Worker.Step(s) is called with "a","ab","abc","abcd" in turn; the probe sets a condition
// s.Length > 3, which first holds at "abcd" (length 4). Tests func-eval INSIDE a conditional
// predicate. Marker token kept off the header comment.

using System;
using System.Diagnostics;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

for (int i = 0; i < 500 && !Debugger.IsAttached; i++) Thread.Sleep(10);
Debugger.Break();

var worker = new Worker();
string[] words = { "a", "ab", "abc", "abcd" }; // lengths 1, 2, 3, 4
int n = 0;
while (true)
{
    worker.Step(words[n % words.Length]);
    n++;
    Thread.Sleep(20);
}

sealed class Worker
{
    public void Step(string word)
    {
        string s = word;   // s is a LOCAL (the eval resolves `this` from named locals)
        GC.KeepAlive(s);   // CONDEVAL_HERE
    }
}
