// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Serving;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Hosting.Tests;

/// <summary>
/// mercuryd, run as its own process (ADR-065 Step 4): it serves one store over HTTP and its attach socket until it
/// is stopped; a stop signal closes the store and writes the stop to the log; a start that fails exits with 1; a second
/// process on the store is refused; a killed one is followed by a start that serves the store.
/// </summary>
/// <remarks>
/// Each test's store has a name of its own: on Windows the attach pipe is named by the store's name, one namespace for
/// the machine, and tests run beside each other. The stop signals are Linux's and macOS's; Windows stops a service
/// with its stop request (ADR-065 Step 9).
/// </remarks>
public sealed class MercurydTests : IDisposable
{
    private readonly TempPath _dir;
    private readonly string _store;
    /// <summary>
    /// The test's port: the one its mercuryd serves on, once <see cref="ServeTheStore"/> has started it; until then,
    /// one the system named, for a start that fails.
    /// </summary>
    private int _port = FreePort.Name();

    public MercurydTests()
    {
        _dir = TempPath.Test("mercuryd");
        _dir.MarkOwnership();
        _store = Path.Combine(_dir.FullPath, $"s{Guid.NewGuid():N}"[..9]);
    }

    public void Dispose() => _dir.Cleanup();

    /// <summary>mercuryd on the test's store, with <paramref name="more"/>, ready, on a free port, which is the test's
    /// port from then on.</summary>
    private async Task<Mercuryd> ServeTheStore(CancellationToken ct, params string[] more)
    {
        (var mercuryd, _port) = await Mercuryd.Serve(port => ["-d", _store, "-p", port.ToString(), .. more], ct);
        return mercuryd;
    }

    [Theory]
    [InlineData(false, HttpStatusCode.OK)]
    [InlineData(true, HttpStatusCode.Forbidden)]
    public async Task ItServesQueriesAndUpdatesOverHttp_AndNoUpdateWhenReadOnly(bool readOnly, HttpStatusCode updateStatus)
    {
        var ct = TestContext.Current.CancellationToken;
        using var mercuryd = readOnly ? await ServeTheStore(ct, "--read-only") : await ServeTheStore(ct);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var update = await http.PostAsync($"http://localhost:{_port}/sparql/update",
            new StringContent("INSERT DATA { <urn:s> <urn:p> \"served\" }", Encoding.UTF8, "application/sparql-update"), ct);
        var answer = await http.GetStringAsync(
            $"http://localhost:{_port}/sparql?query={Uri.EscapeDataString("SELECT ?o WHERE { ?s ?p ?o }")}", ct);

        Assert.Equal(updateStatus, update.StatusCode);
        Assert.Equal(!readOnly, answer.Contains("served"));
    }

