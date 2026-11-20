// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Hosting;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Serving;
using SkyOmega.Mercury.Sparql.Protocol;

namespace SkyOmega.Mercury.Hosting;

/// <summary>The store's SPARQL endpoint, started and stopped with the host.</summary>
public sealed class HttpServerHostedService : IHostedService, IDisposable
{
    private readonly SparqlHttpServer _httpServer;
    private readonly int _port;

    /// <summary>The endpoint over <paramref name="store"/> at <c>http://localhost:{port}/</c>.</summary>
    public HttpServerHostedService(StoreSession store, int port, bool enableUpdates)
    {
        _port = port;
        _httpServer = store.CreateHttpServer(port, enableUpdates);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _httpServer.Start();
        Console.Error.WriteLine($"  HTTP: {MercuryPorts.GetEndpoint(_port)}");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _httpServer.StopAsync();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _httpServer.Dispose();
    }
}
