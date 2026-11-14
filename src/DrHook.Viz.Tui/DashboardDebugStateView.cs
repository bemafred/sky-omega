using SkyOmega.DrHook.Viz;
using SkyOmega.DrHook.Wire;

namespace SkyOmega.DrHook.Viz.Tui;

/// <summary>The TUI dashboard as an <see cref="IDebugStateView"/> (ADR-012 Phase 4). The client's read loop only marks
/// the picture dirty; <see cref="RunRenderLoop"/> — on its own thread — composes and presents it, at most one frame
/// per <see cref="MinFrameInterval"/> (a flood of deltas coalesces into one redraw instead of hogging the CPU), and at
/// least every <see cref="IdleRefresh"/> (so a terminal resize is picked up without an event).</summary>
public sealed class DashboardDebugStateView : IDebugStateView
{
    private static readonly TimeSpan MinFrameInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan IdleRefresh = TimeSpan.FromMilliseconds(500);

    private readonly AnsiTerminal _terminal;
    private readonly Func<(int Width, int Height)> _size;
    private readonly SourceWindowReader _source = new(new SourceWindowOptions { ContextLines = DashboardScreen.SourceContextLines });
    private readonly AutoResetEvent _dirty = new(false);
    private readonly object _lock = new();
    private readonly bool _reconnects;
    private DebugStateClientModel? _model;
    private string _status = "connecting…";
    private bool _connected;
    private bool _connectedOnce;

    /// <param name="reconnects">True when the client waits for the next session after one ends
    /// (<see cref="DebugStateClientOptions.Reconnect"/>): a disconnect is then shown as waiting, not as the end.</param>
    public DashboardDebugStateView(AnsiTerminal terminal, Func<(int Width, int Height)> size, bool reconnects = false)
    {
        _terminal = terminal;
        _size = size;
        _reconnects = reconnects;
    }

    public void OnConnected(string endpoint) { _connectedOnce = true; SetStatus("connected", connected: true); }

    public void OnSnapshot(WireSnapshot snapshot, DebugStateClientModel model) { lock (_lock) _model = model; _dirty.Set(); }

    public void OnDelta(WireDelta delta, DebugStateClientModel model) { lock (_lock) _model = model; _dirty.Set(); }

    // The quit hint leads: the title clips from the right, so it may cut the reason but never the instruction.
    public void OnDisconnected(string? reason) => SetStatus(DisconnectedStatus(reason, _reconnects, _connectedOnce), connected: false);

    /// <summary>The title-bar status for a disconnect. A view whose client reconnects is waiting, and says whether
    /// a session has ended or none has been seen yet; the panes keep the ended session's last state.</summary>
    internal static string DisconnectedStatus(string? reason, bool reconnects, bool connectedOnce) =>
        !reconnects ? $"disconnected (q to quit): {reason}"
        : connectedOnce ? $"session ended, waiting for the next (q to quit): {reason}"
        : $"waiting for a session (q to quit): {reason}";

    private void SetStatus(string status, bool connected) { lock (_lock) { _status = status; _connected = connected; } _dirty.Set(); }

    /// <summary>Compose + present frames until <paramref name="ct"/> is cancelled.</summary>
    public void RunRenderLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            DebugStateClientModel? model;
            string status;
            bool connected;
            lock (_lock) { model = _model; status = _status; connected = _connected; }
            (int width, int height) = _size();
            _terminal.Present(DashboardScreen.Compose(model?.Snapshot, model?.DeltaTail() ?? [], model?.LastSeq ?? 0,
                status, width, height, _source, connected));

            if (ct.WaitHandle.WaitOne(MinFrameInterval)) break;               // frame cap
            WaitHandle.WaitAny([_dirty, ct.WaitHandle], IdleRefresh);          // next change, or a resize check
        }
    }
}
