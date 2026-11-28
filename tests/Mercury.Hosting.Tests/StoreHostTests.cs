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
    private readonly string _pipe = $"mh-{Guid.NewGuid():N}"[..16];
    private StoreSession? _store;
    private int _port;

    public StoreHostTests()
    {
        _dir = TempPath.Test("store-host");
        _dir.MarkOwnership();
    }

    public void Dispose()
    {
        _store?.Dispose();
        _dir.Cleanup();
    }

    /// <summary>
    /// A store of its own, its host on a free port once it has started, and the host's run
    /// (<see cref="StoreHost.RunAsync"/>, with <paramref name="closing"/> of the store); the port is the test's. When
    /// the run ends saying that the port is in use, which another took after the system named it, a host is run again
    /// on another port (<see cref="FreePort"/>), over a store of its own again: the run closed the store, and wrote its
    /// stop to the store's log.
    /// </summary>
    private async Task<Served> Run(bool enableUpdates, Action<StoreSession>? closing, CancellationToken ct)
    {
        (var served, _port) = await FreePort.Start(port => RunOn(port, enableUpdates, closing, ct));
        _store = served.Store;
        return served;
    }

    /// <summary>
    /// A store of its own and its host on <paramref name="port"/> once it has started, and the host's run; or null
    /// when the run ended saying that the port is in use. Fails, with the store's log, when the run ended for another
    /// reason.
    /// </summary>
    private async Task<Served?> RunOn(int port, bool enableUpdates, Action<StoreSession>? closing, CancellationToken ct)
    {
        var store = StoreSession.Open(Path.Combine(_dir.FullPath, $"store-{port}"), "mercury-test", "9.9.9",
            StorageOptions.ForTesting);
        var host = StoreHost.CreateBuilder(store)
            .AddStoreEndpoints(new StoreEndpointOptions
            {
                Hosts = ["localhost"],
                Port = port,
                EnableUpdates = enableUpdates,
                PipeName = _pipe,
                PipeWelcome = "Connected to the test store",
                PipePrompt = "test> ",
            })
            .Build();
        // A start that fails can end the run, which disposes the host, before RunAsync returns (on zbook it did).
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var running = StoreHost.RunAsync(host, store, () => closing?.Invoke(store));
        var started = WaitUntil(lifetime.ApplicationStarted, ct);
        if (await Task.WhenAny(started, running) == started)
        {
            await started;
            return new Served(store, host, running);
        }
        var exitCode = await running;
        var log = ReadLog(store);
        if (!log.Contains($"The HTTP endpoint cannot listen on http://localhost:{port}/sparql: The port is in use."))
            Assert.Fail($"The host ended with {exitCode} before it started:{Environment.NewLine}{log}");
        return null;
    }

    /// <summary>A store, its host, which has started, and the host's run.</summary>
    private sealed record Served(StoreSession Store, IHost Host, Task<int> Running);

    [Fact]
    public async Task AHostOnAPortInUse_EndsSayingSo_AndIsGivenAsAStartToMakeAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        // A socket holds the port from when the system names it, on the first address localhost resolves to: the one
        // the listener of Linux and macOS binds. On Windows a socket on any address holds the port from http.sys.
        using var holder = new TcpListener(Dns.GetHostAddresses("localhost")[0], 0);
        holder.Start();

        Assert.Null(await RunOn(((IPEndPoint)holder.LocalEndpoint).Port, enableUpdates: false, closing: null, ct));
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Forbidden)]
    [InlineData(true, HttpStatusCode.OK)]
    public async Task WhileTheHostRuns_TheStoreIsServedOverHttpAndThePipe(bool enableUpdates, HttpStatusCode updateStatus)
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, host, running) = await Run(enableUpdates, closing: null, ct);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        try
        {
            Assert.Same(store.Pool, host.Services.GetRequiredService<QuadStorePool>());
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
            Assert.Contains(store.Path, await ReadUntil(reader, "test> ", limit.Token));
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
        bool? storeOpenAtClosing = null;
        var (store, host, running) = await Run(enableUpdates: false,
            closing: closed => storeOpenAtClosing = !LogHasStop(closed), ct);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var mark = $"store-host-{Guid.NewGuid():N}";
        store.Pool.Active.AddCurrent("<urn:s>", "<urn:p>", $"\"{mark}\"");

        lifetime.StopApplication();
        await running.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.True(storeOpenAtClosing);
        Assert.True(LogHasStop(store));
        // Windows tries a refused connection again before it gives up: about 2.2 s for each of localhost's two addresses,
        // 4.4 to 4.7 s on an idle machine (measured on omega-windows, 2026-11-21). A timeout of 5 s ended the request
        // before the refusal on a loaded Windows runner (CI 44918126706); this one leaves the refusal time to come.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // The port is the machine's, and once the host has let it go the system can name it to another test, whose
        // mercuryd then answers on it. So the endpoint is gone when a request is refused, or answered by an endpoint
        // without this store's mark. This host's endpoint, still answering, would give the mark, or with its store
        // closed an error.
        try
        {
            using var answer = await http.GetAsync(
                $"http://localhost:{_port}/sparql?query={Uri.EscapeDataString("SELECT ?o WHERE { ?s ?p ?o }")}", ct);
            var body = await answer.Content.ReadAsStringAsync(ct);
            Assert.True(answer.StatusCode == HttpStatusCode.OK && !body.Contains(mark),
                $"After the stop, the port answered {(int)answer.StatusCode}:{Environment.NewLine}{body}");
        }
        catch (HttpRequestException)
        {
            // Refused: nothing listens on the port.
        }
        // The store is closed: it can be opened again.
        using var again = StoreSession.Open(store.Path, "mercury-test", "9.9.9", StorageOptions.ForTesting);
    }

    private static bool LogHasStop(StoreSession store) => ReadLog(store).Split('\n').Any(line =>
        line.TrimEnd('\r').EndsWith($" mercury-test stops, process {Environment.ProcessId}", StringComparison.Ordinal));

    // Read as a reader beside the log's writer: while the store is open its logger holds the file open for writing, and
    // Windows refuses a reader that does not share writing (File.ReadAllLines does not; CI run 44810648562).
    private static string ReadLog(StoreSession store)
    {
        using var reader = new StreamReader(
            new FileStream(StoreSession.LogPathFor(store.Path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        return reader.ReadToEnd();
    }

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
}
