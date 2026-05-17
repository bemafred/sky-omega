using System;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.Diagnostics;
using Xunit;

namespace SkyOmega.Mercury.Tests.Diagnostics;

/// <summary>
/// Tests for <see cref="MetricEmissionThrottle"/> (ADR-043 Part 2). Validates the
/// time-based gate behavior + concurrent-thread correctness.
/// </summary>
public class MetricEmissionThrottleTests
{
    [Fact]
    public void FirstCall_AlwaysReturnsTrue()
    {
        var throttle = new MetricEmissionThrottle(TimeSpan.FromSeconds(1));
        Assert.True(throttle.ShouldEmit());
    }

    [Fact]
    public void SecondCallWithinInterval_ReturnsFalse()
    {
        var throttle = new MetricEmissionThrottle(TimeSpan.FromMinutes(1));
        Assert.True(throttle.ShouldEmit());
        Assert.False(throttle.ShouldEmit());
    }

    [Fact]
    public void SecondCallAfterInterval_ReturnsTrue()
    {
        var throttle = new MetricEmissionThrottle(TimeSpan.FromMilliseconds(50));
        Assert.True(throttle.ShouldEmit());
        Thread.Sleep(TimeSpan.FromMilliseconds(80));
        Assert.True(throttle.ShouldEmit());
    }

    [Fact]
    public void Reset_ForcesNextCallToReturnTrueEvenWithinInterval()
    {
        var throttle = new MetricEmissionThrottle(TimeSpan.FromMinutes(1));
        Assert.True(throttle.ShouldEmit());
        Assert.False(throttle.ShouldEmit());
        throttle.Reset();
        Assert.True(throttle.ShouldEmit());
    }

    [Fact]
    public void ConcurrentCallers_ExactlyOneReturnsTruePerInterval()
    {
        var throttle = new MetricEmissionThrottle(TimeSpan.FromMinutes(1));
        const int callerCount = 64;
        int trueCount = 0;
        var barrier = new Barrier(callerCount);

        Parallel.For(0, callerCount, _ =>
        {
            barrier.SignalAndWait();
            if (throttle.ShouldEmit())
                Interlocked.Increment(ref trueCount);
        });

        Assert.Equal(1, trueCount);
    }

    [Fact]
    public void ZeroInterval_EveryCallReturnsTrue()
    {
        var throttle = new MetricEmissionThrottle(TimeSpan.Zero);
        for (int i = 0; i < 100; i++)
            Assert.True(throttle.ShouldEmit());
    }

    [Fact]
    public void NegativeInterval_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MetricEmissionThrottle(TimeSpan.FromSeconds(-1)));
    }
}
