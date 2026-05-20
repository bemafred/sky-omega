# Finding 04: netcoredbg Attach Flow — Reference Reading

**Status:**   Epistemics partial — netcoredbg reference read (3 of 4 Layer 3 targets done). Modern C# `ComWrappers` idioms still pending.
**Date:**     2026-05-20
**Purpose:** Validate the dbgshim + ICorDebug attach design against a real-world implementation. Per [ADR-006](../../../docs/adrs/drhook/ADR-006-drhook-engine.md): netcoredbg is **reference material, not template**. We read what they do at the protocol boundary, confirm the sequence, surface real-world quirks the spec/IDL don't reveal, then build it ourselves under our own substrate discipline.
**Sources read:**
- `Samsung/netcoredbg/src/debugger/dbgshim.h` — the `dbgshim_t` function-pointer struct + dlopen/dlsym loader
- `Samsung/netcoredbg/src/debugger/manageddebugger.cpp` — `EnumerateCLRs` retry wrapper, `AttachToProcess`, `Startup`, `StartupCallback`

## This reading paid for itself — it corrected two errors in finding 02

The IDL/header readings (findings 01–03) would have produced a **wrong** probe 02. The reference reading caught it. This is exactly the discipline the original PoC direction named ("read the spec **and** netcoredbg's IPC handling first"). Two corrections:

1. **`EnumerateCLRs` returns coreclr MODULE PATHS, not version strings.** Finding 02 said "`CreateDebuggingInterfaceFromVersion(versions[0])`" — treating the string array as version strings. Wrong. netcoredbg's `GetCLRPath` reads `pStringArray[0]` as a **path to libcoreclr**, then calls `CreateVersionStringFromModule(pid, modulePath, ...)` to get the actual version string. The flow has an intermediate step finding 02 omitted.

2. **`EnumerateCLRs` needs a retry loop.** Finding 02 assumed a single call succeeds. netcoredbg wraps it in a retry: 100 ms interval, default 30 tries (3-second timeout). Two reasons: (a) attaching to a process that hasn't loaded coreclr yet returns zero handles; (b) a race where dbgshim catches the coreclr module just-loaded but before `g_hContinueStartupEvent` is initialized — handles come back as `INVALID_HANDLE_VALUE`. netcoredbg checks `AreAllHandlesValid` and retries if any handle is invalid.

## What is now known — the canonical attach flow

### Library loading (dlopen / dlsym pattern)

netcoredbg loads dbgshim dynamically, not at link time:

- Path: same directory as the netcoredbg executable (`GetExeAbsPath` → strip filename), OR a compile-time `DBGSHIM_DIR` override.
- Filename per platform: `dbgshim.dll` (Windows), `libdbgshim.dylib` (macOS), `libdbgshim.so` (Linux).
- `DLOpen` the library, then `DLSym` each function, verify all resolved or throw.
- **netcoredbg resolves only 9 of the 17 exported symbols** — `CreateProcessForLaunch`, `ResumeProcess`, `CloseResumeHandle`, `RegisterForRuntimeStartup`, `UnregisterForRuntimeStartup`, `EnumerateCLRs`, `CloseCLREnumeration`, `CreateVersionStringFromModule`, `CreateDebuggingInterfaceFromVersionEx`. The other 8 (`Ex`/`2`/`3` startup variants, `GetStartupNotificationEvent`, `CLRCreateInstance`, `RegisterForRuntimeStartupRemotePort`, base `CreateDebuggingInterfaceFromVersion`) are unused in practice.

**C# equivalent:** `NativeLibrary.Load(path)` + `NativeLibrary.GetExport(handle, name)` → function pointers, or `[DllImport]` + `NativeLibrary.SetDllImportResolver`. Locate `libdbgshim.dylib` in the runtime directory (where `libcoreclr.dylib` lives). The 9-symbol subset is all probe 02–05 need.

### Attach-to-running-process flow (probe 02 + 03 + 05 target)

