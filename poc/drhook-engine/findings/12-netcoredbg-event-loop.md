# Finding 12: netcoredbg Event Loop — the callback must (likely) be native, not a managed CCW

**Status:**   Epistemics — root-cause hypothesis for probe 05's callback-delivery blocker, grounded in netcoredbg's source. The decisive confirmation is probe 06 (an `[UnmanagedCallersOnly]` raw-vtable callback).
**Date:**     2026-05-21
**Read:**     `Samsung/netcoredbg` `src/debugger/manageddebugger.cpp`, `managedcallback.cpp`, `managedcallback.h`.

## What netcoredbg's source establishes

1. **Attach-to-running IS supported on CoreCLR and DOES deliver a callback.** The `ManagedCallback::CreateProcess` handler carries the comment *"in case `attach` CoreCLR also call CreateProcess() that call this method."* netcoredbg's attach-complete signal (`NotifyProcessCreated` → `m_processAttachedState = Attached`) is set *from inside the `CreateProcess` callback*. So on attach, CoreCLR fires `CreateProcess` into the debugger's callback. **Our probe got zero callbacks — so this is not a platform limitation; it's specific to our setup.**

2. **netcoredbg's callback is NATIVE C++.** `class ManagedCallback final : public ICorDebugManagedCallback, ICorDebugManagedCallback2, ICorDebugManagedCallback3 …` — a native object directly implementing the COM vtables. mscordbi calls it **native-to-native** on mscordbi's own event-delivery thread, with **no transition into a managed runtime**.

3. **No special threading or pump for attach.** `ManagedDebugger::Attach` just sets `m_startMethod = StartAttach` and calls `RunIfReady → AttachToProcess`, which does QI → `Initialize` → `SetManagedHandler` → `DebugActiveProcess`, then waits on a CV. mscordbi delivers callbacks on its own thread; the debugger doesn't run an explicit event pump between `DebugActiveProcess` and the first callback. **This is the same shape as our probe** (attach, then wait) — so a missing pump is not the difference.

4. **Callbacks enqueue to a `CallbacksQueue`; `Continue` is queue-driven, not per-callback** (only 2 inline `Continue` calls, both eval special-cases). So our probe's "no `Continue`" was *not* the cause of zero delivery — delivery is upstream of the queue, and we got nothing to enqueue.

## The differentiator — and the hypothesis

Everything netcoredbg does around attach matches our probe **except one thing: netcoredbg implements the callback in native C++; our probe implements it as a *managed* `[GeneratedComClass]` ComWrappers CCW.** netcoredbg never exercises managed-CCW delivery, so its source cannot validate it — and probe 05 indicates it does **not** happen.

**Leading hypothesis (well-grounded, not yet proven):** mscordbi's callback delivery to a *managed ComWrappers CCW* does not work in this configuration. mscordbi's native event-delivery thread calls the callback vtable directly; for our CCW that call must transition into the debugger's *own* managed runtime to reach the C# stub, and that transition does not occur (or is not supported on the mscordbi event thread). `SetManagedHandler` succeeds (registration / QI — probe 04), but the runtime never *invokes* the managed callback (probe 05). Registration-valid, delivery-blocked.

## Architectural implication for DrHook.Engine

- **Consuming ICorDebug from managed code works** — RCW via source-gen COM, calling `ICorDebug`/`ICorDebugController`/`ICorDebugProcess` methods (probes 02/03/05 all called managed→native fine).
- **Implementing the callback that mscordbi calls back into likely needs to be NATIVE** — a vtable mscordbi can call without a managed transition, exactly as netcoredbg does. Events received natively, then marshaled *up* to managed (via a queue polled from managed, a native→managed upcall at a safe point, or IPC).
- This makes the engine's interop **hybrid**: source-gen COM for the consume direction; a native (or raw-function-pointer) callback for the receive direction. Probe 04's "expose direction validated" must be narrowed: it validated *registration* of a managed CCW, not *delivery* to it.

## Two sub-hypotheses → the decisive probe (06)

The hypothesis splits on *what kind* of managed-callability fails:

- **A1 — the managed transition itself fails on mscordbi's event thread.** Then both a ComWrappers CCW and an `[UnmanagedCallersOnly]` function-pointer vtable fail, and the engine needs a **fully native** callback (C/C++ shim) that handles events natively and marshals up via a non-callback mechanism. This dents pure-BCL (a small native component).
- **A2 — specifically the ComWrappers *object* CCW dispatch fails, but a raw `[UnmanagedCallersOnly]` function-pointer vtable works.** Then the engine stays **BCL-only**: hand-roll the ICorDebugManagedCallback vtable as an array of `[UnmanagedCallersOnly]` static-method function pointers (managed methods, raw native-callable entry points, no ComWrappers object).

**Probe 06 (decisive):** build the callback vtable from `[UnmanagedCallersOnly]` static methods — a native-callable function-pointer vtable, not a ComWrappers object — register it via `SetManagedHandler`, attach, and test delivery. If callbacks fire → A2 (BCL-only path survives, with a hand-rolled vtable). If still nothing → A1 (native shim required). Either outcome is decisive and reshapes the engine's callback design.

## Honesty note

This is a strong inference from (a) netcoredbg getting attach callbacks with a native callback, (b) our probe getting none with a managed CCW, (c) everything else being equivalent. It is **not yet proven** that the managed CCW is the specific cause — probe 06 is the experiment that confirms or refutes it. I stopped here rather than guess-permuting because the netcoredbg read produced a concrete, testable hypothesis and a clear decisive probe.

## References

- `Samsung/netcoredbg/src/debugger/managedcallback.{cpp,h}` — native `ManagedCallback`, `CreateProcess` attach comment, `CallbacksQueue`
- `Samsung/netcoredbg/src/debugger/manageddebugger.cpp` — `Attach`/`RunIfReady`/`AttachToProcess`, `NotifyProcessCreated`
- Finding 10 (probe 05 blocker), Finding 11 (library ruled out)
- Finding 04 (netcoredbg attach flow), Finding 05 (source-gen COM decision — consume direction stands; expose-direction *delivery* now in question)
- Mercury session 2026-05-21 finding `netcoredbg-event-loop-read`
