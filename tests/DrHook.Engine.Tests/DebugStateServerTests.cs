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
}
