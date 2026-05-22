# Finding 33: Breakpoint types across VS / VS Code / Rider — a correlated model, mapped to DrHook.Engine

**Status:**   Analysis (Emergence→Epistemics). Surveys the breakpoint feature sets of the three
mainstream .NET debuggers, factors them into one orthogonal model, and maps each axis to DrHook.Engine's
current substrate. The headline finding: a *logpoint* ("logs but doesn't stop") is **not a new breakpoint
type** — it is the `(action = log) × (suspend = none)` corner of a matrix DrHook.Engine already half-occupies.
**Date:**     2026-05-22
**Drivers:**  Conditional-breakpoint work (findings 30–32) raised the question of what *else* a breakpoint
can be. Martin asked for the survey explicitly, noting the three IDEs "should correlate fairly well." They do.

## What each IDE exposes

**Visual Studio.** A breakpoint carries *Conditions* (Conditional Expression, Hit Count, Filter) and
*Actions*. The Action "Show a message in the Output Window" plus the **"Continue execution"** checkbox turns
the breakpoint into a **tracepoint** — the gutter glyph changes from a circle to a diamond, and it logs
without pausing. Also: function breakpoints (by name), data breakpoints (on a value change), exception
settings.

**VS Code (Debug Adapter Protocol).** A `SourceBreakpoint` has exactly three optional fields that matter
here: `condition`, `hitCondition`, and `logMessage`. A **logpoint** is *not* a distinct protocol type — it is
simply a breakpoint whose `logMessage` is set; it "does not break but logs a message," and `{expr}` fragments
in the message are evaluated and interpolated. Logpoints can additionally carry a condition and/or hit count.

