// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Hosting;
using SkyOmega.Mercury.Sparql.Protocol;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Mcp.Services;

/// <summary>
/// Hosted service wrapper for <see cref="SparqlHttpServer"/>.
/// </summary>
public sealed class HttpServerHostedService : IHostedService, IDisposable
{
    private readonly SparqlHttpServer _httpServer;
    private readonly int _port;

    public HttpServerHostedService(QuadStore store, int port, bool enableUpdates)
    {
        _port = port;
        _httpServer = new SparqlHttpServer(
            store,
            $"http://localhost:{port}/",
            new SparqlHttpServerOptions { EnableUpdates = enableUpdates });
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _httpServer.Start();
        Console.Error.WriteLine($"  HTTP: http://localhost:{_port}/sparql");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _httpServer.StopAsync();
    }

    public void Dispose()
    {
        _httpServer.Dispose();
    }
}
