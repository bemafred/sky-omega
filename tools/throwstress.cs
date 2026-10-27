#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
#:property PublishAot=false
// Runtime stress probe: does this machine's .NET survive exception throws under parallel load?
//
// Reproduces "Fatal error. Internal CLR error. (0x80131506)" on Ubuntu's distro .NET 10
// (dotnet-runtime-10.0 linked to the system libunwind). Microsoft's build survives it.
// See docs/operations/runbook-linux-dotnet-runtime.md.
//
// Throwers throw through deep managed stacks; JIT threads emit and run new methods (some
// of which throw); a GC thread forces suspensions. Exit 0 = survived; exit 134 with the
// CLR error = the defect. One 30 s run is not enough to clear a runtime: on the affected
// build about 1 in 4 runs crashed, so run it 20 times.
//
// Usage: dotnet run tools/throwstress.cs -- [seconds=30]
// The last line prints the runtime directory that actually ran.
using System;
using System.Diagnostics;
using System.Reflection.Emit;
using System.Threading;

int seconds = args.Length > 0 ? int.Parse(args[0]) : 30;
int cores = Environment.ProcessorCount;
var stop = DateTime.UtcNow.AddSeconds(seconds);
long throws = 0, jitted = 0, gcs = 0;
var threads = new System.Collections.Generic.List<Thread>();

for (int t = 0; t < cores; t++)
{
    int seed = t;
    threads.Add(new Thread(() =>
    {
        var rnd = new Random(seed);
        while (DateTime.UtcNow < stop)
        {
            try { Deep(rnd.Next(1, 40)); } catch (InvalidOperationException) { }
            Interlocked.Increment(ref throws);
        }
    }) { IsBackground = true });
}

for (int t = 0; t < Math.Max(2, cores / 2); t++)
{
    int seed = 1000 + t;
    threads.Add(new Thread(() =>
    {
        var rnd = new Random(seed);
        long n = 0;
        while (DateTime.UtcNow < stop)
        {
            // A fresh DynamicMethod is a fresh JIT every time.
            var dm = new DynamicMethod($"m{seed}_{n++}", typeof(int), new[] { typeof(int) });
            var il = dm.GetILGenerator();
            int k = rnd.Next(1, 30);
            il.Emit(OpCodes.Ldarg_0);
            for (int i = 0; i < k; i++) { il.Emit(OpCodes.Ldc_I4, i + 1); il.Emit(i % 2 == 0 ? OpCodes.Add : OpCodes.Xor); }
            if (rnd.Next(3) == 0)
            {
                // Throw from freshly JIT-compiled code.
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldstr, "emitted");
                il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) })!);
                il.Emit(OpCodes.Throw);
            }
            else il.Emit(OpCodes.Ret);
            var f = (Func<int, int>)dm.CreateDelegate(typeof(Func<int, int>));
            try { f(k); } catch (InvalidOperationException) { }
            Interlocked.Increment(ref jitted);
        }
    }) { IsBackground = true });
}

threads.Add(new Thread(() =>
{
    while (DateTime.UtcNow < stop)
    {
        var junk = new byte[rnd2()][];
        for (int i = 0; i < junk.Length; i++) junk[i] = new byte[1024];
        GC.Collect(rnd2() % 3 == 0 ? 2 : 0);
        Interlocked.Increment(ref gcs);
        Thread.Sleep(5);
    }
    static int rnd2() => Random.Shared.Next(100, 2000);
}) { IsBackground = true });

var sw = Stopwatch.StartNew();
foreach (var th in threads) th.Start();
foreach (var th in threads) th.Join();
Console.WriteLine($"ok {sw.Elapsed.TotalSeconds:F0}s throws={throws} jitted={jitted} gcs={gcs} runtime={System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()}");
return 0;

static int Deep(int n)
{
    if (n == 0) throw new InvalidOperationException("deep");
    return Deep(n - 1) + 1;
}
