# Finding 23: SymbolReader — Portable PDB reading, BCL-only, unit-validated (symbols 6a)

**Status:**   **PASSED (unit tests, not a probe).** `SymbolReader` reads a module's Portable PDB to map
an `mdMethodDef` token + IL offset → `(file, line)` and to read local-variable names by slot. It is
**pure managed** — `System.Reflection.Metadata` over the module file on disk — so it needs no live
process and is deterministically unit-testable. This is the symbol layer that turns "Worker.Tick, IL
offset 7" into "Worker.cs:42" and unnamed slots into names: the foundation for source-line breakpoints,
named locals, and the client-side condition evaluation that conditional breakpoints need.
**Date:**     2026-05-22
**Validation:** `tests/DrHook.Engine.Tests/SymbolReaderTests.cs` — reads THIS test assembly's own PDB.

## Substrate sovereignty holds (verified, not assumed)

The worry was that symbols would force a native symbol reader (`ISymUnmanagedReader`/diasymreader —
Windows-centric, dubious on macOS) or a third-party package. Neither: Portable PDB is itself a metadata
format, and `System.Reflection.Metadata` is present in **both** the runtime shared framework *and the
net10.0 targeting ref pack** (checked: `Microsoft.NETCore.App.Ref/10.0.0/ref/net10.0/System.Reflection.Metadata.dll`).
So it is compile-time referenceable with **no PackageReference and no ADR-009 dependency decision** — the
same BCL-only posture as the rest of the engine. We read the PDB ourselves.

## API

- `SymbolReader.TryOpen(modulePath)` — opens the **embedded** Portable PDB (PE debug directory entry
  `EmbeddedPortablePdb`) or a **sidecar** `<module>.pdb` next to the dll; null if neither. Dispose releases
  the file streams.
- `TryGetLine(methodToken, ilOffset)` → `SourceLocation(file, line)` — the nearest sequence point at or
  before the offset.
- `GetLocalNames(methodToken)` → `[(slot, name)]` — from PDB local scopes.

## Validation (4 unit tests, 21 total green)

Because the reader is file-based, the test opens the **test assembly's own** Portable PDB and reads back a
fixture method (`ComputeFixture`) whose source and locals it controls — deterministic, CI-safe, no
debuggee:
- `TryOpen` finds the assembly's (sidecar) Portable PDB.
- `GetLocalNames` returns the named locals `doubled` and `widened`.
- `TryGetLine(token, 0)` maps the entry to `SymbolReaderTests.cs` at a positive line.
- A TypeDef token (`0x02…`) returns null (only `mdMethodDef` resolves).

This is a stronger validation surface than a probe — deterministic and runs in CI — and matches the
testability bar in `docs/limits/drhook-testability.md`.

## Scope / what's next

Not yet wired into the live engine:
- **6b** — source lines in stack frames: read each frame's IL offset (`ICorDebugILFrame.GetIP`@11), map via
  `SymbolReader` → `GetStackFrames` shows `Type.Method @ file:line`.
- **6c** — source-line breakpoints: a reverse `file:line → (token, IL offset)` lookup +
  `ICorDebugCode.CreateBreakpoint(offset)`.
- **6d** — locals by name: `ICorDebugILFrame.GetLocalVariable`@14 + `GetLocalNames` → named local values.

Then the conditional-breakpoint / client-side-eval decision (ADR-006 Open Question 2), which this layer
unblocks.

## References

- Engine: `src/DrHook.Engine/SymbolReader.cs` (`SourceLocation`, `LocalName`)
- Tests: `tests/DrHook.Engine.Tests/SymbolReaderTests.cs`
- Findings 21 (stack frames — gains lines next), 22 (arg values — gains local names next)
- Mercury session 2026-05-21 observation `symbolreader-passed`
