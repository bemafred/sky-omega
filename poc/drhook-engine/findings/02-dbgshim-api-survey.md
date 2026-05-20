# Finding 02: dbgshim API Surface — Epistemics

**Status:**   Epistemics partial — dbgshim API surveyed (1 of 4 Layer 3 reading targets done). ICorDebug COM contract, netcoredbg usage patterns, and modern C# ComWrappers idioms still pending.
**Date:**     2026-05-19
**Hypothesis under study:** A BCL + P/Invoke client can use dbgshim's documented entry points to attach to a running .NET process and obtain a valid `ICorDebug` interface pointer, then release cleanly — without netcoredbg, without `Microsoft.Diagnostics.NETCore.Client` for this surface (the NuGet handles diagnostic IPC; ICorDebug is a separate substrate).
**Spec read:** `dotnet/diagnostics/src/dbgshim/dbgshim.h`
**Exports verified:** `dotnet/diagnostics/src/dbgshim/dbgshim_unixexports.src`

> **Correction note (2026-05-20, after [finding 04](04-netcoredbg-reference.md) netcoredbg reference reading):** The **Probe 02 design and the attach-flow assumptions in this document are partly wrong** and are superseded by finding 04. Two errors the IDL/header reading produced: (1) `EnumerateCLRs` returns coreclr **module paths**, not version strings — an intermediate `CreateVersionStringFromModule(pid, modulePath, ...)` step is required before `CreateDebuggingInterfaceFromVersion*`. (2) `EnumerateCLRs` needs a **retry loop** (100 ms × 30) to handle the coreclr-mid-load race (`INVALID_HANDLE_VALUE` handles). Also: netcoredbg uses `CreateDebuggingInterfaceFromVersionEx` with `CorDebugVersion_4_0 = 4`, not the basic variant. The `LPWSTR`-width open question is **resolved** (16-bit UTF-16 on Unix, confirmed by netcoredbg's `to_utf16`/`to_utf8` boundary conversions). Read finding 04 for the corrected attach flow. The dbgshim symbol enumeration and ABI notes below remain accurate.

## What is now known

### Surface

dbgshim exports **17 symbols** on Unix (macOS / Linux). All `EXTERN_C` (C calling convention, `__cdecl`). All return `HRESULT`. None use Windows-only `STDAPI`.

```
CreateProcessForLaunch                  // launch a process suspended for debugging
ResumeProcess                           // resume a process launched via CreateProcessForLaunch
CloseResumeHandle                       // cleanup without resume

RegisterForRuntimeStartup               // wait for CLR to load in a target process, callback when ready
RegisterForRuntimeStartupEx             // adds szApplicationGroupId (sandboxing)
RegisterForRuntimeStartup3              // adds ICLRDebuggingLibraryProvider3 (custom library lookup)
UnregisterForRuntimeStartup             // cancel the wait

GetStartupNotificationEvent             // raw startup event handle (lower-level than Register*)

EnumerateCLRs                           // find all CoreCLR runtimes in a running process
CloseCLREnumeration                     // free EnumerateCLRs output

CreateVersionStringFromModule           // construct the debuggee-version string from a known CLR module path

CreateDebuggingInterfaceFromVersion     // ICorDebug from version string (basic)
CreateDebuggingInterfaceFromVersionEx   // adds iDebuggerVersion
CreateDebuggingInterfaceFromVersion2    // adds szApplicationGroupId
CreateDebuggingInterfaceFromVersion3    // adds ICLRDebuggingLibraryProvider3

CLRCreateInstance                       // top-level metahost-style instance creation
RegisterForRuntimeStartupRemotePort     // network-port attach (Mono / sandboxed iOS-style)
```

### Attach-to-running-process flow (the probe-02 target)

The simplest documented flow for attaching to an already-running `dotnet` process is three steps + cleanup:

```c
// 1. Enumerate the CoreCLR runtimes in the target process.
HANDLE*  pHandles;     // continue handles (or NULL on Unix — see "Surprises")
LPWSTR*  pVersions;    // version strings, one per runtime instance
DWORD    count;
HRESULT hr = EnumerateCLRs(pid, &pHandles, &pVersions, &count);

// 2. For the chosen runtime (usually count == 1 for normal dotnet processes),
//    obtain the ICorDebug interface pointer.
IUnknown* pCordb;
hr = CreateDebuggingInterfaceFromVersion3(
    /* iDebuggerVersion */    CorDebugVersion_4 /* or similar; needs verification */,
    /* szDebuggeeVersion */   pVersions[0],
    /* szApplicationGroupId */ NULL,           // non-sandboxed target
    /* pLibraryProvider */    NULL,            // optional: custom library lookup
    /* ppCordb */             &pCordb);

// pCordb is now a valid IUnknown* that QIs to ICorDebug.
// At this point the substrate hypothesis is confirmed.

// 3. Clean up.
pCordb->Release();
CloseCLREnumeration(pHandles, pVersions, count);
```

For probe 02 we use `CreateDebuggingInterfaceFromVersion` (the simplest variant) rather than `Version3` — fewer parameters, no `ICLRDebuggingLibraryProvider3` interop concern. The `3` variant is only needed for cross-version debugging or custom library lookup paths.

### Launch-and-startup flow (alternative; out of scope for probe 02)

For probe scenarios where we start the target ourselves:

```c
// 1. Launch suspended.
DWORD  pid;
HANDLE hResume;
CreateProcessForLaunch(L"dotnet ./target.dll", /*bSuspendProcess=*/TRUE,
                       /*lpEnvironment=*/NULL, /*lpCurrentDirectory=*/NULL,
                       &pid, &hResume);

// 2. Register a callback for CLR load.
PVOID hUnregister;
RegisterForRuntimeStartup(pid, &OnRuntimeStartup, /*parameter=*/NULL, &hUnregister);

// 3. Resume the process.
ResumeProcess(hResume);
CloseResumeHandle(hResume);

// 4. Callback fires with pCordb when CLR loads.
//    Cleanup: UnregisterForRuntimeStartup(hUnregister) when done.
```

Launch flow involves async callback handling — heavier interop scaffolding (function-pointer marshalling, threading model). Probe 02 sticks with the synchronous attach flow.

### Calling convention and ABI characteristics

- **Calling convention:** `EXTERN_C` → `__cdecl` (Unix System V; Microsoft x64; ARM64 AAPCS — all consistent for `extern "C"`)
- **Return type:** `HRESULT` (32-bit signed integer; `S_OK = 0`)
- **Pointer types:** `HANDLE` = `void*` on Unix (treat as `nint`/`IntPtr` in C#)
- **String types:** `LPWSTR` and `LPCWSTR` — see "Surprises" below for width semantics
- **Out parameters:** `PVOID*`, `IUnknown**`, etc. — standard double-pointer pattern, caller-allocated single-pointer slot, callee fills
- **Ownership:**
  - `EnumerateCLRs` allocates the arrays; caller must free via `CloseCLREnumeration`
  - `CreateProcessForLaunch` returns a resume handle; caller must release via `ResumeProcess` or `CloseResumeHandle`
  - `Register*` returns an unregister token; caller must release via `UnregisterForRuntimeStartup`
  - `Create*Interface*` returns an `IUnknown*`; standard COM `AddRef`/`Release` semantics

### Callback signature

```c
typedef VOID (*PSTARTUP_CALLBACK)(IUnknown *pCordb, PVOID parameter, HRESULT hr);
```

Invoked from a runtime-owned thread when CLR finishes startup. C# binding:
- **Modern (.NET 5+):** `[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]` static method → function pointer.
- **Legacy:** `Marshal.GetFunctionPointerForDelegate` over a `[UnmanagedFunctionPointer(CallingConvention.Cdecl)]` delegate.

Modern path is the substrate-aligned choice for `ComWrappers`-era interop.

## Surprises (Epistemics adjustments)

1. **`LPWSTR` width is a platform asymmetry — verification required pre-probe.** On Windows, `wchar_t` is 16-bit (UTF-16). On Unix natively, `wchar_t` is 32-bit (UCS-4). However, CoreCLR's PAL layer typedefs `WCHAR` as `char16_t` (16-bit) on Unix to preserve cross-platform ABI compatibility with COM. The dbgshim header inherits this. **In practice:** marshal `LPWSTR` as `char*` of 16-bit code units, i.e., `string` with `CharSet = CharSet.Unicode` in C# (which gives UTF-16) is the correct binding. **Risk:** if a Unix-native `wchar_t` (32-bit) interpretation slipped in anywhere, marshalling would silently corrupt strings. Cross-check by reading PAL's `pal.h` or netcoredbg's interop binding before the probe.

2. **`HANDLE` is opaque and may be `NULL` on Unix paths.** `EnumerateCLRs` outputs `HANDLE*` (an array of "continue handles") — on Windows these are real kernel handles for synchronization; on Unix they may be `NULL` since the synchronization model differs. The probe should not assume `HANDLE` validity; it's the **version strings** that matter for `CreateDebuggingInterfaceFromVersion`.

3. **`szApplicationGroupId` is macOS-sandbox-specific.** For App Store apps and sandboxed processes on macOS, the App Group container ID must be passed (otherwise dbgshim looks in the wrong directory for the runtime's coordination files). For ordinary `dotnet` processes (non-sandboxed), `NULL` is correct. **Implication:** probe 02 targets a non-sandboxed `dotnet` process; sandboxed target support is a Phase 2+ concern.

4. **dbgshim library path on macOS is non-obvious.** The library is `libdbgshim.dylib`, shipped with the .NET runtime, typically under `/usr/local/share/dotnet/shared/Microsoft.NETCore.App/{version}/libdbgshim.dylib`. Loading via P/Invoke `[DllImport("libdbgshim")]` requires the library to be on the loader path — which means either explicit `DllImportResolver` (modern .NET pattern) or `DYLD_LIBRARY_PATH` (operator-set). Probe 02 will use `NativeLibrary.SetDllImportResolver` to point at the runtime's actual `libdbgshim.dylib` location, discovered via `Process.GetCurrentProcess().MainModule.FileName` and runtime-relative path arithmetic.

5. **`CreateDebuggingInterfaceFromVersion` returns `IUnknown*`, not `ICorDebug*`.** Caller must `QueryInterface` for `ICorDebug` (`IID_ICorDebug` — needs lookup in `cordebug.idl`). Standard COM pattern, but our probe must do the QI explicitly. With `ComWrappers`, the path is: marshal `IUnknown*` → `ComWrappers.GetOrCreateObjectForComInstance` → cast to a managed wrapper that implements `ICorDebug`. That requires us to have defined the `ICorDebug` interface in C# — which depends on the COM contract Epistemics reading still pending.

6. **`RegisterForRuntimeStartupRemotePort` exists for Mono / iOS-style remote scenarios.** Out of scope; called out for awareness only.

7. **`CLRCreateInstance` mirrors the metahost-style entry point** for non-attach scenarios. Out of scope for probe 02 (attach flow doesn't use it).

## What is still unknown

The dbgshim surface itself is now characterized. Four substantive open questions remain before probe 02 can be drafted:

1. **`LPWSTR` width verification.** Read PAL's typedef of `WCHAR` on Unix; cross-check with netcoredbg's actual interop binding. (Quick read; should resolve in one fetch.)

2. **`ICorDebug` COM contract.** V-table layout, method ordering, `IID_ICorDebug` GUID, lifecycle of the interface pointer. This is the second Layer 3 Epistemics target — needs its own findings doc (`03-icordebug-contract.md`). The interface is in `dotnet/runtime/src/coreclr/inc/cordebug.idl` or the equivalent C++ header.

3. **netcoredbg's dbgshim usage as reference.** How netcoredbg structures the attach flow in practice; what corner cases it handles; how it loads the library. Per ADR-006: reference material, not template. Third Epistemics target, separate findings doc.

4. **Modern C# `ComWrappers` patterns for ICorDebug.** Substrate-aligned interop: how to wrap an `IUnknown*` from native into a managed proxy that exposes a typed `ICorDebug` interface, V-table thunks for `ICorDebugManagedCallback`, lifetime management between native and managed object graphs. Fourth Epistemics target.

After those four resolve, probe 02 is draftable.

## Probe 02 — design implied (subject to confirmation by remaining Epistemics)

**Scope:** attach to a known-running `dotnet` process by PID, obtain `IUnknown*` via `EnumerateCLRs` + `CreateDebuggingInterfaceFromVersion`, release cleanly. **No** ICorDebug method invocation yet — that's probe 03's territory once the COM contract is characterized.

**Algorithm:**

1. Take a pid as command-line argument.
2. Resolve `libdbgshim.dylib` location relative to the current process's runtime directory; configure `NativeLibrary.SetDllImportResolver`.
3. Call `EnumerateCLRs(pid, out handles, out versions, out count)`.
4. Validate: HRESULT success, count ≥ 1, versions[0] is a non-empty UTF-16 string.
5. Call `CreateDebuggingInterfaceFromVersion(versions[0], out pCordb)`.
6. Validate: HRESULT success, pCordb is non-null.
7. Release: `pCordb->Release()` via `Marshal.Release` (no `ComWrappers` wrapper needed yet — we just want to confirm the pointer is valid).
8. Call `CloseCLREnumeration(handles, versions, count)`.
9. Print version + success indicator; exit 0.

**Fixture capture:** record the version string and the bytes of the IUnknown vtable's first few entries (raw native reads) to `poc/drhook-engine/fixtures/02-dbgshim-attach-{runtime-version}-{os-arch}-{timestamp}.bin`. First protocol-trace fixture for Layer 3.

**Falsification criteria:**

| Outcome | Lesson |
|---|---|
| `libdbgshim.dylib` not found | Resolver assumption wrong; check actual runtime layout on this host |
| `EnumerateCLRs` returns failure HRESULT | Target process not a CLR host, OR ptrace/permission issue on macOS, OR PAL initialization problem |
| `count == 0` | Target's CLR isn't fully started yet, OR runtime detection differs from expected |
| Version string is garbled | `LPWSTR` width assumption wrong — re-verify PAL typedef |
| `CreateDebuggingInterfaceFromVersion` returns failure | dbgshim can't construct ICorDebug for this runtime version (e.g., debugger-side library missing) |
| `pCordb` is null with success HRESULT | Header contract differs from implementation; investigate |
| Probe segfaults | Calling-convention or pointer-marshalling bug; first place to look is the function pointer/calling convention |

**Out of scope for probe 02:**
- ICorDebug method invocation (probe 03)
- Managed callback (`ICorDebugManagedCallback`) — needs V-table interop (probe 04)
- Process launch via `CreateProcessForLaunch` — alternative path (probe later if needed)
- macOS sandboxing — `szApplicationGroupId` is NULL for non-sandboxed targets
- Multiple CLR enumeration (count > 1) — print warning if encountered

## References

- `dotnet/diagnostics/src/dbgshim/dbgshim.h` — public API (read 2026-05-19)
- `dotnet/diagnostics/src/dbgshim/dbgshim_unixexports.src` — Unix exports (17 symbols)
- `dotnet/runtime/src/coreclr/inc/cordebug.idl` — `ICorDebug` interface contract (pending Epistemics target #2)
- netcoredbg `src/debugger/manageddebugger.cpp` and similar — reference material (pending Epistemics target #3)
- `dotnet/runtime` ComWrappers documentation — `System.Runtime.InteropServices.ComWrappers` (pending Epistemics target #4)
- Mercury session 2026-05-19 finding `dbgshim-api-surveyed`

## Probe 02 outcome

*Pending — added after probe 02 runs.*
