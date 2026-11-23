// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Serving;
using Xunit;

namespace SkyOmega.Mercury.Tests.Serving;

/// <summary>The store options the Mercury tools share (ADR-065 D11), read as mercury and mercury-mcp read them before.</summary>
public sealed class StoreArgumentsTests
{
    private const StoreOptions All =
        StoreOptions.Path | StoreOptions.Name | StoreOptions.Profile | StoreOptions.Port | StoreOptions.Listen |
        StoreOptions.ServiceConnectTimeout;

    /// <summary>Reads every argument as a tool would, stopping at the first not read.</summary>
    private static (StoreArguments Store, ArgumentRead Last, int Index) ReadAll(StoreOptions takes, params string[] args)
    {
        var store = new StoreArguments(takes);
        var last = ArgumentRead.Read;
        int i = 0;
        for (; i < args.Length; i++)
        {
            last = store.Read(args, ref i);
            if (last != ArgumentRead.Read)
                break;
        }
        return (store, last, i);
    }

    [Theory]
    [InlineData("-d")]
    [InlineData("--data")]
    public void Data_IsThePath(string option)
    {
        var (store, last, _) = ReadAll(All, option, "/stores/x");

        Assert.Equal(ArgumentRead.Read, last);
        Assert.Equal("/stores/x", store.Path);
    }

    [Fact]
    public void Store_IsTheName_AndResolvesUnderTheStores()
    {
        var (store, _, _) = ReadAll(All, "--store", "wikidata");

        Assert.Equal("wikidata", store.Name);
        Assert.Equal(MercuryPaths.Store("wikidata"), store.ResolvePath("cli"));
    }

    [Fact]
    public void ResolvePath_APathWinsOverAName_AndNeitherIsTheDefault()
    {
        Assert.Equal("/x", ReadAll(All, "--store", "n", "-d", "/x").Store.ResolvePath("cli"));
        Assert.Equal(MercuryPaths.Store("mcp"), new StoreArguments(All).ResolvePath("mcp"));
    }

    [Fact]
    public void ResolvePath_WithNoDefault_IsNullWhenNoStoreIsGiven()
    {
        Assert.Null(new StoreArguments(All).ResolvePath());
        Assert.Equal(MercuryPaths.Store("shared"), ReadAll(All, "--store", "shared").Store.ResolvePath());
        Assert.Equal("/x", ReadAll(All, "-d", "/x").Store.ResolvePath());
    }

    [Fact]
    public void Listen_EachIsAHost_InTheOrderGiven()
    {
        var (store, last, _) = ReadAll(All, "--listen", "100.67.254.98", "--listen", "elite.tail7f8646.ts.net");

        Assert.Equal(ArgumentRead.Read, last);
        Assert.Equal(["100.67.254.98", "elite.tail7f8646.ts.net"], store.Hosts);
    }

    [Fact]
    public void Listen_NoneGiven_IsLocalhost()
    {
        Assert.Equal(["localhost"], new StoreArguments(All).Hosts);
    }

    [Theory]
    [InlineData("-p")]
    [InlineData("--port")]
    public void Port_IsRead(string option)
    {
        var (store, _, _) = ReadAll(All, option, "4000");

        Assert.Equal(4000, store.Port);
    }

    [Fact]
    public void Port_NotANumber_IsIgnored_AndItsValueIsPassed()
    {
        var (store, last, index) = ReadAll(All, "-p", "many", "/stores/x");

        Assert.Equal(ArgumentRead.Read, last);
        Assert.Equal(3, index);
        Assert.Null(store.Port);
        Assert.Equal("/stores/x", store.Path);
    }

    [Theory]
    [InlineData("-d")]
    [InlineData("--store")]
    [InlineData("-p")]
    [InlineData("--listen")]
    public void AValueMissingAtTheEnd_IsIgnored(string option)
    {
        var (store, last, _) = ReadAll(All, option);

        Assert.Equal(ArgumentRead.Read, last);
        Assert.Null(store.Path);
        Assert.Null(store.Name);
        Assert.Null(store.Port);
        Assert.Equal(["localhost"], store.Hosts);
    }

    [Theory]
    [InlineData("graph", StoreProfile.Graph)]
    [InlineData("Reference", StoreProfile.Reference)]
    [InlineData("COGNITIVE", StoreProfile.Cognitive)]
    public void Profile_IsRead_InAnyCase(string value, StoreProfile expected)
    {
        Assert.Equal(expected, ReadAll(All, "--profile", value).Store.Profile);
    }

