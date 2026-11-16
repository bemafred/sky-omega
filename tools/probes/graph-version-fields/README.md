# What the Graph profile keeps in its version fields

Two probes of 2026-11-16, for [ADR-064](../../../docs/adrs/mercury/ADR-064-a-facts-states.md) and
[ADR-062](../../../docs/adrs/mercury/ADR-062-what-a-read-yields.md) Q6. Both read the Graph index entries through
`VersionedQuadIndex.QueryAllVersions`, whose enumerator ADR-062 Step 6 removes, so they run on `c3b6025`. They were
run there in a scratch clone on omega (macOS 27, arm64). Each states its hypothesis and construction at its top.

```bash
git clone <repo> at-c3b6025 && cd at-c3b6025 && git checkout c3b6025
# copy the probes into tools/probes/graph-version-fields/, then:
dotnet run --no-cache tools/probes/graph-version-fields/what-the-graph-profile-keeps.cs
dotnet run --no-cache tools/probes/graph-version-fields/a-killed-graph-store-replays-its-log.cs write <new dir>
dotnet run --no-cache tools/probes/graph-version-fields/a-killed-graph-store-replays-its-log.cs read <same dir>
```

## `what-the-graph-profile-keeps.cs`

A Graph store with a supplied clock fixed at 2031-01-01: assert x, retract x, assert x, assert y, retract y, 5 ms
apart. Times are milliseconds after the probe started.

| | Before the rebuild | After the rebuild |
|---|---|---|
| GSPO | x v3, created 19, modified 46; y v2, created 58, modified 70, retracted | unchanged |
| GPOS, GOSP, TGSP | as GSPO, but y modified 71 | count 1: x v1, created 126; y gone |
| store quad count | 2 | — |
| `SELECT ?s WHERE { ?s ?p ?o }` | 1 row | 1 row |

- Each index stamps `CreatedAt` and `ModifiedAt` from its own `DateTimeOffset.UtcNow`; the supplied clock is
  ignored, and the copies of one fact disagree by a millisecond.
- The rebuild reads what a client sees and inserts it anew: the secondary indexes lose the retracted fact, and every
  version restarts at 1.
- `QueryEvolution` and `QueryAsOf` on the Graph store throw `ProfileCapabilityException`.

## `a-killed-graph-store-replays-its-log.cs`

A new Graph store: assert x, retract x, assert x, three commits; then the process kills itself (SIGKILL, exit 137).

| | GSPO, GPOS, GOSP, TGSP | `SELECT` |
|---|---|---|
| before the kill | x v3, asserted | 1 row |
| after reopen, which recovers | x **v5**, asserted | 1 row |
| after a second open | x v5, asserted | 1 row |

Recovery applies the log's Add, Retract and Add records again to indexes that already hold them: nothing, v4, v5.
What a client sees is right; the held states v4 and v5 never happened. The Graph log holds the operations that were
asked for, not the change each one made (ADR-061 M7 decided the latter for Cognitive only).
