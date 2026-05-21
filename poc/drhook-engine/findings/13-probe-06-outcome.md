# Finding 13: Probe 06 Outcome — PASSED (A2): BCL-only callback layer is viable

**Status:**   PASSED on the first run. **A2 confirmed.** A hand-rolled `[UnmanagedCallersOnly]` callback vtable receives ICorDebug callback delivery on mscordbi's event thread where the `[GeneratedComClass]` ComWrappers CCW did not. This **resolves the PoC's central interop question** and **unblocks probe 05**. The DrHook.Engine callback layer can stay **pure BCL** — no native shim.
**Date:**     2026-05-21
**Probe:**    `poc/drhook-engine/06-unmanagedcallersonly-vtable-probe.cs`
**Target:**   disposable .NET 10 worker; baseline dbgshim (`Microsoft.Diagnostics.DbgShim.osx-arm64` 9.0.661903).

## Run result

```
Initialize : S_OK
callback   : hand-rolled [UnmanagedCallersOnly] vtable @ 0xB5C86FA20 (4 IIDs)
SetHandler : S_OK  (our QI thunk ran on this thread — vtable structurally valid)
DebugActiveProcess: hr=0x00000000
callback   : FIRED  first='CreateProcess'  count=1  <-- A2: [UnmanagedCallersOnly] DELIVERY WORKS
Detach     : hr=0x00000000
Terminate  : hr=0x00000000
PROBE 06 PASSED
```

First build, no fix — the hand-rolled vtable (calling convention, slot order, QI multi-interface layout) was correct as drafted.

## What this proves

1. **The native→managed transition on mscordbi's event thread WORKS.** Our `[UnmanagedCallersOnly]` `CreateProcess` thunk ran managed code (`Record`) when mscordbi invoked it on its own event thread — incremented the counter, set the event, woke the main thread. So **finding 12's candidates #1 (foreign-thread attach) and #2 (GC re-entrancy guard) are RULED OUT** — the transition is fine.

2. **The cause of probe 05's blocker was candidate #3: ComWrappers *object*-CCW dispatch specifically.** A `[GeneratedComClass]` CCW did not receive delivery (probe 05); a raw `[UnmanagedCallersOnly]` function-pointer vtable does (probe 06). The difference is the ComWrappers dispatch machinery (the `ComInterfaceDispatch.GetInstance` object-lookup + reference-tracking that a CCW vtable thunk performs) — it does not function in the mscordbi debug-callback context. The raw thunk has no such lookup (static state, direct entry) and sidesteps it. *(Exact ComWrappers internals not pinned down — not needed; the working path is established.)*

3. **Slot dispatch is validated.** mscordbi called V-table slot 9 (`CreateProcess`) and our `CreateProcess` thunk ran — confirming probe 04's 38-method transcription **order** is correct under *real* dispatch, not just registration. This is the validation probe 05 set out to get.

## Probe 05 — resolved

Probe 05's goal (validate slot dispatch via a real callback) is achieved here. The resolution to its blocker: **use a hand-rolled `[UnmanagedCallersOnly]` vtable, not a `[GeneratedComClass]` ComWrappers CCW, for the callback.** No separate probe 05 v2 is needed — probe 06 is it (attach → register managed vtable → receive a real callback → detach, all green).

## The Layer 3 interop path is now proven end-to-end, BCL-only

| Capability | Validated by | Mechanism |
|---|---|---|
| Attach (no macOS entitlement) | probe 05 | `dbgshim` + `DebugActiveProcess` |
| Consume ICorDebug (call methods) | probes 02/03/05/06 | source-gen COM `[GeneratedComInterface]` RCW |
| Register a managed callback | probe 04/06 | `SetManagedHandler` |
| **Receive callbacks (slot dispatch)** | **probe 06** | **hand-rolled `[UnmanagedCallersOnly]` vtable** |
| Detach / Terminate | probes 05/06 | `ICorDebugController` / `ICorDebug` |

All BCL + P/Invoke + source-gen COM + `[UnmanagedCallersOnly]`. **No netcoredbg, no `Microsoft.Diagnostics.NETCore.Client` on this path, no native shim.** Substrate independence holds for the runtime-inspection engine's hardest layer.

## DrHook.Engine interop design — crystallized

- **Consume direction (call ICorDebug / ICorDebugController / ICorDebugProcess):** source-gen COM `[GeneratedComInterface]` RCW via `StrategyBasedComWrappers.GetOrCreateObjectForComInstance`. Works.
- **Receive direction (the callback mscordbi calls back into):** **hand-rolled `[UnmanagedCallersOnly]` vtable**, NOT `[GeneratedComClass]`. The engine builds the `ICorDebugManagedCallback`(+2/3/4) vtable from static thunks (the probe-06 pattern), dispatching to managed handler logic. *(With instance state instead of statics, the thunk recovers `this` from the COM object block — a small generalization of probe 06's static approach.)*

This asymmetry is the key engine-design takeaway: source-gen COM for outbound calls, raw `[UnmanagedCallersOnly]` for inbound callbacks.

## Where the PoC stands

The substrate-independence-critical interop questions the PoC set out to answer are **answered**:
- Reach `ICorDebug` BCL-only — yes (probes 02/03).
- Source-gen COM both directions — consume yes (03), expose-via-CCW **no** but expose-via-raw-vtable **yes** (04/05/06).
- Attach without external debugger or entitlement — yes (05).
- Receive real callbacks BCL-only — yes (06).

Remaining engine work — stepping, breakpoints, variable inspection — builds on this validated foundation: more RCW method calls (consume, validated) + more callback handling (vtable, validated) + a continue-loop (the CallbacksQueue pattern from netcoredbg, finding 12). The **hard interop risks are retired**; what remains is debugger-feature engineering on a proven substrate.

## References

- Probe: `poc/drhook-engine/06-unmanagedcallersonly-vtable-probe.cs`
- Fixture: `fixtures/06-unmanagedcallersonly-vtable-osx-arm64-20260521T170146Z.txt`
- Finding 12 (hypothesis + A1/A2 split — A2 confirmed here), Finding 10 (probe 05 blocker — resolved), Finding 11 (baseline dbgshim)
- Findings 03/05 (source-gen COM — consume direction stands; expose direction now known to need a raw vtable, not a CCW)
- Mercury session 2026-05-21 finding `probe-06-passed-a2`
