// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using SkyOmega.Mercury.Diagnostics;
using Xunit;

namespace Mercury.Tests.Diagnostics;

public class DiagnosticJsonFormatterTests
{
    [Fact]
    public void Format_SingleError_ProducesValidJson()
    {
        // Arrange
        var bag = new DiagnosticBag();
        bag.Add(DiagnosticCode.UndefinedPrefix,
            new SourceSpan(start: 20, length: 4, line: 2, column: 5),
            "foaf".AsSpan());

        var formatter = new DiagnosticJsonFormatter("mercury-sparql");

        // Act
        var json = formatter.Format(ref bag);

        // Assert - parse to verify valid JSON
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("diagnostics", out var diagnostics));
        Assert.Equal(JsonValueKind.Array, diagnostics.ValueKind);
        Assert.Equal(1, diagnostics.GetArrayLength());

        var diag = diagnostics[0];
        Assert.Equal("E2001", diag.GetProperty("code").GetString());
        Assert.Equal("mercury-sparql", diag.GetProperty("source").GetString());
        Assert.Equal(1, diag.GetProperty("severity").GetInt32()); // Error = 1

        var range = diag.GetProperty("range");
        Assert.Equal(1, range.GetProperty("start").GetProperty("line").GetInt32()); // 0-based
        Assert.Equal(4, range.GetProperty("start").GetProperty("character").GetInt32()); // 0-based

        Assert.Contains("undefined prefix 'foaf'", diag.GetProperty("message").GetString());

        bag.Dispose();
    }

    [Fact]
    public void Format_Warning_HasCorrectSeverity()
    {
        // Arrange
        var bag = new DiagnosticBag();
        bag.Add(DiagnosticCode.CartesianProduct,
            new SourceSpan(start: 0, length: 10, line: 1, column: 1));

        var formatter = new DiagnosticJsonFormatter();

        // Act
        var json = formatter.Format(ref bag);
        var doc = JsonDocument.Parse(json);
        var diag = doc.RootElement.GetProperty("diagnostics")[0];

        // Assert
        Assert.Equal(2, diag.GetProperty("severity").GetInt32()); // Warning = 2
        Assert.Equal("W1001", diag.GetProperty("code").GetString());

        bag.Dispose();
    }

    [Fact]
    public void Format_MultipleArgs_IncludesAllInMessage()
    {
        // Arrange
        var bag = new DiagnosticBag();
        bag.Add(DiagnosticCode.ExpectedToken,
            new SourceSpan(start: 0, length: 5, line: 1, column: 1),
            "SELECT".AsSpan(),
            "SELEC".AsSpan());

        var formatter = new DiagnosticJsonFormatter();

        // Act
        var json = formatter.Format(ref bag);
        var doc = JsonDocument.Parse(json);
        var message = doc.RootElement.GetProperty("diagnostics")[0]
            .GetProperty("message").GetString();

        // Assert
        Assert.Contains("SELECT", message);
        Assert.Contains("SELEC", message);

        bag.Dispose();
    }

    [Fact]
    public void Format_DeprecatedSyntax_IncludesDeprecatedTag()
    {
        // Arrange
        var bag = new DiagnosticBag();
        bag.Add(DiagnosticCode.DeprecatedSyntax,
            new SourceSpan(start: 0, length: 10, line: 1, column: 1),
            "old syntax".AsSpan());

        var formatter = new DiagnosticJsonFormatter();

        // Act
        var json = formatter.Format(ref bag);
        var doc = JsonDocument.Parse(json);
        var diag = doc.RootElement.GetProperty("diagnostics")[0];

        // Assert
        Assert.True(diag.TryGetProperty("tags", out var tags));
        Assert.Equal(2, tags[0].GetInt32()); // LSP DiagnosticTag.Deprecated = 2

        bag.Dispose();
    }

    [Fact]
    public void ToLspSeverity_ReturnsCorrectValues()
    {
        Assert.Equal(1, DiagnosticJsonFormatter.ToLspSeverity(DiagnosticSeverity.Error));
        Assert.Equal(2, DiagnosticJsonFormatter.ToLspSeverity(DiagnosticSeverity.Warning));
        Assert.Equal(3, DiagnosticJsonFormatter.ToLspSeverity(DiagnosticSeverity.Info));
        Assert.Equal(4, DiagnosticJsonFormatter.ToLspSeverity(DiagnosticSeverity.Hint));
    }
}
