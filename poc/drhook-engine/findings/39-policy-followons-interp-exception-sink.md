# Finding 39: Completing the BreakpointPolicy arc — interpolation walker, exception-through-policy, ring-buffer sink

**Status:**   **All three threads PASSED.** The follow-ons named at the close of finding 38 / commit
`f0910da` — one Roslyn walker serving conditions AND log interpolation, `BreakpointPolicy` wired to the
exception location axis, and the host-side ring-buffer sink — are validated end-to-end. The
finding-33 four-axis model is now mechanically complete across the breakpoint AND exception locations,
with a real consumer-ready destination for log output.
**Date:**     2026-05-22
**Probes:**   `poc/drhook-engine/29-interp-smoke.cs` (+ `29-interp-target.cs`),
              `poc/drhook-engine/30-expolicy-smoke.cs` (+ `30-expolicy-target.cs`)
**Engine:**   `src/DrHook.Engine/BoundedLogSink.cs`, `DebugSession.WaitForExceptionPolicyStop`, refactored `EvaluatePolicy`

## 1 — Roslyn interpolation walker (probe 29 → PASSED 2/2)

The "one front end, two consumers" convergence from finding 33: the same Roslyn `Eval` core that
produced `Func<IEvalContext, bool>` conditions (probes 22/25) now also produces
`Func<IEvalContext, string>` log-message renderers from parsed interpolated strings. The walker wraps
the template as `$"…"` and lets Roslyn parse into an `InterpolatedStringExpressionSyntax`; `Render`
walks each part (text → literal append; `{expr}` → `Eval` + `Convert.ToString` with
`CultureInfo.InvariantCulture`).

Two configurations against one target/breakpoint:

- **A. Interpolation logpoint** — `LogMessage` parsed from `$"v={v} doubled={2*v}"`, `Suspend.None`,
  2 s → **66 logs** all of shape `v=N doubled=M`, with `doubled == 2*v` arithmetic-checked. Proves
  the `{2*v}` fragment is actually evaluated, not interpolated as literal text.
- **B. Condition + interpolation from the SAME walker** — `Condition` parsed from `v == 3` AND
  `LogMessage` parsed from `$"matched v={v} doubled={2*v}"`, `Suspend.All` → surfaces at v=3 with
  exactly one log line `matched v=3 doubled=6`. One walker driving both consumers in one policy.

`Eval` also gained the arithmetic operators (`+ - * / %`) needed for `{2*v}`-style fragments; the
member-access path (probe 25) is unchanged.

## 2 — Exception-through-policy (probe 30 → PASSED 2/2)

`BreakpointPolicy` now drives the **exception** location axis (finding 33), reusing the validated
exception-stop machinery (probes 26/27). New engine method:

```csharp
public StopInfo? WaitForExceptionPolicyStop(string exceptionTypeName, BreakpointPolicy policy, TimeSpan timeout)
```

Exception stops whose type doesn't match the filter are auto-resumed without polluting the hit
counter; matching stops run the SAME `EvaluatePolicy` core as `WaitForPolicyStop`. This was the
opportunity to factor out the shared evaluation:

```csharp
private enum PolicyOutcome { Resume, Surface, ConditionFault }
private PolicyOutcome EvaluatePolicy(BreakpointPolicy policy, ref int hitCount) { … }
```

So fault handling, log emission, and the suspend decision are identical across breakpoint and
exception locations — by construction, not by parallel copies that could drift.

The walker special-cases the `ex` operand: `ex.X` resolves via `TryEvalCurrentExceptionMember`
(probe 27) instead of `TryEvalMemberCall` on a local. The engine sources the in-flight exception
via `GetCurrentException` at the stop; the walker just names the convention.

Three configurations:

- **A. Conditional exception breakpoint** — `Condition` `ex.Code == 42`, `Suspend.All` → surfaces at
  the first FirstChance `ProbeException`.
- **B. Exception logpoint** — `LogMessage` `$"caught ex.Code={ex.Code}"`, `Suspend.None`, 2 s →
  **110 logs** all `caught ex.Code=42`, never surfaces.
