// StackInspector observing the test process itself through EventPipe: CI-safe, no debuggee and no dbgshim.
//
// The sample profiler samples every managed thread on each tick, whatever it is doing (observed 2026-11-07: ten
// threads, about 614 samples each over one second). Before that date HOTSPOT was a thread's share of all samples,
// which measures the number of threads: it named a thread blocked in ReadLine in probe 106's two-thread worker, and
// could not fire at all in a process of ten threads. A sample's type says what its thread was doing, and its stack
// where. The GC and contention counts were always zero until the same date: the runtime provider was enabled at
// Warning, and those events are Informational.

using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkyOmega.DrHook.Engine.Diagnostics;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed partial class StackInspectorTests
{
    [Fact]
    public async Task Capture_NamesTheThreadRunningManagedCode_AndNotTheThreadBlockedInARead()
    {
        ulong spinnerId = 0, readerId = 0;
        var spinnerStarted = new ManualResetEventSlim();
        var readerStarted = new ManualResetEventSlim();
        var stop = new StrongBox<bool>(false);
        var spinner = new Thread(() =>
        {
            spinnerId = OperatingSystem.IsWindows() ? GetCurrentThreadId()
                : OperatingSystem.IsMacOS() ? (pthread_threadid_np(0, out ulong id) == 0 ? id : 0)
                : (ulong)gettid();
            spinnerStarted.Set();
            long turns = 0;
            while (!Volatile.Read(ref stop.Value)) turns++;
            GC.KeepAlive(turns);
        }) { IsBackground = true, Name = "spinner" };
        using var pipe = new AnonymousPipeServerStream(PipeDirection.In);
        using var writer = new AnonymousPipeClientStream(PipeDirection.Out, pipe.ClientSafePipeHandle);
        var reader = new Thread(() =>
        {
            readerId = OperatingSystem.IsWindows() ? GetCurrentThreadId()
                : OperatingSystem.IsMacOS() ? (pthread_threadid_np(0, out ulong id) == 0 ? id : 0)
                : (ulong)gettid();
            readerStarted.Set();
            pipe.ReadByte(); // blocks until the test writes a byte
        }) { IsBackground = true, Name = "reader" };
        spinner.Start();
        reader.Start();
        spinnerStarted.Wait(TestContext.Current.CancellationToken);
        readerStarted.Wait(TestContext.Current.CancellationToken);

        ObservationSnapshot snapshot;
        try
        {
            snapshot = await new StackInspector().CaptureAsync(Environment.ProcessId, 1000,
                "the spinner runs managed code in nearly every sample; the reader in none", TestContext.Current.CancellationToken);
        }
        finally
        {
            Volatile.Write(ref stop.Value, true);
            writer.WriteByte(0);
            spinner.Join();
            reader.Join();
        }

        ThreadSummary spun = Assert.Single(snapshot.ThreadSummary, t => (ulong)t.ThreadId == spinnerId);
        ThreadSummary waited = Assert.Single(snapshot.ThreadSummary, t => (ulong)t.ThreadId == readerId);
        // On a Windows arm64 runner the spinner was sampled 10 times in a second (CI run 45227063749, 2026-11-23), and the
        // message said only that, not whether the whole capture had about 10 ticks or the spinner alone was missed. The
        // counts of both threads and of the capture tell the two apart.
        string counts = $"the spinner was sampled {spun.SampleCount} times and the reader {waited.SampleCount}, in a capture of " +
            $"{snapshot.Ticks} ticks over {snapshot.DurationMs} ms, {snapshot.TotalSamples} samples of {snapshot.SampledThreadCount} threads";
        Assert.True(spun.SampleCount > 10, counts);
        Assert.True(waited.SampleCount > 10, counts);
        Assert.True(spun.ManagedPercentage > 80, $"the spinner ran managed code in {spun.ManagedSampleCount}/{spun.SampleCount} samples");
        Assert.True(waited.ManagedPercentage < 10, $"the reader ran managed code in {waited.ManagedSampleCount}/{waited.SampleCount} samples");
        Assert.Contains(snapshot.Anomalies, a => a.StartsWith($"HOTSPOT: Thread {spun.ThreadId} ", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Anomalies, a => a.StartsWith($"HOTSPOT: Thread {waited.ThreadId} ", StringComparison.Ordinal));

        // The thread state overview and the stacks: the spinner runs in this test's own lambda, the reader waits in
        // the pipe's read.
        Assert.Equal("running managed code", spun.State);
        Assert.Equal("outside managed code", waited.State);
        Assert.NotNull(spun.At);
        Assert.Contains(nameof(Capture_NamesTheThreadRunningManagedCode_AndNotTheThreadBlockedInARead), spun.At);
        Assert.Contains(waited.Stacks[0].Frames, f => f.StartsWith("System.IO.Pipes.PipeStream.Read", StringComparison.Ordinal));
        Assert.Contains($"at {spun.At}", snapshot.Anomalies.Single(a => a.StartsWith($"HOTSPOT: Thread {spun.ThreadId} ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Capture_CountsTheGarbageCollectionsTheProcessRuns()
    {
        // When the collector starts each collection, in local time, as TraceEvent stamps a GCStart.
        var started = new List<DateTime>();
        var stop = new StrongBox<bool>(false);
        var collector = new Thread(() =>
        {
            while (!Volatile.Read(ref stop.Value))
            {
                lock (started) started.Add(DateTime.Now);
                GC.Collect();
                Thread.Sleep(50);
            }
        }) { IsBackground = true, Name = "collector" };
        collector.Start();

        ObservationSnapshot snapshot;
        try
        {
            snapshot = await new StackInspector().CaptureAsync(Environment.ProcessId, 1000,
                "a collection every 50 ms is counted", TestContext.Current.CancellationToken);
        }
        finally
        {
            Volatile.Write(ref stop.Value, true);
            collector.Join();
        }

        // How many collections fall in the capture depends on the machine's load: on a loaded runner a second held 4 of
        // them, not about 20 (macOS x64 CI, 2026-11-21, run 44918126706). What does not depend on it: every collection
        // the collector started between the first and the last one counted is counted too (10 ms for the two clocks).
        Assert.True(snapshot.GcEvents.Count >= 2, $"{snapshot.GcEvents.Count} garbage collections were counted in a second of one every 50 ms");
        DateTime first = snapshot.GcEvents.Min(e => e.Timestamp) - TimeSpan.FromMilliseconds(10);
        DateTime last = snapshot.GcEvents.Max(e => e.Timestamp) + TimeSpan.FromMilliseconds(10);
        int ranInBetween;
        lock (started) ranInBetween = started.Count(t => t >= first && t <= last);
        Assert.True(snapshot.GcEvents.Count >= ranInBetween,
            $"{snapshot.GcEvents.Count} garbage collections were counted, and the collector started {ranInBetween} between the first and the last of them");
    }

    [Fact]
    public async Task Capture_CountsTheLockContentionTheProcessHas()
    {
        var stop = new StrongBox<bool>(false);
        var gate = new object();
        var holder = new Thread(() =>
        {
            while (!Volatile.Read(ref stop.Value)) lock (gate) Thread.Sleep(50);
        }) { IsBackground = true, Name = "holder" };
        var contender = new Thread(() =>
        {
            while (!Volatile.Read(ref stop.Value)) { lock (gate) { } Thread.Sleep(10); }
        }) { IsBackground = true, Name = "contender" };
        holder.Start();
        contender.Start();

        ObservationSnapshot snapshot;
        try
        {
            snapshot = await new StackInspector().CaptureAsync(Environment.ProcessId, 1000,
                "the contender blocks on the holder's lock many times", TestContext.Current.CancellationToken);
        }
        finally
        {
            Volatile.Write(ref stop.Value, true);
            holder.Join();
            contender.Join();
        }

        Assert.True(snapshot.ContentionCount >= 5, $"{snapshot.ContentionCount} contentions were counted in a second of a lock held 50 ms at a time");
    }

    [LibraryImport("libc")]
    private static partial int pthread_threadid_np(nint thread, out ulong id);

    [LibraryImport("libc")]
    private static partial int gettid();

    [LibraryImport("kernel32")]
    private static partial uint GetCurrentThreadId();
}
