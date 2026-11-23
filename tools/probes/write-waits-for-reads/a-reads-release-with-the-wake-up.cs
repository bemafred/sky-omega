#!/usr/bin/env -S dotnet
#:project ../../../src/Mercury/Mercury.csproj
#:property TargetFramework=net10.0
#:property PublishAot=false
// What a read's release costs with ADR-066 D1's wake-up: every release of a store's lock leaves it, then takes the
// store's monitor and wakes the writes waiting on it. Measured on QuadStore itself: 4 threads, each taking and releasing
// the read lock 200,000 times, timed around each AcquireReadLock and each ReleaseReadLock.
//   alone:          no other read and no write.
//   a write waits:  one read holds the lock throughout, and a write waits for it. Each release wakes that write, which
//                   tries again under the monitor, fails while the held read lasts, and waits again: the worst case for
//                   a release, which meets the write's try at the monitor.
//
//   dotnet run tools/probes/write-waits-for-reads/a-reads-release-with-the-wake-up.cs
using System.Diagnostics;
using SkyOmega.Mercury.Storage;

var dir = Directory.CreateTempSubdirectory("mercury-probe-wake-up-");
try
{
    foreach (var way in new[] { "alone", "a write waits" })
    {
        using var store = new QuadStore(Path.Combine(dir.FullName, way.Replace(' ', '-')), null, null,
            StorageOptions.ForTesting);
        store.AddCurrent("<urn:s>", "<urn:p>", "\"warm\"");

        var held = new ManualResetEventSlim();
        var end = new ManualResetEventSlim();
        Thread? holder = null, writer = null;
        if (way == "a write waits")
        {
            holder = new Thread(() => { store.AcquireReadLock(); held.Set(); end.Wait(); store.ReleaseReadLock(); });
            holder.Start();
            held.Wait();
            writer = new Thread(() => store.AddCurrent("<urn:s>", "<urn:p>", "\"written\""));
            writer.Start();
            Thread.Sleep(100); // the write is waiting: the probe sees only the store's public surface
        }

        var enters = new List<double>[4];
        var releases = new List<double>[4];
        var readers = Enumerable.Range(0, 4).Select(i =>
        {
            enters[i] = new(200_000);
            releases[i] = new(200_000);
            return new Thread(() =>
            {
                var sw = new Stopwatch();
                for (int n = 0; n < 200_000; n++)
                {
                    sw.Restart();
                    store.AcquireReadLock();
                    enters[i].Add(sw.Elapsed.TotalMicroseconds);
                    sw.Restart();
                    store.ReleaseReadLock();
                    releases[i].Add(sw.Elapsed.TotalMicroseconds);
                }
            });
        }).ToList();
        readers.ForEach(t => t.Start());
        readers.ForEach(t => t.Join());
        bool writeWaitedThroughout = writer?.IsAlive ?? true;

        end.Set();
        holder?.Join();
        writer?.Join();

        Console.WriteLine($"{way}: {readers.Count * 200_000:N0} reads" +
            (way == "alone" ? "" : $"; the write waited throughout: {writeWaitedThroughout}"));
        foreach (var (what, times) in new[] { ("enter", enters), ("release", releases) })
        {
            var all = times.SelectMany(t => t).OrderBy(t => t).ToList();
            Console.WriteLine($"  {what,-7} microseconds: median {all[all.Count / 2]:F2}, " +
                $"99.9% {all[(int)(all.Count * 0.999)]:F2}, max {all[^1]:F0}");
        }
    }
}
finally
{
    dir.Delete(recursive: true);
}
