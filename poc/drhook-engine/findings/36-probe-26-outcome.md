# Finding 36: Probe 26 Outcome — PASSED: ICorDebugManagedCallback2::Exception fires with rich info

**Status:**   **PASSED, 2/2** (clean exit 0 both runs; +1 in-process unit test). The first probe-gated
unknown from finding 35 is resolved: on macOS/ARM64 CoreCLR the runtime **invokes**
`ICorDebugManagedCallback2::Exception`, the pump classifies it as a **stopping** event carrying the
`CorDebugExceptionCallbackType`, and the thrown type is **resolved from the live exception** object with no
hardcoding. The exception location axis (finding 35 / 33) is mechanically viable.
**Date:**     2026-05-22
**Probe:**    `poc/drhook-engine/26-exception-smoke.cs` + `26-exception-target.cs`

## What was already there vs. what this proved

The `Exception2` thunk, the `_v2` (`ManagedCallback2`) vtable, and `QueryInterface` for
`IID_ICorDebugManagedCallback2` (`250E5EEA-…`) were **already wired** in the 38-method host — but the thunk
discarded the rich args (`Fire(p, "Exception2")`, Informational, auto-continued). Unproven: whether the
runtime actually *calls* it here, and whether the `CorDebugExceptionCallbackType` + thrown type are usable.
This probe made it observable and confirmed all three.

```
expecting  : ICorDebugManagedCallback2::Exception (FirstChance) for ProbeException
exception  : phase=FirstChance  type=ProbeException
PROBE 26 PASSED — ManagedCallback2::Exception fires with rich info: ProbeException at FirstChance,
                  type resolved from the live exception (no hardcoding).
```

Notably the **first** exception stop after resume was our `ProbeException` — no stray BCL/JIT first-chance
noise in the post-attach window (the bounded resume-past loop was there for robustness but wasn't needed).

## The increment (engine, BCL-only)

- **`CallbackKind.Exception`** (new stopping kind) + **`CallbackEvent.Detail`** + a `detail` arg on
  `IManagedCallbackSink.OnCallback` to carry a callback-specific scalar.
- **`Exception2` thunk** now `Fire(p, CallbackKind.Exception, "Exception", a, t, evt)` — stopping, forwarding
  the thread and the `CorDebugExceptionCallbackType` as `Detail`.
- **`StopReason.Exception`** + **`ExceptionStopKind`** enum (`None/FirstChance/UserFirstChance/`
  `CatchHandlerFound/Unhandled` = 0..4, values from cordebug.idl) + `StopInfo.ExceptionKind`. The pump
  constructs the exception `StopInfo` from `e.Detail`.
- **`ExceptionInspector.CurrentExceptionTypeName(thread)`** — `ICorDebugThread.GetCurrentException`@10 →
  `ReferenceValue.Dereference`@10 → `ObjectValue.GetClass`@7 → `Class.GetModule`@3/`GetToken`@4 →
  `MetadataResolver.TypeNameFromToken` (new public method over the existing `GetTypeDefProps`@12). The same
  value→class→metadata chain as probe 24's `MemberResolver`; exceptions are reference objects so the
  `ObjectValue` path applies (no `ICorDebugType` needed).
- **`DebugSession.GetCurrentExceptionTypeName()`** — reads from the stop thread, symmetric with
  `GetLocals`/`GetArguments`.
- **Slots/IIDs**: `GetCurrentException`@10 verified from cordebug.idl (consistent with the codebase's
  `CreateEval`@17); `CorDebugExceptionCallbackType` values verified from the IDL. Nothing guessed.

In-process unit test `Exception2Thunk_ClassifiesAsStopping_AndForwardsCallbackType` (22 tests pass) drives the
v2 vtable slot 7 directly and asserts kind=Exception, thread forwarded, detail=`FIRST_CHANCE` — CI-safe, no
debuggee.

## Architectural note — "stopping" is classification, not policy

Classifying every exception as a *stopping* callback is correct at the pump layer: the runtime IS synchronized
when `Exception` fires, so it must surface a stop the caller controls (never an auto-Continue). Whether to
actually *halt the user* is a POLICY decision for the breakpoint layer above — exactly like a breakpoint hit
is "stopping" at the pump but the conditional layer decides whether to surface it. The eventual
exception-breakpoint feature auto-resumes an exception with no matching filter (type/first-chance/condition),
just as `WaitForConditionalStop` auto-resumes a false condition. So this probe does **not** make "stop on every
exception" a permanent default — it validates the mechanism the policy layer will gate.

## Scope / next

- **Probe 27** (blocked-by → now unblocked): func-eval at an Exception stop. `GetCurrentException` is a plain
  read and worked here; func-eval is a different controller interaction (the IDL notes "FuncEval will clear
  out the exception object on setup and restore it on completion"). Probe 27 settles conditional exception
  breakpoints (`ex.Message contains "…"`), reusing probe 24's member resolution on the exception object.
- **Filtering** (the policy layer): type filter (±subclasses), first-chance vs unhandled selector, optional
  condition — the `BreakpointPolicy` work (finding 33/35). Subclass matching needs walking the type's base
  chain (`GetTypeDefProps` `ptkExtends`), a follow-on.
- **`dwFlags`** (`CorDebugExceptionFlags`: `CAN_BE_INTERCEPTED`=1) is currently dropped; relevant only if we
  later intercept (rewind to before the throw), a separate capability.

## References

- Probe: `poc/drhook-engine/26-exception-smoke.cs`, `26-exception-target.cs`
- Fixture: `fixtures/26-exception-osx-arm64-20260522T173625Z.txt` (+ a second clean run, exit 0)
- Engine: `IManagedCallbackSink` (+Detail), `CallbackPump` (Exception mapping), `StopInfo`/`ExceptionStopKind`,
  `Interop/ManagedCallbackHost` (Exception2 thunk), `Interop/ExceptionInspector` (new),
  `MetadataResolver.TypeNameFromToken`, `DebugSession.GetCurrentExceptionTypeName`
- Test: `tests/DrHook.Engine.Tests/ManagedCallbackHostTests.cs` (Exception2 thunk)
- Findings 35 (probe-gated unknowns), 33 (exception = a location axis), 24 (the member-resolution chain reused)
- Mercury session 2026-05-22 observation `probe-26-exception-callback`
