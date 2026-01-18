// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SkyOmega.Mercury.Runtime.IO;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// Tests for ExecutionResult - the result type from REPL execution.
/// </summary>
public class ExecutionResultTests
{
    #region Factory Methods

    [Fact]
    public void Empty_ReturnsEmptyResult()
    {
        var result = ExecutionResult.Empty();

        Assert.Equal(ExecutionResultKind.Empty, result.Kind);
        Assert.True(result.Success);
        Assert.Null(result.Message);
    }

    [Fact]
    public void Error_ReturnsErrorResult()
    {
        var result = ExecutionResult.Error("Something went wrong");

        Assert.Equal(ExecutionResultKind.Error, result.Kind);
        Assert.False(result.Success);
        Assert.Equal("Something went wrong", result.Message);
    }

    [Fact]
    public void Command_ReturnsCommandResult()
    {
        var result = ExecutionResult.Command("Help text here");

        Assert.Equal(ExecutionResultKind.Command, result.Kind);
        Assert.True(result.Success);
        Assert.Equal("Help text here", result.Message);
    }

    [Fact]
    public void PrefixRegistered_ReturnsPrefixResult()
    {
        var result = ExecutionResult.PrefixRegistered("ex", "http://example.org/");

        Assert.Equal(ExecutionResultKind.PrefixRegistered, result.Kind);
        Assert.True(result.Success);
        Assert.Contains("ex:", result.Message);
        Assert.Contains("http://example.org/", result.Message);
    }

    [Fact]
    public void BaseSet_ReturnsBaseResult()
    {
        var result = ExecutionResult.BaseSet("http://base.org/");

        Assert.Equal(ExecutionResultKind.BaseSet, result.Kind);
        Assert.True(result.Success);
        Assert.Contains("http://base.org/", result.Message);
    }

    #endregion

    #region TotalTime Property

    [Fact]
    public void TotalTime_SumOfParseAndExecution()
    {
        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Select,
            ParseTime = TimeSpan.FromMilliseconds(10),
            ExecutionTime = TimeSpan.FromMilliseconds(25)
        };

        Assert.Equal(TimeSpan.FromMilliseconds(35), result.TotalTime);
    }

    [Fact]
    public void TotalTime_DefaultTimesAreZero()
    {
        var result = new ExecutionResult { Kind = ExecutionResultKind.Select };

        Assert.Equal(TimeSpan.Zero, result.TotalTime);
    }

    #endregion

    #region ExecutionResultKind Enum

    [Fact]
    public void ExecutionResultKind_HasExpectedValues()
    {
        // Verify all expected result kinds exist
        Assert.Equal(0, (int)ExecutionResultKind.Empty);
        Assert.Equal(1, (int)ExecutionResultKind.Select);
        Assert.Equal(2, (int)ExecutionResultKind.Ask);
        Assert.Equal(3, (int)ExecutionResultKind.Construct);
        Assert.Equal(4, (int)ExecutionResultKind.Describe);
        Assert.Equal(5, (int)ExecutionResultKind.Update);
        Assert.Equal(6, (int)ExecutionResultKind.PrefixRegistered);
        Assert.Equal(7, (int)ExecutionResultKind.BaseSet);
        Assert.Equal(8, (int)ExecutionResultKind.Command);
        Assert.Equal(9, (int)ExecutionResultKind.Error);
    }

    #endregion

    #region Result Properties

    [Fact]
    public void SelectResult_HasVariablesAndRows()
    {
        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Select,
            Success = true,
            Variables = new[] { "?s", "?p", "?o" },
            Rows = new List<Dictionary<string, string>>
            {
                new Dictionary<string, string>
                {
                    ["?s"] = "<http://example.org/s>",
                    ["?p"] = "<http://example.org/p>",
                    ["?o"] = "<http://example.org/o>"
                }
            },
            RowCount = 1
        };

        Assert.Equal(3, result.Variables!.Length);
        Assert.Single(result.Rows!);
        Assert.Equal(1, result.RowCount);
    }

    [Fact]
    public void AskResult_HasBooleanValue()
    {
        var resultTrue = new ExecutionResult { Kind = ExecutionResultKind.Ask, AskResult = true };
        var resultFalse = new ExecutionResult { Kind = ExecutionResultKind.Ask, AskResult = false };

        Assert.True(resultTrue.AskResult);
        Assert.False(resultFalse.AskResult);
    }

    [Fact]
    public void ConstructResult_HasTriples()
    {
        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Construct,
            Success = true,
            Triples = new List<(string Subject, string Predicate, string Object)>
            {
                ("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>")
            },
            RowCount = 1
        };

        Assert.Single(result.Triples!);
        Assert.Equal("<http://ex.org/s>", result.Triples[0].Subject);
        Assert.Equal("<http://ex.org/p>", result.Triples[0].Predicate);
        Assert.Equal("<http://ex.org/o>", result.Triples[0].Object);
    }

    [Fact]
    public void UpdateResult_HasAffectedCount()
    {
        var result = new ExecutionResult
        {
            Kind = ExecutionResultKind.Update,
            Success = true,
            AffectedCount = 5
        };

        Assert.Equal(5, result.AffectedCount);
    }

    #endregion
}
