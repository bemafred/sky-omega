using SkyOmega.DrHook.Wire;

namespace SkyOmega.DrHook.Viz;

/// <summary>Options for a <see cref="DebugStateClient"/> — strongly typed (mirrors how the Mercury tools take
/// an options object). The thin view shims build one of these from their CLI args.</summary>
public sealed class DebugStateClientOptions
{
    /// <summary>The rendezvous socket to connect to. Defaults to the shared well-known path
    /// (<see cref="WireRendezvous.DefaultSocketPath"/>) — the same one the server binds.</summary>
    public string SocketPath { get; set; } = WireRendezvous.DefaultSocketPath();

    /// <summary>Maximum number of recent deltas the client-side model retains (the bounded tail a view can
    /// render). Oldest are dropped past this. Must be positive.</summary>
    public int DeltaHistory { get; set; } = 1000;
}
