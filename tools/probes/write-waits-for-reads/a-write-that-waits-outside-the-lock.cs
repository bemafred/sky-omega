#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
#:property PublishAot=false
// ADR-066, the rule "a write takes effect only when no read is in progress, and waits for that outside the lock".
// Does ReaderWriterLockSlim (NoRecursion, as QuadStore's) allow it? A read R1 holds the lock, as a query waiting on
// SERVICE would. A write W arrives, then a read R2 (as the SERVICE handler would). Measured, each way W can wait:
//   blocking:  W calls EnterWriteLock, the way every write enters today.
//   outside:   W tries TryEnterWriteLock(0) and, while it fails, waits outside the lock for a read to end.
// For each: how long R2 waits; whether W entered while R1 held the lock; when W entered after R1 let go.
//
//   dotnet run tools/probes/write-waits-for-reads/a-write-that-waits-outside-the-lock.cs
using System.Diagnostics;

const int HoldMs = 2000;
foreach (var way in new[] { "blocking", "outside" })
{
    var rw = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
    var readEnded = new SemaphoreSlim(0);
    var r1Holds = new ManualResetEventSlim();
    var clock = Stopwatch.StartNew();
    long r1Released = -1, wEntered = -1, r2Waited = -1;
    bool wEnteredDuringR1 = false;

    var r1 = new Thread(() => { rw.EnterReadLock(); r1Holds.Set(); Thread.Sleep(HoldMs); Volatile.Write(ref r1Released, clock.ElapsedMilliseconds); rw.ExitReadLock(); readEnded.Release(); });
    r1.Start(); r1Holds.Wait();

    var w = new Thread(() =>
    {
        if (way == "blocking") rw.EnterWriteLock();
        else while (!rw.TryEnterWriteLock(0)) readEnded.Wait(50);
        wEntered = clock.ElapsedMilliseconds;
        wEnteredDuringR1 = Volatile.Read(ref r1Released) < 0;
        Thread.Sleep(10);
        rw.ExitWriteLock();
    });
    w.Start(); Thread.Sleep(100); // W is waiting

    var r2 = new Thread(() => { var t = clock.ElapsedMilliseconds; rw.EnterReadLock(); r2Waited = clock.ElapsedMilliseconds - t; rw.ExitReadLock(); readEnded.Release(); });
    r2.Start();
    r2.Join(); r1.Join(); w.Join();
    Console.WriteLine($"{way,-9} R2 waited {r2Waited,5} ms; W entered at {wEntered,5} ms, R1 let go at {r1Released,5} ms; W entered while R1 held the lock: {wEnteredDuringR1}");
}
