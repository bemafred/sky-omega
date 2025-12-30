// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SkyOmega.Mercury.Diagnostics;
using Xunit;

namespace Mercury.Tests.Diagnostics;

public class SourceSpanTests
{
    [Fact]
    public void Constructor_SetsAllFields()
    {
        var span = new SourceSpan(start: 10, length: 5, line: 2, column: 3);

        Assert.Equal(10, span.Start);
        Assert.Equal(5, span.Length);
        Assert.Equal(2, span.Line);
        Assert.Equal(3, span.Column);
    }

    [Fact]
    public void End_ReturnsStartPlusLength()
    {
        var span = new SourceSpan(start: 10, length: 5, line: 1, column: 1);

        Assert.Equal(15, span.End);
    }

    [Fact]
    public void IsEmpty_TrueWhenLengthZero()
    {
        var empty = new SourceSpan(start: 10, length: 0, line: 1, column: 11);
        var nonEmpty = new SourceSpan(start: 10, length: 1, line: 1, column: 11);

        Assert.True(empty.IsEmpty);
        Assert.False(nonEmpty.IsEmpty);
    }

    [Fact]
    public void Empty_IsDefaultValue()
    {
        var empty = SourceSpan.Empty;

        Assert.Equal(0, empty.Start);
        Assert.Equal(0, empty.Length);
        Assert.Equal(0, empty.Line);
        Assert.Equal(0, empty.Column);
        Assert.True(empty.IsEmpty);
    }

    [Fact]
    public void SingleChar_CreatesLengthOneSpan()
    {
        var span = SourceSpan.SingleChar(offset: 42, line: 5, column: 10);

        Assert.Equal(42, span.Start);
        Assert.Equal(1, span.Length);
        Assert.Equal(5, span.Line);
        Assert.Equal(10, span.Column);
    }

    [Fact]
    public void ExtendTo_CombinesSpans()
    {
        var first = new SourceSpan(start: 10, length: 5, line: 1, column: 11);
        var second = new SourceSpan(start: 20, length: 3, line: 1, column: 21);

        var combined = first.ExtendTo(second);

        Assert.Equal(10, combined.Start);
        Assert.Equal(13, combined.Length); // 20 + 3 - 10 = 13
        Assert.Equal(1, combined.Line);
        Assert.Equal(11, combined.Column);
    }

    [Fact]
    public void ExtendTo_HandlesReversedOrder()
    {
        var first = new SourceSpan(start: 20, length: 3, line: 1, column: 21);
        var second = new SourceSpan(start: 10, length: 5, line: 1, column: 11);

        var combined = first.ExtendTo(second);

        // Should swap and produce same result
        Assert.Equal(10, combined.Start);
        Assert.Equal(13, combined.Length);
    }

    [Fact]
    public void GetText_ExtractsSubstring()
    {
        var source = "SELECT * WHERE { ?s ?p ?o }";
        var span = new SourceSpan(start: 9, length: 5, line: 1, column: 10);

        var text = span.GetText(source.AsSpan());

        Assert.Equal("WHERE", text.ToString());
    }

    [Fact]
    public void GetText_ClampsToSourceBounds()
    {
        var source = "SHORT";
        var span = new SourceSpan(start: 2, length: 100, line: 1, column: 3);

        var text = span.GetText(source.AsSpan());

        Assert.Equal("ORT", text.ToString());
    }

    [Fact]
    public void GetText_InvalidStart_ReturnsEmpty()
    {
        var source = "TEST";
        var span = new SourceSpan(start: 100, length: 5, line: 1, column: 1);

        var text = span.GetText(source.AsSpan());

        Assert.True(text.IsEmpty);
    }

    [Fact]
    public void Equality_WorksCorrectly()
    {
        var span1 = new SourceSpan(10, 5, 2, 3);
        var span2 = new SourceSpan(10, 5, 2, 3);
        var span3 = new SourceSpan(10, 5, 2, 4); // Different column

        Assert.True(span1 == span2);
        Assert.False(span1 == span3);
        Assert.True(span1 != span3);
        Assert.True(span1.Equals(span2));
        Assert.False(span1.Equals(span3));
        Assert.True(span1.Equals((object)span2));
        Assert.False(span1.Equals((object)span3));
        Assert.False(span1.Equals(null));
    }

    [Fact]
    public void GetHashCode_EqualSpansHaveSameHash()
    {
        var span1 = new SourceSpan(10, 5, 2, 3);
        var span2 = new SourceSpan(10, 5, 2, 3);

        Assert.Equal(span1.GetHashCode(), span2.GetHashCode());
    }

    [Fact]
    public void ToString_ProducesReadableFormat()
    {
        var span = new SourceSpan(start: 10, length: 5, line: 2, column: 3);

        var str = span.ToString();

        Assert.Contains("2", str);
        Assert.Contains("3", str);
        Assert.Contains("5", str);
    }
}
