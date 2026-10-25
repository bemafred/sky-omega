# Finding 93 — A .NET process whose executable was deleted breaks process listing on Linux

**Date:** 2026-10-25
**Status:** Root cause **PROVEN** on zbook (Ubuntu 26.04.1 LTS, x86_64, distro-built .NET 10.0.13); **open, not fixed**.
Linux data point for [ADR-007](../../../docs/adrs/drhook/ADR-007-teardown-concurrency-test-debug.md) Phase 9. Not
covered by [finding 92](92-linux-first-run.md).

## Symptom

During the first full Linux test run (1.8.12), `ProcessInspectorTests.ListDotnetProcesses_IncludesSelf` failed with:

```
FileNotFoundException: .../src/DrHook.Mcp/bin/Debug/net10.0/drhook-mcp (deleted)
```

The process was the local-source DrHook MCP server that Claude Code was running. A solution build had replaced its
executable while it ran.

## Root cause (proven)

Linux shows a running process's executable through `/proc/<pid>/exe`. When that file is deleted or replaced, as a
rebuild does, the link target becomes `<path> (deleted)`. `ProcessModule.FileName` returns that string, and
`ProcessModule.FileVersionInfo` then calls `FileVersionInfo.GetVersionInfo` on a path that doesn't exist:

```
System.IO.FileNotFoundException: .../src/DrHook.Mcp/bin/Debug/net10.0/drhook-mcp (deleted)
   at System.Diagnostics.FileVersionInfo.GetVersionInfo(String fileName)
   at System.Diagnostics.ProcessModule.get_FileVersionInfo()
   at SkyOmega.DrHook.Engine.Observation.ProcessInspector.ListDotnetProcesses() in .../ProcessInspector.cs:line 74
```

DrHook has **two** process-listing implementations, and they fail in opposite ways:

| Code | Catches | Effect of one process with a deleted executable |
|---|---|---|
| `ProcessInspector.ListDotnetProcesses` (`src/DrHook.Engine/Observation/ProcessInspector.cs:80`) | only `InvalidOperationException`, `ArgumentException` | **the whole listing throws** |
| `ProcessAttacher.ListDotNetProcessesAsync` (`src/DrHook.Engine/Diagnostics/ProcessAttacher.cs:34`), behind the `drhook_processes` MCP tool | everything (bare `catch`) | **the process silently disappears from the list** |

The second failure matters more. A debug target whose binary has been rebuilt is the typical orphan, and it can't be
seen in `drhook_processes`. DRHOOK.md and CLAUDE.md both tell the operator to find orphaned targets there.

## Reproduction

1. Build any console app that sleeps forever, run it, then delete its executable while it runs
   (`ls -l /proc/<pid>/exe` shows `... (deleted)`).
2. Call `ProcessInspector.ListDotnetProcesses()` from a file-based app referencing `DrHook.Engine`
   (`#:property TargetFramework=net10.0`). It throws `FileNotFoundException` naming the deleted path.
3. `drhook_processes` returns a list without that process.

It also happens without step 1: during a normal session, any build that rebuilds `src/DrHook.Mcp` while Claude Code
runs the local-source DrHook MCP leaves that server in this state. On 2026-10-25 the baseline call (before deleting
anything) already threw because of it, and `drhook_processes` left the stale server out.

The integration suite passed 16/16 the same day only because no such process happened to be running then.

## Fix direction

- Read the version in its own `try` and catch `IOException` (`FileNotFoundException` derives from it): keep the process
  in the list with an unknown version instead of throwing or dropping it.
- Report the executable as stale. Strip the ` (deleted)` suffix from the path, or add a flag, so an operator can see
  that a target is running an old build.
- Merge the two listing implementations, or at least give them the same handling.
- Add a test that runs a target, deletes its executable, and checks it still appears in both listings.

## Open

- **macOS is unverified.** macOS has no ` (deleted)` path. A rebuild that replaces the file probably makes the listing
  report the *new* file's version rather than throw. Deleting the executable without replacing it is expected to
  throw the same way, but nobody has checked.
