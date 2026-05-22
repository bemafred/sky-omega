# Finding 27: Probe 19 Outcome — PASSED: func-eval WORKS in our engine (the netcoredbg deadlock is not a platform limit)

**Status:**   **PASSED, 4/4 — a landmark, assumption-overturning result.** ICorDebug function
evaluation executes managed code in the debuggee on macOS/ARM64 **without deadlocking** in our
engine. At a stop, `DebugSession.TryEvalStaticCall` func-eval'd `Probe.Answer()` and got
`EvalStatus.Completed` with the result `I4 = 42`, four times running. We had been carrying "func-eval
is broken on mac" as fact; it was never tested in our engine. **It was netcoredbg's bug, not the
platform's.**
**Date:**     2026-05-22
**Probe:**    `poc/drhook-engine/19-funceval-smoke.cs` + `19-eval-target.cs`

## Why this matters

The entire reason DrHook.Engine exists is that netcoredbg's func-eval deadlocks on macOS/ARM64, which
killed conditional breakpoints and expression evaluation. ADR-006 recorded that the deadlock was
*netcoredbg-localized* (vsdbg/Rider eval fine → the platform surface is reachable), but we never
verified it for our own ICorDebug usage — and I had been designing around the assumption that we'd
inherit the deadlock. Probe 19 tests it directly. **It works.** That changes the eval strategy
entirely:

- The full path is open: **Roslyn parses the C# expression → func-eval executes method/property calls
  in the debuggee → ICorDebug reads locals/args** → result. `s.Length`, `seq.Count()`, arbitrary calls
  are reachable. The LLM consumer writes standard C#; no custom dialect.
- DrHook.Engine becomes **strictly more capable than netcoredbg** — it does the thing netcoredbg
  could not do on this platform. The "we have the advantage" thesis is now evidence, not hope.

## How it was tested (engine scaffold, minimal + reuses everything)

`EvalComplete`@7 / `EvalException`@8 callbacks were classified as **stopping** events
(`CallbackKind` + `StopReason.EvalComplete`/`EvalException`). `Eval.cs`: `ICorDebugThread.CreateEval`@17
→ `ICorDebugEval.CallFunction`@3 (static, 0 args) → `GetResult`@10 (slots from cordebug.idl).
`DebugSession.TryEvalStaticCall`: resolve the method (existing nav+metadata) → `CreateEval` on the
stop thread → `CallFunction` → `Resume` (the worker `Continue`s, running the eval) → `WaitForStop(timeout)`.
**The timeout *is* the deadlock detector** — had func-eval hung like netcoredbg, `WaitForStop` would
return null → `TimedOut`. It returned `EvalComplete` with the result instead.

```
stopped    : Break — func-evaluating Probe.Answer() …
eval status: Completed
eval result: elementType=0x08  value=42
PROBE 19 PASSED — func-eval WORKS in our engine.
```

## Honest scope (what's proven vs next)

Proven: **func-eval of a static, parameterless method returning a primitive completes without
deadlock, reproducibly.** The decisive platform question — does executing managed code via func-eval
hang on mac/arm64 — is answered **no, not for us.**

Not yet exercised (interop breadth, not platform unknowns):
- arguments + instance methods (`CallFunction` with args / `this`; `CallParameterizedFunction` for generics),
- reference-typed results (dereference),
- `ICorDebugEval::Abort` for runaway/deadlocking evals (a real eval can still hang on a lock in the
  target — we need Abort + a timeout policy as a safety net),
- the Roslyn front end that turns a C# expression into the func-eval calls + local reads.

So the eval *architecture* is now decided (Roslyn + func-eval), and the remaining work is breadth and
safety, not a viability gamble.

## Discipline note

This is the EEE lesson in miniature: a 45-day-old observation ("func-eval deadlock on macOS/ARM64")
had hardened, in my framing, into a platform fact. It was a *netcoredbg* fact. One probe falsified the
inherited assumption. The memory `project_drhook_eval_dead` is corrected accordingly.

## References

- Probe: `poc/drhook-engine/19-funceval-smoke.cs`, `19-eval-target.cs`
- Fixture: `fixtures/19-funceval-osx-arm64-20260522T124405Z.txt`
- Engine: `Interop/Eval.cs`, `DebugSession.TryEvalStaticCall` + `EvalStatus`, `CallbackPump`/`StopInfo` (eval stopping)
- ADR-006 Open Question 2 (func-eval) — resolved: works; eval path is Roslyn + func-eval
- Findings 16 (stopping model), 26 (locals — the read side), 18 (metadata IID lesson on not guessing)
- Mercury session 2026-05-21 observation `probe-19-funceval-works`
