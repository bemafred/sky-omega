// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SkyOmega.Mercury.Diagnostics;
using Xunit;

namespace Mercury.Tests.Diagnostics;

public class DiagnosticBagTests
{
    [Fact]
    public void Add_NoArgs_StoresDiagnostic()
    {
        var bag = new DiagnosticBag();
        var span = new SourceSpan(10, 5, 2, 3);

        bag.Add(DiagnosticCode.UnexpectedEndOfInput, span);

        Assert.Equal(1, bag.Count);
        Assert.False(bag.IsEmpty);
        Assert.True(bag.HasErrors);

        var diag = bag[0];
        Assert.Equal(DiagnosticCode.UnexpectedEndOfInput, diag.Code);
        Assert.Equal(span, diag.Span);
        Assert.Equal(0, diag.ArgCount);

        bag.Dispose();
    }

    [Fact]
    public void Add_OneArg_StoresArgument()
    {
        var bag = new DiagnosticBag();
        var span = new SourceSpan(0, 4, 1, 1);

        bag.Add(DiagnosticCode.UndefinedPrefix, span, "foaf".AsSpan());

        Assert.Equal(1, bag.Count);
        var diag = bag[0];
        Assert.Equal(1, diag.ArgCount);

        var arg = bag.GetArg(in diag, 0);
        Assert.Equal("foaf", arg.ToString());

        bag.Dispose();
    }

    [Fact]
    public void Add_TwoArgs_StoresBothArguments()
    {
        var bag = new DiagnosticBag();
        var span = new SourceSpan(0, 5, 1, 1);

        bag.Add(DiagnosticCode.ExpectedToken, span, "SELECT".AsSpan(), "SELEC".AsSpan());

        var diag = bag[0];
        Assert.Equal(2, diag.ArgCount);

        Assert.Equal("SELECT", bag.GetArg(in diag, 0).ToString());
        Assert.Equal("SELEC", bag.GetArg(in diag, 1).ToString());

        bag.Dispose();
    }

    [Fact]
    public void Add_ThreeArgs_StoresAllArguments()
    {
        var bag = new DiagnosticBag();
        var span = new SourceSpan(0, 10, 1, 1);

        bag.Add(DiagnosticCode.TypeMismatch, span,
            "add".AsSpan(), "string".AsSpan(), "integer".AsSpan());

        var diag = bag[0];
        Assert.Equal(3, diag.ArgCount);

        Assert.Equal("add", bag.GetArg(in diag, 0).ToString());
        Assert.Equal("string", bag.GetArg(in diag, 1).ToString());
        Assert.Equal("integer", bag.GetArg(in diag, 2).ToString());

        bag.Dispose();
    }

    [Fact]
    public void GetArg_OutOfRange_ReturnsEmpty()
    {
        var bag = new DiagnosticBag();
        bag.Add(DiagnosticCode.UndefinedPrefix,
            new SourceSpan(0, 4, 1, 1),
            "foaf".AsSpan());

        var diag = bag[0];

        Assert.True(bag.GetArg(in diag, 1).IsEmpty);
        Assert.True(bag.GetArg(in diag, 99).IsEmpty);

        bag.Dispose();
    }

    [Fact]
    public void HasErrors_WithOnlyWarnings_ReturnsFalse()
    {
        var bag = new DiagnosticBag();
        bag.Add(DiagnosticCode.CartesianProduct, new SourceSpan(0, 10, 1, 1));

        Assert.False(bag.HasErrors);
        Assert.False(bag.IsEmpty);

        bag.Dispose();
    }

    [Fact]
    public void HasErrors_WithMixed_ReturnsTrue()
    {
        var bag = new DiagnosticBag();
        bag.Add(DiagnosticCode.CartesianProduct, new SourceSpan(0, 10, 1, 1));
        bag.Add(DiagnosticCode.UndefinedPrefix,
            new SourceSpan(20, 4, 1, 21), "foaf".AsSpan());

        Assert.True(bag.HasErrors);

        bag.Dispose();
    }

    [Fact]
    public void Clear_RemovesAllDiagnostics()
    {
        var bag = new DiagnosticBag();
        bag.Add(DiagnosticCode.UnexpectedEndOfInput, new SourceSpan(0, 1, 1, 1));
        bag.Add(DiagnosticCode.UndefinedPrefix,
            new SourceSpan(10, 4, 1, 11), "ex".AsSpan());

        Assert.Equal(2, bag.Count);

        bag.Clear();

        Assert.Equal(0, bag.Count);
        Assert.True(bag.IsEmpty);

        // Can add more after clear
        bag.Add(DiagnosticCode.UnexpectedToken,
            new SourceSpan(0, 5, 1, 1), "test".AsSpan());
        Assert.Equal(1, bag.Count);

        bag.Dispose();
    }

    [Fact]
    public void Enumeration_YieldsAllDiagnostics()
    {
        var bag = new DiagnosticBag();
        bag.Add(DiagnosticCode.UnexpectedEndOfInput, new SourceSpan(0, 1, 1, 1));
        bag.Add(DiagnosticCode.UndefinedPrefix,
            new SourceSpan(10, 4, 2, 1), "ex".AsSpan());
        bag.Add(DiagnosticCode.CartesianProduct, new SourceSpan(20, 10, 3, 1));

        var codes = new List<int>();
        foreach (var diag in bag)
        {
            codes.Add(diag.Code);
        }

        Assert.Equal(3, codes.Count);
        Assert.Equal(DiagnosticCode.UnexpectedEndOfInput, codes[0]);
        Assert.Equal(DiagnosticCode.UndefinedPrefix, codes[1]);
        Assert.Equal(DiagnosticCode.CartesianProduct, codes[2]);

        bag.Dispose();
    }

    [Fact]
    public void Add_ManyDiagnostics_GrowsCapacity()
    {
        var bag = new DiagnosticBag();

        // Add more than initial capacity (8)
        for (int i = 0; i < 100; i++)
        {
            bag.Add(DiagnosticCode.UnexpectedToken,
                new SourceSpan(i, 1, 1, i + 1),
                $"token{i}".AsSpan());
        }

        Assert.Equal(100, bag.Count);

        // Verify all are accessible
        for (int i = 0; i < 100; i++)
        {
            var diag = bag[i];
            Assert.Equal(DiagnosticCode.UnexpectedToken, diag.Code);
            Assert.Equal($"token{i}", bag.GetArg(in diag, 0).ToString());
        }

        bag.Dispose();
    }

    [Fact]
    public void AddWithRelated_StoresRelatedSpan()
    {
        var bag = new DiagnosticBag();
        var mainSpan = new SourceSpan(50, 4, 3, 5);
        var relatedSpan = new SourceSpan(10, 4, 1, 11);

        bag.AddWithRelated(DiagnosticCode.DuplicateVariable, mainSpan, relatedSpan, "name".AsSpan());

        var diag = bag[0];
        Assert.True(diag.HasRelatedSpan);
        Assert.Equal(relatedSpan, diag.RelatedSpan);
        Assert.Equal("name", bag.GetArg(in diag, 0).ToString());

        bag.Dispose();
    }
}
