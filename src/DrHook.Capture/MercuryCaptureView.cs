using SkyOmega.DrHook.Viz;
using SkyOmega.DrHook.Wire;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.DrHook.Capture;

/// <summary>An <see cref="IDebugStateView"/> that PERSISTS the live debug-state stream — instead of rendering
/// it — as raw, append-only triples in a dedicated Graph-profile Mercury store, one named graph per session.
/// The capture half of ADR-012 Phase 3's "one capture, two consumers": the same transport the console view
/// renders, this records for later analysis (mercury-cli / SERVICE federation from the cognition store).
///
/// <para>RAW capture only — NOT consolidation. Consolidating braid knowledge into cognition memory needs an
/// owner of the whole (Omega / James), which does not exist yet; this just records the facts for that future
/// owner to read. The schema is a thin, MECHANICAL projection of the wire stream — a session node plus one
/// node per message (seq, type, timestamp, the kind-specific fields, and the re-serialized NDJSON as
/// <c>dh:raw</c> for full, re-projectable fidelity). Meaning is deliberately NOT modeled here; the ontology
/// for consolidation emerges later, owned elsewhere.</para>
///
/// <para>Writes never throw out of a callback — a capture hiccup must not tear down the session. The store is
/// written ONLY by this consumer (single writer); mercury-cli and the cognition MCP (via SERVICE) read it.</para></summary>
public sealed class MercuryCaptureView : IDebugStateView
{
    private const string Ns  = "https://sky-omega.dev/drhook/ns#";
    private const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string Xsd = "http://www.w3.org/2001/XMLSchema#";

    private readonly QuadStore _store;
    private readonly TextWriter _diag;     // diagnostics → stderr; capture is otherwise silent
    private string _sessionId = "";
    private string _sessionGraph = "";
    private int _seq;
    private bool _sessionEnriched;

    public MercuryCaptureView(QuadStore store, TextWriter diagnostics)
    {
        _store = store;
        _diag = diagnostics;
    }

    /// <summary>The capture-side session id (unique per run), so this run's records share one named graph.</summary>
    public string SessionGraph => _sessionGraph;

    public void OnConnected(string endpoint)
    {
        _sessionId = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}";
        _sessionGraph = $"https://sky-omega.dev/drhook/session/{_sessionId}";
        Capture(() =>
        {
            AddT(_sessionGraph, Rdf + "type", Iri(Ns + "Session"));
            AddT(_sessionGraph, Ns + "endpoint", Lit(endpoint));
            AddT(_sessionGraph, Ns + "startedAt", DateTimeLit(DateTimeOffset.UtcNow));
        });
        _diag.WriteLine($"drhook-capture → session {_sessionId} (capturing)");
    }

    public void OnSnapshot(WireSnapshot snapshot, DebugStateClientModel model)
    {
        Capture(() =>
        {
            if (!_sessionEnriched)
            {
                AddT(_sessionGraph, Ns + "pid", IntLit(snapshot.Session.Pid));
                if (snapshot.Session.RuntimeMajor is { } rm) AddT(_sessionGraph, Ns + "runtimeMajor", IntLit(rm));
                _sessionEnriched = true;
            }

            string msg = Msg(++_seq);
            AddMessageCore(msg, "snapshot", snapshot.CapturedAt);
            if (snapshot.Position.Stop is { } stop) AddT(msg, Ns + "stopReason", Lit(stop));
            if (snapshot.Position.CallStack is { Length: > 0 } cs) AddT(msg, Ns + "topFrame", Lit(cs[0].Display));
            AddT(msg, Ns + "raw", Lit(Raw(new WireMessage("snapshot", _seq, Snapshot: snapshot))));
        });
    }

    public void OnDelta(WireDelta delta, DebugStateClientModel model)
    {
        Capture(() =>
        {
            string msg = Msg(++_seq);
            AddMessageCore(msg, "delta", delta.At);
            AddT(msg, Ns + "deltaKind", Lit(delta.Kind));
            // The braid's prediction half — surfaced as first-class fields so a later query can find every
            // stated hypothesis (its verbatim text + lens) without re-parsing dh:raw.
            if (delta.Kind == "hypothesis")
            {
                if (delta.HypothesisText is { } t) AddT(msg, Ns + "hypothesisText", Lit(t));
                if (delta.HypothesisLens is { } l) AddT(msg, Ns + "hypothesisLens", Lit(l));
            }
            AddT(msg, Ns + "raw", Lit(Raw(new WireMessage("delta", _seq, Delta: delta))));
        });
    }

    public void OnDisconnected(string? reason)
    {
        if (_sessionGraph.Length == 0) return;
        Capture(() =>
        {
            AddT(_sessionGraph, Ns + "endedAt", DateTimeLit(DateTimeOffset.UtcNow));
            AddT(_sessionGraph, Ns + "messageCount", IntLit(_seq));
            if (reason is not null) AddT(_sessionGraph, Ns + "disconnectReason", Lit(reason));
        });
        _diag.WriteLine($"drhook-capture ← session {_sessionId} ended, {_seq} messages{(reason is null ? "" : $" ({reason})")}");
    }

    // ── projection helpers ──
    private string Msg(int seq) => $"{_sessionGraph}/msg/{seq}";

    private void AddMessageCore(string msg, string type, string atIso)
    {
        AddT(msg, Rdf + "type", Iri(Ns + "Message"));
        AddT(msg, Ns + "session", Iri(_sessionGraph));
        AddT(msg, Ns + "seq", IntLit(_seq));
        AddT(msg, Ns + "type", Lit(type));
        AddT(msg, Ns + "at", DateTimeStrLit(atIso));
    }

    // Re-serialize the parsed wire message back to its NDJSON line — full fidelity preserved as dh:raw, so a
    // later consolidation can re-project with any schema regardless of how thin this projection is.
    private static string Raw(WireMessage message) => WireCodec.Serialize(message).TrimEnd('\n');

    // Every triple goes into this run's session graph (the 4th AddCurrent arg).
    private void AddT(string subject, string predicate, string objectTerm)
        => _store.AddCurrent(Iri(subject), Iri(predicate), objectTerm, Iri(_sessionGraph));

    private void Capture(Action write)
    {
        try { write(); }
        catch (Exception ex) { _diag.WriteLine($"drhook-capture: write failed (continuing): {ex.GetType().Name}: {ex.Message}"); }
    }

    private static string Iri(string s) => $"<{s}>";
    private static string Lit(string s) => $"\"{Escape(s)}\"";
    private static string IntLit(long n) => $"\"{n}\"^^<{Xsd}integer>";
    private static string DateTimeLit(DateTimeOffset t) => $"\"{t:O}\"^^<{Xsd}dateTime>";
    private static string DateTimeStrLit(string isoAt) => $"\"{Escape(isoAt)}\"^^<{Xsd}dateTime>";

    private static string Escape(string s) => s
        .Replace("\\", "\\\\").Replace("\"", "\\\"")
        .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
}
