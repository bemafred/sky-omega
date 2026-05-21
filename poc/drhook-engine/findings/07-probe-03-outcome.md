# Finding 07: Probe 03 Outcome — PASSED (first build, no fix)

**Status:**   Probe 03 PASSED on the first build — no compile fix needed. Source-generated COM works in a .NET 10 file-based app, and the `ICorDebug` `Initialize`/`Terminate` lifecycle behaves per the cordebug.idl contract.
**Date:**     2026-05-21
**Probe:**    `poc/drhook-engine/03-icordebug-lifecycle-probe.cs`
**Target:**   disposable .NET 10 sleeper, one CLR.

## Run result

```
runtime    : .NET 10.0.0
os-arch    : osx-arm64
target pid : 5861
dbgshim    : .../ms-dotnettools.csharp-2.130.5-darwin-arm64/.debugger/arm64/libdbgshim.dylib
coreclr    : /usr/local/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/libcoreclr.dylib
version    : 00000004;000016e5;0000000109414000
IUnknown   : 0x100DE76D8
ICorDebug  : source-gen RCW created (StrategyBasedComWrappers)
Initialize : hr=0x00000000
Terminate  : hr=0x00000000
released   : my IUnknown ref -> 2 (ComWrappers RCW ref GC-managed)
PROBE 03 PASSED
```

No build fix was required — the source-gen COM design from finding 05 (`[GeneratedComInterface]` + `[PreserveSig] int` + declaration-order V-table + `StrategyBasedComWrappers`) compiled and ran correctly on the first attempt. Contrast probe 02, which needed the `ref`→`in` `QueryInterface` fix.

## Findings

### 1. Source-generated COM works in a .NET 10 file-based app — tooling question resolved YES

The `[GeneratedComInterface]` source generator (`Microsoft.Interop.ComInterfaceGenerator`, bundled in the SDK) activated for a file-based app (`dotnet run probe.cs`) with only `#:property AllowUnsafeBlocks=true`. No `.csproj`, no package reference, no extra MSBuild property. **Engine probes can stay file-based through the source-gen-COM layers** — probe 04's callback V-table doesn't force a project conversion.

### 2. The ICorDebug Initialize/Terminate lifecycle works via the typed wrapper

`Initialize()` → `0x00000000` (S_OK), `Terminate()` → `0x00000000` (S_OK). Finding 03's contract holds: `Initialize` + `Terminate` with no debuggee attached is a valid lifecycle (the "Terminate forbidden until ExitProcess fires for all attached processes" constraint doesn't apply when nothing is attached). The calls dispatched through the source-gen RCW to the correct native V-table slots.

### 3. A minimal `[GeneratedComInterface]` (a prefix of the V-table) dispatches correctly

Declaring only `Initialize` (slot 3) + `Terminate` (slot 4), in IDL order, with no other methods, produced a working RCW that called the right native functions. Confirms the design assumption from finding 05: source-gen COM interfaces can be partial views of a larger native interface, as long as the declared methods form a correct prefix in declaration order. **For probe 04, this means we only need to declare the callback methods we actually exercise** — though the callback V-table is a different case (we *implement* it, so every slot the runtime might call must be present; see probe 04 planning).

### 4. ComWrappers holds two native references (refcount accounting)

`Marshal.Release(pUnknown)` returned **2**, so before the release the refcount was 3: my own reference (from `CreateDebuggingInterfaceFromVersionEx`) plus **two** held by ComWrappers. The two are almost certainly the IUnknown-identity reference plus the QI'd `ICorDebug`-interface reference that the cast materialized. This is normal `StrategyBasedComWrappers` behavior, not a leak — the RCW releases both at GC / process exit.

**Engine note:** for a long-lived engine (not a short probe), deterministic RCW teardown matters. The RCW does not expose `IDisposable` on the interface; release is via GC or via the ComWrappers instance's lifetime management. The engine should hold the `StrategyBasedComWrappers` instance as a substrate singleton and decide a deterministic-release strategy (e.g., track wrappers and release at session end) rather than relying on GC — flagged for the engine-engineering phase, not a probe concern.

### 5. version-string token third field varied (ASLR), confirming it's an address

Probe 02: `…;0000000107FF0000`. Probe 03: `…;0000000109414000`. The third field is the coreclr module base address, which ASLR varies per run. The first field stayed `00000004` (`CorDebugInterfaceVersion` = 4). Confirms finding 06's read: the version string is an opaque token encoding an address, not a semver.

## What probe 03 validates for the substrate

- **Source-generated COM is the right mechanism** (finding 05's decision) — it works end-to-end in the PoC's file-based form, with explicit HRESULT control via `[PreserveSig]`.
- **The V-table-by-declaration-order model is correct** — the engine can define ICorDebug interfaces as `[GeneratedComInterface]` prefixes and trust slot dispatch.
- **Probe 04 (the former boss fight) is substantially de-risked** — the consume direction (RCW) works; the remaining unknown is the *expose* direction (`[GeneratedComClass]` implementing the ~36-method callback V-table, handed to `SetManagedHandler`).

## Next — probe 04

`ICorDebugManagedCallback` (+2/3/4) as `[GeneratedComInterface]` interfaces, a `[GeneratedComClass]` stub implementing all four, exposed via `GetOrCreateComInterfaceForObject`, handed to `iCorDebug.SetManagedHandler`. No attach yet — probe 04 validates that the runtime accepts our managed-implemented callback V-table (QI for all four IIDs, slot dispatch). The expose direction is the genuinely new thing; probe 03 proved the consume direction. **Caveat for probe 04:** unlike a consumed interface (where a prefix suffices), an *implemented* callback interface must declare **every** method the runtime might invoke, in exact order — a missing or misordered slot crashes when that callback fires.

## References

- Probe: `poc/drhook-engine/03-icordebug-lifecycle-probe.cs`
- Fixture: `poc/drhook-engine/fixtures/03-icordebug-lifecycle-osx-arm64-20260521T020243Z.txt`
- Findings 03 (ICorDebug contract), 05 (COM interop decision), 06 (probe 02 outcome)
- Mercury session 2026-05-21 finding `probe-03-passed`
