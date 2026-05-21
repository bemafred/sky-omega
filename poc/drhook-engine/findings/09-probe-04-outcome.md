# Finding 09: Probe 04 Outcome — PASSED (the boss fight, first build/run)

**Status:**   Probe 04 PASSED on the first build/run — no fix needed. The EXPOSE direction of source-gen COM works: a 38-method `[GeneratedComClass]` callback V-table builds, and `ICorDebug.SetManagedHandler` accepts it. The callback V-table — finding 03's "biggest single interop surface" — is retired as a risk.
**Date:**     2026-05-21
**Probe:**    `poc/drhook-engine/04-managedcallback-vtable-probe.cs`
**Target:**   disposable .NET 10 sleeper, one CLR.

## Run result

```
runtime    : .NET 10.0.0
os-arch    : osx-arm64
target pid : 9600
dbgshim    : .../ms-dotnettools.csharp-2.130.5-darwin-arm64/.debugger/arm64/libdbgshim.dylib
coreclr    : /usr/local/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/libcoreclr.dylib
version    : 00000004;00002580;0000000105CA4000
IUnknown   : 0xAC89602A8
ICorDebug  : source-gen RCW created
Initialize : hr=0x00000000
callback   : CCW created 0xAC8866368 (38-method V-table across 4 IIDs)
SetHandler : hr=0x00000000
SetHandler : ACCEPTED  <-- callback V-table registered (dispatches so far: 0)
Terminate  : hr=0x00000000
released   : callback->0, IUnknown->2; unexpected dispatches: 0
PROBE 04 PASSED
```

No build fix and no signature change — the source-gen COM design (finding 05) and the verified type mapping (finding 08) were both correct as drafted. Third probe in a row to pass first-run (probe 02 needed the `ref`→`in` fix; 03 and 04 ran clean).

## Findings

### 1. The 38-method `[GeneratedComClass]` CCW builds — expose direction works

The source generator accepted all 38 callback-method signatures across four `[GeneratedComInterface]` interfaces and produced a CCW with V-tables for all four IIDs. This was finding 03's flagged "boss fight" — the largest single interop surface in the engine — and it built from declarations alone, no hand-rolled `[UnmanagedCallersOnly]` thunk array. **The hardest interop risk on the Layer 3 path is retired.** Source-gen COM is now validated in both directions: consume (probe 03, RCW) and expose (probe 04, CCW).

### 2. `SetManagedHandler` accepted the managed callback — required IIDs satisfied

`SetManagedHandler(pCallback)` returned `S_OK`. The runtime QI'd our CCW for the callback IIDs it requires (at least `ICorDebugManagedCallback` and `...2`, per finding 03's contract note) and accepted the registration. Confirms that implementing all four sibling interfaces on one `[GeneratedComClass]` covers the contract — the ".NET-2.0-app requires ICorDebugManagedCallback2" requirement is satisfied because the CCW exposes that IID.

### 3. The verified type mapping (finding 08) passed through the build

All 38 signatures used the finding-08 mappings (`LONG`/`BOOL`/enums → `int`; `DWORD`/`ULONG`/`ULONG32`/`CONNID` → `uint`; pointers → `nint`; `[PreserveSig] int`). The generator accepted them and built a V-table the runtime found structurally valid (`SetManagedHandler` succeeded). The build is a structural check, not a dispatch check — slot-by-slot semantic correctness is still probe 05's job (see Deferred).

### 4. Refcount: clean callback teardown

`callback->0` after my `Marshal.Release`: the runtime AddRef'd the callback in `SetManagedHandler` and released it at `Terminate`, so my single release brought it to 0 — clean, no leak. `IUnknown->2` matches probe 03's `StrategyBasedComWrappers` double-reference pattern (identity + interface), GC-managed. The asymmetry (callback reaches 0, IUnknown stays 2) is expected: the callback CCW was only referenced by the runtime (released at Terminate) plus my one ref; the ICorDebug RCW holds its own two refs until GC.

### 5. `dispatches = 0` — nothing fired, as designed

The `StubCallback.Dispatches` counter stayed 0: no callback method was invoked, because probe 04 does not attach (`DebugActiveProcess` is probe 05). This confirms `SetManagedHandler` is pure registration — it stores the handler but does not call into it. A non-zero count here would have been a surprise worth investigating.

## What probe 04 validates — and what it deliberately does not

**Validated:** the callback V-table is *structurally* correct and *acceptable to the runtime* — it builds, the CCW exposes the right IIDs, and `SetManagedHandler` takes it.

**NOT validated (probe 05's job):** slot-dispatch *correctness* — that calling native V-table slot N invokes the C# method intended for slot N. That requires a callback to actually fire, which requires attaching (`DebugActiveProcess`). A misordered or miscounted slot would have passed probe 04 (registration only QIs; it doesn't invoke) and would crash or misbehave in probe 05 when the runtime calls a slot. **This is the load-bearing reason the 38-method transcription order (finding 03 / probe 04) had to be exact even though probe 04 fires nothing.**

## Next — probe 05 (the integration probe)

`DebugActiveProcess(pid, FALSE, &pProcess)` + wait for the first managed callback (typically `CreateProcess` / `LoadModule`) + `Continue` + `Detach`. Probe 05 is where:

1. **Slot-dispatch correctness is validated** — a real callback fires into `StubCallback`; if the V-table order is right, the correct method runs and `Dispatches` increments with sensible arguments.
2. **The macOS entitlement question is re-tested** — probe 02 found no entitlement wall for enumerate + create-interface, but flagged that `DebugActiveProcess` actually attaches and may hit `ptrace`/`task_for_pid`-class restrictions. Probe 05 settles it.
3. **64-bit / pointer-sized types first appear in anger** — the callback args are interface pointers (`nint`), but driving the attached `ICorDebugProcess` (e.g., `ReadMemory`) brings in `CORDB_ADDRESS` (= `ULONG64` → `ulong`) per finding 08.

Probe 05 is genuinely invasive (it attaches and controls the target), unlike probes 02–04. It needs a cooperative, disposable target and careful teardown (`Detach` before the probe exits, or the target stays frozen).

## References

- Probe: `poc/drhook-engine/04-managedcallback-vtable-probe.cs`
- Fixture: `poc/drhook-engine/fixtures/04-managedcallback-vtable-osx-arm64-20260521T025003Z.txt`
- Findings 03 (ICorDebug contract — the 38-method callback surface), 05 (COM interop decision), 06 (probe 02), 07 (probe 03), 08 (type mapping)
- Mercury session 2026-05-21 finding `probe-04-passed`
