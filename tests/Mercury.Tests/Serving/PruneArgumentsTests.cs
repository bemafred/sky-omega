// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SkyOmega.Mercury.Serving;
using Xunit;

namespace SkyOmega.Mercury.Tests.Serving;

/// <summary>The options of the REPL's <c>:prune</c>, read as the CLI read them before ADR-065.</summary>
public sealed class PruneArgumentsTests
{
    [Fact]
    public void None_IsAPruneOfEverything()
    {
        var options = PruneArguments.Parse("");

        Assert.False(options.DryRun);
        Assert.Null(options.ExcludeGraphs);
        Assert.Null(options.ExcludePredicates);
    }

    [Fact]
    public void EachOption_AndEachExclusionAsOftenAsGiven()
    {
        var options = PruneArguments.Parse("--exclude-graph <urn:g1>  --dry-run --exclude-predicate <urn:p> --exclude-graph <urn:g2>");

        Assert.True(options.DryRun);
        Assert.Equal(["<urn:g1>", "<urn:g2>"], options.ExcludeGraphs!);
        Assert.Equal(["<urn:p>"], options.ExcludePredicates!);
    }

    [Fact]
    public void AnExclusionWithNoValue_AndAnythingElse_AreIgnored()
    {
        var options = PruneArguments.Parse("--verbose --exclude-graph");

        Assert.False(options.DryRun);
        Assert.Null(options.ExcludeGraphs);
    }

    [Fact]
    public void History_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => PruneArguments.Parse("--dry-run --history"));

        Assert.Contains("ADR-061 D12", ex.Message);
    }
}
