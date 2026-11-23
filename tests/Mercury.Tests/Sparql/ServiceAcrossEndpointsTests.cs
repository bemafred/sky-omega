// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Sparql.Protocol;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// SERVICE from every tool (ADR-065 D10, Step 6): a query to one endpoint reads another endpoint's store with
/// SERVICE. The endpoint queries with <c>SparqlEngine.Query</c>, as mercury, mercury-mcp and mercuryd do.
/// </summary>
public sealed class ServiceAcrossEndpointsTests : IDisposable
{
    private readonly TempPath _dir;
    private readonly QuadStore _shared;
    private readonly QuadStore _local;

    public ServiceAcrossEndpointsTests()
    {
        _dir = TempPath.Test("service-endpoints");
        _dir.MarkOwnership();
        _shared = new QuadStore(Path.Combine(_dir.FullPath, "shared"), null, null, StorageOptions.ForTesting);
        _local = new QuadStore(Path.Combine(_dir.FullPath, "local"), null, null, StorageOptions.ForTesting);
        _shared.AddCurrent("<http://ex/s>", "<http://ex/shared>", "\"hej\"@sv");
        _local.AddCurrent("<http://ex/s>", "<http://ex/local>", "\"kept here\"");
    }

    public void Dispose()
    {
        _shared.Dispose();
        _local.Dispose();
        _dir.Cleanup();
    }

    [Fact]
    public async Task AQueryToOneEndpoint_ReadsAnothersStoreWithService()
    {
        var ct = TestContext.Current.CancellationToken;
        int sharedPort = FreePort(), localPort = FreePort();
        using var shared = new SparqlHttpServer(() => _shared, $"http://localhost:{sharedPort}/");
        using var local = new SparqlHttpServer(() => _local, $"http://localhost:{localPort}/");
        shared.Start();
        local.Start();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var query = "PREFIX ex: <http://ex/> SELECT ?here ?there WHERE { ?s ex:local ?here . " +
                    $"SERVICE <http://localhost:{sharedPort}/sparql> {{ ?s ex:shared ?there }} }}";

        using var answer = await http.GetAsync($"http://localhost:{localPort}/sparql?query={Uri.EscapeDataString(query)}", ct);

        var body = await answer.Content.ReadAsStringAsync(ct);
        Assert.True(answer.StatusCode == HttpStatusCode.OK, body);
        using var results = JsonDocument.Parse(body);
        var row = Assert.Single(results.RootElement.GetProperty("results").GetProperty("bindings").EnumerateArray());
        Assert.Equal("kept here", row.GetProperty("here").GetProperty("value").GetString());
        Assert.Equal("hej", row.GetProperty("there").GetProperty("value").GetString());
        Assert.Equal("sv", row.GetProperty("there").GetProperty("xml:lang").GetString());
        await local.StopAsync();
        await shared.StopAsync();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
