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
        await view.GotSnapshot.WaitAsync(TimeSpan.FromSeconds(10));
        server.OnEvent("Breakpoint");
        await view.GotDelta.WaitAsync(TimeSpan.FromSeconds(10));

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
}
