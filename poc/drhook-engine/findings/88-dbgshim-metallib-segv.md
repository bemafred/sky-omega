# Finding 88 — Attaching to any Metal GUI app SEGVs the debugger: dbgshim's single-file scan parses a GPU-driver Mach-O with no `LC_SYMTAB`

**Date:** 2026-10-03
**Status:** Root cause **PROVEN** (lldb backtrace + per-file trace); **fix IMPLEMENTED + validated** — DrHook no
longer calls `EnumerateCLRs`; upstream bug drafted for dotnet/diagnostics.

## Symptom

On macOS 27.0.1 / Apple M5, `DebugSession.Attach` to an Avalonia app (the pristine `poc/avalonia-tictactoe`, or
`poc/avalonia-hang-target`) killed the **debugger** process with SIGSEGV (exit 139) before returning — so
`drhook_attach` to a GUI app would take `drhook-mcp` down, and `drhook_capture_visual` (which needs an attach)
was unusable. A non-GUI target started the same way (Aqua session, via `open`) attached fine, so the audit-session
boundary was ruled out. dbgshim **9.0.661903 and 10.0.745401 crash identically**.

## Root cause (proven)

```
EXC_BAD_ACCESS (code=1, address=0x8)
libdbgshim.dylib  MachOModule::ReadSymbolTable() + 64
libdbgshim.dylib  MachOModule::TryLookupSymbol(char const*, unsigned long long*)
libdbgshim.dylib  TryReadSymbolFromFile
libdbgshim.dylib  GetTargetCLRMetrics(...)
libdbgshim.dylib  EnumerateCLRs
```

1. `EnumerateCLRs` (dbgshim.cpp) loops over **every module mapped into the target** and calls
   `GetTargetCLRMetrics` on each; for every non-coreclr module it opens the **on-disk file** and looks up the
   single-file `DotNetRuntimeInfo` export (`TryReadSymbolFromFile`).
2. `MachOModule::ReadSymbolTable` (shared/debug/dbgutil/machoreader.cpp) calls `ReadLoadCommands()`, which
   succeeds, then dereferences `m_symtabCommand` — guarded only by `_ASSERTE`, compiled out in release. A Mach-O
   without `LC_SYMTAB` leaves it null; `symtab_command.symoff` is at offset 8 → the fault address `0x8`.
3. The file (an lldb breakpoint on `TryReadSymbolFromFile` logged every path; this was the last before the fault):
   `/System/Library/Extensions/AGXMetalG17X.bundle/Contents/Resources/ds.binary.metallib.g17s-a0` — 28.8 MB,
   dated 2026-09-24, mapped into every Metal process on this GPU. A **valid** 64-bit Mach-O (`MH_MAGIC_64`,
   filetype 13, a GPU cputype) whose load commands are `LC_NOTE`×8, `LC_SEGMENT_64`×2, `LC_BUILD_VERSION`,
   `LC_UUID` — **no `LC_SYMTAB`**.

Avalonia renders through Metal, so every Avalonia GUI app maps it; a console app does not.

## Two surprises on the way to the fix

- **BCL `Process.Modules` on macOS returns only the main executable** (count = 1 for TicTacToe) — it cannot
  locate `libcoreclr`. (Linux and Windows return the full list.)
- **`CreateVersionStringFromModule` does not validate on Unix.** It calls `GetTargetCLRMetrics(path, &metrics)`
  *without* the `ClrInfo` out-parameter, and the non-Windows branch only runs the single-file check when that
  parameter is non-null — so it returns a version token for **any** mapped file. Handing it a framework-dependent
  app host produced a bogus token → `0x80131C3C` (`CORDBG_E_DEBUG_COMPONENT_MISSING`) at
  `CreateDebuggingInterfaceFromVersionEx`.

## Fix

`DbgShim.CreateCordbForProcess` no longer calls `EnumerateCLRs`:

1. `Interop.MappedFiles.FindByFileName(pid, "libcoreclr.dylib" | "libcoreclr.so" | "coreclr.dll")` — macOS:
   `proc_pidinfo(PROC_PIDREGIONPATHINFO)` region walk (no task port; works unentitled for a same-user target;
   bounded at 65,536 regions); Linux: `/proc/<pid>/maps`; Windows: `Process.Modules`. Nothing is opened or parsed.
   Polled for 3 s (finding 04's retry budget — a fresh target may not have loaded its runtime yet).
2. Only if no runtime module appears in that window: the **main executable** (a single-file app embeds the
   runtime in its host). The order is load-bearing because of the non-validation above.
3. `CreateVersionStringFromModule(pid, runtimeModule)` → `CreateDebuggingInterfaceFromVersionEx` — unchanged.
   This parses only the file it is handed.

`proc_pidinfo`'s import is shared in `Interop/LibProc.cs` (used by `ProcessParentage` too — finding 87).
Region-walk layout verified by compiling a C probe against the SDK: `PROC_PIDREGIONPATHINFO = 8`,
`sizeof(proc_regionwithpathinfo) = 1272`, `pri_address` @ 80, `pri_size` @ 88, `vip_path` @ 248.

## Validation

- `MappedFilesTests` (3): the test host's own runtime module is found and exists on disk; an unmapped name and an
  exited pid yield null. `DrHook.Engine.Tests` 162/162.
- Live: `DebugSession.Attach` to TicTacToe — attached, Pause-stopped, disposed, app still running (was: SEGV).
- Probe 88 (UI liveness) attaches to three Avalonia instances in one process without fault.
- Full integration suite green, plus the repeat-run gate recorded with finding 89.

## Upstream

A dotnet/diagnostics issue is drafted (`88-dbgshim-upstream-issue.md`): null-check `m_symtabCommand` /
`m_dysymtabCommand` after `ReadLoadCommands` (and, ideally, skip non-dylib Mach-O filetypes in the single-file
scan). DrHook does not depend on the upstream fix.

## References

- `src/DrHook.Engine/Interop/DbgShim.cs` (`CreateCordbForProcess`), `src/DrHook.Engine/Interop/MappedFiles.cs`,
  `src/DrHook.Engine/Interop/LibProc.cs`, `tests/DrHook.Engine.Tests/MappedFilesTests.cs`.
- dotnet/diagnostics `src/dbgshim/dbgshim.cpp` (`EnumerateCLRs`, `GetTargetCLRMetrics`,
  `CreateVersionStringFromModule`), `src/shared/debug/dbgutil/machoreader.cpp` (`ReadSymbolTable`).
- Finding 89 surfaced immediately after this one, on the same attach→detach path.