**Rider / IntelliJ.** A breakpoint has *Condition*, *Hit count* ("pass count"), thread filter ("Suspend only
on specific thread"), **Dependent breakpoints** (armed only after another breakpoint is hit), and a
**Suspend** policy of **All / Thread / None**. Setting `Suspend: None` + an **"Evaluate and log"** action is
Rider's logpoint — the most honest framing of the three: a logpoint is a breakpoint that doesn't suspend.

## The correlated model — four orthogonal axes

Every breakpoint in all three IDEs factors into **location + gates + action + suspend policy**:

| Axis | Variants | VS | VS Code | Rider |
|------|----------|----|---------|-------|
| **1. Location** | line / function / exception / data-or-field-watch | all four | line, function, exception, data, instruction | all four |
| **2. Gates** (all must pass) | condition · hit-count · thread/process filter · dependency | cond, hit, filter | `condition`, `hitCondition` | cond, pass-count, thread, **dependent** |
| **3. Action** | break · log-a-message (interpolated) | message action | `logMessage` | "Evaluate and log" |
| **4. Suspend** | all · current-thread · none | "Continue execution" ⇒ none | logMessage ⇒ none | **All / Thread / None** |

**The unifying insight.** "Logs but doesn't stop" is the intersection of two independent axes — Action = *log*
and Suspend = *none* — not a fourth breakpoint species. DAP proves this is the right abstraction: the wire
protocol has no "logpoint" enum; `logMessage != null` *is* the logpoint. VS expresses the same thing as a
mode toggle (the "Continue execution" checkbox); Rider as `Suspend: None`. Same machine, three skins.

**"Optionally on the same line"** (Martin's phrasing): VS and Rider let a breakpoint and a tracepoint/logpoint
coexist on one line because each is a distinct breakpoint *object* bound to the same location. The general
substrate must therefore allow ≥1 policy per location, not one-policy-per-line.

## Mapping to DrHook.Engine

| Axis · variant | DrHook.Engine today | Gap |
|---|---|---|
| Location · line | `SetBreakpointAtLine` (probe 17) | ✓ |
| Location · function/method | `SetBreakpoint` by method token (probe 12) | ✓ |
| Location · exception | callback classifies exceptions; DrHook MCP exposes `drhook_step_break_exception` | engine-level break-on-throw path TBD |
| Location · data/field-watch | — | not built (ICorDebug has no cheap data-watch; emulated via field re-read) |
| Gate · condition | `WaitForConditionalStop(Func<IEvalContext,bool>)` + Roslyn front end (probes 22, 25) | ✓ |
| Gate · hit-count | — | **trivial**: a counter in the hit handler, gate before surfacing |
| Gate · thread filter | pump/`StopInfo` carry the thread | compare-and-resume — not wired |
| Gate · dependency | — | an `armed` flag toggled by another bp's hit; composes |
| Action · break/suspend | the whole stopping model (findings 16, 18) | ✓ |
| Action · **log message** | — | **the headline new capability — see below** |
| Suspend · all | process-level Stop/Continue | ✓ |
| Suspend · none | `WaitForConditionalStop` auto-resumes when the predicate is false | ✓ (already exercised) |
| Suspend · current-thread | — | ICorDebug supports per-thread; our pump is process-level — future |

### The logpoint already lives inside `WaitForConditionalStop`

This is the load-bearing engineering observation. At each breakpoint hit, `WaitForConditionalStop` already:

1. evaluates a predicate against the live frame — **func-eval composes here** (finding 31);
2. if false → **auto-resumes without surfacing** — this *is* Suspend = none;
3. if true → surfaces the stop.

A logpoint is the same loop with a different leaf:

1. at each hit, render the **log message** — interpolating `{expr}` via the *same* func-eval substrate the
   conditional walker uses (`TryEvalMemberCall`, probe 24);
2. emit the rendered line to `IDebugEventSink`;
3. **always auto-resume** (never surface).

So the generalization is not a new mechanism — it lifts the per-hit leaf from "return bool" to a small
**hit-action policy** whose fields are the four axes, each already validated in isolation:

```csharp
sealed record BreakpointPolicy(
    Func<IEvalContext, bool>?   Condition,   // null ⇒ always
    HitCountGate?               HitCount,    // null ⇒ every hit
    Func<IEvalContext, string>? LogMessage,  // non-null ⇒ logpoint (render + emit)
    SuspendPolicy               Suspend);    // All | None  (Thread later)
```

The hit handler runs: gates (`Condition && HitCount`) → if `LogMessage` present, render + emit → suspend per
`Suspend`. **Conditional breakpoint and logpoint fall out as two configurations of one policy.** This is the
[[feedback_no_behavior_flags]]-compliant shape: not a `bool isLogpoint` inside a class, but a composed policy
object of independent capabilities. (A genuinely distinct *location* kind — exception, data-watch — still gets
its own concrete type; the policy varies the gate/action/suspend behavior *within* a location kind.)

### Convergence: the member-access walker serves both

Message interpolation (`"box.Size = {box.Size}"`) reuses the **exact** Roslyn path probe 25 builds for
conditions: `MemberAccessExpressionSyntax → TryEvalMemberCall`, except the result is rendered to string instead
of compared. So the member-access walker (probe 25) is a prerequisite for conditions **and** logpoints — one
front end, two consumers.

## Scope boundaries (honest)

- **Per-thread suspend**: ICorDebug supports it; our pump stops the whole process. A real axis, deferred.
- **Data/field watchpoints**: no cheap ICorDebug primitive; would be emulated (re-read a field each hit and
  diff) — a different cost class. Deferred.
- **Dependent breakpoints**: an `armed` flag toggled by a predecessor's hit. Composes with the policy; deferred.
- **Exception breakpoints**: the callback already classifies exceptions and DrHook MCP surfaces a tool; the
  engine-level "break on throw of type T (first-chance/unhandled)" path is not yet built.

## Recommendation

1. **Adopt the four-axis model as the engine's breakpoint vocabulary** — it matches DAP, so a future DAP/MCP
   surface maps 1:1 with no impedance.
2. **Build `BreakpointPolicy` next** (after probe 25), implementing condition + hit-count gates, the log action,
   and Suspend ∈ {All, None}. This is small and entirely composed from validated parts.
3. Track per-thread suspend, data-watch, dependent, and exception-through-engine as named future axes (not
   YAGNI — they are known consumers per [[feedback_infrastructure_no_yagni]]), but do not build ahead of need.

## Sources

- Visual Studio — [Use the right type of breakpoint](https://learn.microsoft.com/en-us/visualstudio/debugger/using-breakpoints?view=visualstudio),
  [Log info with tracepoints](https://learn.microsoft.com/en-us/visualstudio/debugger/using-tracepoints?view=visualstudio),
  [Tracepoints: Debug with less clutter (VS Blog)](https://devblogs.microsoft.com/visualstudio/tracepoints/)
- VS Code — [Debug code with Visual Studio Code](https://code.visualstudio.com/docs/debugtest/debugging) (Logpoints, conditional/hit-count breakpoints)
- Rider / IntelliJ — [Breakpoints (Rider)](https://www.jetbrains.com/help/rider/Using_Breakpoints.html),
  [Breakpoints (IntelliJ IDEA)](https://www.jetbrains.com/help/idea/using-breakpoints.html) (Condition, pass count, Evaluate and log, Suspend All/Thread/None, dependent breakpoints)

## References

- Findings 30 (Roslyn front end), 31 (func-eval inside a conditional predicate), 32 (general member resolution)
- Engine: `DebugSession.WaitForConditionalStop`, `TryEvalMemberCall`, `IDebugEventSink`, `IEvalContext`
- ADR-006 (Phase 4 — conditions/eval); the `BreakpointPolicy` recommendation feeds the next ADR-006 increment
