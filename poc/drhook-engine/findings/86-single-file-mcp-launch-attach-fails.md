# Finding 86 — Single-file launch *and* attach fail through the live MCP with 0x80131C3C (engine path works)

**Date:** 2026-06-24
**Status:** Open — investigation. The single-file *symbol* work (findings 82–85) is engine-validated and
unaffected; this is a live-MCP launch/attach environment issue.

## Symptom

Through the running `drhook-mcp` server, both:
- `drhook_launch program=<single-file apphost> args=[]` → `DbgShim.LaunchWithDebuggerPosix failed (HRESULT 0x80131C3C)`
- `drhook_attach pid=<single-file apphost>` → `CreateDebuggingInterfaceFromVersion failed (HRESULT 0x80131C3C)`

fail. The **same** `DebugSession.Launch(apphost, [], dir, sink)` **succeeds** from the engine probe
`single-file-smoke.cs` (run via `dotnet run`): it stops, binds, hits, and resolves locals + argument
names. So this is **not** the launch-vs-attach distinction, and **not** the single-file symbol fix — it
is dbgshim being unable to create the debugging interface for this target **from the long-lived
MCP-server process context**, where a fresh `dotnet run` probe process can.

## What's established

- `dotnet exec X.dll` launches work fine through the MCP all session (hello, argname-fidelity,
  eval-arg-receiver, condbp-demo). Only the bare single-file apphost fails.
- The failure is at the dbgshim *create-the-debugging-interface* step (both the launch and attach error
  messages name it), i.e. **before** any breakpoint / hold-gate / symbol resolution.
- **Version split (confounded):** the single-file target is **net10.0**; the MCP server is **net10.0**;
  every target the MCP debugged successfully this session was a **net11.0** file-based app. So the
  differentiator is either net10.0-vs-net11.0 *or* the single-file packaging — not yet isolated.

## Hypotheses

1. **mscordbi/dbgshim version resolution** differs between the MCP-server process and a fresh
   `dotnet run`: `CreateDebuggingInterfaceFromVersion` loads the `mscordbi` matching the *target's* CLR;
   the MCP server may resolve a wrong/incompatible one for a net10.0 (or bundled) target. (0x80131C3C is
   a CORDBG facility error consistent with an interface/version-compat failure.)
2. **Single-file CLR-version detection**: dbgshim's version probe of a single-file apphost's runtime
   may behave differently from the MCP-server context (env: `DOTNET_ROOT`, `DBGSHIM_PATH`, the server's
   own bundled `libmscordbi`).

## Next steps (isolation)

- Launch a **net10.0 non-single-file** target (`dotnet exec` a net10.0 dll) through the MCP — if it also
  fails, it's net-version; if it works, it's single-file packaging.
- Launch a **net11.0 single-file** apphost through the MCP — the converse control.
- Diff `DBGSHIM_PATH` / resolved `libmscordbi` path + the runtime version dbgshim detects, between the
  MCP-server process and the `dotnet run` probe process.

## Operator note

Documented in DRHOOK.md "Lifecycle discipline & common pitfalls": until this is fixed, validate
single-file via the engine probes, not the live MCP tools.

## References

- `src/DrHook.Engine/Interop/DbgShim.cs` (`LaunchWithDebuggerPosix`, `CreateCordbForProcess`).
- Findings 82–85 (single-file symbol work — engine-validated, unaffected).
