// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using SkyOmega.Mercury.Repl;
using Xunit;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for ResultTableFormatter - formats query results as ASCII tables.
/// </summary>
public class ResultTableFormatterTests
{
    #region FormatSelect Tests

    [Fact]
    public void FormatSelect_NoVariables_ShowsNoVariablesMessage()
    {
        var writer = new StringWriter();
        var formatter = new ResultTableFormatter(writer, useColor: false);

        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Select,
            Variables = null,
            Rows = null
        };

        formatter.FormatSelect(result);
        var output = writer.ToString();

        Assert.Contains("no variables selected", output);
    }

    [Fact]
    public void FormatSelect_EmptyVariables_ShowsNoVariablesMessage()
    {
        var writer = new StringWriter();
        var formatter = new ResultTableFormatter(writer, useColor: false);

        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Select,
            Variables = Array.Empty<string>(),
            Rows = new List<Dictionary<string, string>>()
        };

        formatter.FormatSelect(result);
        var output = writer.ToString();

        Assert.Contains("no variables selected", output);
    }

    [Fact]
    public void FormatSelect_NoRows_ShowsNoResultsMessage()
    {
        var writer = new StringWriter();
        var formatter = new ResultTableFormatter(writer, useColor: false);

        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Select,
            Variables = new[] { "?s", "?p", "?o" },
            Rows = null
        };

        formatter.FormatSelect(result);
        var output = writer.ToString();

        Assert.Contains("no results", output);
    }

    [Fact]
    public void FormatSelect_EmptyRows_ShowsNoResultsMessage()
    {
        var writer = new StringWriter();
        var formatter = new ResultTableFormatter(writer, useColor: false);

        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Select,
            Variables = new[] { "?s", "?p", "?o" },
            Rows = new List<Dictionary<string, string>>()
        };

        formatter.FormatSelect(result);
        var output = writer.ToString();

        Assert.Contains("no results", output);
    }

    [Fact]
    public void FormatSelect_WithRows_ShowsTable()
    {
        var writer = new StringWriter();
        var formatter = new ResultTableFormatter(writer, useColor: false);

        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Select,
            Variables = new[] { "?name" },
            Rows = new List<Dictionary<string, string>>
            {
                new Dictionary<string, string> { ["?name"] = "\"Alice\"" },
                new Dictionary<string, string> { ["?name"] = "\"Bob\"" }
            },
            ParseTime = TimeSpan.FromMilliseconds(1),
            ExecutionTime = TimeSpan.FromMilliseconds(2)
        };

        formatter.FormatSelect(result);
        var output = writer.ToString();

        // Check for table structure (box drawing characters)
        Assert.Contains("┌", output);
        Assert.Contains("┐", output);
        Assert.Contains("└", output);
        Assert.Contains("┘", output);
        Assert.Contains("│", output);
        // Check for header
        Assert.Contains("?name", output);
        // Check for data
        Assert.Contains("Alice", output);
        Assert.Contains("Bob", output);
        // Check for summary
        Assert.Contains("2 rows", output);
        // Check for timing
        Assert.Contains("ms", output);
    }

    [Fact]
    public void FormatSelect_MultipleColumns_ShowsAllColumns()
    {
        var writer = new StringWriter();
        var formatter = new ResultTableFormatter(writer, useColor: false);

        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Select,
            Variables = new[] { "?s", "?p", "?o" },
            Rows = new List<Dictionary<string, string>>
            {
                new Dictionary<string, string>
                {
                    ["?s"] = "<http://ex.org/alice>",
                    ["?p"] = "<http://ex.org/knows>",
                    ["?o"] = "<http://ex.org/bob>"
                }
            },
            ParseTime = TimeSpan.Zero,
            ExecutionTime = TimeSpan.Zero
        };

        formatter.FormatSelect(result);
        var output = writer.ToString();

        Assert.Contains("?s", output);
        Assert.Contains("?p", output);
        Assert.Contains("?o", output);
    }

    [Fact]
    public void FormatSelect_SingleRow_ShowsSingularRowText()
    {
        var writer = new StringWriter();
        var formatter = new ResultTableFormatter(writer, useColor: false);

        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Select,
            Variables = new[] { "?x" },
            Rows = new List<Dictionary<string, string>>
            {
                new Dictionary<string, string> { ["?x"] = "\"value\"" }
            },
            ParseTime = TimeSpan.Zero,
            ExecutionTime = TimeSpan.Zero
        };

        formatter.FormatSelect(result);
        var output = writer.ToString();

        Assert.Contains("1 row", output);
        Assert.DoesNotContain("1 rows", output);
    }

    [Fact]
    public void FormatSelect_MaxRows_LimitsOutput()
    {
        var writer = new StringWriter();
        var formatter = new ResultTableFormatter(writer, useColor: false, maxRows: 2);

        var rows = new List<Dictionary<string, string>>();
        for (int i = 0; i < 10; i++)
        {
            rows.Add(new Dictionary<string, string> { ["?x"] = $"\"value{i}\"" });
        }

        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Select,
            Variables = new[] { "?x" },
            Rows = rows,
            ParseTime = TimeSpan.Zero,
            ExecutionTime = TimeSpan.Zero
        };

        formatter.FormatSelect(result);
        var output = writer.ToString();

        // Should show truncation message
        Assert.Contains("2 of 10", output);
    }

    [Fact]
    public void FormatSelect_LongValues_Truncated()
    {
        var writer = new StringWriter();
        var formatter = new ResultTableFormatter(writer, useColor: false, maxColumnWidth: 20);

        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Select,
            Variables = new[] { "?x" },
            Rows = new List<Dictionary<string, string>>
            {
                new Dictionary<string, string> { ["?x"] = "\"This is a very long value that exceeds the maximum column width\"" }
            },
            ParseTime = TimeSpan.Zero,
            ExecutionTime = TimeSpan.Zero
        };

        formatter.FormatSelect(result);
        var output = writer.ToString();

        // Should contain ellipsis for truncation
        Assert.Contains("…", output);
    }

    #endregion

    #region FormatAsk Tests

    [Fact]
    public void FormatAsk_True_ShowsTrue()
    {
        var writer = new StringWriter();
        var formatter = new ResultTableFormatter(writer, useColor: false);

        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Ask,
            AskResult = true,
            ParseTime = TimeSpan.FromMilliseconds(5),
            ExecutionTime = TimeSpan.FromMilliseconds(10)
        };

        formatter.FormatAsk(result);
        var output = writer.ToString();

        Assert.Contains("true", output);
        Assert.Contains("ms", output);
    }

    [Fact]
    public void FormatAsk_False_ShowsFalse()
    {
        var writer = new StringWriter();
        var formatter = new ResultTableFormatter(writer, useColor: false);

        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Ask,
            AskResult = false,
            ParseTime = TimeSpan.Zero,
            ExecutionTime = TimeSpan.Zero
        };

        formatter.FormatAsk(result);
        var output = writer.ToString();

        Assert.Contains("false", output);
    }

    #endregion

    #region FormatTriples Tests

    [Fact]
    public void FormatTriples_NoTriples_ShowsNoTriplesMessage()
    {
        var writer = new StringWriter();
        var formatter = new ResultTableFormatter(writer, useColor: false);

        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Construct,
            Triples = null
        };

        formatter.FormatTriples(result);
        var output = writer.ToString();

        Assert.Contains("no triples", output);
    }

    [Fact]
    public void FormatTriples_EmptyTriples_ShowsNoTriplesMessage()
    {
        var writer = new StringWriter();
        var formatter = new ResultTableFormatter(writer, useColor: false);

        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Construct,
            Triples = new List<(string, string, string)>()
        };

        formatter.FormatTriples(result);
        var output = writer.ToString();

        Assert.Contains("no triples", output);
    }

    [Fact]
    public void FormatTriples_WithTriples_ShowsNTriplesFormat()
    {
        var writer = new StringWriter();
        var formatter = new ResultTableFormatter(writer, useColor: false);

        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Construct,
            Triples = new List<(string, string, string)>
            {
                ("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>")
            },
            ParseTime = TimeSpan.Zero,
            ExecutionTime = TimeSpan.Zero
        };

        formatter.FormatTriples(result);
        var output = writer.ToString();

        Assert.Contains("<http://ex.org/s>", output);
        Assert.Contains("<http://ex.org/p>", output);
        Assert.Contains("<http://ex.org/o>", output);
        Assert.Contains(".", output); // N-Triples terminator
        Assert.Contains("1 triple", output);
    }

    [Fact]
    public void FormatTriples_MultipleTriples_ShowsPluralText()
    {
        var writer = new StringWriter();
        var formatter = new ResultTableFormatter(writer, useColor: false);

        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Construct,
            Triples = new List<(string, string, string)>
            {
                ("<http://ex.org/s1>", "<http://ex.org/p>", "<http://ex.org/o1>"),
                ("<http://ex.org/s2>", "<http://ex.org/p>", "<http://ex.org/o2>")
            },
            ParseTime = TimeSpan.Zero,
            ExecutionTime = TimeSpan.Zero
        };

        formatter.FormatTriples(result);
        var output = writer.ToString();

        Assert.Contains("2 triples", output);
    }

    [Fact]
    public void FormatTriples_MaxRows_LimitsOutput()
    {
        var writer = new StringWriter();
        var formatter = new ResultTableFormatter(writer, useColor: false, maxRows: 2);

        var triples = new List<(string, string, string)>();
        for (int i = 0; i < 10; i++)
        {
            triples.Add(($"<http://ex.org/s{i}>", "<http://ex.org/p>", $"<http://ex.org/o{i}>"));
        }

        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Construct,
            Triples = triples,
            ParseTime = TimeSpan.Zero,
            ExecutionTime = TimeSpan.Zero
        };

        formatter.FormatTriples(result);
        var output = writer.ToString();

        Assert.Contains("and 8 more triples", output);
    }

    #endregion

    #region FormatUpdate Tests

    [Fact]
    public void FormatUpdate_Success_ShowsOk()
    {
        var writer = new StringWriter();
        var formatter = new ResultTableFormatter(writer, useColor: false);

        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Update,
            Success = true,
            AffectedCount = 3,
            ParseTime = TimeSpan.FromMilliseconds(1),
            ExecutionTime = TimeSpan.FromMilliseconds(5)
        };

        formatter.FormatUpdate(result);
        var output = writer.ToString();

        Assert.Contains("OK", output);
        Assert.Contains("3 triples affected", output);
    }

    [Fact]
    public void FormatUpdate_SingleTriple_ShowsSingularText()
    {
        var writer = new StringWriter();
        var formatter = new ResultTableFormatter(writer, useColor: false);

        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Update,
            Success = true,
            AffectedCount = 1,
            ParseTime = TimeSpan.Zero,
            ExecutionTime = TimeSpan.Zero
        };

        formatter.FormatUpdate(result);
        var output = writer.ToString();

        Assert.Contains("1 triple affected", output);
        Assert.DoesNotContain("1 triples", output);
    }

    [Fact]
    public void FormatUpdate_Failure_ShowsFailed()
    {
        var writer = new StringWriter();
        var formatter = new ResultTableFormatter(writer, useColor: false);

        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Update,
            Success = false,
            Message = "Something went wrong",
            ParseTime = TimeSpan.Zero,
            ExecutionTime = TimeSpan.Zero
        };

        formatter.FormatUpdate(result);
        var output = writer.ToString();

        Assert.Contains("FAILED", output);
        Assert.Contains("Something went wrong", output);
    }

    #endregion

    #region Color Mode Tests

    [Fact]
    public void FormatSelect_ColorEnabled_ContainsAnsiCodes()
    {
        var writer = new StringWriter();
        var formatter = new ResultTableFormatter(writer, useColor: true);

        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Select,
            Variables = new[] { "?x" },
            Rows = new List<Dictionary<string, string>>
            {
                new Dictionary<string, string> { ["?x"] = "\"value\"" }
            },
            ParseTime = TimeSpan.Zero,
            ExecutionTime = TimeSpan.Zero
        };

        formatter.FormatSelect(result);
        var output = writer.ToString();

        // ANSI escape code prefix
        Assert.Contains("\x1b[", output);
    }

    [Fact]
    public void FormatSelect_ColorDisabled_NoAnsiCodes()
    {
        var writer = new StringWriter();
        var formatter = new ResultTableFormatter(writer, useColor: false);

        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Select,
            Variables = new[] { "?x" },
            Rows = new List<Dictionary<string, string>>
            {
                new Dictionary<string, string> { ["?x"] = "\"value\"" }
            },
            ParseTime = TimeSpan.Zero,
            ExecutionTime = TimeSpan.Zero
        };

        formatter.FormatSelect(result);
        var output = writer.ToString();

        // No ANSI escape codes
        Assert.DoesNotContain("\x1b[", output);
    }

    #endregion

    #region IRI Formatting Tests

    [Fact]
    public void FormatSelect_LongIRI_ShowsLocalName()
    {
        var writer = new StringWriter();
        var formatter = new ResultTableFormatter(writer, useColor: false, maxColumnWidth: 50);

        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Select,
            Variables = new[] { "?type" },
            Rows = new List<Dictionary<string, string>>
            {
                new Dictionary<string, string>
                {
                    ["?type"] = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>"
                }
            },
            ParseTime = TimeSpan.Zero,
            ExecutionTime = TimeSpan.Zero
        };

        formatter.FormatSelect(result);
        var output = writer.ToString();

        // Should show abbreviated form
        Assert.Contains("type", output);
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void Constructor_NullWriter_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ResultTableFormatter(null!));
    }

    [Fact]
    public void Constructor_DefaultParameters_WorksCorrectly()
    {
        var writer = new StringWriter();
        var formatter = new ResultTableFormatter(writer);

        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Ask,
            AskResult = true,
            ParseTime = TimeSpan.Zero,
            ExecutionTime = TimeSpan.Zero
        };

        formatter.FormatAsk(result);
        var output = writer.ToString();

        Assert.Contains("true", output);
    }

    #endregion
}
