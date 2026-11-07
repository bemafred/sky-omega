// StackInspector observing the test process itself through EventPipe: CI-safe, no debuggee and no dbgshim.
//
// The sample profiler samples every managed thread on each tick, whatever it is doing (observed 2026-11-07: ten
// threads, about 614 samples each over one second). Before that date HOTSPOT was a thread's share of all samples,
// which measures the number of threads: it named a thread blocked in ReadLine in probe 106's two-thread worker, and
// could not fire at all in a process of ten threads. A sample's type says what its thread was doing.

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
        Assert.True(spun.SampleCount > 10, $"the spinner was sampled {spun.SampleCount} times");
        Assert.True(waited.SampleCount > 10, $"the reader was sampled {waited.SampleCount} times");
        Assert.True(spun.ManagedPercentage > 80, $"the spinner ran managed code in {spun.ManagedSampleCount}/{spun.SampleCount} samples");
        Assert.True(waited.ManagedPercentage < 10, $"the reader ran managed code in {waited.ManagedSampleCount}/{waited.SampleCount} samples");
        Assert.Contains(snapshot.Anomalies, a => a.StartsWith($"HOTSPOT: Thread {spun.ThreadId} ", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Anomalies, a => a.StartsWith($"HOTSPOT: Thread {waited.ThreadId} ", StringComparison.Ordinal));
    }

    [LibraryImport("libc")]
    private static partial int pthread_threadid_np(nint thread, out ulong id);

    [LibraryImport("libc")]
    private static partial int gettid();

    [LibraryImport("kernel32")]
    private static partial uint GetCurrentThreadId();
}
