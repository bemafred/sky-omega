// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SkyOmega.Mercury.Runtime;
using Xunit;

namespace SkyOmega.Mercury.Hosting.Tests;

/// <summary>
/// A prune has its store to itself: a mercury that serves its store over HTTP refuses <c>:prune</c>, and the store keeps
/// what it held; a mercury that serves no endpoint prunes (ADR-066 D8). mercury runs as a process.
/// </summary>
/// <remarks>
/// Each store holds a fact that is remembered and one that is forgotten. A prune keeps the first and leaves the second
/// out, so a question that includes what is forgotten tells a prune that ran from one that did not.
/// </remarks>
public sealed class PruneOfAServedStoreTests : IDisposable
{
    private readonly TempPath _dir;
    private readonly IReadOnlyDictionary<string, string> _environment = new Dictionary<string, string>();

    public PruneOfAServedStoreTests()
    {
        _dir = TempPath.Test("prune-served");
        _dir.MarkOwnership();
    }

    public void Dispose() => _dir.Cleanup();

    [Fact]
    public async Task APruneInAMercuryThatServesItsStoreOverHttp_IsRefused_AndTheStoreKeepsWhatItHeld()
    {
        var ct = TestContext.Current.CancellationToken;
        using var mercury = await MercuryCli.ServeOverHttp(_environment,
            port => ["-d", Path.Combine(_dir.FullPath, "served"), "-p", port.ToString()], ct);

        mercury.Type("INSERT DATA { <urn:s> <urn:p> \"remembered\" . <urn:s> <urn:p> \"forgotten\" }");
        mercury.Type("DELETE DATA { <urn:s> <urn:p> \"forgotten\" }");
        mercury.Type(":prune");
        mercury.Type("SELECT ?o WHERE { <urn:s> <urn:p> ?o } INCLUDING FORGOTTEN");
        mercury.EndInput();

        Assert.Equal(0, await mercury.Ended(ct));
        Assert.Contains("Prune is not available in this session: a prune is for a store that nothing else reads or " +
            "writes, and this store is served. Prune it in mercury --no-http.", mercury.Output);
        Assert.DoesNotContain("Prune complete", mercury.Output);
        Assert.Contains("\"remembered\"", mercury.Output);
        Assert.Contains("\"forgotten\"", mercury.Output);
    }

    [Fact]
    public async Task APruneInAMercuryThatServesNoEndpoint_Prunes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var mercury = MercuryCli.Start(_environment, "-d", Path.Combine(_dir.FullPath, "unserved"), "--no-http");

        mercury.Type("INSERT DATA { <urn:s> <urn:p> \"remembered\" . <urn:s> <urn:p> \"forgotten\" }");
        mercury.Type("DELETE DATA { <urn:s> <urn:p> \"forgotten\" }");
        mercury.Type(":prune");
        mercury.Type("SELECT ?o WHERE { <urn:s> <urn:p> ?o } INCLUDING FORGOTTEN");
        mercury.EndInput();

        Assert.Equal(0, await mercury.Ended(ct));
        Assert.Contains("Prune complete:", mercury.Output);
        Assert.Contains("\"remembered\"", mercury.Output);
        Assert.DoesNotContain("\"forgotten\"", mercury.Output);
    }
}
