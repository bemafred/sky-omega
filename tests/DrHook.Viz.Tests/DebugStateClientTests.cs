// DrHook.Viz — the shared client, end-to-end over a real Unix-domain socket against the engine's
// DebugStateServer: connect -> snapshot-on-connect -> live deltas -> model updates -> clean disconnect.
// This is the server->wire->client round-trip the views depend on.

using SkyOmega.DrHook.Engine;
using SkyOmega.DrHook.Engine.Transport;
using SkyOmega.DrHook.Viz;
using SkyOmega.DrHook.Wire;
using Xunit;

namespace SkyOmega.DrHook.Viz.Tests;

public sealed class DebugStateClientTests
{
    private sealed class RecordingView : IDebugStateView
    {
        private readonly TaskCompletionSource _gotSnapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gotDelta = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<WireSnapshot> _snapshots = new();
        private readonly List<WireDelta> _deltas = new();

        public string? Endpoint { get; private set; }
        public string? DisconnectReason { get; private set; }
        public Task GotSnapshot => _gotSnapshot.Task;
        public Task GotDelta => _gotDelta.Task;
        public WireSnapshot[] Snapshots { get { lock (_snapshots) return _snapshots.ToArray(); } }
        public WireDelta[] Deltas { get { lock (_deltas) return _deltas.ToArray(); } }

        public void OnConnected(string endpoint) => Endpoint = endpoint;
        public void OnSnapshot(WireSnapshot s, DebugStateClientModel m) { lock (_snapshots) _snapshots.Add(s); _gotSnapshot.TrySetResult(); }
        public void OnDelta(WireDelta d, DebugStateClientModel m) { lock (_deltas) _deltas.Add(d); _gotDelta.TrySetResult(); }
        public void OnDisconnected(string? reason) => DisconnectReason = reason;
    }

    private static DebugStateSnapshot SampleSnapshot() => new(
        DateTimeOffset.UnixEpoch, new SessionInfo(1234, OwnsTarget: true, RuntimeMajor: 10, IsDetached: false, IsDisposed: false, ExecutionState.Stopped),
        ExecutionPosition.None, Array.Empty<BreakpointStatus>(), Array.Empty<ExceptionFilterStatus>(),
        new ConsoleDrainResult(Array.Empty<ConsoleOutputRecord>(), 0), new DrainResult(Array.Empty<LogRecord>(), 0),
        new AnomalyDrainResult(Array.Empty<EngineAnomaly>(), 0));

    private static string TempSocketPath() => $"/tmp/dh-viz-{Guid.NewGuid():N}.sock";

    [Fact]
    public async Task Client_ConnectsToServer_ReceivesSnapshotThenDeltas_AndUpdatesModel()
    {
        string sock = TempSocketPath();
        using var server = new DebugStateServer(sock);
        server.Start();
        server.PublishSnapshot(SampleSnapshot());

        var view = new RecordingView();
        var client = new DebugStateClient(new DebugStateClientOptions { SocketPath = sock });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task run = client.RunAsync(view, cts.Token);

        // wait until the client connected + received the snapshot-on-connect, THEN publish a live delta
        await view.GotSnapshot.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        server.OnEvent("Breakpoint");
        await view.GotDelta.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(sock, view.Endpoint);
        WireSnapshot snap = Assert.Single(view.Snapshots);
        Assert.Equal(1234, snap.Session.Pid);
        Assert.Equal("Stopped", snap.Session.Execution);
        Assert.Contains(view.Deltas, d => d.Kind == "event" && d.Event == "Breakpoint");

        // the client-side model reflects the same state a richer view would render
        Assert.NotNull(client.Model.Snapshot);
        Assert.Equal(1234, client.Model.Snapshot!.Session.Pid);
        Assert.True(client.Model.DeltaCount >= 1);

        cts.Cancel();
        await run; // returns after cancellation/disconnect
        Assert.NotNull(view.DisconnectReason);
    }

    [Fact]
    public async Task Client_NoServer_DisconnectsCleanly_WithoutThrowing()
    {
        var view = new RecordingView();
        var client = new DebugStateClient(new DebugStateClientOptions { SocketPath = TempSocketPath() }); // nothing listening

        await client.RunAsync(view, CancellationToken.None); // a failed connect surfaces as OnDisconnected, not a throw

        Assert.Null(view.Endpoint);              // never connected
        Assert.Empty(view.Snapshots);
        Assert.NotNull(view.DisconnectReason);   // a clean reason
    }

