# Finding 05: COM Interop Strategy — Source-Generated COM (Epistemics 4/4)

**Status:**   Epistemics COMPLETE for Layer 3 (4 of 4 reading targets done). Probe 02 is now fully draftable.
**Date:**     2026-05-20
**Decision:** Use **source-generated COM** (`[GeneratedComInterface]` / `[GeneratedComClass]`, `System.Runtime.InteropServices.Marshalling`) for the ICorDebug interop. Fall back to manual `ComWrappers` only for individual signatures the generator can't express.
**Sources read:**
- learn.microsoft.com — ComWrappers source generation (the `[GeneratedComInterface]` system)
- learn.microsoft.com — `System.Runtime.InteropServices.ComWrappers` class reference (the manual base API)

## The decision

Two mechanisms exist for COM interop without the legacy `[ComImport]` runtime-IL-stub system:

| | Source-generated COM (.NET 8+) | Manual ComWrappers (.NET 5+) |
|---|---|---|
| How | `[GeneratedComInterface]` + `[GeneratedComClass]` attributes; compile-time source generator | Override `ComputeVtables` + `CreateObject`; hand-build function-pointer V-tables |
| Code we write | Interface definitions + the callback class | All of the above PLUS the V-table marshalling boilerplate by hand |
| The ~36-method callback V-table | **Generated** | **Hand-rolled** (`ComInterfaceEntry[]`, `[UnmanagedCallersOnly]` thunks, `ComInterfaceDispatch.GetInstance<T>`) |
| Assembly | `System.Runtime.InteropServices.dll` (BCL) | Same (BCL) |
| Trimming / AOT | Friendly (no runtime IL stub) | Friendly |

**Source-generated COM wins for the ICorDebug interop.** Reasons:

1. **It's BCL — `System.Runtime.InteropServices.Marshalling`, shipped in `System.Runtime.InteropServices.dll`.** No NuGet. It doesn't even reach the ADR-009 four-axis test — it's `System.*`. Substrate-clean by default.

2. **It eliminates the ~36-method hand-rolled V-table.** Finding 03 called `ICorDebugManagedCallback`'s V-table "the boss-fight probe." With source-gen, probe 04 shrinks dramatically: define the four callback interfaces as `[GeneratedComInterface]`, implement one `[GeneratedComClass]`, and the generator produces the V-table dispatch + `QueryInterface` routing. No `[UnmanagedCallersOnly]` thunk array by hand.

3. **ICorDebug interfaces are exactly the supported shape.** Source-gen COM supports **`IUnknown`-based interfaces only** (not `IDispatch`/dual). Every ICorDebug interface is declared `[object, local]` deriving from `IUnknown` — a perfect match. (Built-in `[ComImport]` defaults to `InterfaceIsDual`, which would be wrong here; source-gen defaults to `IUnknown`, which is right.)

4. **Multiple-interface dispatch is built in.** A single `[GeneratedComClass]` implementing `ICorDebugManagedCallback` + `...2` + `...3` + `...4` exposes all four IIDs; the runtime's `QueryInterface` dispatches to the right V-table. This is exactly finding 03's "four sibling interfaces, one managed instance" requirement — handled by the generator.

