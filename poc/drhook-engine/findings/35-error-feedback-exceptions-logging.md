# Finding 35: Error feedback, exception breakpoints, and the logging sink — boundary design

**Status:**   Analysis (Emergence→Epistemics). Three design questions raised once `BreakpointPolicy` came
into view (finding 33): how do malformed conditions/interpolations report failure, how do exception
breakpoints work, and where does logpoint output go. All three are *boundary* concerns — error surface,
a new location kind, and an output sink. Each folds back into the four-axis model. Two of the questions are
decidable now; the exception-breakpoint mechanism has two probe-gated unknowns (probes 26, 27 queued).
**Date:**     2026-05-22
**Drivers:**  Conditions/interpolation can be syntactically wrong → need adequate feedback. Exception
breakpoints are important in some circumstances. Logpoints beg "to where?" and risk unbounded volume.

## The through-line

All three are the missing pieces of `BreakpointPolicy` + the four-axis model (finding 33):

- **Error handling** is the missing **outcome** type — a condition's evaluation has a third result besides
  true/false: *couldn't evaluate*.
- **Exception breakpoints** are a distinct **location** axis (alongside line / function / data-watch).
- **Logging** is a **sink** behind the *log action* — a destination policy, not an engine concern.

## 1. Expression & interpolation errors — two phases, two policies

### Static (parse-time, once at set)

Roslyn's `ParseExpression` does **not** throw on bad input; it returns a tree plus `GetDiagnostics()` with
messages carrying `TextSpan`s. So: **validate at breakpoint-set time, reject before arming, never run an
unparseable condition.** Feedback to the LLM consumer carries the column span + Roslyn's message + a caret
underline — precisely what lets an agent self-correct in one turn. Extend with a set-time tree walk that
rejects node kinds the interpreter doesn't yet support (chained `a.b.c`, etc.), turning a per-hit surprise
into a clean set-time rejection. Same treatment per `{…}` fragment of an interpolation string. **Decidable
now; cheap, deterministic, happens once.**

### Dynamic (eval-time, per hit)

A well-formed condition can still fail live: local not in scope at this line, member absent on the runtime
type, getter throws, eval times out, type mismatch. These already map onto `EvalStatus`
(`Completed / ThrewException / TimedOut / SetupFailed`), plus name-not-found and type-mismatch.

**The load-bearing problem:** `DebugSession.WaitForConditionalStop(Func<IEvalContext, bool>, timeout)` — the
`Func<…, bool>` signature *cannot express "I couldn't evaluate."* Today the Roslyn walker **throws** on
failure, and that exception escapes into the ICorDebug callback loop while the process is stopped — the worst
place for an unhandled managed exception.

**Principle (non-negotiable):** a broken condition must **never** silently behave like a never-true condition.
That is the trap. The predicate must become **tri-state**:

```csharp
enum ConditionResult { True, False, Fault }
// Func<IEvalContext, ConditionOutcome>  where Fault carries a diagnostic
```

On `Fault` the engine **fails loud once** — surfaces a distinct `StopReason.ConditionError` with the
diagnostic — rather than re-faulting every iteration or silently resuming. Interpolation gets the **opposite**
policy: a faulting `{expr}` renders inline as `{expr=<error: not found>}` and the line still emits — a logpoint
must stay useful with one bad field. A real, small engine refinement: widen the predicate return type, catch
at the boundary so nothing throws into the callback loop. **Decidable now.**

## 2. Exception breakpoints — a distinct location axis

The cases conditions can't reach: "something throws deep in a library and gets swallowed — break me at the
throw site" (first-chance, filtered by type), and "why did this crash" (break on unhandled).

**Mechanism:** ICorDebug delivers these via `ICorDebugManagedCallback2::Exception`, whose
`CorDebugExceptionCallbackType` gives **first-chance / user-first-chance / catch-handler-found / unhandled**
directly. Model (correlates to VS Exception Settings):

> **type filter** (with/without subclasses) × **first-chance vs unhandled** × **optional condition on the
> exception object**.

