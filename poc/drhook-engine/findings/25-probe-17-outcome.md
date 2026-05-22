# Finding 25: Probe 17 Outcome — PASSED: source-line breakpoints (symbols 6c)

**Status:**   **PASSED, 2/2.** A breakpoint can now be set by `file:line` — the way a caller actually
thinks — and binds **mid-method**, not just at entry. Probe 17 set a breakpoint at the
`int b = a * 2;` statement (`17-line-target.cs:31`), ran, and the hit landed exactly there:
`Worker.Step @ 17-line-target.cs:31`.
**Date:**     2026-05-22
**Probe:**    `poc/drhook-engine/17-line-breakpoint-smoke.cs` + `17-line-target.cs`

## How it composes

`DebugSession.SetBreakpointAtLine(moduleSubstr, fileHint, line)`:
1. `RuntimeNavigation.FindModule` → the module; its path opens (cached) `SymbolReader`.
2. **Reverse symbol lookup** — `SymbolReader.TryFindLine(fileHint, line)` scans every method's
   sequence points for the nearest one at or after the line in a matching document → `(mdMethodDef
   token, IL offset)`. (Forward `TryGetLine` from 6b; this is its inverse over the whole PDB.)
3. **Bind at the offset** — `Breakpoints.TryCreateAtOffset`: `GetFunctionFromToken`@9 →
   `ICorDebugFunction.GetILCode`@6 → `ICorDebugCode.CreateBreakpoint(offset)`@7 → `Activate`@3.
   (vs `ICorDebugFunction.CreateBreakpoint`@8 which is function-entry-only.)

The hit arrives as a `Breakpoint` callback → `StopReason.Breakpoint`, riding the stopping model
unchanged. The stack frame's GetIP (6b) maps back to the same line, confirming the hit is *at* the
requested statement.

## A self-inflicted probe bug (worth noting)

First run set the breakpoint at line 4 and timed out: the probe finds the `BREAK_HERE` marker by text
search, and the marker string also appeared in the target's **header comment** — so it matched the
comment line first, bound to the nearest executable line (a one-time startup statement), and never
hit. Fixed by removing the literal token from the comment. (A reminder that text-marker fixtures must
be unambiguous; the engine path was correct throughout — `SetBreakpointAtLine` *did* bind, just to the
wrong line.)

## What this proves

1. **`file:line` → IL offset → bound breakpoint works** on CoreCLR 10 / macOS-arm64, mid-method.
2. **The forward+reverse PDB symmetry holds** — 6b maps offset→line; 6c maps line→offset, over the
   same sequence-point data.
3. **`ICorDebugCode.CreateBreakpoint(offset)`** binds at an arbitrary IL offset (not just entry) —
   the mechanism conditional breakpoints will reuse (set at the line, evaluate the condition there).

## Next

- **6d** — locals by name: `ICorDebugILFrame.GetLocalVariable`@14 + `SymbolReader.GetLocalNames` →
  named local values (completing "stopped here, with THESE named values").
- Then the **conditional-breakpoint / client-side-eval** decision: a line breakpoint + reading named
  locals/args + a client-side predicate = conditional breakpoints without func-eval (the netcoredbg gap).

## References

- Probe: `poc/drhook-engine/17-line-breakpoint-smoke.cs`, `17-line-target.cs`
- Fixture: `fixtures/17-line-breakpoint-osx-arm64-20260522T100829Z.txt`
- Engine: `SymbolReader.TryFindLine`, `Interop/Breakpoints.TryCreateAtOffset`, `DebugSession.SetBreakpointAtLine`
- Findings 24 (6b offset→line), 23 (SymbolReader), 19 (function-entry breakpoints)
- Mercury session 2026-05-21 observation `probe-17-passed`