5. **.NET 10 target** (per `CLAUDE.md`) means .NET 9+ features are available — including cross-assembly generated-COM inheritance (relevant if interface definitions ever split across assemblies; for the PoC they're all in one).

## How the two directions work

### Consuming a native `IUnknown*` as a typed managed interface (probe 02, 03)

```csharp
[GeneratedComInterface]
[Guid("3d6f5f61-7538-11d3-8d5b-00104b35e7ef")]
internal partial interface ICorDebug   // : derives from IUnknown implicitly
{
    [PreserveSig] int Initialize();
    [PreserveSig] int Terminate();
    [PreserveSig] int SetManagedHandler(nint pCallback);
    [PreserveSig] int SetUnmanagedHandler(nint pCallback);
    // ... CreateProcess, DebugActiveProcess, EnumerateProcesses, GetProcess, CanLaunchOrAttach
}

// pUnknown is the IntPtr from CreateDebuggingInterfaceFromVersionEx
ComWrappers cw = new StrategyBasedComWrappers();
var iCorDebug = (ICorDebug)cw.GetOrCreateObjectForComInstance(pUnknown, CreateObjectFlags.None);
int hr = iCorDebug.Initialize();   // direct V-table call, explicit HRESULT
```

`StrategyBasedComWrappers` is the ready-made `ComWrappers` implementation the generator targets — **we don't subclass `ComWrappers`**; we instantiate `StrategyBasedComWrappers` (one per process is fine).

### Exposing a managed callback object to native code (probe 04, 05)

```csharp
[GeneratedComInterface]
[Guid("3d6f5f60-7538-11d3-8d5b-00104b35e7ef")]
internal partial interface ICorDebugManagedCallback
{
    [PreserveSig] int Breakpoint(nint pAppDomain, nint pThread, nint pBreakpoint);
    [PreserveSig] int StepComplete(nint pAppDomain, nint pThread, nint pStepper, int reason);
    // ... ~21 more
}
// + ICorDebugManagedCallback2, 3, 4 likewise

[GeneratedComClass]
internal partial class StubCallback : ICorDebugManagedCallback,
                                       ICorDebugManagedCallback2,
                                       ICorDebugManagedCallback3,
                                       ICorDebugManagedCallback4
{
    public int Breakpoint(nint a, nint t, nint b) => 0;  // S_OK
    // ... every method returns S_OK for the stub
}

var cw = new StrategyBasedComWrappers();
nint pCallback = cw.GetOrCreateComInterfaceForObject(new StubCallback(), CreateComInterfaceFlags.None);
iCorDebug.SetManagedHandler(pCallback);   // runtime now calls back into StubCallback
```

The generator produces the V-table for all four interfaces; `SetManagedHandler` receives a pointer that `QueryInterface`s to each.

## Substrate-design details

1. **Use `[PreserveSig]` returning `int` on every method.** The default source-gen behavior hides the `HRESULT` and throws on failure, converting the last `[out]` param to the return value. For a debugger substrate that's wrong — many ICorDebug HRESULTs are informational (`S_FALSE`, `CORDBG_S_*` success-with-info codes) and must not throw. netcoredbg checks every HRESULT explicitly (`IfFailRet`). `[PreserveSig] int` keeps the C# signature 1:1 with the IDL's `HRESULT`, preserves V-table layout exactly, and gives explicit HRESULT control. **This is the substrate-aligned choice for the entire ICorDebug surface.**

2. **Interface-pointer parameters start as `nint`, get wrapped lazily.** Callback methods receive `ICorDebugAppDomain*`, `ICorDebugThread*`, etc. Declaring each as a fully-defined `[GeneratedComInterface]` would mean defining dozens of interfaces up front. Instead: declare them as `nint` (raw pointer) in the V-table-critical definitions, and wrap to typed interfaces only when a probe actually needs to call methods on them. Keeps the V-table layout correct (a pointer is a pointer) while deferring the interface-definition surface to when it's load-bearing.

3. **Raw-pointer / address parameters** (`CORDB_ADDRESS` = UInt64, `BYTE*`, `void**`) are blittable — `nint`/`ulong`/`nint*`. No custom marshalling. `LPCWSTR`/`LPWSTR` use `[MarshalAs(UnmanagedType.LPWStr)]` or the interface-level `StringMarshalling = StringMarshalling.Utf16` (confirmed correct width by finding 04).

4. **V-table slot order must match the IDL exactly.** Source-gen lays out the V-table in C# declaration order after `IUnknown`'s 3 slots. The C# interface method order MUST match the IDL method order (finding 03's V-table tables). A mis-ordered method silently calls the wrong native slot — the kind of bug finding 03 surprise 1 warned about. Fixture-replay testing (deterministic callback sequences) catches this.

5. **Lifetime:** `GetOrCreateComInterfaceForObject` keeps the managed callback alive while native holds the pointer (the ComWrappers wrapper holds a reference). The `StrategyBasedComWrappers` instance should outlive all wrappers — hold it as a substrate-level singleton.

## Impact on the probe sequence

Source-gen COM **shrinks probe 04** — the former boss fight. Revised sequence:

| Probe | Scope | COM mechanism |
|---|---|---|
| 02 | dbgshim attach + QI to ICorDebug | Define `ICorDebug` `[GeneratedComInterface]`; `GetOrCreateObjectForComInstance`; verify non-null; release |
| 03 | `ICorDebug.Initialize()` + `Terminate()` | Call methods on the typed wrapper from probe 02 |
| 04 | `ICorDebugManagedCallback` (+2/3/4) + `SetManagedHandler` | Define four `[GeneratedComInterface]` + one `[GeneratedComClass]` stub; `GetOrCreateComInterfaceForObject`; `SetManagedHandler` — **generator builds the V-table** |
| 05 | `DebugActiveProcess` + one event round-trip + `Detach` | Real attach; callback fires into the `[GeneratedComClass]` |

The hardest remaining work is no longer "hand-roll a 36-slot V-table" but "define ~36 method signatures correctly in declaration order and let the generator marshal." Substantially smaller and far less error-prone.

## What remains genuinely uncertain (to be settled by the probes, not more reading)

1. **Does source-gen COM handle `local` IDL interfaces cleanly?** `[object, local]` means in-process direct V-table, no marshalling proxy. Source-gen produces direct V-table calls, so this should be fine — but it's an empirical question probe 02 answers.
2. **`GetOrCreateObjectForComInstance` on a pointer from a non-standard COM source.** dbgshim's `IUnknown*` should follow standard `IUnknown` rules (it's real COM under the hood), but the wrapping is unverified until probe 02 runs.
3. **Method-order correctness across ~36 callback slots** — verified only when a real callback fires (probe 05) or via a deliberate V-table-offset fixture.

These are Engineering-phase falsifications, not Epistemics gaps. The reading is done.

## Epistemics arc — closed

Four targets, all read:

1. ✅ Diagnostic IPC protocol (finding 01) — admitted dependency, not engine work
2. ✅ dbgshim API surface (finding 02, corrected by 04)
3. ✅ ICorDebug COM contract (finding 03)
4. ✅ netcoredbg attach flow as reference (finding 04)
5. ✅ COM interop strategy (this finding)

Probe 02 is draftable. The corrected attach flow (finding 04) + source-gen COM (this finding) + the `ICorDebug` V-table (finding 03) compose into a complete probe-02 design with no open Epistemics dependency.

## References

- learn.microsoft.com — ComWrappers source generation; `ComWrappers` class reference
- `System.Runtime.InteropServices.Marshalling` — `GeneratedComInterfaceAttribute`, `GeneratedComClassAttribute`, `StrategyBasedComWrappers`
- Findings 02 (dbgshim), 03 (ICorDebug contract — V-table order), 04 (netcoredbg attach flow — corrected design, UTF-16 confirmation)
- Mercury session 2026-05-20 finding `com-interop-decision`