```
AttachToProcess(pid):
  1. modulePath = GetCLRPath(pid)
       └─ EnumerateCLRs(pid) WITH RETRY (100ms × 30; check AreAllHandlesValid)
          → pStringArray[0] is the coreclr MODULE PATH
       └─ CloseCLREnumeration(...)
  2. CreateVersionStringFromModule(pid, modulePath, buffer[100], &dwLength)
       → versionString
  3. CreateDebuggingInterfaceFromVersionEx(CorDebugVersion_4_0 /* = 4 */, versionString, &pCordb)
       → IUnknown*
  4. Startup(pCordb):
       a. pCordb->QueryInterface(IID_ICorDebug, &iCorDebug)
       b. iCorDebug->Initialize()
       c. iCorDebug->SetManagedHandler(managedCallback)
       d. iCorDebug->DebugActiveProcess(pid, FALSE /* managed-only */, &iCorProcess)
```

Failure teardown at step (c) or (d): `iCorDebug->Terminate()`, release the callback, return the failure HRESULT.

### Launch-a-new-process flow (alternative, out of probe-02 scope)

```
Launch(cmdline):
  1. CreateProcessForLaunch(cmdline, bSuspendProcess=TRUE, ..., &pid, &resumeHandle)
  2. RegisterForRuntimeStartup(pid, StartupCallback, this, &unregisterToken)
  3. ResumeProcess(resumeHandle); CloseResumeHandle(resumeHandle)
  4. [runtime loads, StartupCallback(pCordb, this, hr) fires on a runtime thread]
       └─ Startup(pCordb)   // same QI + Initialize + SetManagedHandler + DebugActiveProcess
       └─ UnregisterForRuntimeStartup(unregisterToken)
```

`StartupCallback` is a static function; the `this` pointer is threaded through the `parameter` argument. C# binding: `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]` static method, with the managed instance passed via a `GCHandle` cast to `PVOID` (not a raw `this` — managed objects move).

### CorDebugVersion constant values (from cordebug.idl)

```
CorDebugInvalidVersion = 0
CorDebugVersion_1_0    = 1
CorDebugVersion_1_1    = 2
CorDebugVersion_2_0    = 3
CorDebugVersion_4_0    = 4   ← netcoredbg passes this to CreateDebuggingInterfaceFromVersionEx
CorDebugVersion_4_5    = 5
```

## Resolved open questions (carried from findings 02 / 03)

1. **`LPWSTR` width on Unix — RESOLVED: 16-bit UTF-16.** netcoredbg uses `to_utf16(std::string)` to build every `LPWSTR` it passes to dbgshim, and `to_utf8(LPWSTR)` to read every string dbgshim returns. The runtime's PAL defines `WCHAR` as 16-bit on Unix for COM ABI compatibility. **C# binding: marshal as `CharSet.Unicode` (UTF-16), or `char*` of `ushort`.** The finding-02 risk (32-bit `wchar_t` corruption) is confirmed avoidable by treating all dbgshim/ICorDebug strings as UTF-16.

