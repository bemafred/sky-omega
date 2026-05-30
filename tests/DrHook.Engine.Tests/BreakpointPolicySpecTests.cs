// Unit tests for BreakpointPolicySpec → BreakpointPolicy compilation. Compiles via the
// substrate's internal seam (spec.CompileWith(IMemberResolver)) which DebugSession.Compile
// also calls — see Shape B in ADR-010 Increment 2b. The end-to-end conditional breakpoint
// path (live debuggee, real DebugSession) is validated by probes 22-25 / 28 / 30.

using System;
using System.Collections.Generic;
using SkyOmega.DrHook.Engine;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed class BreakpointPolicySpecTests
{
    const int ELEMENT_TYPE_I4 = 0x08;

    // ─── Null-argument validation ──────────────────────────────────────────────

    [Fact]
    public void CompileWith_NullResolver_ThrowsArgumentNull()
    {
        var spec = new BreakpointPolicySpec(Condition: "value == 3");
        Assert.Throws<ArgumentNullException>(() => spec.CompileWith(null!));
    }

    // ─── LogMessage template compilation (ADR-010 Increment 7) ────────────────

    [Fact]
    public void CompileWith_LogMessage_ProducesRenderer()
    {
        var spec = new BreakpointPolicySpec(LogMessage: "v={value}");
        var policy = spec.CompileWith(new NullMemberResolver());
        Assert.NotNull(policy.LogMessage);
        var ctx = new FakeEvalContext(Locals: new() { new LocalValue("value", ELEMENT_TYPE_I4, 7) });
        Assert.Equal("v=7", policy.LogMessage!(ctx));
    }

    // ─── Empty spec round-trips with all fields null/default ───────────────────

    [Fact]
    public void CompileWith_EmptySpec_ProducesPolicyWithAllDefaults()
    {
        var spec = new BreakpointPolicySpec();
        var policy = spec.CompileWith(new NullMemberResolver());
        Assert.Null(policy.Condition);
        Assert.Null(policy.HitCount);
        Assert.Null(policy.LogMessage);
        Assert.Equal(SuspendPolicy.All, policy.Suspend);
    }

    // ─── Condition compilation ─────────────────────────────────────────────────

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void CompileWith_Condition_ProducesEvaluatingDelegate(int actual, bool expected)
    {
        var spec = new BreakpointPolicySpec(Condition: "value == 3");
        var policy = spec.CompileWith(new NullMemberResolver());
        Assert.NotNull(policy.Condition);
        var ctx = new FakeEvalContext(Locals: new() { new LocalValue("value", ELEMENT_TYPE_I4, actual) });
        Assert.Equal(expected, policy.Condition!(ctx));
    }

    [Fact]
    public void CompileWith_ConditionUsingMemberAccess_DelegatesToResolver()
    {
        var resolver = new FakeMemberResolver();
        resolver.Provide("box", "Size", new ArgumentValue(ELEMENT_TYPE_I4, RawValue: 42));
        var spec = new BreakpointPolicySpec(Condition: "box.Size == 42");
        var policy = spec.CompileWith(resolver);
        var ctx = new FakeEvalContext(Locals: new() { new LocalValue("box", 0x12 /*Class*/, RawValue: null) });
        Assert.True(policy.Condition!(ctx));
        Assert.Equal(("box", "Size"), resolver.LastResolved);
    }

    // ─── Passthrough fields ────────────────────────────────────────────────────

    [Fact]
    public void CompileWith_HitCount_IsPassedThroughVerbatim()
    {
        var gate = new HitCountGate(HitCountMode.Equals, 5);
        var spec = new BreakpointPolicySpec(HitCount: gate);
        var policy = spec.CompileWith(new NullMemberResolver());
        Assert.Same(gate, policy.HitCount);
    }

    [Theory]
    [InlineData(SuspendPolicy.All)]
    [InlineData(SuspendPolicy.None)]
    public void CompileWith_Suspend_IsPassedThroughVerbatim(SuspendPolicy suspend)
    {
        var spec = new BreakpointPolicySpec(Suspend: suspend);
        var policy = spec.CompileWith(new NullMemberResolver());
        Assert.Equal(suspend, policy.Suspend);
    }

    // ─── Compiler-fault propagation (unsupported syntax in expression) ─────────

    [Fact]
    public void CompileWith_UnsupportedSyntaxInCondition_PropagatesAtEvaluation()
    {
        // Conditional ?: is outside the substrate walker's supported expression set; the spec
        // compiles lazily, so the NotSupportedException surfaces on first invocation. The
        // substrate's policy evaluator (DebugSession.EvaluatePolicy) catches this and surfaces
        // as StopReason.ConditionError; for this unit test we only assert the delegate behavior.
        var spec = new BreakpointPolicySpec(Condition: "value > 0 ? true : false");
        var policy = spec.CompileWith(new NullMemberResolver());
        var ctx = new FakeEvalContext(Locals: new() { new LocalValue("value", ELEMENT_TYPE_I4, 3) });
        Assert.Throws<NotSupportedException>(() => policy.Condition!(ctx));
    }

    // ─── Test helpers ──────────────────────────────────────────────────────────

    private sealed record FakeEvalContext(List<LocalValue> Locals, List<ArgumentValue>? Arguments = null) : IEvalContext
    {
        IReadOnlyList<LocalValue> IEvalContext.Locals => Locals;
        IReadOnlyList<ArgumentValue> IEvalContext.Arguments => Arguments ?? new();
    }

    private sealed class NullMemberResolver : IMemberResolver
    {
        public EvalStatus TryEvalMemberCall(string thisLocalName, string memberName, TimeSpan timeout, out ArgumentValue result)
        {
            result = default;
            throw new InvalidOperationException("NullMemberResolver: TryEvalMemberCall unexpectedly invoked.");
        }
    }

    private sealed class FakeMemberResolver : IMemberResolver
    {
        readonly Dictionary<(string, string), ArgumentValue> _provided = new();
        public (string, string)? LastResolved { get; private set; }

        public void Provide(string thisLocal, string member, ArgumentValue value)
            => _provided[(thisLocal, member)] = value;

        public EvalStatus TryEvalMemberCall(string thisLocalName, string memberName, TimeSpan timeout, out ArgumentValue result)
        {
            LastResolved = (thisLocalName, memberName);
            if (_provided.TryGetValue((thisLocalName, memberName), out result))
                return EvalStatus.Completed;
            result = default;
            return EvalStatus.SetupFailed;
        }
    }
}
