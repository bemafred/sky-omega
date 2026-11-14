// ADR-012 Phase 2: the transport, end-to-end over a real Unix-domain socket (no MCP host). Proves the
// Phase-2 headlines: snapshot-on-connect, live delta streaming, multiple decoupled consumers, and that a
// terminated view (a dropped client) never disturbs the server (the session-analog). macOS/arm64 + Linux.

using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SkyOmega.DrHook.Engine;
using SkyOmega.DrHook.Engine.Transport;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed class DebugStateServerTests
{
    private static DebugStateSnapshot Snapshot(ExecutionState execution) => new(
        CapturedAt: DateTimeOffset.UnixEpoch,
        Session: new SessionInfo(1234, OwnsTarget: true, RuntimeMajor: 10, IsDetached: false, IsDisposed: false, execution),
        Position: ExecutionPosition.None,
        Breakpoints: Array.Empty<BreakpointStatus>(),
        ExceptionFilters: Array.Empty<ExceptionFilterStatus>(),
        Console: new ConsoleDrainResult(Array.Empty<ConsoleOutputRecord>(), 0),
        Logs: new DrainResult(Array.Empty<LogRecord>(), 0),
        Anomalies: new AnomalyDrainResult(Array.Empty<EngineAnomaly>(), 0));

    private static string TempSocketPath() => $"/tmp/dh-{Guid.NewGuid():N}.sock"; // short — UDS sun_path is ~104 chars

    private static Socket Connect(string path)
    {
        var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { ReceiveTimeout = 5000 };
        client.Connect(new UnixDomainSocketEndPoint(path));
        return client;
    }

    private static JsonDocument ReadMessage(Socket s)
    {
        var sb = new StringBuilder();
        byte[] one = new byte[1];
        while (true)
        {
            int n = s.Receive(one, 0, 1, SocketFlags.None);
            if (n <= 0) break;
            if (one[0] == (byte)'\n') break;
            sb.Append((char)one[0]); // wire content is ASCII JSON in these tests
        }
        return JsonDocument.Parse(sb.ToString());
    }

    [Fact]
    public void OnConnect_SendsSnapshot_ThenStreamsDeltas()
    {
        string sock = TempSocketPath();
        using var server = new DebugStateServer(sock);
        server.Start();
        server.PublishSnapshot(Snapshot(ExecutionState.Stopped));

        using Socket client = Connect(sock);

        using (JsonDocument first = ReadMessage(client)) // snapshot-on-connect
            Assert.Equal("snapshot", first.RootElement.GetProperty("type").GetString());

        server.OnEvent("Breakpoint"); // a live delta
        using (JsonDocument second = ReadMessage(client))
        {
            Assert.Equal("delta", second.RootElement.GetProperty("type").GetString());
            Assert.Equal("Breakpoint", second.RootElement.GetProperty("delta").GetProperty("event").GetString());
        }
    }

    [Fact]
    public void MultipleConsumers_BothReceiveTheSameStream()
    {
        string sock = TempSocketPath();
        using var server = new DebugStateServer(sock);
        server.Start();
        server.PublishSnapshot(Snapshot(ExecutionState.Stopped));

        using Socket a = Connect(sock);
        using Socket b = Connect(sock);
        using (var _ = ReadMessage(a)) { }  // each drains its connect-time snapshot
        using (var _ = ReadMessage(b)) { }

        server.OnLog(new LogRecord(DateTimeOffset.UnixEpoch, "shared"));

        using JsonDocument da = ReadMessage(a);
        using JsonDocument db = ReadMessage(b);
        Assert.Equal("shared", da.RootElement.GetProperty("delta").GetProperty("logMessage").GetString());
        Assert.Equal("shared", db.RootElement.GetProperty("delta").GetProperty("logMessage").GetString());
    }

    [Fact]
    public void TerminatedView_IsDropped_OtherConsumerAndServerUnaffected()
    {
        string sock = TempSocketPath();
        using var server = new DebugStateServer(sock);
        server.Start();
        server.PublishSnapshot(Snapshot(ExecutionState.Stopped));

        Socket doomed = Connect(sock);
        using Socket survivor = Connect(sock);
        using (var _ = ReadMessage(doomed)) { }
        using (var _ = ReadMessage(survivor)) { }

        // "Killing a view never affects the session": abruptly drop the doomed client.
        doomed.Close();

        // Publish until the server notices the broken pipe and drops it (first write after peer-close can
        // still succeed in the socket buffer; the next fails). The survivor keeps receiving throughout.
        for (int i = 0; i < 20 && server.ClientCount > 1; i++)
        {
            server.OnEvent($"e{i}");
            using JsonDocument d = ReadMessage(survivor); // survivor stays live
            Assert.Equal("delta", d.RootElement.GetProperty("type").GetString());
            Thread.Sleep(10);
        }

        Assert.Equal(1, server.ClientCount); // doomed dropped; survivor remains; server never threw
    }

    [Fact]
    public void MaxClients_RefusesExcessConnections()
    {
        string sock = TempSocketPath();
        using var server = new DebugStateServer(sock, maxClients: 1);
        server.Start();
        server.PublishSnapshot(Snapshot(ExecutionState.Stopped));

        using Socket first = Connect(sock);
        using (var _ = ReadMessage(first)) { } // accepted; gets the snapshot

        using Socket second = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { ReceiveTimeout = 3000 };
        second.Connect(new UnixDomainSocketEndPoint(sock)); // listen backlog accepts the TCP-level connect...
        // ...but the server refuses past the cap and closes it: a read returns EOF (0 bytes), not a message.
        byte[] buf = new byte[16];
        int n = second.Receive(buf);
        Assert.Equal(0, n); // closed by the server with no data — over the cap

        Assert.Equal(1, server.ClientCount);
    }

    [Fact]
    public void Stop_IsCleanAndIdempotent_UnlinksTheSocketFile()
    {
        string sock = TempSocketPath();
        var server = new DebugStateServer(sock);
        server.Start();
        Assert.True(server.IsRunning);
        Assert.True(File.Exists(sock));

        server.Stop();
        Assert.False(server.IsRunning);
        Assert.False(File.Exists(sock)); // unlinked on stop
        server.Stop(); // idempotent — no throw
        server.Dispose();
    }

    [Fact]
    public void Restart_StartStopStart_AcceptsConnectionsAgain()
    {
        string sock = TempSocketPath();
        using var server = new DebugStateServer(sock);
        server.Start();
        server.Stop();
        Assert.False(server.IsRunning);

        // Restart the SAME server object — the per-session lifecycle the MCP host uses. Stop cancels the
        // shutdown token; Start must recreate it, else the accept loop would exit immediately on restart.
        server.Start();
        Assert.True(server.IsRunning);
        server.PublishSnapshot(Snapshot(ExecutionState.Stopped));

        using Socket client = Connect(sock);
        using JsonDocument first = ReadMessage(client);
        Assert.Equal("snapshot", first.RootElement.GetProperty("type").GetString()); // accepts + serves after restart

        server.Stop();
    }

    // ─── Replay on connect: a view is shown what the session emitted before it connected ───────────────

    private static string Type(JsonDocument m) => m.RootElement.GetProperty("type").GetString()!;
    private static long Seq(JsonDocument m) => m.RootElement.GetProperty("seq").GetInt64();
    private static string? Field(JsonDocument m, string name) => m.RootElement.GetProperty("delta").GetProperty(name).GetString();

    [Fact]
    public void OnConnect_ReplaysWhatTheSessionEmittedBefore_ThenTheSnapshot_ThenStaysLive()
    {
        string sock = TempSocketPath();
        using var server = new DebugStateServer(sock);
        server.Start();
        server.OnHypothesis(new HypothesisRecord(DateTimeOffset.UnixEpoch, "the loop runs three times", HypothesisLens.Navigation));
        server.OnConsoleOutput(new ConsoleOutputRecord(DateTimeOffset.UnixEpoch, ConsoleStream.Stdout, "first line"));
        server.PublishSnapshot(Snapshot(ExecutionState.Running)); // superseded: only the latest snapshot is sent
        server.OnEvent("Breakpoint");
        server.PublishSnapshot(Snapshot(ExecutionState.Stopped));

        using Socket client = Connect(sock); // the session is well under way

        long last = 0;
        using (JsonDocument m = ReadMessage(client))
        {
            Assert.Equal("the loop runs three times", Field(m, "hypothesisText"));
            last = Seq(m);
        }
        using (JsonDocument m = ReadMessage(client))
        {
            Assert.Equal("first line", Field(m, "consoleText"));
            Assert.True(Seq(m) > last); last = Seq(m);
        }
        using (JsonDocument m = ReadMessage(client))
        {
            Assert.Equal("Breakpoint", Field(m, "event"));
            Assert.True(Seq(m) > last); last = Seq(m);
        }
        using (JsonDocument m = ReadMessage(client)) // then the current state, once
        {
            Assert.Equal("snapshot", Type(m));
            Assert.Equal("Stopped", m.RootElement.GetProperty("snapshot").GetProperty("session").GetProperty("execution").GetString());
            Assert.True(Seq(m) > last); last = Seq(m);
        }

        server.OnEvent("StepComplete"); // and nothing from before the connect arrives a second time
        using (JsonDocument m = ReadMessage(client))
        {
            Assert.Equal("StepComplete", Field(m, "event"));
            Assert.True(Seq(m) > last);
        }
    }

    [Fact]
    public void OnConnect_BeforeTheFirstSnapshot_ReplaysTheDeltasAlone()
    {
        string sock = TempSocketPath();
        using var server = new DebugStateServer(sock);
        server.Start();
        server.OnConsoleOutput(new ConsoleOutputRecord(DateTimeOffset.UnixEpoch, ConsoleStream.Stdout, "starting"));

        using Socket client = Connect(sock);
        using (JsonDocument m = ReadMessage(client)) Assert.Equal("starting", Field(m, "consoleText"));

        server.PublishSnapshot(Snapshot(ExecutionState.Stopped)); // the first stop arrives live
        using (JsonDocument m = ReadMessage(client)) Assert.Equal("snapshot", Type(m));
    }

    [Fact]
    public void ReplayCapacity_KeepsTheNewest_AndCountsWhatFellOff()
    {
        string sock = TempSocketPath();
        using var server = new DebugStateServer(sock, replayCapacity: 2);
        server.Start();
        for (int i = 0; i < 5; i++) server.OnEvent($"e{i}");
        Assert.Equal(3, server.ReplayDropped);

        using Socket client = Connect(sock);
        using (JsonDocument m = ReadMessage(client)) Assert.Equal("e3", Field(m, "event"));
        using (JsonDocument m = ReadMessage(client)) Assert.Equal("e4", Field(m, "event"));

        server.OnEvent("live");
        using (JsonDocument m = ReadMessage(client)) Assert.Equal("live", Field(m, "event"));
    }

    [Fact]
    public void ReplayCapacity_Zero_SendsTheSnapshotOnly()
    {
        string sock = TempSocketPath();
        using var server = new DebugStateServer(sock, replayCapacity: 0);
        server.Start();
        server.OnEvent("before");
        server.PublishSnapshot(Snapshot(ExecutionState.Stopped));

        using Socket client = Connect(sock);
        using (JsonDocument m = ReadMessage(client)) Assert.Equal("snapshot", Type(m));

        server.OnEvent("live");
        using (JsonDocument m = ReadMessage(client)) Assert.Equal("live", Field(m, "event"));

        Assert.Throws<ArgumentOutOfRangeException>(() => new DebugStateServer(sock, replayCapacity: -1));
    }

    [Fact]
    public void Restart_DoesNotReplayThePreviousSession()
    {
        string sock = TempSocketPath();
        using var server = new DebugStateServer(sock);
        server.Start();
        server.OnEvent("from the first session");
        server.PublishSnapshot(Snapshot(ExecutionState.Stopped));
        server.Stop();
        server.OnEvent("after it ended"); // a late event from the session that has ended is not kept either

        server.Start();
        using Socket client = Connect(sock);
        server.OnEvent("from the second session");
        // Whether the event was emitted before or after the join, it is the first and only thing the view is sent.
        using (JsonDocument m = ReadMessage(client)) Assert.Equal("from the second session", Field(m, "event"));
    }

    // A view as the real client is: it reads through a buffer, on a thread of its own. ReadMessage above takes
    // one byte per call, which is fine for a handful of messages and far too slow for thousands of them.
    private sealed class BufferedView : IDisposable
    {
        private readonly Socket _socket;
        private readonly StreamReader _reader;

        public BufferedView(string path, int receiveTimeoutMs = 30_000)
        {
            _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { ReceiveTimeout = receiveTimeoutMs };
            _socket.Connect(new UnixDomainSocketEndPoint(path));
            _reader = new StreamReader(new NetworkStream(_socket, ownsSocket: false), Encoding.UTF8);
        }

        /// <summary>The next message, or null when the server closed the connection.</summary>
        public JsonDocument? Read() => _reader.ReadLine() is { } line ? JsonDocument.Parse(line) : null;

        public void Dispose() { _reader.Dispose(); _socket.Dispose(); }
    }

    private static string Number(int i) => i.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task ConnectingWhileTheSessionEmits_EveryDeltaArrivesOnce_InOrder()
    {
        // The join has to hand over from the replayed history to the live stream without a gap and without a
        // repeat, while the publisher is busy. A writer emits numbered events throughout; views connect at
        // arbitrary moments; each must be sent 0, 1, 2, … with nothing missing and nothing twice.
        // The timeouts are long on purpose: this test is about order and completeness, and a loaded runner
        // must not turn it into a test of speed (the bound on a slow view has its own test below).
        const int total = 4000;
        string sock = TempSocketPath();
        using var server = new DebugStateServer(sock, queueCapacity: total * 2, sendTimeoutMs: 30_000, replayCapacity: total * 2);
        server.Start();

        var writer = new Thread(() =>
        {
            for (int i = 0; i < total; i++)
            {
                server.OnEvent(Number(i));
                if (i % 200 == 0) server.PublishSnapshot(Snapshot(ExecutionState.Running));
                if (i % 50 == 0) Thread.Sleep(1);
            }
        }) { IsBackground = true };
        writer.Start();

        var readers = new List<Task>();
        for (int c = 0; c < 4; c++)
        {
            Thread.Sleep(15); // each view joins at a different point of the stream
            var view = new BufferedView(sock);
            readers.Add(Task.Factory.StartNew(() =>
            {
                using (view)
                {
                    int expected = 0;
                    long lastSeq = 0;
                    while (expected < total)
                    {
                        using JsonDocument? m = view.Read();
                        Assert.True(m is not null, $"the server closed the connection at event {expected}");
                        Assert.True(Seq(m) > lastSeq, $"seq went from {lastSeq} to {Seq(m)}");
                        lastSeq = Seq(m);
                        if (Type(m) != "delta") continue;
                        Assert.Equal(Number(expected), Field(m, "event"));
                        expected++;
                    }
                }
            }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default));
        }

        // A view that is not sent the whole stream fails here: its read times out, or this wait does.
        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        writer.Join();
        Assert.Equal(0, server.DroppedMessages);
        Assert.Equal(0, server.ReplayDropped);
        Assert.Equal(0, server.WorkerErrors);
    }

    [Fact]
    public void AViewThatTakesTheHistoryTooSlowly_IsDropped_AndTheNextViewIsServed()
    {
        // The other views wait while one view is sent the history, so the history has to be taken within the
        // send timeout. This view keeps reading, a little at a time: no single send to it times out, and it
        // would need half a minute for the whole history. It is dropped at the bound instead.
        const int count = 6000;
        string pad = new('x', 80);
        string sock = TempSocketPath();
        using var server = new DebugStateServer(sock, sendTimeoutMs: 2000, replayCapacity: count);
        server.Start();
        for (int i = 0; i < count; i++) server.OnEvent($"{Number(i)}-{pad}"); // about 900 kB on the wire

        using (Socket slow = Connect(sock))
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            byte[] buf = new byte[512];
            long taken = 0;
            bool closed = false;
            while (clock.Elapsed < TimeSpan.FromSeconds(15))
            {
                int n;
                try { n = slow.Receive(buf); } catch (SocketException) { closed = true; break; }
                if (n == 0) { closed = true; break; }
                taken += n;
                Thread.Sleep(20); // at most 25 kB a second
            }
            Assert.True(closed, "the slow view was still being served after 15 s");
            Assert.True(taken < 600_000, $"the slow view was sent {taken} bytes, most of the history");
        }
        Assert.Equal(0, server.ClientCount); // it was never registered

        using var view = new BufferedView(sock);
        for (int i = 0; i < count; i++)
        {
            using JsonDocument? m = view.Read();
            Assert.True(m is not null, $"the server closed the connection at event {i}");
            Assert.StartsWith(Number(i) + "-", Field(m, "event"));
        }
        server.OnEvent("live");
        using (JsonDocument? m = view.Read()) Assert.Equal("live", Field(m!, "event"));
        Assert.Equal(1, server.ClientCount);
    }
}
