// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SkyOmega.Mercury.Serving;

namespace SkyOmega.Mercury.Hosting;

/// <summary>Where a store host serves its store.</summary>
public sealed class StoreEndpointOptions
{
    /// <summary>The HTTP endpoint's port.</summary>
    public required int Port { get; init; }

    /// <summary>Whether the HTTP endpoint takes updates (<c>POST /sparql/update</c>).</summary>
    public bool EnableUpdates { get; init; }

    /// <summary>The attach pipe's name.</summary>
    public required string PipeName { get; init; }

    /// <summary>What a client of the attach pipe is told when it connects.</summary>
    public required string PipeWelcome { get; init; }

    /// <summary>The attach pipe's REPL prompt.</summary>
    public required string PipePrompt { get; init; }
}

/// <summary>
/// A process that holds one store and serves it until it is stopped, on the .NET Generic Host: mercury-mcp, and
/// mercuryd (ADR-065 D11).
/// </summary>
public static class StoreHost
{
    /// <summary>
    /// A host builder for <paramref name="store"/>: the host's own log lines on standard error, and the store and
    /// its pool as services. A tool adds its own services, then the endpoints (<see cref="AddStoreEndpoints"/>).
    /// </summary>
    public static HostApplicationBuilder CreateBuilder(StoreSession store)
    {
        var builder = Host.CreateApplicationBuilder();
        // Standard output may be a protocol (MCP's), so every log line goes to standard error.
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(store.Pool);
        return builder;
    }

    /// <summary>The store's HTTP endpoint and its attach pipe, each started and stopped with the host.</summary>
    public static HostApplicationBuilder AddStoreEndpoints(this HostApplicationBuilder builder, StoreEndpointOptions options)
    {
        builder.Services.AddSingleton(sp =>
            new HttpServerHostedService(sp.GetRequiredService<StoreSession>(), options.Port, options.EnableUpdates));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<HttpServerHostedService>());
        builder.Services.AddSingleton(sp =>
            new PipeServerHostedService(sp.GetRequiredService<StoreSession>(), options.PipeName, options.PipeWelcome, options.PipePrompt));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<PipeServerHostedService>());
        return builder;
    }

    /// <summary>
    /// Runs <paramref name="host"/> until it is stopped, by SIGTERM, SIGINT or the host itself, then closes
    /// <paramref name="store"/>. The stop signals stay handled until the store is closed (<see cref="StopSignals"/>).
    /// </summary>
    /// <param name="host">The host, which this disposes.</param>
    /// <param name="store">The store, closed once the host has stopped.</param>
    /// <param name="closing">Called after the host has stopped and before the store is closed.</param>
    public static async Task RunAsync(IHost host, StoreSession store, Action? closing = null)
    {
        using var signals = StopSignals.Register();
        await host.RunAsync(signals.Token);
        closing?.Invoke();
        store.Dispose();
    }
}
