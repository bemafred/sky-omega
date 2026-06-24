# Finding 85 — Source breakpoints in managed PublishSingleFile apps (sidecar-PDB fallback) + the single-file landscape

**Date:** 2026-06-24
**Status:** Engine + probe validated for managed single-file **binding + local names**. Residuals
characterized below — one tracked fix (argument names), one structural (NativeAOT).
**Probe:** `poc/drhook-engine/single-file-smoke.cs` + `single-file-target/`

## The two "single-file" meanings (an important distinction)

1. **NativeAOT** — and note this is the **default** for file-based `dotnet publish app.cs` in
   .NET 10/11 (the publish pulls `microsoft.netcore.app.runtime.nativeaot`, emits a native Mach-O +
   `.dSYM`, no managed `.dll`/`.pdb`). NativeAOT has **no managed runtime / no IL / no metadata / no
   Portable PDB**, so **ICorDebug does not apply** — DrHook cannot debug a NativeAOT binary. This is
   **structural, not a DrHook gap**: native debugging (lldb/DWARF via the `.dSYM`) is the tool there.
2. **Managed `PublishSingleFile=true`** (CoreCLR + JIT, assemblies bundled into the apphost). ICorDebug
   applies. This finding's subject.

## Problem (managed single-file)

ICorDebug **does** attach to the bundle (verified: stops at `Debugger.Break`). But the app assembly
loads **from the bundle**, so ICorDebug reports its module path as a **bare name** (`sfprobe`,
`exists=False`). `SymbolReader.TryOpen` bails (no on-disk PE), the breakpoint goes **pending and never
binds** → the process runs to completion — even though the sidecar `sfprobe.pdb` sits next to the apphost.

## Fix

- `SymbolReader.TryOpenPdb(pdbPath)` — open a sidecar Portable PDB by its own path (no PE).
- `DebugSession` captures the launched/attached executable's directory (`_imageBaseDir`: the Launch
  `program` path, or the Attach process's `MainModule`).
- `SymbolsFor` — when a module path has no on-disk PE, fall back to `<imageDir>/<name>.pdb`.

So a bundled module resolves to its sidecar PDB → source breakpoints **bind + hit**, and **local**
names resolve (PDB local scopes).

## Validated

`single-file` probe: publish `single-file-target` (Debug — Release optimizes away the return-line
sequence point and the local), launch the apphost, arm a source breakpoint → **BOUND + HIT**; local
`doubled=14` resolved from the sidecar PDB. Full `DrHook.Engine.Tests` **130/130**, no regression.

## Residuals

1. **Argument names are positional (`arg0`) for single-file.** Parameter names live in the assembly
   `Param` table, which is **inside the bundle, not the sidecar PDB**; the file-based `MethodMetadata`
   (reads the on-disk PE) can't reach them. Fix: a loaded-module-metadata fallback via ICorDebug
   `IMetaDataImport` (`GetMethodProps` `mdStatic` → HasThis; `EnumParams`@22; `GetParamProps`@57) when
   the file PE is absent. **Tracked — next increment.** Argument *values* are correct; only names fall back.
2. **`DebugType=embedded` single-file** (PDB embedded in the bundled assembly, no sidecar) — DrHook
   can't read the embedded PDB off-disk; would need to read assembly bytes via ICorDebug. Unverified; deeper.
3. **NativeAOT** — structural not-supported (above).

## References

- `src/DrHook.Engine/SymbolReader.cs` (`TryOpenPdb`), `src/DrHook.Engine/DebugSession.cs`
  (`_imageBaseDir`, `ImageDirOf`, `SymbolsFor` fallback).
- Mercury session graph `https://sky-omega.dev/sessions/2026-06-23-drhook-filebased-breakpoints/`.
