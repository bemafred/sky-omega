# Finding 38: Probe 28 Outcome — PASSED: BreakpointPolicy unifies conditional / logpoint / hit-count / fault

**Status:**   **PASSED, 2/2** (clean exit 0 both runs; +15 in-process unit tests for `HitCountGate`).
The engineering increment named in findings 33 + 35 is live: ONE `BreakpointPolicy` record drives a
breakpoint with four orthogonal configurations against the SAME target + breakpoint, exercising every
axis (condition gate, log action, hit-count gate, suspend policy) plus the finding-35 fault path.
The unification claim from finding 33 ("two configs of one policy") is now mechanically validated.
**Date:**     2026-05-22
**Probe:**    `poc/drhook-engine/28-policy-smoke.cs` + `28-policy-target.cs`

## The four configurations of one policy

```
A. conditional   : Condition v == 3, Suspend.All …
               -> stop=Breakpoint  v=3
B. logpoint      : LogMessage "v={v}", Suspend.None, 2s …
               -> stop=null (timeout, expected)  logs=68  first="v=4"
C. hit-count gate: HitCount Equals(3) + LogMessage, Suspend.None, 2s …
               -> stop=null (timeout, expected)  logs=1  (must be exactly 1)  first="hit-3 v=4"
D. fault         : Condition throws -> ConditionError + IsFault log …
               -> stop=ConditionError  faultLogs=1
```

The same `BreakpointPolicy` type produces each behavior by varying its independent fields:

| Config | Condition | HitCount | LogMessage | Suspend | Surface |
|---|---|---|---|---|---|
| A — conditional bp | `v == 3` | — | — | All | `Breakpoint` at v=3 |
| B — logpoint | — | — | `v={v}` | None | never; logs every hit (68 in 2s) |
| C — hit-count logpoint | — | `Equals(3)` | `hit-3 v={v}` | None | never; **exactly one** log line |
| D — fault | throws | — | — | All | `ConditionError` + IsFault `LogRecord` |

C deterministically proves the hit-count gate doubles as **logpoint sampling** (finding 33): in a window of
~100 hits the policy logs exactly once. D proves a broken condition fails **loud once** (finding 35) instead
of silently behaving like a never-true condition — the cardinal trap.

## The increment

- **`BreakpointPolicy.cs`** (new) — `SuspendPolicy {All, None}`, `HitCountMode {Equals, AtLeast, Multiple}`,
  `HitCountGate(Mode, Value).Admits(int)`, `LogRecord(TimestampUtc, Message, IsFault)`,
  `BreakpointPolicy(Condition, HitCount, LogMessage, Suspend = All)`.
- **`IDebugEventSink`** — `void OnLog(LogRecord record) { }` added as a **default interface method** so
  existing `NullSink` implementers (probes, tests) compile unchanged; logpoint-aware sinks override.
- **`StopReason.ConditionError`** — distinct stop for a faulting condition; never a silent false.
- **`DebugSession.WaitForPolicyStop(policy, timeout)`** — generalizes `WaitForConditionalStop`:
  per-hit, evaluate the hit-count gate, then the condition gate (tri-state via try/catch → fault
  log + `ConditionError`), then the log action (best-effort render, faulting `{expr}` renders inline),
  then surface (`All`) or auto-resume (`None`).
- **`DebugSession._sink`** — sink reference threaded through `Attach` so the policy loop can emit
  `LogRecord`s (the pump still holds it for `OnEvent`; both methods on the same instance).

## Surfaced bug — and fix

The first probe-28 run hung at config B (10+ minutes). Root cause: `WaitForPolicyStop` originally used the
caller's `timeout` per `WaitForStop` call. A fast-hitting breakpoint (every 20 ms) returns within budget on
every iteration → the timeout never fires → a pure logpoint never terminates. **This is a real engine flaw
the probe correctly surfaced.**

Fix: deadline-based loop. `deadline = UtcNow + timeout`; each iteration computes `remaining = deadline -
UtcNow` and bails when ≤ 0. The same latent flaw existed in `WaitForConditionalStop` (a never-true condition
on a fast breakpoint would hang); fixed both for consistency. Probe 22 (the existing conditional-bp probe)
still PASSES after the fix — no regression. The "checked nothing" discipline at work: the probe is what made
the unbounded-loop bug visible.

## Architectural notes

- **No behavior flags.** `BreakpointPolicy` composes four independent capabilities (each gate/action is a
  nullable delegate; suspend is a closed enum) — exactly the shape finding 33 specified. A conditional
  breakpoint and a logpoint are not two implementations of one class with a mode switch; they are two
  *values* of one record with different optional fields set.
- **Engine emits, host decides destination.** `IDebugEventSink.OnLog` is a structured `LogRecord` —
  timestamp + message + fault marker. Where it lands (ring buffer / file tee / Mercury observations,
  finding 35) is the host's policy. The probe's `RecordingSink` collects into a list for assertions.
- **Default interface method.** `OnLog` as a default no-op spared updating ~17 `NullSink` probe scaffolds.
  A library-friendly extension shape — existing consumers compile; new consumers override.
- **Fault is loud and singular.** A faulting condition surfaces `ConditionError` AND emits a `LogRecord(...,
  IsFault: true)` with the exception message — the user can correct the expression. Re-firing is prevented
  because `ConditionError` is a surface (returns from `WaitForPolicyStop`); the caller decides whether to
  re-arm or fix.

## Scope / next

- **Exception-stop policy.** Probes 36/37 validated the exception-stop mechanism + func-eval on the
  in-flight exception. Reusing `BreakpointPolicy` for exception breakpoints (type filter × first-chance /
  unhandled × `Condition` on the exception) is the natural follow-on; needs an exception-arming surface and
  a small operand-source change in the Roslyn walker (`ex.` rooted).
- **Per-thread suspend.** ICorDebug supports it; the pump is process-level today. A future Suspend axis
  value, not built ahead of need.
- **Structured-sink ring buffer + file tee.** The destination policy in `DrHook.Mcp` (finding 35) is the
  next consumer of `OnLog` — bounded ring buffer drained by a tool, file tee for high-volume runs.
- **`{expr}` interpolation in `LogMessage` via the Roslyn walker.** The probe's renderer is a hand-written
  `ctx => $"v={ReadLocal(ctx,\"v\")}"`. The Roslyn walker (probes 22/25) already does this for conditions;
  routing it to render log strings is one extension (finding 33's "one front end, two consumers").

## References

- Probe: `poc/drhook-engine/28-policy-smoke.cs`, `28-policy-target.cs`
- Fixture: `fixtures/28-policy-osx-arm64-20260522T234808Z.txt` (+ a first run that surfaced the unbounded-loop bug)
- Engine: `BreakpointPolicy.cs` (new), `IDebugEventSink.OnLog` (default), `StopInfo`/`StopReason.ConditionError`,
  `DebugSession.WaitForPolicyStop` (new), `DebugSession.WaitForConditionalStop` (deadline fix),
  `DebugSession._sink` (threaded through)
- Tests: `tests/DrHook.Engine.Tests/BreakpointPolicyTests.cs` (new, 15 cases pinning `HitCountGate.Admits`); 37 total pass
- Findings 33 (four-axis model + the BreakpointPolicy recommendation), 35 (tri-state condition + structured sink),
  22/25 (the Roslyn walker the renderer will share), 36/37 (the exception-stop mechanism this composes with)
- Mercury session 2026-05-22 observation `probe-28-breakpoint-policy`
