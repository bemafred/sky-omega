// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Serving;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Serving;

/// <summary>The REPL session over a store, with the commands each tool offers (ADR-065 D11).</summary>
public sealed class ReplSessionsTests : IDisposable
{
    private readonly TempPath _dir;
    private readonly StoreSession _store;

    public ReplSessionsTests()
    {
        _dir = TempPath.Test("repl-sessions");
        _dir.MarkOwnership();
        _store = StoreSession.Open(Path.Combine(_dir.FullPath, "store"), "mercury-test", "9.9.9", StorageOptions.ForTesting);
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Cleanup();
    }

    [Fact]
    public void EverySession_QueriesAndUpdatesTheStore_AndNamesIt()
    {
        using var repl = ReplSessions.Create(_store);

        var update = repl.Execute("INSERT DATA { <urn:s> <urn:p> \"o\" }");
        var query = repl.Execute("SELECT ?o WHERE { <urn:s> <urn:p> ?o }");
        var path = repl.Execute(":store");

        Assert.True(update.Success, update.Message);
        Assert.Equal(1, query.RowCount);
        Assert.Equal(_store.Path, path.Message);
    }

    [Theory]
    [InlineData(":prune")]
    [InlineData(":attach")]
    [InlineData(":load data.nt")]
    [InlineData(":convert a.ttl b.nt")]
    [InlineData(":rebuild-indexes")]
    public void ASessionWithNoCommands_OffersNoneOfThem(string command)
    {
        using var repl = ReplSessions.Create(_store, ReplCommands.None);

        var result = repl.Execute(command);

        Assert.Equal(ExecutionResultKind.Error, result.Kind);
        Assert.Contains("not available", result.Message);
    }

    [Fact]
    public void Prune_ReadsItsOptions_AndPrunesThePool()
    {
        using var repl = ReplSessions.Create(_store, ReplCommands.Prune);
        repl.Execute("INSERT DATA { <urn:s> <urn:p> \"o\" }");

        var dryRun = repl.Execute(":prune --dry-run");
        var history = repl.Execute(":prune --history");

        Assert.Equal(ExecutionResultKind.Command, dryRun.Kind);
        Assert.Contains("Prune dry-run complete", dryRun.Message);
        Assert.Equal(ExecutionResultKind.Error, history.Kind);
        Assert.Contains("--history is gone", history.Message);
    }

    [Fact]
    public void Load_LoadsAFileIntoTheActiveStore()
    {
        var file = Path.Combine(_dir.FullPath, "data.nt");
        File.WriteAllText(file, "<urn:a> <urn:b> \"c\" .\n<urn:a> <urn:b> \"d\" .\n");
        using var repl = ReplSessions.Create(_store, ReplCommands.Load);

        var load = repl.Execute($":load {file}");
        var query = repl.Execute("SELECT ?o WHERE { <urn:a> <urn:b> ?o }");

        Assert.NotEqual(ExecutionResultKind.Error, load.Kind);
        Assert.Equal(2, query.RowCount);
    }

    [Fact]
    public void RebuildIndexes_RebuildsThem()
    {
        using var repl = ReplSessions.Create(_store, ReplCommands.RebuildIndexes);

        var result = repl.Execute(":rebuild-indexes");

        Assert.NotEqual(ExecutionResultKind.Error, result.Kind);
    }
}
