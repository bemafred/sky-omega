# Finding 24: Probe 16 Outcome — PASSED: source lines in stack frames (symbols 6b)

**Status:**   **PASSED, 2/2.** Stack frames now carry source locations. At the `Worker.Tick`
breakpoint, `GetStackFrames` reads each frame's IL offset and maps it through the module's Portable
PDB to `Type.Method @ file:line`:
```
#0  Worker.Tick @ 11-bp-target.cs:28      ← Tick's body
#1  Program.<Main>$ @ 11-bp-target.cs:19  ← the worker.Tick() call site
```
This turns "where did we stop" from a bare method name into the source location DrHook's consumer
actually reasons about. The file-based-app target carried a usable Portable PDB and the mapping was
exact.
**Date:**     2026-05-22
**Probe:**    `poc/drhook-engine/16-frame-lines-smoke.cs` + `11-bp-target.cs`

## How it composes

`Frames.WalkManagedFrames` now returns structured `FrameInfo` (method, module path, token, IL
offset). The IL offset comes from QI to `ICorDebugILFrame` → `GetIP`@11 (the one new interop call;
the ILFrame IID `03E26311` was already validated in 5b). `DebugSession.GetStackFrames` resolves each
frame through a **per-module `SymbolReader` cache** (open the PDB once, dispose all on session
Dispose) and formats `Type.Method @ file:line`, falling back to `Type.Method` when no PDB or no
mapping. Clean separation: `Frames` stays pure interop; line resolution lives in `DebugSession`,
which owns the symbol cache.

## What this proves

1. **The 6a SymbolReader composes with the live engine** — real frames, real IL offsets, real
   source lines, end to end.
2. **`GetIP` gives the live execution point** per frame (not just method entry) — so a frame mid-method
   resolves to the executing line, which is what matters for stepping + conditional breakpoints.
3. **File-based-app targets carry usable PDBs** — `SymbolReader.TryOpen` found and read it without
   special handling.

## Scope / what's next

- **6c** — source-line breakpoints: the reverse map (file:line → token + IL offset) +
  `ICorDebugCode.CreateBreakpoint(offset)`, so callers set breakpoints the way they think (a line),
  not by method entry.
- **6d** — locals by name: `ICorDebugILFrame.GetLocalVariable`@14 + `SymbolReader.GetLocalNames`.
- Frames currently surface as formatted strings; a structured `StackFrame(method, file, line)` API is
  a straightforward enrichment when a consumer needs the parts separately.

## References

- Probe: `poc/drhook-engine/16-frame-lines-smoke.cs`
- Fixture: `fixtures/16-frame-lines-osx-arm64-20260522T093815Z.txt`
- Engine: `Interop/Frames.cs` (`FrameInfo` + `GetIP`), `DebugSession.GetStackFrames` + `SymbolsFor` cache, `SymbolReader` (6a)
- Findings 23 (SymbolReader), 21 (stack frames — names), 22 (ILFrame IID)
- Mercury session 2026-05-21 observation `probe-16-passed`
