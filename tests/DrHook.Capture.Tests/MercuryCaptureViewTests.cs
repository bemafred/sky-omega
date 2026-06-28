// The capture view persists the live debug-state stream to a Graph-profile Mercury store. This drives it
// in-process against a real temp store and queries the result back via SPARQL — locking the thin capture
// schema (session graph + per-message triples + the hypothesis braid fields) and proving it is queryable.

using System;
using System.IO;
using SkyOmega.DrHook.Capture;
using SkyOmega.DrHook.Viz;
using SkyOmega.DrHook.Wire;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.DrHook.Capture.Tests;

public sealed class MercuryCaptureViewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "drhook-capture-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    [Fact]
    public void Capture_PersistsBraidAndSession_ToAGraphStore_QueryableBack()
    {
        using var store = new QuadStore(_dir, null, null, new StorageOptions { Profile = StoreProfile.Graph });
        Assert.Equal(StoreProfile.Graph, store.Schema.Profile);

        var diag = new StringWriter();
        var model = new DebugStateClientModel(100);
        var view = new MercuryCaptureView(store, diag);

        view.OnConnected("/tmp/test.sock");
        view.OnSnapshot(Snapshot(), model);
        view.OnDelta(new WireDelta("hypothesis", "2026-06-28T00:00:01.0000000+00:00",
            HypothesisText: "span.Length == 5", HypothesisLens: "Inspection"), model);
        view.OnDisconnected("test complete");

        // The braid's prediction half is queryable back — verbatim text + lens. (GRAPH union; PREFIX declared.)
        QueryResult braid = SparqlEngine.Query(store, """
            PREFIX dh: <https://sky-omega.dev/drhook/ns#>
            SELECT ?text ?lens WHERE { GRAPH ?g { ?m dh:hypothesisText ?text ; dh:hypothesisLens ?lens } }
            """);
        Assert.True(braid.Success, $"{braid.ErrorMessage}\ncapture diag: {diag}");
        System.Collections.Generic.Dictionary<string, string> row = Assert.Single(braid.Rows!);
        Assert.Contains("span.Length == 5", row["text"]);
        Assert.Contains("Inspection", row["lens"]);

        // Two messages recorded (the snapshot + the hypothesis delta), each tagged dh:type.
        QueryResult count = SparqlEngine.Query(store, """
            PREFIX dh: <https://sky-omega.dev/drhook/ns#>
            SELECT (COUNT(?m) AS ?c) WHERE { GRAPH ?g { ?m dh:type ?t } }
            """);
        Assert.True(count.Success, $"{count.ErrorMessage}\ncapture diag: {diag}");
        Assert.Contains("2", count.Rows![0]["c"]);

        // The session node carries the pid lifted from the first snapshot.
        QueryResult session = SparqlEngine.Query(store, """
            PREFIX dh: <https://sky-omega.dev/drhook/ns#>
            SELECT ?pid WHERE { GRAPH ?g { ?s dh:pid ?pid } }
            """);
        Assert.True(session.Success, $"{session.ErrorMessage}\ncapture diag: {diag}");
        Assert.Contains("123", session.Rows![0]["pid"]);

        // The raw NDJSON line is preserved (full re-projectable fidelity) on the snapshot message.
        QueryResult raw = SparqlEngine.Query(store, """
            PREFIX dh: <https://sky-omega.dev/drhook/ns#>
            SELECT ?raw WHERE { GRAPH ?g { ?m dh:type "snapshot" ; dh:raw ?raw } }
            """);
        Assert.True(raw.Success, $"{raw.ErrorMessage}\ncapture diag: {diag}");
        Assert.Contains("snapshot", raw.Rows![0]["raw"]);

        Assert.True(diag.ToString().Length == 0 || !diag.ToString().Contains("write failed"),
            $"capture reported a write failure: {diag}");
    }

    private static WireSnapshot Snapshot() => new(
        "2026-06-28T00:00:00.0000000+00:00",
        new WireSession(123, true, 10, false, false, "Stopped"),
        new WirePosition("Breakpoint", null,
            new[] { new WireFrame("Worker.Compute @ Program.cs:32", "Program.cs", 32) },
            Array.Empty<WireVar>(), Array.Empty<WireVar>()),
        Array.Empty<WireBreakpoint>(), Array.Empty<WireExceptionFilter>(),
        new WireStreams(0, 0, 0, 0, 0, 0));
}
