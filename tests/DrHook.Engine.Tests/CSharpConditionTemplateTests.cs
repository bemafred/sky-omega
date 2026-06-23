// Unit tests for CSharpCondition.CompileTemplate — the logpoint template compiler that turns a
// string like "v={value}" into a Func<IEvalContext, string> renderer. End-to-end logpoint emission
// (BreakpointPolicy.LogMessage evaluated each hit; result emitted as a LogRecord via
// IDebugEventSink.OnLog; auto-resume on Suspend.None) is validated live by probe 58. Here we pin
// the parser/renderer arithmetic against a fake IMemberResolver + IEvalContext so refactors can't
// drift without notice. ADR-010 Increment 7.

using System;
using System.Collections.Generic;
using SkyOmega.DrHook.Engine;
using SkyOmega.DrHook.Engine.Expressions;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed class CSharpConditionTemplateTests
{
    const int ELEMENT_TYPE_I4 = 0x08;

    // ─── Null-argument validation ──────────────────────────────────────────────

    [Fact]
    public void CompileTemplate_NullTemplate_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(() => CSharpCondition.CompileTemplate(null!, new NullMemberResolver()));

    [Fact]
    public void CompileTemplate_NullResolver_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(() => CSharpCondition.CompileTemplate("hi", null!));

    // ─── Empty + literal-only templates ───────────────────────────────────────

    [Fact]
    public void CompileTemplate_EmptyTemplate_RendersEmptyString()
    {
        var render = CSharpCondition.CompileTemplate("", new NullMemberResolver());
        Assert.Equal("", render(EmptyCtx));
    }

    [Fact]
    public void CompileTemplate_LiteralOnly_RendersVerbatim()
    {
        var render = CSharpCondition.CompileTemplate("hello world", new NullMemberResolver());
        Assert.Equal("hello world", render(EmptyCtx));
    }

    // ─── Identifier interpolation ─────────────────────────────────────────────

    [Theory]
    [InlineData(0, "v=0")]
    [InlineData(7, "v=7")]
    [InlineData(-3, "v=-3")]
    public void CompileTemplate_IdentifierFragment_RendersLocalValue(int value, string expected)
    {
        var render = CSharpCondition.CompileTemplate("v={value}", new NullMemberResolver());
        Assert.Equal(expected, render(new FakeEvalContext(Locals: new() { L("value", value) })));
    }

    [Fact]
    public void CompileTemplate_MultipleFragments_InterleaveWithLiterals()
    {
        var render = CSharpCondition.CompileTemplate("a={a} b={b}", new NullMemberResolver());
        Assert.Equal("a=1 b=2", render(new FakeEvalContext(Locals: new() { L("a", 1), L("b", 2) })));
    }

    [Fact]
    public void CompileTemplate_OnlyFragments_NoLiteralText()
    {
        var render = CSharpCondition.CompileTemplate("{a}{b}", new NullMemberResolver());
        Assert.Equal("12", render(new FakeEvalContext(Locals: new() { L("a", 1), L("b", 2) })));
    }

    // ─── Member-access fragment delegates to resolver ─────────────────────────

    [Fact]
    public void CompileTemplate_MemberAccessFragment_DelegatesToResolver()
    {
        var resolver = new FakeMemberResolver();
        resolver.Provide("box", "Size", new ArgumentValue(ELEMENT_TYPE_I4, RawValue: 42));
        var render = CSharpCondition.CompileTemplate("size={box.Size}", resolver);
        Assert.Equal("size=42", render(new FakeEvalContext(Locals: new() { new LocalValue("box", 0x12 /*Class*/, RawValue: null) })));
        Assert.Equal(("box", "Size"), resolver.LastResolved);
    }

    // ─── Argument interpolation (by real parameter name) ──────────────────────

    [Fact]
    public void CompileTemplate_InterpolatesArgumentByName()
    {
        // A logpoint template can interpolate a parameter by its real name — the same identifier
        // path conditions use — so "logging instead of breaking" works over arguments too.
        var render = CSharpCondition.CompileTemplate("idx={index}", new NullMemberResolver());
        var ctx = new FakeEvalContext(Locals: new(),
            Arguments: new() { new ArgumentValue(ELEMENT_TYPE_I4, RawValue: 9, Name: "index") });
        Assert.Equal("idx=9", render(ctx));
    }

    // ─── Escape sequences {{ and }} ───────────────────────────────────────────

    [Fact]
    public void CompileTemplate_EscapedOpenBrace_RendersLiteralBrace()
    {
        var render = CSharpCondition.CompileTemplate("{{not-a-fragment}}", new NullMemberResolver());
        Assert.Equal("{not-a-fragment}", render(EmptyCtx));
    }

    [Fact]
    public void CompileTemplate_EscapedBracesMixedWithFragment_RendersCorrectly()
    {
        var render = CSharpCondition.CompileTemplate("set {{count={count}}}", new NullMemberResolver());
        Assert.Equal("set {count=5}", render(new FakeEvalContext(Locals: new() { L("count", 5) })));
    }

    // ─── Malformed templates ──────────────────────────────────────────────────

    [Fact]
    public void CompileTemplate_UnmatchedOpenBrace_ThrowsFormat()
    {
        var ex = Assert.Throws<FormatException>(() => CSharpCondition.CompileTemplate("v={value", new NullMemberResolver()));
        Assert.Contains("'{'", ex.Message);
    }

    [Fact]
    public void CompileTemplate_UnmatchedCloseBrace_ThrowsFormat()
    {
        var ex = Assert.Throws<FormatException>(() => CSharpCondition.CompileTemplate("v=}", new NullMemberResolver()));
        Assert.Contains("'}'", ex.Message);
    }

    [Fact]
    public void CompileTemplate_EmptyFragment_ThrowsFormat()
    {
        var ex = Assert.Throws<FormatException>(() => CSharpCondition.CompileTemplate("nothing={}", new NullMemberResolver()));
        Assert.Contains("empty", ex.Message);
    }

    // ─── Arithmetic fragments (ADR-010 Increment 7: typed Expression.Compile() per fragment) ─

    [Fact]
    public void CompileTemplate_ArithmeticFragment_RendersTypedResult()
    {
        var render = CSharpCondition.CompileTemplate("doubled={value + value}", new NullMemberResolver());
        Assert.Equal("doubled=6", render(new FakeEvalContext(Locals: new() { L("value", 3) })));
    }

    [Fact]
    public void CompileTemplate_BooleanFragment_RendersTrueFalse()
    {
        // counter > 0 evaluates to bool; Convert.ToString(InvariantCulture) renders "True"/"False".
        var render = CSharpCondition.CompileTemplate("positive={counter > 0}", new NullMemberResolver());
        Assert.Equal("positive=False", render(new FakeEvalContext(Locals: new() { L("counter", 0) })));
        Assert.Equal("positive=True",  render(new FakeEvalContext(Locals: new() { L("counter", 5) })));
    }

    // ─── Unsupported syntax in fragment propagates at compile (first invocation) ─────────────

    [Fact]
    public void CompileTemplate_UnsupportedFragmentSyntax_PropagatesAtRender()
    {
        // Conditional ?: is outside the walker's syntactic support. Inside an interpolation the
        // bare ':' is a format-spec separator, so the expression must be parenthesized; first
        // invocation triggers compilation, which propagates NotSupportedException to the caller.
        var render = CSharpCondition.CompileTemplate("c={(value > 0 ? 1 : 0)}", new NullMemberResolver());
        var ctx = new FakeEvalContext(Locals: new() { L("value", 3) });
        Assert.Throws<NotSupportedException>(() => render(ctx));
    }

    // ─── Test helpers ─────────────────────────────────────────────────────────

    static LocalValue L(string name, long value) => new(name, ELEMENT_TYPE_I4, value);
    static readonly FakeEvalContext EmptyCtx = new(Locals: new());

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