2. **dbgshim library location — RESOLVED in principle.** Lives next to the runtime (`libcoreclr.dylib`'s directory) or wherever the host bundles it. Our resolver discovers it relative to the running process's runtime directory.

3. **`HANDLE` validity from `EnumerateCLRs` — CORRECTED.** Finding 02 said "may be NULL on Unix, ignore them." Actually the handles are **continue-startup-event handles** and their validity is a **readiness signal**: `INVALID_HANDLE_VALUE` (not NULL) means coreclr is mid-load and you must retry. The handle array IS load-bearing — for the retry gate, not for the attach itself.

## Surprises (real-world quirks the spec/IDL didn't reveal)

1. **The retry loop is mandatory, not optional.** Without it, attaching to a freshly-started or busy process fails intermittently. The 100 ms × 30 default (3 s) is netcoredbg's empirically-chosen window. Our probe must replicate this or accept flaky attach.

2. **Two-step version resolution.** `EnumerateCLRs` → module path → `CreateVersionStringFromModule` → version string → `CreateDebuggingInterfaceFromVersionEx`. The string array from `EnumerateCLRs` is NOT directly consumable by the interface-creation call.

3. **`CreateDebuggingInterfaceFromVersionEx` with explicit version 4**, not the basic variant. The `iDebuggerVersion` parameter pins the debugger-side ICorDebug version. netcoredbg hardcodes `CorDebugVersion_4_0`.

4. **`buffer[100]` for the version string** — netcoredbg uses a fixed 100-WCHAR stack buffer for `CreateVersionStringFromModule`. The version string is short; 100 WCHARs is ample. `CreateVersionStringFromModule` also returns the required length via out-param, so a two-call grow pattern is possible but unnecessary in practice.

5. **`StartupCallback` runs on a runtime-owned thread.** The launch-flow callback fires from inside the runtime's startup machinery — threading and GCHandle lifetime matter for the C# binding. (Not a probe-02 concern; probe 02 uses the synchronous attach flow.)

6. **The whole `dbgshim_t` struct is constructed once and cached.** netcoredbg loads the library in the struct constructor and holds it for the debugger's lifetime. Our engine should do the same — resolve once, reuse.

## Probe 02 — corrected design (supersedes finding 02's probe-02 section)

```
1. Resolve libdbgshim.dylib relative to the running process's runtime directory;
   load via NativeLibrary.Load + GetExport for the 9-symbol subset.
2. EnumerateCLRs(pid, &handles, &paths, &count) WITH RETRY:
     - loop up to 30 times, 100ms apart
     - on success with count > 0 AND all handles != INVALID_HANDLE_VALUE → proceed
     - else CloseCLREnumeration, sleep, retry
     - bail on E_INVALIDARG / E_FAIL (no such process / bad args)
3. modulePath = paths[0]   (a coreclr module path, NOT a version string)
4. CreateVersionStringFromModule(pid, modulePath, buffer[100], &len) → versionString
5. CreateDebuggingInterfaceFromVersionEx(4, versionString, &pUnknown) → IUnknown*
6. pUnknown->QueryInterface(IID_ICorDebug {3d6f5f61-7538-11d3-8d5b-00104b35e7ef}, &pCordb)
7. Validate pCordb non-null; release pCordb, release pUnknown, CloseCLREnumeration.
```

All string marshalling is UTF-16. Library symbols resolved via `NativeLibrary`. No `ComWrappers` needed for probe 02 — `Marshal.QueryInterface` / `Marshal.Release` on raw `IntPtr` suffice (ComWrappers becomes load-bearing at probe 04's V-table construction).

**Fixture capture:** record `modulePath`, `versionString`, and the first few V-table pointer values of `pCordb` to `fixtures/02-dbgshim-attach-{rt-version}-{os-arch}-{timestamp}.bin`.

## What is still unknown

One Layer 3 Epistemics target remains:

1. **Modern C# `ComWrappers` idioms** — the fourth and final target. How to wrap an incoming `IUnknown*` as a typed managed `ICorDebug` proxy; how to expose a managed `ICorDebugManagedCallback` (+2/3/4) implementation as a native-callable IUnknown with four IIDs over one instance; `[UnmanagedCallersOnly]` thunk arrays for the ~36 callback methods; GCHandle lifetime across the native/managed boundary. Load-bearing for probe 04, not probe 02 or 03. After this, the Epistemics arc closes and probe 02 (corrected) is draftable.

Lower-priority residual:
- `HPROCESS` semantics on Unix (probe 05+).
- Which `ICorDebugProcess` version (2–12) is current in .NET 10 (probe 05+).

## References

- `Samsung/netcoredbg/src/debugger/dbgshim.h` — dbgshim_t loader (read 2026-05-20)
- `Samsung/netcoredbg/src/debugger/manageddebugger.cpp` — attach flow (read 2026-05-20)
- `dotnet/runtime/src/coreclr/inc/cordebug.idl` — CorDebugInterfaceVersion enum (CorDebugVersion_4_0 = 4)
- Findings 02 (dbgshim API — probe-02 section now superseded) and 03 (ICorDebug contract)
- Mercury session 2026-05-20 finding `netcoredbg-reference-read`
