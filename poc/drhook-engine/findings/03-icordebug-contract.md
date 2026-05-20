# Finding 03: ICorDebug COM Contract — Epistemics

**Status:**   Epistemics partial — ICorDebug contract characterized (2 of 4 Layer 3 reading targets done). netcoredbg usage patterns and modern C# `ComWrappers` idioms still pending.
**Date:**     2026-05-20
**Hypothesis under study:** A BCL + P/Invoke client can wrap dbgshim's `IUnknown*` as a typed `ICorDebug` interface via `ComWrappers`, exercise the Initialize/Terminate lifecycle, and (in subsequent probes) implement the `ICorDebugManagedCallback` V-table to receive debug events.
**Spec read:** `dotnet/runtime/src/coreclr/inc/cordebug.idl` — 7,830 lines, 203 interface declarations.

## What is now known

### Interface hierarchy (probe-relevant subset)

The IDL declares 203 interfaces. The probe-relevant root set is small; the rest is reachable from these as out-parameters or inheritance chains.

```
IUnknown                              (Windows COM root: QueryInterface, AddRef, Release)
├── ICorDebug                         (the root debugger interface, what dbgshim hands us)
├── ICorDebugManagedCallback          (debug event callback — we IMPLEMENT this; 23 methods)
├── ICorDebugManagedCallback2         (additional events for v2.0; 8 methods)
├── ICorDebugManagedCallback3         (custom notification + GC events; 4 methods)
├── ICorDebugManagedCallback4         (additional GC + data breakpoint; ~4 methods)
├── ICorDebugUnmanagedCallback        (unmanaged debug events — skipped, we are managed-only)
└── ICorDebugController               (Stop/Continue/Detach/Terminate base for processes + appdomains)
    └── ICorDebugProcess              (attached process — inherits Controller + 18 own methods)
        └── ICorDebugProcess2/3/.../12 (later additions, layered via QI not inheritance)
```

The graph reachable from a single `ICorDebugProcess` includes `ICorDebugAppDomain`, `ICorDebugAssembly`, `ICorDebugModule`, `ICorDebugClass`, `ICorDebugFunction`, `ICorDebugThread`, `ICorDebugFrame`, `ICorDebugValue`, `ICorDebugBreakpoint`, `ICorDebugStepper`, `ICorDebugChain`, and many child variants. Probe 02 touches only `ICorDebug`. Probe 03 touches only `ICorDebug`. Probe 04 adds the callback V-table. Probe 05+ adds attach.

### IIDs (for QueryInterface)

```
ICorDebug                  3d6f5f61-7538-11d3-8d5b-00104b35e7ef
ICorDebugManagedCallback   3d6f5f60-7538-11d3-8d5b-00104b35e7ef
ICorDebugManagedCallback2  250E5EEA-DB5C-4C76-B6F3-8C46F12E3203
ICorDebugManagedCallback3  264EA0FC-2591-49AA-868E-835E6515323F
ICorDebugManagedCallback4  322911AE-16A5-49BA-84A3-ED69678138A3
ICorDebugController        3d6f5f62-7538-11d3-8d5b-00104b35e7ef
ICorDebugProcess           3d6f5f63-7538-11d3-8d5b-00104b35e7ef
```

(Note the `3d6f5f6X` prefix — the original `IID_ICorDebug*` block sharing a sequence; the callback subtypes use unrelated GUIDs since they were added later.)

### ICorDebug — V-table layout (12 slots)

```
Slot 0:  HRESULT QueryInterface(REFIID riid, void** ppvObject)              [from IUnknown]
Slot 1:  ULONG   AddRef()                                                   [from IUnknown]
Slot 2:  ULONG   Release()                                                  [from IUnknown]
Slot 3:  HRESULT Initialize()
Slot 4:  HRESULT Terminate()
Slot 5:  HRESULT SetManagedHandler(ICorDebugManagedCallback* pCallback)
Slot 6:  HRESULT SetUnmanagedHandler(ICorDebugUnmanagedCallback* pCallback)
Slot 7:  HRESULT CreateProcess(
            LPCWSTR lpApplicationName, LPWSTR lpCommandLine,
            LPSECURITY_ATTRIBUTES lpProcessAttributes,
            LPSECURITY_ATTRIBUTES lpThreadAttributes,
            BOOL bInheritHandles, DWORD dwCreationFlags,
            PVOID lpEnvironment, LPCWSTR lpCurrentDirectory,
            LPSTARTUPINFOW lpStartupInfo, LPPROCESS_INFORMATION lpProcessInformation,
            CorDebugCreateProcessFlags debuggingFlags,
            ICorDebugProcess** ppProcess)
Slot 8:  HRESULT DebugActiveProcess(
            DWORD id, BOOL win32Attach, ICorDebugProcess** ppProcess)
Slot 9:  HRESULT EnumerateProcesses(ICorDebugProcessEnum** ppProcess)
Slot 10: HRESULT GetProcess(DWORD dwProcessId, ICorDebugProcess** ppProcess)
Slot 11: HRESULT CanLaunchOrAttach(DWORD dwProcessId, BOOL win32DebuggingEnabled)
```

