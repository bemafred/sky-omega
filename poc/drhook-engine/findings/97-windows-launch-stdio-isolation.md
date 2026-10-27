# Finding 97 — on Windows a launched debuggee shared the host's stdin, stdout and stderr; the spawn is now DrHook's

**Date:** 2026-10-26
**Status:** Problem **PROVEN** (probe 64 on omega-windows). **Fixed**: the Windows launch owns its spawn, as POSIX
has since [finding 75](75-probe-59-outcome.md) (ADR-011 D2).

## Problem

`DebugSession.Launch` on Windows used dbgshim's `CreateProcessForLaunch`, which takes no stdio handles. Finding 96
listed "launch stdio is not isolated" as a gap, inferred from a code comment. Probe 64 observed it.

`poc/drhook-engine/64-launch-stdio-smoke.cs` plays drhook-mcp. The outer process starts itself again ("inner") with
stdin, stdout and stderr redirected to pipes, as an MCP client starts the server, and writes one request line to the
inner's stdin. The inner launches a target through the public `DebugSession.Launch` and runs it to exit. The target
writes a marker to stdout and to stderr, tries for 1.5 s to read stdin, and records what it read in a report file.

| | macOS (control) | Windows 11 arm64, before |
|---|---|---|
| target markers on the host's stdout / stderr | none | **both** |
| target's stdin | EOF (`/dev/null`) | **the client's request line** |

So under drhook-mcp a launched Windows debuggee would not only write into the JSON-RPC stream: a debuggee that reads
stdin would **consume the client's requests**.

## Fix

`Interop/WindowsSpawn.cs`, the counterpart of `PosixSpawn`:

- `CreateProcessW` with `CREATE_SUSPENDED`. stdin is `NUL`; stdout and stderr are fresh pipes whose read ends stay with
  DrHook.
- **Only those three handles are inherited** (`PROC_THREAD_ATTRIBUTE_HANDLE_LIST`). `bInheritHandles` alone would hand
  the child every inheritable handle the host holds, the MCP channel included.
- `CREATE_NO_WINDOW`: the child gets a console of its own with no window. Console APIs work in the target, nothing it
  does reaches a console the host may share, and no window appears when the host has none.
- Arguments are quoted by the Windows parsing rules (`CommandLineToArgvW`): backslashes are doubled only before a
  quote. The old `Quote` did not do that, so a path ending in `\` broke.
- The same call takes the environment block, so **launch with environment overrides now works on Windows**. It used to
  throw `PlatformNotSupportedException`. Names are case-insensitive, so an override replaces an entry of any case.

`DbgShim.LaunchWithDebuggerWindows` arms `RegisterForRuntimeStartup` while the child is suspended, then resumes its
main thread; this is the POSIX flow with `ResumeThread` for `SIGCONT`. The console drain is now one loop on both
platforms, given each platform's read function (a file descriptor on POSIX, a pipe handle on Windows). dbgshim's
`CreateProcessForLaunch`, `ResumeProcess` and `CloseResumeHandle` are no longer used.

## Validation

- Probe 64 on omega-windows after the fix: **RESULT 0, isolated**. The host's stdout carries only the inner's own
  line, its stderr is empty, and the target read `EOF`.
- `ConsoleIsolationTest` (integration, every platform): the target's markers arrive as `OnConsoleOutput` on the right
  streams, and its stdin is empty.
- `WindowsSpawnTests` (unit): quoting checked against expected strings everywhere and round-tripped through
  `CommandLineToArgvW` on Windows; environment overrides replace an entry of any case; a spawned child stays
  suspended until resumed, sees the override, and writes to the returned pipe.
- Integration suite: omega-windows 17 passed / 2 skipped (POSIX-only) in 3 of 3 runs; omega 18 / 1 skipped in 3 of 3;
  elite 18 / 1 in 2 of 2. No leftover processes.

## Still open on Windows

- `RequestExit` (graceful stop). The child's own console (`CREATE_NO_WINDOW`) bears on the design:
  `GenerateConsoleCtrlEvent` reaches only processes that share the caller's console.
- The probe corpus and `drhook-mcp` on Windows; Windows x64.