    // --- reconnect (DebugStateClientOptions.Reconnect): a view that outlives a session ---

    private sealed class CountingView : IDebugStateView
    {
        private readonly object _lock = new();
        private readonly List<string> _calls = new();
        private readonly List<int> _pids = new();

        public string[] Calls { get { lock (_lock) return _calls.ToArray(); } }
        public int[] SnapshotPids { get { lock (_lock) return _pids.ToArray(); } }
        public int Count(string call) { lock (_lock) return _calls.Count(c => c == call); }

        public void OnConnected(string endpoint) { lock (_lock) _calls.Add("connected"); }
        public void OnSnapshot(WireSnapshot s, DebugStateClientModel m) { lock (_lock) { _calls.Add("snapshot"); _pids.Add(s.Session.Pid); } }
        public void OnDelta(WireDelta d, DebugStateClientModel m) { lock (_lock) _calls.Add("delta"); }
        public void OnDisconnected(string? reason) { lock (_lock) _calls.Add("disconnected"); }
    }

    private static DebugStateSnapshot SnapshotOf(int pid) => SampleSnapshot() with
    {
        Session = new SessionInfo(pid, OwnsTarget: true, RuntimeMajor: 10, IsDetached: false, IsDisposed: false, ExecutionState.Stopped),
    };

    private static async Task Until(Func<bool> condition, string what)
    {
        var limit = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < limit, $"timed out waiting for: {what}");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private static DebugStateClientOptions Reconnecting(string sock) =>
        new() { SocketPath = sock, Reconnect = true, ReconnectDelay = TimeSpan.FromMilliseconds(50) };

    [Fact]
    public async Task Reconnect_NoSessionYet_TellsTheViewOnce_ThenConnectsWhenASessionStarts()
    {
        string sock = TempSocketPath();
        var view = new CountingView();
        var client = new DebugStateClient(Reconnecting(sock));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task run = client.RunAsync(view, cts.Token);

        await Until(() => view.Count("disconnected") == 1, "the view is told no session is up");
        await Task.Delay(300, TestContext.Current.CancellationToken); // six retries at 50 ms
        Assert.Equal(new[] { "disconnected" }, view.Calls);            // told once, not once per retry
        Assert.False(run.IsCompleted);                                 // still waiting

        using var server = new DebugStateServer(sock);
        server.Start();
        server.PublishSnapshot(SnapshotOf(111));
        await Until(() => view.SnapshotPids.Contains(111), "the snapshot of the session that started later");
        Assert.Equal(new[] { "disconnected", "connected", "snapshot" }, view.Calls);

        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Reconnect_SessionEnds_KeepsItsState_ThenShowsTheNextSessionFromAResetModel()
    {
        string sock = TempSocketPath();
        var view = new CountingView();
        var client = new DebugStateClient(Reconnecting(sock));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task run;

        using (var first = new DebugStateServer(sock))
        {
            first.Start();
            first.PublishSnapshot(SnapshotOf(111));
            run = client.RunAsync(view, cts.Token);
            await Until(() => view.SnapshotPids.Contains(111), "the first session's snapshot");
            first.OnEvent("Breakpoint");
            first.OnEvent("StepComplete");
            await Until(() => view.Count("delta") == 2, "the first session's deltas");
        } // the first session ends: its server closes the connection and unlinks the socket

        await Until(() => view.Count("disconnected") == 1, "the view is told the session ended");
        Assert.Equal(111, client.Model.Snapshot!.Session.Pid); // the ended session stays readable while waiting
        Assert.Equal(2, client.Model.DeltaCount);

        using var second = new DebugStateServer(sock);
        second.Start();
        second.PublishSnapshot(SnapshotOf(222));
        await Until(() => view.SnapshotPids.Contains(222), "the second session's snapshot");

        Assert.Equal(2, view.Count("connected"));
        Assert.Equal(222, client.Model.Snapshot!.Session.Pid);
        Assert.Equal(0, client.Model.DeltaCount);              // none of the first session's deltas carried over
        Assert.False(run.IsCompleted);

        cts.Cancel();
        await run;
    }

    [Fact]
    public void Client_RefusesANonPositiveReconnectDelay()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new DebugStateClient(new DebugStateClientOptions { ReconnectDelay = TimeSpan.Zero }));
}
