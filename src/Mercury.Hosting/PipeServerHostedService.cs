// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Hosting;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Serving;

namespace SkyOmega.Mercury.Hosting;

/// <summary>The store's attach pipe, started and stopped with the host: each client that connects gets a REPL
/// session of its own over the store (<see cref="ReplSessions"/>).</summary>
public sealed class PipeServerHostedService : IHostedService, IDisposable
{
    private readonly PipeServer _pipeServer;

    /// <summary>The pipe <paramref name="pipeName"/> over <paramref name="store"/>.</summary>
    /// <param name="store">The store each session queries and updates.</param>
    /// <param name="pipeName">The pipe's name.</param>
    /// <param name="welcomeMessage">What a client is told when it connects.</param>
    /// <param name="prompt">The REPL's prompt.</param>
    public PipeServerHostedService(StoreSession store, string pipeName, string welcomeMessage, string prompt)
    {
        _pipeServer = new PipeServer(pipeName, () => ReplSessions.Create(store), welcomeMessage, prompt);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _pipeServer.Start();
        Console.Error.WriteLine($"  Pipe: {_pipeServer.PipeName}");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _pipeServer.StopAsync();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _pipeServer.Dispose();
    }
}
