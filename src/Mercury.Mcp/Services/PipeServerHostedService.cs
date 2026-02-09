// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Hosting;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Mcp.Services;

/// <summary>
/// Hosted service wrapper for <see cref="PipeServer"/>.
/// </summary>
public sealed class PipeServerHostedService : IHostedService, IDisposable
{
    private readonly PipeServer _pipeServer;

    public PipeServerHostedService(QuadStorePool pool, Func<QuadStorePool, ReplSession> sessionFactory, string storePath)
    {
        _pipeServer = new PipeServer(
            MercuryPorts.McpPipeName,
            () => sessionFactory(pool),
            welcomeMessage: $"Connected to Mercury MCP (store: {storePath})",
            prompt: "mcp> ");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _pipeServer.Start();
        Console.Error.WriteLine($"  Pipe: {MercuryPorts.McpPipeName}");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _pipeServer.StopAsync();
    }

    public void Dispose()
    {
        _pipeServer.Dispose();
    }
}
