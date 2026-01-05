// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SkyOmega.Mercury.Diagnostics;
using Xunit;

namespace SkyOmega.Mercury.Tests.Diagnostics;

public class DiagnosticMessagesTests
{
    [Theory]
    [InlineData(DiagnosticCode.UnexpectedCharacter)]
    [InlineData(DiagnosticCode.UndefinedPrefix)]
    [InlineData(DiagnosticCode.QueryTimeout)]
    [InlineData(DiagnosticCode.CartesianProduct)]
    [InlineData(DiagnosticCode.SuggestIndex)]
    public void GetTemplate_ReturnsNonEmptyTemplate(int code)
    {
        var template = DiagnosticMessages.GetTemplate(code);

        Assert.NotEmpty(template);
        Assert.NotEqual("unknown diagnostic", template);
    }

    [Fact]
    public void GetTemplate_UnknownCode_ReturnsDefault()
    {
        var template = DiagnosticMessages.GetTemplate(99999);

        Assert.Equal("unknown diagnostic", template);
    }

    [Fact]
    public void Format_NoArgs_ReturnsTemplate()
    {
        var bag = new DiagnosticBag();
        bag.Add(DiagnosticCode.UnexpectedEndOfInput, new SourceSpan(0, 1, 1, 1));

        var diag = bag[0];
        var message = DiagnosticMessages.Format(in diag, ref bag);

        Assert.Equal("unexpected end of input", message);

        bag.Dispose();
    }

    [Fact]
    public void Format_OneArg_SubstitutesCorrectly()
    {
        var bag = new DiagnosticBag();
        bag.Add(DiagnosticCode.UndefinedPrefix,
            new SourceSpan(0, 4, 1, 1),
            "foaf".AsSpan());

        var diag = bag[0];
        var message = DiagnosticMessages.Format(in diag, ref bag);

        Assert.Equal("undefined prefix 'foaf'", message);

        bag.Dispose();
    }

    [Fact]
    public void Format_TwoArgs_SubstitutesBoth()
    {
        var bag = new DiagnosticBag();
        bag.Add(DiagnosticCode.ExpectedToken,
            new SourceSpan(0, 5, 1, 1),
            "SELECT".AsSpan(),
            "SELEC".AsSpan());

        var diag = bag[0];
        var message = DiagnosticMessages.Format(in diag, ref bag);

        Assert.Equal("expected SELECT, found 'SELEC'", message);

        bag.Dispose();
    }

    [Fact]
    public void TryFormat_Success_WritesToSpan()
    {
        var bag = new DiagnosticBag();
        bag.Add(DiagnosticCode.UndefinedPrefix,
            new SourceSpan(0, 4, 1, 1),
            "ex".AsSpan());

        var diag = bag[0];
        var buffer = new char[100];

        var written = DiagnosticMessages.TryFormat(in diag, ref bag, buffer.AsSpan());

        Assert.True(written > 0);
        Assert.Equal("undefined prefix 'ex'", buffer.AsSpan(0, written).ToString());

        bag.Dispose();
    }

    [Fact]
    public void TryFormat_BufferTooSmall_ReturnsNegative()
    {
        var bag = new DiagnosticBag();
        bag.Add(DiagnosticCode.UndefinedPrefix,
            new SourceSpan(0, 4, 1, 1),
            "foaf".AsSpan());

        var diag = bag[0];
        var buffer = new char[5]; // Too small

        var written = DiagnosticMessages.TryFormat(in diag, ref bag, buffer.AsSpan());

        Assert.Equal(-1, written);

        bag.Dispose();
    }

    [Fact]
    public void Format_ThreeArgs_SubstitutesAll()
    {
        var bag = new DiagnosticBag();
        bag.Add(DiagnosticCode.TypeMismatch,
            new SourceSpan(0, 10, 1, 1),
            "add".AsSpan(),
            "string".AsSpan(),
            "integer".AsSpan());

        var diag = bag[0];
        var message = DiagnosticMessages.Format(in diag, ref bag);

        Assert.Contains("add", message);
        Assert.Contains("string", message);
        Assert.Contains("integer", message);

        bag.Dispose();
    }

    [Fact]
    public void Templates_ContainCorrectPlaceholders()
    {
        // Verify templates with args have {0}, {1} etc.
        var template1 = DiagnosticMessages.GetTemplate(DiagnosticCode.UndefinedPrefix);
        Assert.Contains("{0}", template1);

        var template2 = DiagnosticMessages.GetTemplate(DiagnosticCode.ExpectedToken);
        Assert.Contains("{0}", template2);
        Assert.Contains("{1}", template2);

        // Templates without args should not have placeholders
        var template3 = DiagnosticMessages.GetTemplate(DiagnosticCode.UnexpectedEndOfInput);
        Assert.DoesNotContain("{0}", template3);
    }
}
