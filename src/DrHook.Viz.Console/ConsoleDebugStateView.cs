using SkyOmega.DrHook.Viz;
using SkyOmega.DrHook.Wire;

namespace SkyOmega.DrHook.Viz.ConsoleView;

/// <summary>The console visualizer — renders the debug-state stream as readable text (a "tail" of the session):
/// a compact block per snapshot, one line per delta. The simplest <see cref="IDebugStateView"/>; the TUI and
/// Avalonia views are richer renderings of the SAME client + model. Writes to an injected <see cref="TextWriter"/>
/// (testable; the shim passes <c>Console.Out</c>) — mirrors the Mercury tool pattern. The snapshot/delta RENDERING
/// lives in the shared <see cref="DebugStateTextRenderer"/>, so the on-demand image tool (drhook_snapshot_image)
/// rasterizes exactly what this view shows, never a forked copy.</summary>
public sealed class ConsoleDebugStateView : IDebugStateView
{
    private readonly TextWriter _out;
    private readonly SourceWindowReader _source;
    private readonly bool _waitsForNextSession;

    /// <param name="waitsForNextSession">True when the client reconnects
    /// (<see cref="DebugStateClientOptions.Reconnect"/>): a disconnect line then says the view is waiting.</param>
    public ConsoleDebugStateView(TextWriter output, SourceWindowReader? source = null, bool waitsForNextSession = false)
    {
        _out = output;
        _source = source ?? new SourceWindowReader();
        _waitsForNextSession = waitsForNextSession;
    }

    public void OnConnected(string endpoint)
        => _out.WriteLine($"● connected to {endpoint} — waiting for debug-state…");

    public void OnSnapshot(WireSnapshot s, DebugStateClientModel model)
        => DebugStateTextRenderer.RenderSnapshot(_out, s, model.LastSeq, _source);

    public void OnDelta(WireDelta d, DebugStateClientModel model)
        => DebugStateTextRenderer.RenderDelta(_out, d);

    public void OnDisconnected(string? reason)
        => _out.WriteLine(_waitsForNextSession
            ? $"● disconnected: {reason} — waiting for a session…"
            : $"● disconnected: {reason}");
}
