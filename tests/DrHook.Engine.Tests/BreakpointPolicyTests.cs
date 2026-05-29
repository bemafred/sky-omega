// Unit tests for the pure logic of BreakpointPolicy gates. The end-to-end policy loop
// (BreakpointPolicy attached at SetBreakpoint time + DebugSession.WaitForStop evaluating
// condition + hit-count + log action + suspend on the caller thread) is validated live by
// probe 28; here we just pin the arithmetic so refactors can't drift.

using SkyOmega.DrHook.Engine;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed class BreakpointPolicyTests
{
    [Theory]
    [InlineData(3, 2, false)]
    [InlineData(3, 3, true)]
    [InlineData(3, 4, false)]
    public void HitCountGate_Equals_AdmitsOnlyTheExactHit(int value, int hit, bool expected)
        => Assert.Equal(expected, new HitCountGate(HitCountMode.Equals, value).Admits(hit));

    [Theory]
    [InlineData(3, 2, false)]
    [InlineData(3, 3, true)]
    [InlineData(3, 4, true)]
    [InlineData(3, 100, true)]
    public void HitCountGate_AtLeast_AdmitsFromTheNthHitOn(int value, int hit, bool expected)
        => Assert.Equal(expected, new HitCountGate(HitCountMode.AtLeast, value).Admits(hit));

    [Theory]
    [InlineData(3, 1, false)]
    [InlineData(3, 3, true)]
    [InlineData(3, 5, false)]
    [InlineData(3, 6, true)]
    [InlineData(3, 9, true)]
    public void HitCountGate_Multiple_AdmitsEveryNthHit(int value, int hit, bool expected)
        => Assert.Equal(expected, new HitCountGate(HitCountMode.Multiple, value).Admits(hit));

    [Fact]
    public void HitCountGate_Multiple_WithZeroValue_NeverAdmits()
        => Assert.False(new HitCountGate(HitCountMode.Multiple, 0).Admits(7));

    [Fact]
    public void BreakpointPolicy_Default_IsSuspendAllNoGatesNoAction()
    {
        var p = new BreakpointPolicy();
        Assert.Null(p.Condition);
        Assert.Null(p.HitCount);
        Assert.Null(p.LogMessage);
        Assert.Equal(SuspendPolicy.All, p.Suspend);
    }

    [Fact]
    public void LogRecord_Default_IsNotFault()
    {
        var r = new LogRecord(DateTimeOffset.UtcNow, "hi");
        Assert.False(r.IsFault);
    }
}
