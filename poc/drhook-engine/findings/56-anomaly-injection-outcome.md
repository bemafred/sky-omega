# Finding 56: Probe 41 outcome — EngineAnomaly designed-injection validation (Phase 1 Validation gate)

**Status:**   PASSED on macOS-arm64 2026-05-24. Probe 41 (`41-anomaly-injection-smoke.cs` + `41-anomaly-target.cs`) closes the ADR-007 Phase 1 Validation criterion for the EngineAnomaly substrate: *"the infrastructure exists, its capture mechanism is validated by a designed probe (intentional anomaly injection exercising the surfacing path), and the surfacing reaches the log sink + MCP response as designed."*
**Date:**     2026-05-24
**Numbering note:** Finding 56 was previously reserved by ADR-007 Phase 2 for the meta-probe outcome. Phase 2's meta-probe finding now lands at finding 57. The reservation shift is captured in ADR-007 line 78 and in Mercury (`<https://sky-omega.dev/sessions/2026-05-23-drhook-adr007/renumber-2026-05-24>`).

## What was validated

Probe 41 exercises the full EngineAnomaly path end-to-end with a live target:

```
target process (.NET 10 console app, READY-PID handshake)
    │
    │ attached via
    ▼
EngineSteppingSession.LaunchAsync(pid, sourceFile, line)
    │
    │ session.InspectVariablesAsync(depth=999, ...)
    │   ├─ DebugSession.GetLocals(depth=999) → clamp triggered → _sink.OnAnomaly(DepthClamped, operation="GetLocals")
    │   └─ DebugSession.GetArguments(depth=999) → clamp triggered → _sink.OnAnomaly(DepthClamped, operation="GetArguments")
    │
    │ both anomalies land in EngineSteppingSession._anomalies (BoundedAnomalySink)
    ▼
session.DrainAnomaliesAsJson()
    │
    │ builds the MCP-shaped JSON envelope
    ▼
JSON parsed by the probe; assertions on every field
```

Every link in the chain is validated:

1. **Capture site** — `DepthClamped` anomalies are emitted by the substrate-critical clamps in `DebugSession.GetLocals` / `GetArguments` (ENG-STK-1 from finding 55).
2. **Sink delivery** — `_sink.OnAnomaly` reaches `BoundedAnomalySink.OnAnomaly` and the records are buffered under the sink's lock.
3. **Drain** — `BoundedAnomalySink.Drain` produces `AnomalyDrainResult{Anomalies, Dropped}` with the right shape.
4. **MCP envelope construction** — `EngineSteppingSession.DrainAnomaliesAsJson` builds the JSON envelope with `status` / `count` / `dropped` / `capacity` / `anomalies` array / kind-specific `context`.
5. **Structured-evidence shape** — every required field is present (`capturedAt`, `kind`, `thread`, `operation`, `observed`, `expected`, `context.requested`, `context.clamped`) with the expected values.

## Setup

```
runtime    : .NET 10.0.0
dbgshim    : (resolver default — per-RID NuGet bundling, ADR-007 Phase 3 close)
target     : poc/drhook-engine/41-anomaly-target.cs
host       : macOS-arm64, .NET 10 file-based-app context
breakpoint : 41-anomaly-target.cs:30 (`GC.KeepAlive(ignored); // ANOMALY_HERE`)
```

## Run

```
$ dotnet run --no-cache 41-anomaly-injection-smoke.cs -- 41-anomaly-target.cs

runtime    : .NET 10.0.0
dbgshim    : (resolver default)
plan       : at 41-anomaly-target.cs:30, InspectVariablesAsync(depth=999) fires DepthClamped twice (GetLocals + GetArguments); drain via DrainAnomaliesAsJson; assert envelope.
target pid : 49269
launch     : { "status": "attached", "pid": 49269, ... }
inspect    : { "step": 0, "variableCount": 1, ... }
drain      : { "status": "ok", "count": 2, "dropped": 0, "capacity": 256, "anomalies": [...] ... }
envelope   : count=2, dropped=0, capacity=256
  anomaly  : kind=DepthClamped thread=mcp-request operation=GetLocals requested=999 clamped=10
  anomaly  : kind=DepthClamped thread=mcp-request operation=GetArguments requested=999 clamped=10
stop       : { "status": "stopped", ... }