- **C. Exception fault** — `Condition` `ex.Nope == 0` (member doesn't exist on the runtime type) →
  walker throws → `ConditionError` + `IsFault` `LogRecord` naming `ex.Nope`. The fault path holds at
  the exception location too.

## 3 — BoundedLogSink: the host-side ring-buffer sink (engine type + 9 unit tests)

The default destination for logpoint output (finding 35). Reusable `IDebugEventSink` implementation in
the engine (BCL-only): fixed-capacity ring buffer, oldest-dropped, thread-safe, with an atomic `Drain`
returning `DrainResult(Records, Dropped)`.

```csharp
public sealed class BoundedLogSink : IDebugEventSink
{
    public BoundedLogSink(int capacity);
    public int Capacity { get; }
    public int Count { get; }     // diagnostic — racy by nature; Drain is the atomic snapshot
    public void OnEvent(string name);      // not our channel — ignored
    public void OnLog(LogRecord record);   // O(1); drops the OLDEST on overflow, counts the drop
    public DrainResult Drain();            // newest-last snapshot + drops-since-last-drain; resets
}
public sealed record DrainResult(IReadOnlyList<LogRecord> Records, long Dropped);
```

Backpressure by construction: a hot logpoint can't exhaust memory or blow an LLM consumer's context.
The `Dropped` counter is the finding-35 "checked nothing" marker — silent loss is rejected; volume
truncation stays visible to the consumer.

Unit-tested (9 new, 46 total pass): empty drain, below/at/over capacity, drop counting, drain reset,
`OnEvent` ignored, non-positive capacity rejected, concurrent appends from 8 threads × 1000 each →
records-kept + dropped equals total appended.

MCP-tool integration (`drhook_log_drain`, file tee, Mercury observations) is the natural follow-on
once `DrHook.Engine` becomes the active MCP backend (ADR-006 Phase 3). The reusable building block
sits in the engine ready to compose.

## Why this is the right close on the arc

Findings 33 / 35 / 38 named exactly these three follow-ons. Each is small in code (one walker
extension, one engine method + factored helper, one ~80-line type with tests), but together they
close the policy substrate:

- **Front-end coverage** — every place `BreakpointPolicy` accepts a delegate, the same Roslyn walker
  produces it. One front end, two consumers (booleans + strings).
- **Location coverage** — both breakpoint *and* exception locations drive `BreakpointPolicy` through
  the same evaluation core. Fault, hit-count gating, logpoint sampling, suspend policy — all behave
  identically across location kinds.
- **Destination coverage** — engine emits structured `LogRecord`s to `IDebugEventSink.OnLog`; the
  ring-buffer sink turns "emit" into "host-drainable backpressured channel."

## Scope / next

- **`drhook_log_drain` MCP tool** wiring `BoundedLogSink` into a real tool surface — gated on
  ADR-006 Phase 3 (the MCP switchover from netcoredbg to DrHook.Engine), not on the engine.
- **Per-thread suspend** (`SuspendPolicy.CurrentThread`) — ICorDebug supports it; the pump is
  process-level today.
- **First-chance / unhandled filter** on `WaitForExceptionPolicyStop` — currently filters by type
  only. A future axis param when a consumer needs it (e.g., "break only on unhandled").
- **`DrHook.Engine.Expressions` package home** — the walker still lives in each probe (Roslyn-using
  files outside the BCL-only core). When MCP integration lands, extract the walker to a sibling
  assembly with `Microsoft.CodeAnalysis.CSharp` as its only non-BCL dep, keeping the core clean.
- **Set-time parse validation** via `tree.GetDiagnostics()` (finding 35 leftover) — reject malformed
  expressions before arming; small refinement when the policy gets a public arming surface.

## References

- Probes: `29-interp-smoke.cs`, `29-interp-target.cs`, `30-expolicy-smoke.cs`, `30-expolicy-target.cs`
- Fixtures: `29-interp-osx-arm64-…`, `30-expolicy-osx-arm64-…`
- Engine: `BoundedLogSink.cs` (new + 9 unit tests), `DebugSession.WaitForExceptionPolicyStop` (new),
  `DebugSession.EvaluatePolicy` + `PolicyOutcome` (factored from probe-28's `WaitForPolicyStop`)
- Tests: `BoundedLogSinkTests.cs` (new) — 46 total pass
- Findings 33 (four-axis model), 35 (the three named follow-ons), 38 (`BreakpointPolicy` increment),
  26/27 (exception stop + func-eval at it — reused here), 25 (member-access walker — extended here)
- Mercury session 2026-05-22 observation `policy-arc-completion`
