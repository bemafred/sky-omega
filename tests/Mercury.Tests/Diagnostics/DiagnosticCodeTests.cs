// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SkyOmega.Mercury.Diagnostics;
using Xunit;

namespace SkyOmega.Mercury.Tests.Diagnostics;

public class DiagnosticCodeTests
{
    [Theory]
    [InlineData(DiagnosticCode.UnexpectedCharacter, DiagnosticSeverity.Error)]
    [InlineData(DiagnosticCode.UndefinedPrefix, DiagnosticSeverity.Error)]
    [InlineData(DiagnosticCode.QueryTimeout, DiagnosticSeverity.Error)]
    [InlineData(DiagnosticCode.StoreNotFound, DiagnosticSeverity.Error)]
    [InlineData(DiagnosticCode.CartesianProduct, DiagnosticSeverity.Warning)]
    [InlineData(DiagnosticCode.RedundantDistinct, DiagnosticSeverity.Warning)]
    [InlineData(DiagnosticCode.SuggestIndex, DiagnosticSeverity.Hint)]
    [InlineData(DiagnosticCode.PrefixAutoRegistered, DiagnosticSeverity.Hint)]
    public void GetSeverity_ReturnsCorrectSeverity(int code, DiagnosticSeverity expected)
    {
        Assert.Equal(expected, DiagnosticCode.GetSeverity(code));
    }

    [Theory]
    [InlineData(DiagnosticCode.UnexpectedCharacter, "E1001")]
    [InlineData(DiagnosticCode.UndefinedPrefix, "E2001")]
    [InlineData(DiagnosticCode.QueryTimeout, "E3001")]
    [InlineData(DiagnosticCode.StoreNotFound, "E4001")]
    [InlineData(DiagnosticCode.CartesianProduct, "W1001")]
    [InlineData(DiagnosticCode.DeprecatedSyntax, "W1010")]
    [InlineData(DiagnosticCode.SuggestIndex, "I1001")]
    [InlineData(DiagnosticCode.PrefixAutoRegistered, "I1010")]
    public void FormatCode_ReturnsExpectedFormat(int code, string expected)
    {
        Assert.Equal(expected, DiagnosticCode.FormatCode(code));
    }

    [Theory]
    [InlineData(DiagnosticCode.UnexpectedCharacter, true)]
    [InlineData(DiagnosticCode.UndefinedPrefix, true)]
    [InlineData(DiagnosticCode.CartesianProduct, false)]
    [InlineData(DiagnosticCode.SuggestIndex, false)]
    public void IsError_ReturnsCorrectValue(int code, bool expected)
    {
        Assert.Equal(expected, DiagnosticCode.IsError(code));
    }

    [Theory]
    [InlineData(DiagnosticCode.UnexpectedCharacter, false)]
    [InlineData(DiagnosticCode.CartesianProduct, true)]
    [InlineData(DiagnosticCode.RedundantDistinct, true)]
    [InlineData(DiagnosticCode.SuggestIndex, false)]
    public void IsWarning_ReturnsCorrectValue(int code, bool expected)
    {
        Assert.Equal(expected, DiagnosticCode.IsWarning(code));
    }

    [Theory]
    [InlineData(DiagnosticCode.UnexpectedCharacter, false)]
    [InlineData(DiagnosticCode.CartesianProduct, false)]
    [InlineData(DiagnosticCode.SuggestIndex, true)]
    [InlineData(DiagnosticCode.PrefixAutoRegistered, true)]
    public void IsInfo_ReturnsCorrectValue(int code, bool expected)
    {
        Assert.Equal(expected, DiagnosticCode.IsInfo(code));
    }

    [Fact]
    public void ErrorCodes_AreInCorrectRange()
    {
        // E1xxx - Lexical/Parse errors
        Assert.True(DiagnosticCode.UnexpectedCharacter >= 1000 && DiagnosticCode.UnexpectedCharacter < 2000);
        Assert.True(DiagnosticCode.UnterminatedString >= 1000 && DiagnosticCode.UnterminatedString < 2000);

        // E2xxx - Semantic errors
        Assert.True(DiagnosticCode.UndefinedPrefix >= 2000 && DiagnosticCode.UndefinedPrefix < 3000);
        Assert.True(DiagnosticCode.UnboundVariable >= 2000 && DiagnosticCode.UnboundVariable < 3000);

        // E3xxx - Runtime errors
        Assert.True(DiagnosticCode.QueryTimeout >= 3000 && DiagnosticCode.QueryTimeout < 4000);
        Assert.True(DiagnosticCode.DivisionByZero >= 3000 && DiagnosticCode.DivisionByZero < 4000);

        // E4xxx - Storage errors
        Assert.True(DiagnosticCode.StoreNotFound >= 4000 && DiagnosticCode.StoreNotFound < 5000);
        Assert.True(DiagnosticCode.WalCorrupted >= 4000 && DiagnosticCode.WalCorrupted < 5000);

        // W1xxx - Warnings (10000 + 1xxx)
        Assert.True(DiagnosticCode.CartesianProduct >= 10000 && DiagnosticCode.CartesianProduct < 20000);
        Assert.True(DiagnosticCode.DeprecatedSyntax >= 10000 && DiagnosticCode.DeprecatedSyntax < 20000);

        // I1xxx - Info (20000 + 1xxx)
        Assert.True(DiagnosticCode.SuggestIndex >= 20000);
        Assert.True(DiagnosticCode.PrefixAutoRegistered >= 20000);
    }
}