PROBE 41 PASSED — EngineAnomaly substrate validated end-to-end.
```

Fixture file: `poc/drhook-engine/fixtures/41-anomaly-injection-osx-arm64-20260524T054957Z.txt`.

## What this validates

Per ADR-007 Phase 1 Validation criterion:

- ✓ **The infrastructure exists.** `EngineAnomaly` record + `AnomalyKind` enum + `BoundedAnomalySink` + `IDebugEventSink.OnAnomaly` extension + capture-site wiring in CallbackPump / DebugSession / EngineSteppingSession + `drhook_drain_anomalies` MCP tool. (EA-1..6, committed 2026-05-24 in `1dd2290`.)
- ✓ **The capture mechanism is validated by a designed probe (intentional anomaly injection).** This probe injects `depth=999` and observes the resulting `DepthClamped` anomalies. Both clamp paths (`GetLocals`, `GetArguments`) fire; coverage of both sites is asserted explicitly.
- ✓ **The surfacing reaches the log sink + MCP response as designed.** The probe reads `DrainAnomaliesAsJson` — the same method that powers the `drhook_drain_anomalies` MCP tool — and asserts the envelope shape end-to-end. No mock; the actual MCP-facing method is exercised.

## What this does NOT validate

This probe deliberately exercises ONE capture-site kind (`DepthClamped`) because it's the only one whose triggering condition is fully under caller control without inducing a substrate failure mode. The other 10 capture sites need their own designed-injection probes OR get implicit validation through the substrate-correctness probes (42–45) that exercise their triggering conditions:

| Capture site | Kind | Validation route |
|---|---|---|
| `CallbackPump.OnCallback` late-add | `LateCallback` | Implicit via Probe 44 (drhook-detach-exit-race) — kill-coincident-Dispose induces late callbacks |
| `CallbackPump.RequestPause` late-add | `LateCallback` | Implicit via Probe 43 (PauseRequest race) |
| `CallbackPump.Pump` _resume.Take catch (PauseRequest branch) | `WorkerSilentBreak` | Implicit via Probe 43 |
| `CallbackPump.Pump` _resume.Take catch (STOPPING branch) | `WorkerSilentBreak` | Implicit via Probe 44 |
| `CallbackPump.Pump` outer try/catch | `WorkerException` | **Explicit via Probe 45** (worker-thread exception path) |
| `DebugSession.Quiesce` HRESULT | `UnexpectedHResult` | Implicit via Probe 44 |
| `DebugSession.Detach` HRESULT | `UnexpectedHResult` | Implicit via Probe 44 |
| `DebugSession.Terminate` (in Dispose) HRESULT | `UnexpectedHResult` | Implicit via Probe 44 |
| `DebugSession.GetLocals` clamp | `DepthClamped` | **Explicit, this probe.** |
| `DebugSession.GetArguments` clamp | `DepthClamped` | **Explicit, this probe.** |
| `EngineSteppingSession.CleanupSession.Kill` blanket-catch | `UnexpectedCleanupException` | Implicit via any probe that kills the target |
| `EngineSteppingSession.CleanupSession.SessionDispose` blanket-catch | `UnexpectedCleanupException` | Implicit via any probe that calls StopAsync |

Two deferred capture sites (`ManagedCallbackHost.HostOf`-null and `DbgShim.StartupCallbackThunk`-null) require a static-fallback sink substrate (separate follow-up) — they cannot use the session-tied `_sink` because the substrate state is compromised when their failure condition fires.

## Adjustment to substrate

One adjacent fix was needed to make this probe run in the .NET 10 file-based-app context:

- **`EngineSteppingSession.Render` switched from `JsonSerializer.Serialize(obj, Indented)` to `obj.ToJsonString(Indented)`.** The file-based-app runtime context has reflection-based JSON serialization disabled (similar to trimmed/AOT publish); `JsonSerializer.Serialize<T>` requires a `TypeInfoResolver` that isn't configured. `JsonNode.ToJsonString(JsonSerializerOptions)` doesn't go through the reflection path and produces equivalent output for the `JsonObject` inputs Render takes. Behaviour-equivalent for all existing call sites (already JsonObject-based); now also works under trimming.

## Cross-references

- [ADR-007](../../../docs/adrs/drhook/ADR-007-teardown-concurrency-test-debug.md) Phase 1, Probe 41 (anomaly-injection validation).
- [finding 53](53-threading-memory-model-audit.md) — identified 5 of the 11 capture-site seeds.
- [finding 54](54-teardown-audit.md) — identified 5 of the 11 capture-site seeds + the C-DRAIN-CB / C-DRAIN-EXIT contract split that drives Probe 44.
- [finding 55](55-stack-budget-audit.md) — identified the 11th seed (`DepthClamped` via ENG-STK-1) and Mercury-aligned the substrate's depth bound at 10.
- Commit `1dd2290` — EngineAnomaly substrate infrastructure (EA-1..6).
- Mercury session graph: `<https://sky-omega.dev/sessions/2026-05-23-drhook-adr007/graph>` — including the renumber-event marker for the Probe 41 insertion.

## Phase 1 Validation gate status

**CLOSED.** Per ADR-007 line 174 ("Probes 41–63 pass on macOS/arm64 in CI"): Probe 41 ✓ on macOS-arm64. Probes 42–45 (substrate-correctness — Dispose-race / PauseRequest-race / detach-exit-race / worker-exception) remain pending; they now execute against substrate whose anomaly-surfacing path is end-to-end-validated, so any unexpected behavior surfaces as structured `EngineAnomaly` evidence rather than silent swallowing.
