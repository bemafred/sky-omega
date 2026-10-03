# DRAFT — dotnet/diagnostics issue (not filed; Martin files it)

**Title:** dbgshim `EnumerateCLRs` crashes the debugger (null deref in `MachOModule::ReadSymbolTable`) when the target maps a Mach-O without `LC_SYMTAB` (macOS 27, Apple GPU `.metallib`)

**Component:** dbgshim (`src/dbgshim/dbgshim.cpp`, `src/shared/debug/dbgutil/machoreader.cpp`)

### Description

On macOS, `EnumerateCLRs` calls `GetTargetCLRMetrics` for every module mapped into the target process, and for
each non-coreclr module `TryReadSymbolFromFile` parses the module's on-disk Mach-O to look for the single-file
`DotNetRuntimeInfo` export. `MachOModule::ReadSymbolTable` assumes the image has an `LC_SYMTAB` (and
`LC_DYSYMTAB`) load command: after `ReadLoadCommands()` succeeds it dereferences `m_symtabCommand`, which is only
guarded by `_ASSERTE` (compiled out in release builds).

On macOS 27 with an Apple M5 GPU, every Metal-using process maps
`/System/Library/Extensions/AGXMetalG17X.bundle/Contents/Resources/ds.binary.metallib.g17s-a0`, a valid 64-bit
Mach-O (`MH_MAGIC_64`, filetype 13, GPU cputype) whose load commands are only `LC_NOTE`, `LC_SEGMENT_64`,
`LC_BUILD_VERSION` and `LC_UUID`. `m_symtabCommand` stays null and the **debugger process** crashes, so any
debugger using dbgshim's attach flow (`EnumerateCLRs` → `CreateVersionStringFromModule` →
`CreateDebuggingInterfaceFromVersionEx`) cannot attach to a GUI app that uses Metal (e.g. any Avalonia app).

### Repro

1. macOS 27.0.1, Apple M5 (GPU driver bundle `AGXMetalG17X`).
2. Run any .NET GUI app that renders through Metal (e.g. an Avalonia 11 app).
3. From another process, call `EnumerateCLRs(pid, ...)` from libdbgshim (9.0.661903 and 10.0.745401 both crash).

```
EXC_BAD_ACCESS (code=1, address=0x8)
frame #0: libdbgshim.dylib`MachOModule::ReadSymbolTable() + 64
frame #1: libdbgshim.dylib`MachOModule::TryLookupSymbol(char const*, unsigned long long*) + 36
frame #2: libdbgshim.dylib`TryReadSymbolFromFile + 368
frame #3: libdbgshim.dylib`GetTargetCLRMetrics(char16_t const*, tagCLR_ENGINE_METRICS*, ClrInfo*, unsigned int*) + 156
frame #4: libdbgshim.dylib`EnumerateCLRs + 508
```

`otool -l` on the file shows no `LC_SYMTAB`; the fault address `0x8` is `symtab_command.symoff`.

### Suggested fix

In `MachOModule::ReadSymbolTable` (and anywhere else `m_symtabCommand` / `m_dysymtabCommand` are used), return
`false` when `ReadLoadCommands()` did not find them, instead of relying on `_ASSERTE`. Optionally, skip Mach-O
filetypes other than `MH_EXECUTE` / `MH_DYLIB` / `MH_BUNDLE` in the single-file scan.

### Related observation

On Unix, `CreateVersionStringFromModule` calls `GetTargetCLRMetrics` without the `ClrInfo` out-parameter, so the
single-file check never runs and a version string is returned for any mapped file — a caller that passes the wrong
module gets `CORDBG_E_DEBUG_COMPONENT_MISSING` later rather than an error here. Possibly intended; noting it in case not.

### Workaround (what we did)

Locate `libcoreclr.dylib` ourselves (`proc_pidinfo(PROC_PIDREGIONPATHINFO)` region walk) and call
`CreateVersionStringFromModule` with that path, skipping `EnumerateCLRs`.
