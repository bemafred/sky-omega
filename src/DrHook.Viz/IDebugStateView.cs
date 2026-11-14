using SkyOmega.DrHook.Wire;

namespace SkyOmega.DrHook.Viz;

/// <summary>The view seam — the client-side counterpart of the engine's <c>IDebugEventSink</c>. The shared
/// <see cref="DebugStateClient"/> connects to the transport and PUSHES debug-state to a view through this
/// interface; concrete views (console, TUI, Avalonia GUI) each implement it and render in their own way, so
/// they all share the one client + model. Each callback also receives the current
/// <see cref="DebugStateClientModel"/>, so a view can render the specific update or re-render the whole picture.
///
/// <para>Threading: the client invokes these on its read loop (a background task with <c>ConfigureAwait(false)</c>),
/// NOT necessarily a UI thread. A console view can write directly; a TUI/GUI view must marshal to its UI thread.</para></summary>
public interface IDebugStateView
{
    /// <summary>The client connected to <paramref name="endpoint"/> (the rendezvous socket path). What the
    /// session emitted before this connection follows as deltas, oldest first, and then a snapshot (the
    /// server's snapshot-on-connect); before the session's first stop there is no snapshot yet.</summary>
    void OnConnected(string endpoint);

    /// <summary>A point-in-time snapshot arrived — the full current state. <paramref name="model"/> now has it
    /// as <see cref="DebugStateClientModel.Snapshot"/>.</summary>
    void OnSnapshot(WireSnapshot snapshot, DebugStateClientModel model);

    /// <summary>A live delta arrived (lifecycle event / log / anomaly / console). <paramref name="model"/> has
    /// appended it to its bounded delta tail.</summary>
    void OnDelta(WireDelta delta, DebugStateClientModel model);

    /// <summary>The session ended for this view: the server closed the connection, the read was cancelled, or
    /// the connect failed. <paramref name="reason"/> is a short human-readable cause (never null in practice).
    /// A client that reconnects (<see cref="DebugStateClientOptions.Reconnect"/>) calls this once each time it
    /// starts waiting, and <see cref="OnConnected"/> again when the next session is up; the model's
    /// state from the ended session stays readable in between.</summary>
    void OnDisconnected(string? reason);
}
