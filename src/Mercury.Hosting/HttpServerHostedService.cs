// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Microsoft.Extensions.Hosting;
using SkyOmega.Mercury.Diagnostics;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Serving;
using SkyOmega.Mercury.Sparql.Protocol;

namespace SkyOmega.Mercury.Hosting;

/// <summary>The store's SPARQL endpoint, started and stopped with the host.</summary>
public sealed class HttpServerHostedService : IHostedService, IDisposable, IAsyncDisposable
{
    private readonly StoreSession _store;
    private readonly SparqlHttpServer _httpServer;
    private readonly IReadOnlyList<string> _endpoints;

    /// <summary>The endpoint over <paramref name="store"/> at <c>http://{host}:{port}/</c> for each host.</summary>
    public HttpServerHostedService(StoreSession store, IReadOnlyList<string> hosts, int port, bool enableUpdates)
    {
        _store = store;
        _httpServer = store.CreateHttpServer(hosts, port, enableUpdates);
        _endpoints = [.. hosts.Select(host => MercuryPorts.GetEndpoint(host, port))];
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The endpoint cannot listen on one of its addresses: one that is
    /// not the machine's, a port in use, or on Windows an address that needs a URL reservation.</exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _httpServer.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new InvalidOperationException(
                $"The HTTP endpoint cannot listen on {string.Join(", ", _endpoints)}: {ex.Message}", ex);
        }
        foreach (var endpoint in _endpoints)
        {
            Console.Error.WriteLine($"  HTTP: {endpoint}");
            _store.Log?.Log(LogLevel.Info, $"Serving SPARQL at {endpoint}");
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>Returns once the responses in progress are written (<see cref="SparqlHttpServer.StopAsync"/>), whatever
    /// the host's token: the store is closed after the host has stopped.</remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _httpServer.StopAsync();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _httpServer.Dispose();
    }

    /// <summary>
    /// Stops the endpoint as <see cref="StopAsync"/> does, then disposes it. The host disposes its services so, after a
    /// start that failed once this endpoint had started.
    /// </summary>
    public ValueTask DisposeAsync() => _httpServer.DisposeAsync();
}
