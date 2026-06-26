using SkyOmega.DrHook.Wire;

namespace SkyOmega.DrHook.Viz;

/// <summary>The client-side debug-state model — the current <see cref="Snapshot"/> plus a bounded tail of
/// recent <see cref="WireDelta"/>s. The <see cref="DebugStateClient"/> updates it as messages arrive; views
/// render from it. This is the view-agnostic state every visualizer shares.
///
/// <para>Threading: updates happen on the client's read loop. <see cref="Snapshot"/> is an atomic reference
/// read (a reader sees the previous or the new snapshot, never a torn one). The delta tail is guarded by a
/// lock; use <see cref="DeltaTail"/> for a safe point-in-time copy from another (e.g. UI) thread.</para></summary>
public sealed class DebugStateClientModel
{
    private readonly int _cap;
    private readonly object _lock = new();
    private readonly LinkedList<WireDelta> _deltas = new();

    /// <summary>Construct a model retaining at most <paramref name="deltaHistory"/> recent deltas.</summary>
    public DebugStateClientModel(int deltaHistory)
    {
        if (deltaHistory <= 0) throw new ArgumentOutOfRangeException(nameof(deltaHistory), "deltaHistory must be positive");
        _cap = deltaHistory;
    }

    /// <summary>The latest snapshot (full current state), or null before the first one arrives.</summary>
    public WireSnapshot? Snapshot { get; private set; }

    /// <summary>The sequence number of the most recently applied message (snapshot or delta) — monotonic
    /// per the server; lets a view detect gaps.</summary>
    public long LastSeq { get; private set; }

    /// <summary>Number of deltas currently retained in the tail (≤ the configured history).</summary>
    public int DeltaCount { get { lock (_lock) return _deltas.Count; } }

    /// <summary>A safe point-in-time copy of the retained delta tail, oldest first.</summary>
    public WireDelta[] DeltaTail()
    {
        lock (_lock)
        {
            var copy = new WireDelta[_deltas.Count];
            _deltas.CopyTo(copy, 0);
            return copy;
        }
    }

    internal void ApplySnapshot(WireSnapshot snapshot, long seq)
    {
        Snapshot = snapshot;
        LastSeq = seq;
    }

    internal void ApplyDelta(WireDelta delta, long seq)
    {
        lock (_lock)
        {
            if (_deltas.Count >= _cap) _deltas.RemoveFirst();
            _deltas.AddLast(delta);
        }
        LastSeq = seq;
    }
}
