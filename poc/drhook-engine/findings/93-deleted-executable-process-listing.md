# Finding 93 — A .NET process whose executable was deleted breaks process listing on Linux

**Date:** 2026-10-25
**Status:** Root cause **PROVEN** on zbook (Ubuntu 26.04.1 LTS, x86_64, distro-built .NET 10.0.13); **fixed** on Linux, macOS unverified.
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

## Fix

- **One per-process read, shared by both listings.** `ProcessIdentity.TryRead`
  (`src/DrHook.Engine/Observation/ProcessIdentity.cs`) skips only a process that has exited. It strips the
  ` (deleted)` suffix (Linux only), treats a main module whose file no longer exists as deleted on every platform,
  and reads the version only from a file that still exists, catching `IOException` and `UnauthorizedAccessException`.
  A main module that can't be read at all (access denied) keeps the process, with no path.
- **The process stays listed and is marked.** `DotnetProcess` and the `drhook_processes` row gain `ExecutableDeleted`
  (`"executableDeleted"` in the JSON); the version is null, or `"unknown"` in the tool.
- **One listing.** `ProcessAttacher.ListDotNetProcessesAsync` now maps `ProcessInspector.ListDotnetProcesses`. Its own
  copy, with the bare `catch`, is gone. The MCP tool also gets the bounded discovery from DBG-PI-1 (finding 59).

## Validation

- `DeletedExecutableListingTest` (integration) runs a private copy of the snapshot target, deletes the copy's
  apphost, and checks both listings. Against the old code it fails with the original
  `FileNotFoundException: ... (deleted)`. With the fix it passes, and the full suite passed 17/17 three times on zbook
  with no leftover processes.
- Seven unit tests in `ProcessInspectorTests` cover the suffix parsing, a pid that isn't running, and self.
- The stale local-source `drhook-mcp` server that started this finding is now listed with
  `ExecutableDeleted = True` instead of making the listing throw.

## Open

- **macOS is unverified.** macOS has no ` (deleted)` path, so the fix relies on the missing-file check there. On macOS
  the integration test only checks that the process is listed. Whether a deleted main module is marked has not been
  checked. A rebuild that writes a new file at the same path probably shows the *new* file's version, and that case
  isn't detected on macOS.
