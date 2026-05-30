// Unit tests for the pure logic of CSharpCondition (the Roslyn-based expression walker that turns
// a condition string into a Func<IEvalContext, bool>). The end-to-end conditional-breakpoint flow
// (BreakpointPolicy.Condition evaluated at each hit on the caller thread inside
// DebugSession.WaitForStop) is validated live by probes 22-25; here we pin the parser/walker
// arithmetic against a fake IMemberResolver + IEvalContext so refactors can't drift without notice.

using System;
using System.Collections.Generic;
using SkyOmega.DrHook.Engine;
using SkyOmega.DrHook.Engine.Expressions;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed class CSharpConditionTests
{
    const int ELEMENT_TYPE_I4 = 0x08;
    const int ELEMENT_TYPE_BOOLEAN = 0x02;

    // ─── Locals (identifier resolution) ────────────────────────────────────────

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void Local_Equality_AgainstLiteral(int actual, bool expected)
    {
        var predicate = CSharpCondition.Compile("value == 3", new NullMemberResolver());
        var ctx = new FakeEvalContext(Locals: new() { L("value", actual) });
        Assert.Equal(expected, predicate(ctx));
    }

    [Fact]
    public void Local_NotFound_ThrowsInvalidOperation()
    {
        var predicate = CSharpCondition.Compile("missing == 1", new NullMemberResolver());
        var ctx = new FakeEvalContext(Locals: new());
        var ex = Assert.Throws<InvalidOperationException>(() => predicate(ctx));
        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public void Local_WithoutPrimitive_ThrowsInvalidOperation()
    {
        // Object references have RawValue == null; resolving them as a value throws.
        var predicate = CSharpCondition.Compile("obj == 1", new NullMemberResolver());
        var ctx = new FakeEvalContext(Locals: new() { new LocalValue("obj", 0x12 /*Class*/, RawValue: null) });
        var ex = Assert.Throws<InvalidOperationException>(() => predicate(ctx));
        Assert.Contains("obj", ex.Message);
    }

    // ─── Binary comparisons (typed operands via System.Linq.Expressions.MakeBinary) ──────────

    [Theory]
    [InlineData("value == 5", 5, true)]
    [InlineData("value == 5", 4, false)]
    [InlineData("value != 5", 5, false)]
    [InlineData("value != 5", 4, true)]
    [InlineData("value < 5",  4, true)]
    [InlineData("value < 5",  5, false)]
    [InlineData("value > 5",  6, true)]
    [InlineData("value > 5",  5, false)]
    [InlineData("value <= 5", 5, true)]
    [InlineData("value <= 5", 6, false)]
    [InlineData("value >= 5", 5, true)]
    [InlineData("value >= 5", 4, false)]
    public void BinaryComparisons(string expr, int actual, bool expected)
    {
        var predicate = CSharpCondition.Compile(expr, new NullMemberResolver());
        Assert.Equal(expected, predicate(new FakeEvalContext(Locals: new() { L("value", actual) })));
    }

    // ─── Logical NOT, AND, OR (with short-circuit semantics) ──────────────────

    [Fact]
    public void Not_FlipsBoolean()
    {
        var predicate = CSharpCondition.Compile("!(value == 3)", new NullMemberResolver());
        Assert.True(predicate(new FakeEvalContext(Locals: new() { L("value", 4) })));
        Assert.False(predicate(new FakeEvalContext(Locals: new() { L("value", 3) })));
    }

    [Fact]
    public void And_ShortCircuit_DoesNotEvaluateRightSideOnFalseLeft()
    {
        // Right side references a local that doesn't exist — if evaluated, would throw.
        // Short-circuit means the predicate returns false without touching the right side.
        var predicate = CSharpCondition.Compile("value == 3 && missing == 1", new NullMemberResolver());
        Assert.False(predicate(new FakeEvalContext(Locals: new() { L("value", 4) })));
    }

    [Fact]
    public void Or_ShortCircuit_DoesNotEvaluateRightSideOnTrueLeft()
    {
        var predicate = CSharpCondition.Compile("value == 3 || missing == 1", new NullMemberResolver());
        Assert.True(predicate(new FakeEvalContext(Locals: new() { L("value", 3) })));
    }

    // ─── Parenthesized passthrough ────────────────────────────────────────────

    [Fact]
    public void Parenthesized_PassesThroughEvaluation()
    {
        var predicate = CSharpCondition.Compile("(value == 3)", new NullMemberResolver());
        Assert.True(predicate(new FakeEvalContext(Locals: new() { L("value", 3) })));
    }

    // ─── Member access via IMemberResolver ────────────────────────────────────

    [Fact]
    public void MemberAccess_DelegatesToResolver()
    {
        var resolver = new FakeMemberResolver();
        resolver.Provide("box", "Size", new ArgumentValue(ELEMENT_TYPE_I4, RawValue: 42));
        var predicate = CSharpCondition.Compile("box.Size == 42", resolver);
        Assert.True(predicate(new FakeEvalContext(Locals: new() { new LocalValue("box", 0x12 /*Class*/, RawValue: null) })));
        Assert.Equal(("box", "Size"), resolver.LastResolved);
    }

    [Fact]
    public void MemberAccess_ResolverFails_ThrowsInvalidOperation()
    {
        var resolver = new FakeMemberResolver(failingStatus: EvalStatus.TimedOut);
        var predicate = CSharpCondition.Compile("box.Size == 42", resolver);
        var ex = Assert.Throws<InvalidOperationException>(()
            => predicate(new FakeEvalContext(Locals: new() { new LocalValue("box", 0x12, RawValue: null) })));
        Assert.Contains("TimedOut", ex.Message);
    }

    [Fact]
    public void MemberAccess_OperandNotIdentifier_ThrowsNotSupported()
    {
        // Roslyn parses "1.ToString()" as a member access whose operand is a numeric literal.
        // The walker rejects non-identifier operands.
        var predicate = CSharpCondition.Compile("(1).ToString == 1", new NullMemberResolver());
        var ex = Assert.Throws<NotSupportedException>(()
            => predicate(new FakeEvalContext(Locals: new())));
        Assert.Contains("identifier", ex.Message);
    }

    // ─── Arithmetic (ADR-010 Increment 7: typed Expression.MakeBinary on typed operands) ─────

    [Theory]
    [InlineData("value + 1 == 4",  3, true)]
    [InlineData("value + 1 == 5",  3, false)]
    [InlineData("value - 1 == 2",  3, true)]
    [InlineData("value * 2 == 6",  3, true)]
    [InlineData("value / 2 == 1",  3, true)]
    [InlineData("value % 2 == 1",  3, true)]
    public void Arithmetic_OperatorsProduceTypedResults(string expr, int value, bool expected)
    {
        // The substrate compiles `value + 1 == 4` as Equal(Add(Convert(rawCall,int), Constant(1,int)), Constant(4,int)) → bool.
        // No long-flattening: int + int → int, then compared with int → bool.
        var predicate = CSharpCondition.Compile(expr, new NullMemberResolver());
        Assert.Equal(expected, predicate(new FakeEvalContext(Locals: new() { L("value", value) })));
    }

    // ─── Unsupported syntax (the parser accepts, the walker rejects) ─────────────────────────

    [Fact]
    public void ConditionalExpression_ThrowsNotSupported()
    {
        // "value > 0 ? 1 : 0" — ConditionalExpressionSyntax is outside the walker's supported set.
        var predicate = CSharpCondition.Compile("value > 0 ? 1 : 0", new NullMemberResolver());
        var ex = Assert.Throws<NotSupportedException>(()
            => predicate(new FakeEvalContext(Locals: new() { L("value", 3) })));
        Assert.Contains("unsupported expression", ex.Message);
    }

    // ─── Null arguments ───────────────────────────────────────────────────────

    [Fact]
    public void Compile_NullExpression_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(() => CSharpCondition.Compile(null!, new NullMemberResolver()));

    [Fact]
    public void Compile_NullResolver_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(() => CSharpCondition.Compile("value == 1", null!));

    // ─── Test helpers ─────────────────────────────────────────────────────────

    static LocalValue L(string name, long value) => new(name, ELEMENT_TYPE_I4, value);

    private sealed record FakeEvalContext(List<LocalValue> Locals, List<ArgumentValue>? Arguments = null) : IEvalContext
    {
        IReadOnlyList<LocalValue> IEvalContext.Locals => Locals;
        IReadOnlyList<ArgumentValue> IEvalContext.Arguments => Arguments ?? new();
    }

    /// <summary>Member resolver that fails every call with the configured status. Used by tests that
    /// don't exercise member access (the resolver should never be touched) or that assert on
    /// fault propagation.</summary>
    private sealed class NullMemberResolver : IMemberResolver
    {
        public EvalStatus TryEvalMemberCall(string thisLocalName, string memberName, TimeSpan timeout, out ArgumentValue result)
        {
            result = default;
            throw new InvalidOperationException("NullMemberResolver: TryEvalMemberCall was unexpectedly invoked by a test that should not require member resolution.");
        }
    }

    /// <summary>Member resolver with explicit (thisLocal, member) → ArgumentValue mappings.</summary>
    private sealed class FakeMemberResolver : IMemberResolver
    {
        readonly Dictionary<(string, string), ArgumentValue> _provided = new();
        readonly EvalStatus _failingStatus;
        public (string, string)? LastResolved { get; private set; }

        public FakeMemberResolver(EvalStatus failingStatus = EvalStatus.Completed) => _failingStatus = failingStatus;

        public void Provide(string thisLocal, string member, ArgumentValue value)
            => _provided[(thisLocal, member)] = value;

        public EvalStatus TryEvalMemberCall(string thisLocalName, string memberName, TimeSpan timeout, out ArgumentValue result)
        {
            LastResolved = (thisLocalName, memberName);
            if (_failingStatus != EvalStatus.Completed)
            {
                result = default;
                return _failingStatus;
            }
            if (_provided.TryGetValue((thisLocalName, memberName), out result))
                return EvalStatus.Completed;
            result = default;
            return EvalStatus.SetupFailed;
        }
    }
}
