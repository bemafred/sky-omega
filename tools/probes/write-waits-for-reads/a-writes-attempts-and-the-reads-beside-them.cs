#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
#:property PublishAot=false
// Worst case for the rule "a write waits outside the lock": a write that tries TryEnterWriteLock(0) without pause,
// while one read holds the lock throughout and other reads enter and leave on 4 threads. Does a try hold a read up?
//
//   dotnet run tools/probes/write-waits-for-reads/a-writes-attempts-and-the-reads-beside-them.cs
using System.Diagnostics;

var rw = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
var held = new ManualResetEventSlim();
var stop = false;
var holder = new Thread(() => { rw.EnterReadLock(); held.Set(); while (!Volatile.Read(ref stop)) Thread.Sleep(10); rw.ExitReadLock(); });
holder.Start(); held.Wait();
long tries = 0, entered = 0;
var writer = new Thread(() => { while (!Volatile.Read(ref stop)) { if (rw.TryEnterWriteLock(0)) { entered++; rw.ExitWriteLock(); } tries++; } });
writer.Start();
var waits = new List<double>[4];
var readers = Enumerable.Range(0, 4).Select(i => { waits[i] = new(); return new Thread(() =>
{
    var sw = new Stopwatch();
    for (int n = 0; n < 200_000; n++) { sw.Restart(); rw.EnterReadLock(); var us = sw.Elapsed.TotalMicroseconds; rw.ExitReadLock(); waits[i].Add(us); }
}); }).ToList();
readers.ForEach(t => t.Start()); readers.ForEach(t => t.Join());
Volatile.Write(ref stop, true); writer.Join(); holder.Join();
var all = waits.SelectMany(w => w).OrderBy(x => x).ToList();
Console.WriteLine($"{all.Count:N0} reads beside {tries:N0} write tries ({entered} entered while a read held the lock)");
Console.WriteLine($"a read's wait to enter, microseconds: median {all[all.Count / 2]:F2}, 99.9% {all[(int)(all.Count * 0.999)]:F2}, max {all[^1]:F0}");