    [Fact]
    public async Task ItListensOnEachAddressGiven()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(),
            "A listener on 127.0.0.1 needs a URL reservation or an administrator on Windows (ADR-065 Step 9).");
        var ct = TestContext.Current.CancellationToken;
        using var mercuryd = await ServeTheStore(ct, "--listen", "localhost", "--listen", "127.0.0.1");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"http://localhost:{_port}/sparql", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"http://127.0.0.1:{_port}/sparql", ct)).StatusCode);
    }

    [Fact]
    public async Task ItIsAttachedToOverTheStoresSocket()
    {
        var ct = TestContext.Current.CancellationToken;
        using var mercuryd = await ServeTheStore(ct);

        var (welcome, store) = await AttachAndAskTheStore(ct);

        Assert.StartsWith($"Connected to mercuryd (store: {_store})", welcome);
        Assert.Contains(_store, store);
    }

    [Theory]
    [InlineData(Mercuryd.Sigterm)]
    [InlineData(Mercuryd.Sigint)]
    public async Task AStopSignal_ClosesTheStore_WritesTheStopToTheLog_AndItExits0(int signal)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows stops a service with its stop request (ADR-065 Step 9).");
        Assert.SkipWhen(signal == Mercuryd.Sigint && Mercuryd.SigintIsIgnoredHere,
            "SIGINT is ignored in this test run, as in a command a non-interactive shell starts in the background, and " +
            "mercuryd inherits it ignored: SIGINT is not a stop signal for it then.");
        var ct = TestContext.Current.CancellationToken;
        using var mercuryd = await ServeTheStore(ct);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var update = await http.PostAsync($"http://localhost:{_port}/sparql/update",
            new StringContent("INSERT DATA { <urn:s> <urn:p> \"kept\" }", Encoding.UTF8, "application/sparql-update"), ct);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        mercuryd.Signal(signal);

        Assert.Equal(0, await mercuryd.Ended(ct));
        Assert.EndsWith($" INF mercuryd stops, process {mercuryd.Id}", LogLines()[^1]);
        Assert.EndsWith("mercuryd stopped.", mercuryd.Errors);
        using var reopened = StoreSession.Open(_store, "mercury-test", "9.9.9", StorageOptions.ForTesting);
        Assert.Equal(1, reopened.Pool.Active.GetStatistics().QuadCount);
    }

    /// <summary>
    /// The ways a start fails, each said on standard error with the part that failed: what is said, of the test's port.
    /// A port in use is said in Mercury's words, which are the same on every platform.
    /// </summary>
    public static TheoryData<string, string> FailedStarts => new()
    {
        { "the port is in use", "Error: The HTTP endpoint cannot listen on http://localhost:{0}/sparql: The port is in use." },
        { "the address is not the machine's", "Error: The HTTP endpoint cannot listen on http://192.0.2.1:{0}/sparql: " },
        { "a file is where the store goes", "Error: The store cannot be opened:" },
    };

    [Theory]
    [MemberData(nameof(FailedStarts))]
    public async Task AStartThatFails_SaysWhy_ClosesWhatItOpened_AndExits1(string failure, string said)
    {
        // TEST-NET-1 (RFC 5737): no machine has it. Windows' http.sys registers such an address without binding it.
        Assert.SkipWhen(failure == "the address is not the machine's" && OperatingSystem.IsWindows(),
            "http.sys registers an address that is not the machine's without binding it.");
        var ct = TestContext.Current.CancellationToken;
        // A socket holds the port from when the system names it, on the first address localhost resolves to: the one
        // the listener of Linux and macOS binds. On Windows a socket on any address holds the port from http.sys.
        using var holder = new TcpListener(Dns.GetHostAddresses("localhost")[0], 0);
        string[] args = failure switch
        {
            "the address is not the machine's" => ["--listen", "192.0.2.1"],
            _ => [],
        };
        if (failure == "the port is in use")
        {
            holder.Start();
            _port = ((IPEndPoint)holder.LocalEndpoint).Port;
        }
        if (failure == "a file is where the store goes")
            File.WriteAllText(_store, "not a store");

        using var mercuryd = Mercuryd.Start(["-d", _store, "-p", _port.ToString(), .. args]);

        Assert.Equal(1, await mercuryd.Ended(ct));
        Assert.Contains(string.Format(CultureInfo.InvariantCulture, said, _port), mercuryd.Errors);
        if (failure != "a file is where the store goes")
        {
            var log = LogLines();
            Assert.Contains(log, line => line.Contains(" ERR The store cannot be served: "));
            Assert.EndsWith($" INF mercuryd stops, process {mercuryd.Id}", log[^1]);
        }
    }

    [Fact]
    public async Task ASecondProcessOnTheStore_IsRefused_AndTheFirstServesOn()
    {
        var ct = TestContext.Current.CancellationToken;
        using var first = await ServeTheStore(ct);

        using var second = Mercuryd.Start("-d", _store, "-p", FreePort.Name().ToString());

        Assert.Equal(1, await second.Ended(ct));
        Assert.Contains("A store is served by one process.", second.Errors);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"http://localhost:{_port}/sparql", ct)).StatusCode);
        var (welcome, _) = await AttachAndAskTheStore(ct); // the second never reached the first's socket
        Assert.StartsWith("Connected to mercuryd", welcome);
    }

    [Fact]
    public async Task AfterAKill_ANewStartServesTheStore_WithWhatWasWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using (var killed = await ServeTheStore(ct))
        {
            var update = await http.PostAsync($"http://localhost:{_port}/sparql/update",
                new StringContent("INSERT DATA { <urn:s> <urn:p> \"before the kill\" }", Encoding.UTF8, "application/sparql-update"), ct);
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            killed.Kill();
            await killed.Ended(ct);
        }

        // On Linux and macOS the killed process left its socket file in the store's directory.
        using var next = await ServeTheStore(ct);

        var answer = await http.GetStringAsync(
            $"http://localhost:{_port}/sparql?query={Uri.EscapeDataString("SELECT ?o WHERE { ?s ?p ?o }")}", ct);
        Assert.Contains("before the kill", answer);
        var (welcome, _) = await AttachAndAskTheStore(ct);
        Assert.StartsWith("Connected to mercuryd", welcome);
    }

    [Fact]
    public async Task TheProfileApplies_WhenTheStoreIsMade_AndAStoreThatExistsKeepsItsOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var made = await ServeTheStore(ct, "--profile", "Graph"))
        {
            Assert.Contains("  Profile: Graph", made.Errors);
        }

        using var again = await ServeTheStore(ct, "--profile", "Reference");

        Assert.Contains("  Profile: Graph", again.Errors);
    }

    // The attach socket's own bytes, as the client's REPL reads them: the welcome up to the prompt, then what :store
    // answers up to the next prompt.
    private async Task<(string Welcome, string Store)> AttachAndAskTheStore(CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        await using var pipe = new NamedPipeClientStream(".", MercuryPorts.DaemonPipeName(_store), PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, limit.Token);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var welcome = await ReadUntil(reader, "mercuryd> ", limit.Token);
        await writer.WriteLineAsync(":store".AsMemory(), limit.Token);
        var store = await ReadUntil(reader, "mercuryd> ", limit.Token);
        await writer.WriteLineAsync(":detach".AsMemory(), limit.Token);
        return (welcome, store);
    }

    // Read beside a writer: a test may read while mercuryd holds the log open (Windows refuses a reader that does not
    // share writing).
    private string[] LogLines()
    {
        using var reader = new StreamReader(new FileStream(StoreSession.LogPathFor(_store), FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
}
