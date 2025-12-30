// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SkyOmega.Mercury.Diagnostics;
using Xunit;

namespace Mercury.Tests.Diagnostics;

public class DiagnosticFormatterTests
{
    [Fact]
    public void Format_SingleError_ProducesExpectedOutput()
    {
        // Arrange
        var source = "SELECT * WHERE { ?s foaf:name ?name }";
        var bag = new DiagnosticBag();
        bag.Add(DiagnosticCode.UndefinedPrefix,
            new SourceSpan(start: 20, length: 4, line: 1, column: 21),
            "foaf".AsSpan());

        using var sw = new StringWriter();
        var formatter = new DiagnosticFormatter(sw, useColor: false, sourceName: "query");

        // Act
        formatter.Format(ref bag, source.AsSpan());
        var output = sw.ToString();

        // Assert
        Assert.Contains("error[E2001]", output);
        Assert.Contains("undefined prefix 'foaf'", output);
        Assert.Contains("--> query:1:21", output);
        Assert.Contains("foaf:name", output);
        Assert.Contains("^^^^", output);
        Assert.Contains("1 error", output);

        bag.Dispose();
    }

    [Fact]
    public void Format_MultipleErrors_ShowsAll()
    {
        // Arrange
        var source = "SELEC * WHER { ?s foaf:name ?name }";
        var bag = new DiagnosticBag();
        bag.Add(DiagnosticCode.UnexpectedToken,
            new SourceSpan(start: 0, length: 5, line: 1, column: 1),
            "SELEC".AsSpan());
        bag.Add(DiagnosticCode.UnexpectedToken,
            new SourceSpan(start: 8, length: 4, line: 1, column: 9),
            "WHER".AsSpan());

        using var sw = new StringWriter();
        var formatter = new DiagnosticFormatter(sw, useColor: false);

        // Act
        formatter.Format(ref bag, source.AsSpan());
        var output = sw.ToString();

        // Assert
        Assert.Contains("error[E1010]", output);
        Assert.Contains("unexpected token 'SELEC'", output);
        Assert.Contains("unexpected token 'WHER'", output);
        Assert.Contains("2 errors", output);

        bag.Dispose();
    }

    [Fact]
    public void Format_Warning_UsesCorrectSeverityLabel()
    {
        // Arrange
        var source = "SELECT * WHERE { ?s ?p ?o . ?x ?y ?z }";
        var bag = new DiagnosticBag();
        bag.Add(DiagnosticCode.CartesianProduct,
            new SourceSpan(start: 28, length: 10, line: 1, column: 29));

        using var sw = new StringWriter();
        var formatter = new DiagnosticFormatter(sw, useColor: false);

        // Act
        formatter.Format(ref bag, source.AsSpan());
        var output = sw.ToString();

        // Assert
        Assert.Contains("warning[W1001]", output);
        Assert.Contains("Cartesian product", output);
        Assert.Contains("1 warning", output);

        bag.Dispose();
    }

    [Fact]
    public void Format_MixedSeverities_ShowsCorrectSummary()
    {
        // Arrange
        var source = "SELECT * WHERE { ?s foaf:name ?name . ?x ?y ?z }";
        var bag = new DiagnosticBag();
        bag.Add(DiagnosticCode.UndefinedPrefix,
            new SourceSpan(start: 20, length: 4, line: 1, column: 21),
            "foaf".AsSpan());
        bag.Add(DiagnosticCode.CartesianProduct,
            new SourceSpan(start: 38, length: 10, line: 1, column: 39));

        using var sw = new StringWriter();
        var formatter = new DiagnosticFormatter(sw, useColor: false);

        // Act
        formatter.Format(ref bag, source.AsSpan());
        var output = sw.ToString();

        // Assert
        Assert.Contains("1 error", output);
        Assert.Contains("1 warning", output);

        bag.Dispose();
    }

    [Fact]
    public void Format_MultilineSource_ShowsCorrectLine()
    {
        // Arrange
        var source = """
            PREFIX ex: <http://example.org/>
            SELECT * WHERE {
                ?s foaf:name ?name
            }
            """;
        var bag = new DiagnosticBag();
        bag.Add(DiagnosticCode.UndefinedPrefix,
            new SourceSpan(start: 56, length: 4, line: 3, column: 8),
            "foaf".AsSpan());

        using var sw = new StringWriter();
        var formatter = new DiagnosticFormatter(sw, useColor: false);

        // Act
        formatter.Format(ref bag, source.AsSpan());
        var output = sw.ToString();

        // Assert
        Assert.Contains("--> query:3:8", output);
        Assert.Contains("foaf:name", output);

        bag.Dispose();
    }
}
