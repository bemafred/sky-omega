// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Serving;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Hosting.Tests;

/// <summary>
/// A store served on the Generic Host, as mercury-mcp serves its store (ADR-065 D11): the HTTP endpoint and the
/// attach pipe while the host runs, and the store closed, with its stop in the log, once the host has stopped.
/// </summary>
/// <remarks>
/// The stop signals themselves (SIGTERM, SIGINT) are tested on a process of their own, mercuryd's (ADR-065 Step 4):
/// a signal sent to this test process would reach the test platform's handlers too.
/// </remarks>
public sealed class StoreHostTests : IDisposable
{
    private readonly TempPath _dir;
    private readonly StoreSession _store;
    private readonly int _port = FreePort();
    private readonly string _pipe = $"mh-{Guid.NewGuid():N}"[..16];

    public StoreHostTests()
    {
        _dir = TempPath.Test("store-host");
        _dir.MarkOwnership();
        _store = StoreSession.Open(Path.Combine(_dir.FullPath, "store"), "mercury-test", "9.9.9", StorageOptions.ForTesting);
    }

    public void Dispose()
    {
        _store.Dispose();
        _dir.Cleanup();
    }

    private IHost Build(bool enableUpdates) => StoreHost.CreateBuilder(_store)
        .AddStoreEndpoints(new StoreEndpointOptions
        {
            Port = _port,
            EnableUpdates = enableUpdates,
            PipeName = _pipe,
            PipeWelcome = "Connected to the test store",
            PipePrompt = "test> ",
        })
        .Build();

    [Theory]
    [InlineData(false, HttpStatusCode.Forbidden)]
    [InlineData(true, HttpStatusCode.OK)]
    public async Task WhileTheHostRuns_TheStoreIsServedOverHttpAndThePipe(bool enableUpdates, HttpStatusCode updateStatus)
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Build(enableUpdates);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        Assert.Same(_store.Pool, host.Services.GetRequiredService<QuadStorePool>());
        var running = StoreHost.RunAsync(host, _store);
        try
        {
            await WaitUntil(lifetime.ApplicationStarted, ct);
            using var http = new HttpClient();
            var update = new StringContent("INSERT DATA { <urn:s> <urn:p> \"over http\" }", Encoding.UTF8, "application/sparql-update");
            var response = await http.PostAsync($"http://localhost:{_port}/sparql/update", update, ct);
            Assert.Equal(updateStatus, response.StatusCode);

            // The pipe's own bytes: PipeClient.SendCommandAsync waits for a prompt on a line of its own, and the
            // server writes its prompt with no line end.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            await using var pipe = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000, limit.Token);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            Assert.StartsWith("Connected to the test store", await ReadUntil(reader, "test> ", limit.Token));
            await writer.WriteLineAsync(":store".AsMemory(), limit.Token);
            Assert.Contains(_store.Path, await ReadUntil(reader, "test> ", limit.Token));
        }
        finally
        {
            lifetime.StopApplication();
            await running.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
    }

    [Fact]
    public async Task WhenTheHostStops_TheStoreIsClosedAfterClosing_AndTheEndpointsAreGone()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = Build(enableUpdates: false);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        bool? storeOpenAtClosing = null;
        var running = StoreHost.RunAsync(host, _store, closing: () => storeOpenAtClosing = !LogHasStop());
        await WaitUntil(lifetime.ApplicationStarted, ct);

        lifetime.StopApplication();
        await running.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.True(storeOpenAtClosing);
        Assert.True(LogHasStop());
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync($"http://localhost:{_port}/sparql", ct));
        // The store is closed: it can be opened again.
        using var again = StoreSession.Open(_store.Path, "mercury-test", "9.9.9", StorageOptions.ForTesting);
    }

    private bool LogHasStop() =>
        File.ReadAllLines(_store.Log!.Path).Any(l => l.EndsWith($" mercury-test stops, process {Environment.ProcessId}", StringComparison.Ordinal));

    private static async Task<string> ReadUntil(StreamReader reader, string end, CancellationToken ct)
    {
        var text = new StringBuilder();
        var one = new char[1];
        while (!text.ToString().EndsWith(end, StringComparison.Ordinal))
        {
            if (await reader.ReadAsync(one.AsMemory(), ct) == 0)
                break;
            text.Append(one[0]);
        }
        return text.ToString();
    }

    private static async Task WaitUntil(CancellationToken signal, CancellationToken ct)
    {
        var started = new TaskCompletionSource();
        using var _ = signal.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
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