    [Theory]
    [InlineData("--profile", "nope")]
    [InlineData("--profile")]
    public void Profile_NotOne_IsRefused_NamingTheProfiles(params string[] args)
    {
        var (store, last, _) = ReadAll(All, args);

        Assert.Equal(ArgumentRead.Refused, last);
        Assert.Equal("--profile requires one of: Cognitive, Graph, Reference, Minimal.", store.Error);
    }

    /// <summary>A SERVICE call's connect timeout, in whole seconds, for the process (ADR-066 D5).</summary>
    [Fact]
    public void ServiceConnectTimeout_IsReadInSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(7), ReadAll(All, "--service-connect-timeout", "7").Store.ServiceConnectTimeout);
    }

    /// <summary>Not given, it is null, and the process keeps SparqlEngine's 5 seconds (ADR-066 D5).</summary>
    [Fact]
    public void ServiceConnectTimeout_NotGiven_IsNull()
    {
        Assert.Null(ReadAll(All, "-d", "/a").Store.ServiceConnectTimeout);
    }

    [Theory]
    [InlineData("--service-connect-timeout", "0")]
    [InlineData("--service-connect-timeout", "-1")]
    [InlineData("--service-connect-timeout", "1.5")]
    [InlineData("--service-connect-timeout", "five")]
    [InlineData("--service-connect-timeout")]
    public void ServiceConnectTimeout_NotAWholeNumberOfSecondsFromOne_IsRefused(params string[] args)
    {
        var (store, last, _) = ReadAll(All, args);

        Assert.Equal(ArgumentRead.Refused, last);
        Assert.Equal("--service-connect-timeout requires a whole number of seconds, 1 or more.", store.Error);
        Assert.Null(store.ServiceConnectTimeout);
    }

    [Fact]
    public void AnArgumentWithNoOption_IsThePath()
    {
        Assert.Equal("./mydata", ReadAll(All, "./mydata").Store.Path);
    }

    [Fact]
    public void ASecondPath_IsRefused_WithWhereHelpIs()
    {
        var (store, last, _) = ReadAll(All, "-d", "/a", "/b");

        Assert.Equal(ArgumentRead.Refused, last);
        Assert.Equal(
            $"Unexpected argument '/b'. Store path already set to '/a'.{Environment.NewLine}Use --help for usage information.",
            store.Error);
    }

    [Fact]
    public void AQueryWhereThePathGoes_IsRefusedAsSparql()
    {
        var (store, last, _) = ReadAll(All, "SELECT * WHERE { ?s ?p ?o }");

        Assert.Equal(ArgumentRead.LooksLikeSparql, last);
        Assert.Equal("'SELECT * WHERE { ?s ?p ?o }' looks like a SPARQL query, not a store path.", store.Error);
        Assert.Null(store.Path);
    }

    [Theory]
    [InlineData("--store", "n")]
    [InlineData("--profile", "graph")]
    [InlineData("--enable-http-updates")]
    [InlineData("--listen", "+")]
    [InlineData("--service-connect-timeout", "5")]
    [InlineData("-x")]
    public void AnOptionNotTaken_IsLeftToTheTool(params string[] args)
    {
        // A tool that takes a path and a port.
        var (store, last, index) = ReadAll(StoreOptions.Path | StoreOptions.Port, args);

        Assert.Equal(ArgumentRead.NotMine, last);
        Assert.Equal(0, index);
        Assert.Null(store.Name);
        Assert.Null(store.Profile);
    }

    [Fact]
    public void AnArgumentWithNoOption_IsLeftToTheTool_WhenItTakesNoPath()
    {
        Assert.Equal(ArgumentRead.NotMine, ReadAll(StoreOptions.Port, "./mydata").Last);
    }

    [Theory]
    [InlineData("SELECT ?s WHERE { }", true)]
    [InlineData("ask {}", true)]
    [InlineData("INSERT DATA", true)]
    [InlineData("?s ?p ?o", true)]
    [InlineData("./stores/wikidata", false)]
    [InlineData("/home/someone/select", false)]
    [InlineData(@"C:\Stores\Ask", false)]
    public void LooksLikeSparql(string arg, bool expected)
    {
        Assert.Equal(expected, StoreArguments.LooksLikeSparql(arg));
    }
}