**Lifecycle contract (from header comments, verbatim where noted):**

- `Initialize()` "must be called at creation time before any other method on ICorDebug is called."
- `SetManagedHandler` "should be called at creation time to specify the event handler object for managed events." (Required before `DebugActiveProcess` for attach scenarios.)
- `Terminate()` "must be called when the ICorDebug is no longer needed. NOTE: Terminate should not be called until an ExitProcess callback has been received for all processes being debugged." (Implication: if no process is attached, Initialize + Terminate is a valid lifecycle.)
- `DebugActiveProcess` is the attach call. `win32Attach=TRUE` means the debugger also receives Win32 (native) debug events. For managed-only debugging, pass `FALSE`.
- `CanLaunchOrAttach` is informational — it does not gate subsequent operations. Possible HRESULTs: `S_OK`, `CORDBG_E_DEBUGGING_NOT_POSSIBLE`, `CORDBG_E_KERNEL_DEBUGGER_PRESENT`, `CORDBG_E_KERNEL_DEBUGGER_ENABLED`.

### ICorDebugManagedCallback — the interface we implement (~23 methods)

The callback receives all managed debug events from the debuggee. All callbacks are documented as called **with the process in the synchronized state** — the debuggee threads are stopped at a coherent point.

Method families:

```
Per-event-class (the substantive ones the debugger consumes):
  Breakpoint, StepComplete, Break, Exception, EvalComplete, EvalException,
  CreateProcess, ExitProcess, CreateThread, ExitThread,
  LoadModule, UnloadModule, LoadClass, UnloadClass,
  DebuggerError, LogMessage, LogSwitch,
  CreateAppDomain, ExitAppDomain,
  LoadAssembly, UnloadAssembly,
  ControlCTrap, NameChange, UpdateModuleSymbols,
  EditAndContinueRemap, BreakpointSetError
```

Total ~26 V-table slots (3 from IUnknown + ~23 declared). Every slot must be implemented — COM doesn't allow null entries in a V-table.

### ICorDebugManagedCallback2/3/4 — additional callbacks added in later .NET versions

These are NOT a subclass chain. Each is a separate `IUnknown`-derived interface that the same managed callback object should implement. The debugger object implements all four; the runtime QIs for each at registration time.

```
ICorDebugManagedCallback2  (v2.0 additions):
  FunctionRemapOpportunity, CreateConnection, ChangeConnection, DestroyConnection,
  Exception (richer overload), ExceptionUnwind, FunctionRemapComplete, MDANotification

ICorDebugManagedCallback3  (custom notifications + GC events):
  CustomNotification

ICorDebugManagedCallback4  (newer GC + data breakpoint):
  BeforeGarbageCollection, AfterGarbageCollection, DataBreakpoint
  (plus possibly newer additions; need verification at probe time)
```

Combined V-table surface across all four callback interfaces: roughly **36 methods to implement** as native-callable thunks. Substantial.

### ICorDebugController — base of attached entities (10 methods)

```
HRESULT Stop(DWORD dwTimeoutIgnored)
HRESULT Continue(BOOL fIsOutOfBand)
HRESULT IsRunning(BOOL* pbRunning)
HRESULT HasQueuedCallbacks(ICorDebugThread* pThread, BOOL* pbQueued)
HRESULT EnumerateThreads(ICorDebugThreadEnum** ppThreads)
HRESULT SetAllThreadsDebugState(CorDebugThreadState state, ICorDebugThread* pExceptThisThread)
HRESULT Detach()
HRESULT Terminate(UINT exitCode)
HRESULT CanCommitChanges(ULONG cSnapshots, ICorDebugEditAndContinueSnapshot** pSnapshots, ICorDebugErrorInfoEnum** pError)
HRESULT CommitChanges(ULONG cSnapshots, ICorDebugEditAndContinueSnapshot** pSnapshots, ICorDebugErrorInfoEnum** pError)
```

