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
    private DebugStateClientModel? _model;
    private string _status = "connecting…";

    public DashboardDebugStateView(AnsiTerminal terminal, Func<(int Width, int Height)> size)
    {
        _terminal = terminal;
        _size = size;
    }

    public void OnConnected(string endpoint) => SetStatus("connected");

    public void OnSnapshot(WireSnapshot snapshot, DebugStateClientModel model) { lock (_lock) _model = model; _dirty.Set(); }

    public void OnDelta(WireDelta delta, DebugStateClientModel model) { lock (_lock) _model = model; _dirty.Set(); }

    // The quit hint leads: the title clips from the right, so it may cut the reason but never the instruction.
    public void OnDisconnected(string? reason) => SetStatus($"disconnected (q to quit): {reason}");

    private void SetStatus(string status) { lock (_lock) _status = status; _dirty.Set(); }

    /// <summary>Compose + present frames until <paramref name="ct"/> is cancelled.</summary>
    public void RunRenderLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            DebugStateClientModel? model;
            string status;
            lock (_lock) { model = _model; status = _status; }
            (int width, int height) = _size();
            _terminal.Present(DashboardScreen.Compose(model?.Snapshot, model?.DeltaTail() ?? [], model?.LastSeq ?? 0,
                status, width, height, _source));

            if (ct.WaitHandle.WaitOne(MinFrameInterval)) break;               // frame cap
            WaitHandle.WaitAny([_dirty, ct.WaitHandle], IdleRefresh);          // next change, or a resize check
        }
    }
}
