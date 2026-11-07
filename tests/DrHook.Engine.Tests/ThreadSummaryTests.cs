// A thread's state and the frame it is at, read from its samples. The stacks and counts are the ones drhook_snapshot
// captured on 2026-11-07 from a target with a spinner, a lock holder and contender, a collector, and a finalizer
// (dogfooding over the live MCP). Before the fix, `At` took the innermost frame of the most frequent stack, so a
// thread split near evenly between two stacks was "at" whichever won by a few samples, and a thread's state was
// read from its own samples only, so a thread sampled in 2 of the capture's 1,176 ticks was "running managed code".

using SkyOmega.DrHook.Engine.Diagnostics;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed class ThreadSummaryTests
{
    [Fact]
    public void At_IsTheSpinMethod_NotTheRuntimeHelperItCallsInHalfItsSamples()
    {
        var spinner = new ThreadSummary(ThreadId: 1, SampleCount: 1176, ManagedSampleCount: 1176, Ticks: 1176, Stacks: [
            new CollapsedStack([
                "System.Runtime.CompilerServices.StaticsHelpers.GetNonGCStaticBase(value class System.Runtime.CompilerServices.MethodTable*)",
                "Behaviours.Spin()",
                "System.Threading.Thread.StartCallback()"], 622, 622),
            new CollapsedStack(["Behaviours.Spin()", "System.Threading.Thread.StartCallback()"], 554, 554)]);

        Assert.Equal("Behaviours.Spin()", spinner.At);
        Assert.Equal("running managed code", spinner.State);
    }

    [Fact]
    public void At_IsTheContendingMethod_WhenTheThreadSplitsItsTimeBetweenTheLockAndASleep()
    {
        var contender = new ThreadSummary(ThreadId: 2, SampleCount: 1173, ManagedSampleCount: 59, Ticks: 1176, Stacks: [
            new CollapsedStack([
                "System.Threading.Monitor.Enter_Slowpath(class System.Object)",
                "System.Threading.Monitor.Enter(class System.Object,bool&)",
                "Behaviours.ContendForTheGate()",
                "System.Threading.Thread.StartCallback()"], 633, 2),
            new CollapsedStack([
                "System.Threading.Thread.Sleep(int32)",
                "Behaviours.ContendForTheGate()",
                "System.Threading.Thread.StartCallback()"], 540, 57)]);

        Assert.Equal("Behaviours.ContendForTheGate()", contender.At);
        Assert.Equal("outside managed code", contender.State);
    }

    [Fact]
    public void At_StaysTheInnermostFrame_WhenTheThreadIsThereInNearlyAllItsSamples()
    {
        var collector = new ThreadSummary(ThreadId: 3, SampleCount: 1176, ManagedSampleCount: 12, Ticks: 1176, Stacks: [
            new CollapsedStack([
                "System.Threading.Thread.Sleep(int32)",
                "Behaviours.CollectGarbage()",
                "System.Threading.Thread.StartCallback()"], 1173, 9),
            new CollapsedStack([
                "System.GC._Collect(int32,int32,int32)",
                "Behaviours.CollectGarbage()",
                "System.Threading.Thread.StartCallback()"], 3, 3)]);

        Assert.Equal("System.Threading.Thread.Sleep(int32)", collector.At);
    }

    [Fact]
    public void State_IsOutsideManagedCode_ForAThreadSampledInFewOfTheCapturesTicks()
    {
        // The sampler takes no sample of a thread with no managed frame on its stack: the finalizer waits below every
        // managed frame, and was sampled only in the 2 ticks it ran finalizers.
        var finalizer = new ThreadSummary(ThreadId: 4, SampleCount: 2, ManagedSampleCount: 2, Ticks: 1176, Stacks: [
            new CollapsedStack(["System.GC.RunFinalizers()"], 2, 2)]);

        Assert.Equal("outside managed code", finalizer.State);
        Assert.Equal(100.0 * 2 / 1176, finalizer.ManagedPercentage);
        Assert.Equal("System.GC.RunFinalizers()", finalizer.At);
    }
}