Probe 05+ scope. Detach + Terminate are the lifecycle-close operations.

### ICorDebugProcess — attached process (28 V-table slots = 13 inherited from Controller + IUnknown + 18 own)

Method highlights (the own methods, after Controller's):

```
GetID(DWORD* pdwProcessId)
GetHandle(HPROCESS* phProcessHandle)
GetThread(DWORD dwThreadId, ICorDebugThread** ppThread)
EnumerateObjects(ICorDebugObjectEnum** ppObjects)
IsTransitionStub(CORDB_ADDRESS address, BOOL* pbTransitionStub)
IsOSSuspended(DWORD threadID, BOOL* pbSuspended)
GetThreadContext / SetThreadContext           (Win32 CONTEXT struct — register state)
ReadMemory / WriteMemory                       (target memory access)
ClearCurrentException(DWORD threadID)
EnableLogMessages / ModifyLogSwitch
EnumerateAppDomains(ICorDebugAppDomainEnum** ppAppDomains)
GetObject(ICorDebugValue** ppObject)
ThreadForFiberCookie / GetHelperThreadID
```

Probe 05+ scope.

## Surprises (Epistemics adjustments)

1. **`ICorDebugManagedCallback`'s V-table is substantial — ~36 methods across four sibling interfaces.** All slots must be filled (COM forbids null entries). This is the substrate's biggest single interop surface, and it ships as part of `SetManagedHandler` Phase 1 work — there's no way to attach without it. `ComWrappers` makes this tractable but each method needs an `[UnmanagedCallersOnly]` thunk. Plan accordingly when sequencing probes: probe 04 (V-table construction) is genuinely the boss-fight probe before any actual debugging behavior.

2. **The four `ICorDebugManagedCallback*` interfaces are siblings, not a chain.** Each derives from `IUnknown` directly. The same C# object implements all four; `ComWrappers` exposes four separate entries (one per IID) that all dispatch to the same managed instance. The runtime QIs for each separately — failure of QI for v2/3/4 is fine on older debuggees; failure of QI for v1 is fatal.

3. **`Terminate()` exists on both `ICorDebug` and `ICorDebugController`.** Different lifecycles:
   - `ICorDebug::Terminate()` — release the debugger itself (no parameter).
   - `ICorDebugController::Terminate(UINT exitCode)` — terminate the debugged process with a given exit code.
   - The header explicitly disallows calling `ICorDebug::Terminate` until ExitProcess fires for all debugged processes. Probe 03 (no attach) sidesteps this.

4. **All callbacks fire "with the process in the synchronized state".** Threading model: the debuggee threads are stopped while the callback runs; the debugger must call `Continue` (on `ICorDebugController`) to resume. This determinism is a gift for fixture-replay testing — callback sequences are deterministic for the same debuggee program.

5. **`DebugActiveProcess` takes `win32Attach` BOOL.** For managed-only debugging (our v1 scope), pass `FALSE`. Native (Win32) debug events go to a separate `ICorDebugUnmanagedCallback`, which we don't implement.

6. **`CanLaunchOrAttach` is purely informational.** "the rest of the API will not stop you from launching or attaching to a process anyway." Don't gate on it; use it only for pre-flight diagnostics.

7. **`Initialize()` returning `S_OK` doesn't mean attach will work.** Initialize is local to the ICorDebug object; actual attach happens in `DebugActiveProcess`, which can still fail for many reasons (process protected, permissions, runtime not in attachable state). Probe 03 stays inside Initialize+Terminate to keep the falsification surface narrow.

8. **`ICorDebugProcess2/3/.../12` are layered via QI, not inheritance.** Each later interface adds methods to the process surface. The runtime exposes all variants on a single underlying object; the client QIs the original `ICorDebugProcess*` for the specific version it wants. Probe 05+ concern.

## What is still unknown

Two of four Layer 3 Epistemics targets are now done (dbgshim + ICorDebug contract). Two remain:

1. **netcoredbg's usage patterns as reference.** How netcoredbg structures the attach flow in practice, how it handles thread context switches across the callback boundary, how it manages COM interface lifetimes against the managed/native boundary. Third Epistemics target — separate findings doc.

2. **Modern C# `ComWrappers` idioms.** Concretely: how to construct a `ComWrappers` subclass that exposes a managed `ICorDebugManagedCallback` implementation as a native-callable IUnknown with four IIDs; how to wrap an incoming `IUnknown*` from dbgshim as a typed managed proxy for `ICorDebug`; lifetime management between native and managed object graphs; how `[UnmanagedCallersOnly]` interacts with V-table thunk arrays. Fourth Epistemics target — also separate findings doc.

Lower-priority residual:

3. **`LPWSTR` width verification** (carried from finding 02) — quick cross-check.
4. **`HPROCESS` semantics on Unix** — used in `ICorDebugProcess::GetHandle`; might be `NULL` on Unix like other `HANDLE` types. Probe 05+ concern.
5. **The `ICorDebugProcess` "2 through 12" cadence** — which version is current in .NET 10 runtime; needed when picking which interfaces to expose in the substrate's first version.

## Probe sequence — refined by the contract

The contract supports a clean four-probe sequence, each smaller than the next would be if reordered:

### Probe 02 — refined: dbgshim attach + QI to ICorDebug

Previous design (from finding 02) was "obtain IUnknown via dbgshim, release." Refinement: **add the QueryInterface step**. Probe 02 now:

1. Resolve `libdbgshim.dylib` location (NativeLibrary.SetDllImportResolver).
2. `EnumerateCLRs(pid, &handles, &versions, &count)`.
3. `CreateDebuggingInterfaceFromVersion(versions[0], &pUnknown)`.
4. `pUnknown->QueryInterface(IID_ICorDebug, &pCordb)` — **the new step**. Confirms the pointer is a real ICorDebug, not just IUnknown.
5. `pCordb->Release()`, `pUnknown->Release()`, `CloseCLREnumeration(...)`.

Falsification scope unchanged. The QI step adds one COM negotiation; failure would surface as `E_NOINTERFACE` (substrate doesn't expose ICorDebug for this runtime version — substantive finding) or success (we have a real interface — green light for probe 03).

### Probe 03 — ICorDebug lifecycle (Initialize + Terminate)

After probe 02 succeeds:

1. Get to `pCordb` as in probe 02.
2. `pCordb->Initialize()`.
3. Validate HRESULT.
4. `pCordb->Terminate()` (no processes attached, so the "wait for ExitProcess" constraint doesn't apply).
5. Release.

Validates the lifecycle without yet needing the callback V-table. Falsification:
- `Initialize()` fails → runtime / dbgshim version mismatch, or platform-specific init issue
- `Terminate()` fails when no process is attached → contract differs from headers

### Probe 04 — `ICorDebugManagedCallback` V-table construction

The boss-fight probe. Doesn't attach to anything — just constructs the V-table and validates `SetManagedHandler` accepts it.

1. Implement a managed class `StubCallback` exposing `ICorDebugManagedCallback` (and 2/3/4) — each method returns `S_OK` without doing anything.
2. Build a `ComWrappers` subclass exposing the four IIDs over the same managed instance via `ComInterfaceEntry[]`.
3. `pCordb->SetManagedHandler(pStubCallback)` — validates the V-table dispatch through `QueryInterface(ICorDebugManagedCallback)` and the slot-3-or-later method-pointer resolution.
4. Validate HRESULT.
5. Don't attach; clean up.

Falsification:
- `SetManagedHandler` returns `E_NOINTERFACE` → V-table or IID layout wrong
- Process crashes during the QI → V-table thunk signature mismatch (calling convention, parameter widths)
- Subtle: a wrong method order in the V-table won't surface until an actual callback fires — which is why we defer attach to probe 05

### Probe 05 — actual attach with one event round-trip

After probes 02-04 land:

1. Setup as in probe 04 — but replace `StubCallback` with one that captures the callback arguments and records the event class.
2. `pCordb->DebugActiveProcess(pid, FALSE, &pProcess)`.
3. Wait for the first managed event (typically `CreateProcess` or `LoadModule`).
4. `pProcess->Continue(FALSE)` — let it run.
5. After a deliberate event or timeout, `pProcess->Detach()`.
6. Cleanup.

First real exercise of attach + callback dispatch. Validates the end-to-end Layer 3 surface.

## References

- `dotnet/runtime/src/coreclr/inc/cordebug.idl` — read 2026-05-20, 7,830 lines, 203 interfaces
- IIDs collected above (probe-relevant subset)
- Mercury session 2026-05-20 finding `icordebug-contract-surveyed`
- Findings 01 (IPC protocol survey) and 02 (dbgshim API survey) — predecessor Epistemics outputs
- Pending targets:
  - netcoredbg's usage of ICorDebug — third Epistemics target
  - `System.Runtime.InteropServices.ComWrappers` — fourth Epistemics target

## Probe 03 outcome

*Pending — added after probe 03 runs.*
