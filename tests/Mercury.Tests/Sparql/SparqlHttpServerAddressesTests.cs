// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Sparql.Protocol;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// A SPARQL endpoint on several addresses (ADR-065 D4): one listener, one port, every address answered; the service
/// description names the address a request reached; a start fails whole, never on some of the addresses.
/// </summary>
/// <remarks>
/// The two addresses every machine has are <c>localhost</c> and <c>127.0.0.1</c>. A request names its address in its
/// Host, and a listener answers only the names it listens on (measured on Linux, 2026-11-20, ADR-065 Context), so the
/// two are two addresses to the listener although they reach the same interface. On Windows a listener on
/// <c>127.0.0.1</c> needs a URL reservation or an administrator; without either, those tests are skipped.
/// </remarks>
public sealed class SparqlHttpServerAddressesTests : IDisposable
{
    private readonly TempPath _dir;
    private readonly QuadStore _store;

    public SparqlHttpServerAddressesTests()
    {
        _dir = TempPath.Test("http-addresses");
        _dir.MarkOwnership();
        _store = new QuadStore(_dir.FullPath, null, null, StorageOptions.ForTesting);
        _store.AddCurrent("<urn:s>", "<urn:p>", "\"answered\"");
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Cleanup();
    }

    private static string[] BothAddresses(int port) => [$"http://localhost:{port}/", $"http://127.0.0.1:{port}/"];

    [Fact]
    public async Task OneServer_AnswersOnEveryAddress()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = FreePort.Start(port => new SparqlHttpServer(() => _store, BothAddresses(port)),
            StartOrSkipWithoutAReservation, out var port);
        try
        {
            using var http = new HttpClient();
            var query = Uri.EscapeDataString("SELECT ?o WHERE { <urn:s> <urn:p> ?o }");
            foreach (var address in BothAddresses(port))
            {
                var response = await http.GetAsync($"{address}sparql?query={query}", ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Contains("answered", await response.Content.ReadAsStringAsync(ct));
            }
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task TheServiceDescription_NamesTheEndpointTheRequestReached()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = FreePort.Start(port => new SparqlHttpServer(() => _store, BothAddresses(port)),
            StartOrSkipWithoutAReservation, out var port);
        try
        {
            using var http = new HttpClient();
            foreach (var address in BothAddresses(port))
            {
                var description = await http.GetStringAsync($"{address}sparql", ct);
                Assert.Contains($"<{address}sparql> a sd:Service", description);
            }
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task AnAddressGivenTwice_IsListenedOnOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = FreePort.Start(
            port => new SparqlHttpServer(() => _store, [$"http://localhost:{port}/", $"http://localhost:{port}"]),
            server => server.Start(), out var port);

        Assert.Equal([$"http://localhost:{port}/"], server.BaseUris);
        try
        {
            using var http = new HttpClient();
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"http://localhost:{port}/sparql", ct)).StatusCode);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public void NoAddress_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new SparqlHttpServer(() => _store, Array.Empty<string>()));
    }

    [Fact]
    public async Task AnAddressThatCannotBeListenedOn_FailsTheStart_AndNoAddressIsLeftListening()
    {
        // 192.0.2.1 is TEST-NET-1 (RFC 5737), no machine's own address. The managed listener of Linux and macOS binds
        // each address and fails on that one; Windows' http.sys registers the name without binding it, so there it
        // starts, and nothing ever reaches it.
        if (OperatingSystem.IsWindows())
            Assert.Skip("http.sys registers an address that is not the machine's without binding it.");
        var ct = TestContext.Current.CancellationToken;
        // A localhost port another took first would refuse the start too, and the request below could reach the taker:
        // the start is made again on another port while its refusal says that.
        SparqlHttpServer? server = null;
        Assert.Throws<HttpListenerException>(() => FreePort.Start(
            port => server = new SparqlHttpServer(
                () => _store, [$"http://localhost:{port}/", $"http://192.0.2.1:{port}/"]),
            made => made.Start(), out _));

        Assert.False(server!.IsListening);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync($"{server.BaseUris[0]}sparql", ct));
        // A server whose start failed is stopped and disposed without a further exception, so the start's is the one
        // its process reports.
        await server.StopAsync();
        server.Dispose();
    }

    [Theory]
    [InlineData("localhost", "http://localhost:3032/")]
    [InlineData("100.67.254.98", "http://100.67.254.98:3032/")]
    [InlineData("elite.tail7f8646.ts.net", "http://elite.tail7f8646.ts.net:3032/")]
    [InlineData("+", "http://+:3032/")]
    [InlineData("::1", "http://[::1]:3032/")]
    [InlineData("[::1]", "http://[::1]:3032/")]
    [InlineData("fd7a:115c:a1e0::1", "http://[fd7a:115c:a1e0::1]:3032/")]
    public void TheAddressOfAHostAndAPort(string host, string address)
    {
        Assert.Equal(address, MercuryPorts.GetBaseUrl(host, 3032));
        Assert.Equal(address + "sparql", MercuryPorts.GetEndpoint(host, 3032));
    }

    [Fact]
    public void TheAddressOfAPortAlone_IsLocalhost()
    {
        Assert.Equal("http://localhost:3031/", MercuryPorts.GetBaseUrl(3031));
        Assert.Equal("http://localhost:3031/sparql", MercuryPorts.GetEndpoint(3031));
    }

    private static void StartOrSkipWithoutAReservation(SparqlHttpServer server)
    {
        try
        {
            server.Start();
        }
        catch (HttpListenerException ex) when (OperatingSystem.IsWindows() && ex.ErrorCode == 5)
        {
            Assert.Skip("A listener on 127.0.0.1 needs a URL reservation or an administrator on Windows.");
        }
    }
}