The optional condition is a clean convergence: at an exception stop the thrown object is in hand, so
`ex.Message`-style conditions reuse probe 24's member-resolution machinery verbatim. Exception breakpoints +
conditions are mostly *composition*, not new unknowns. "Break on unhandled" is special — the process is about
to die, so it is post-mortem-ish but lets you inspect the final frame.

**Probe-gated unknowns (Epistemics before Engineering — asserted by neither training nor code-reading):**

- **Probe 26** — does `ICorDebugManagedCallback2::Exception` actually fire with rich info
  (`CorDebugExceptionCallbackType` + exception type) on macOS/ARM64 CoreCLR? The rich data lives on
  `ManagedCallback2`, a separately-QI'd vtable; our 38-method `ManagedCallbackHost` wires `ManagedCallback`.
  Confirm `ManagedCallback2::Exception` is invoked here and that we expose its slots (extract slots/IID from
  cordebug.idl — no guessing).
- **Probe 27** — does func-eval work at an **Exception** stop, not just a **Breakpoint** stop? Probe 23 proved
  re-entrancy at a breakpoint; an exception stop is a different controller state. Analogous, not identical —
  a one-shot probe settles it and unlocks conditional exception breakpoints (`ex.Message contains "timeout"`).

## 3. Logging — to where?

**Reframe that resolves "might become large":** DrHook's consumer is an **LLM agent over MCP**, not a human
watching an Output window. A human scrolls an unbounded log happily; an LLM pays context (and token budget)
per line. So streaming every logpoint hit into an MCP response is the hazard — a logpoint in a hot loop blows
the window.

**Clean separation:** the engine emits structured log **events** to `IDebugEventSink` and chooses **no**
destination. (Today's sink is `OnEvent(string)` — too thin; it needs a structured event: timestamp, breakpoint
id, rendered message, fault markers.) The host (`DrHook.Mcp`) wires the sink; destination is a host policy,
tiered by volume:

- **Bounded ring buffer, drained by a `drhook_log_drain` tool (default).** Fixed capacity (last K bytes /
  N lines), oldest dropped with a visible `"dropped M lines"` marker. Backpressure by construction; the agent
  pulls a digestible window on demand instead of the firehose. Right default for the MCP shape.
- **File tee (opt-in, high volume).** Append to a path; agent reads ranges / greps via existing file tools.
  For "capture everything, analyze after." Pairs with the append-only bias.
- **Mercury observations (structured, low-volume).** Each hit a timestamped observation — the bitemporal
  substrate makes the trace valid-time-queryable. Native answer for "log a few semantically-meaningful
  events," distinct from high-volume raw tracing.

**Volume guards regardless of sink:** the **hit-count gate already in `BreakpointPolicy` doubles as sampling**
(log every Nth hit) — the volume problem is partly solved for free by the gates axis. Add a global byte cap
and *visible* truncation accounting; silent loss is exactly the "untrusted nothing" the checked-nothing
discipline rejects.

## Net

| Question | Decidable now | Needs a probe |
|---|---|---|
| Expr/interp errors | parse-validate at set-time (Roslyn diagnostics + span); widen predicate to tri-state `True/False/Fault`; best-effort interpolation rendering | — |
| Exception breakpoints | model = type filter × first-chance/unhandled × optional condition (reuses member resolution) | **probe 26** (which callback fires here); **probe 27** (func-eval at an exception stop) |
| Logging sink | engine emits structured events to a widened `IDebugEventSink`; host = ring-buffer default + file tee + Mercury tier; hit-count gate = sampling; visible truncation | — |

## References

- Finding 33 (breakpoint-types model: location · gates · action · suspend; `BreakpointPolicy`)
- Findings 31 (func-eval re-entrancy at a breakpoint stop — the analogue probe 27 extends), 32 (member
  resolution — reused by exception conditions), 34 (Roslyn walker — reused by interpolation)
- Engine: `DebugSession.WaitForConditionalStop` (predicate widening), `EvalStatus`, `IDebugEventSink`
  (structured-event widening), `Interop/ManagedCallbackHost` (ManagedCallback2 slots for probe 26)
- ADR-006 Phase 4 — `BreakpointPolicy` + exception-breakpoint probes queued from here
