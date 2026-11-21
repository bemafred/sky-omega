// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime.IO;
using Xunit;

namespace SkyOmega.Mercury.Hosting.Tests;

/// <summary>mercuryd's options (ADR-065 D2, D4, D5): one store, named or given; where it listens; updates.</summary>
public sealed class DaemonArgumentsTests
{
    [Fact]
    public void TheStore_IsNamedOrGiven()
    {
        Assert.Equal(MercuryPaths.Store("shared"), DaemonArguments.Read(["--store", "shared"]).Store.ResolvePath());
        Assert.Equal("/srv/store", DaemonArguments.Read(["-d", "/srv/store"]).Store.ResolvePath());
    }

    [Fact]
    public void NoStore_IsRefused_SayingHowToNameOne()
    {
        var read = DaemonArguments.Read([]);

        Assert.Equal(
            "mercuryd serves one store: name it with --store <name>, or give its directory with -d <path>." +
            $"{Environment.NewLine}Use --help for usage information.",
            read.Error);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-v")]
    public void NoStore_IsNotRefused_ForHelpOrTheVersion(string option)
    {
        Assert.Null(DaemonArguments.Read([option]).Error);
    }

    [Fact]
    public void WhereItListens_AndThePort_AndTheProfile()
    {
        var read = DaemonArguments.Read(
            ["--store", "shared", "--listen", "100.67.254.98", "--listen", "elite.tail7f8646.ts.net", "-p", "4000", "--profile", "graph"]);

        Assert.Null(read.Error);
        Assert.Equal(["100.67.254.98", "elite.tail7f8646.ts.net"], read.Store.Hosts);
        Assert.Equal(4000, read.Store.Port);
        Assert.Equal(StoreProfile.Graph, read.Store.Profile);
    }

    [Fact]
    public void Updates_AreOn_UnlessReadOnly()
    {
        Assert.False(DaemonArguments.Read(["--store", "shared"]).ReadOnly);
        Assert.True(DaemonArguments.Read(["--store", "shared", "--read-only"]).ReadOnly);
    }

    [Fact]
    public void AnOptionOfAnotherTool_IsRefused()
    {
        var read = DaemonArguments.Read(["--store", "shared", "--enable-http-updates"]);

        Assert.Equal($"Unknown option '--enable-http-updates'.{Environment.NewLine}Use --help for usage information.", read.Error);
    }

    [Fact]
    public void AQueryWhereThePathGoes_IsRefused_SayingWhereQueriesGo()
    {
        var read = DaemonArguments.Read(["SELECT * WHERE { ?s ?p ?o }"]);

        Assert.Equal(
            $"'SELECT * WHERE {{ ?s ?p ?o }}' looks like a SPARQL query, not a store path.{Environment.NewLine}" +
            "mercuryd serves a store; query it at its endpoint, or over its attach socket.",
            read.Error);
    }
}
