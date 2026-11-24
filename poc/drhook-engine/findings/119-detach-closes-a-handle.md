# Finding 119 — a detach closes one of a Windows target's own handles (mscordbi): 30 of 40 times, always the one that took the debugger's handle's value

**Date:** 2026-11-24
**Status:** **RECORDED** on omega-windows (Windows 11 26200 arm64, 4 cores, .NET 10.0.15), one run of probe 119 at
`30b8da9`, 40 sessions, RESULT(1). Read in dotnet/runtime `release/10.0`. Not run: the Windows runners, x64, other .NET
versions.

## Why

After a borrowed detach, some busy Windows targets were held, some crashed with "Internal CLR error" (`0x80131506`), and
some failed a write to a file they had just opened with "the handle is invalid" ([finding 118](118-held-after-detach.md)).
The last one says a handle of the target's was closed by code that was not the target's.

## What the source shows: one handle value closed twice in every detach

- **At attach.** mscordbi duplicates its own process handle into the target, with `SYNCHRONIZE` access only, so that the
  target's runtime can tell when the debugger exits (`LocalEventChannel::Init`, `RemoteHANDLE::DuplicateToRemoteProcess`,
  `debug/inc/dbgappdomain.h`). mscordbi keeps the handle's value in its own copy of the debugger control block.
- **At the detach, in the target.** The target's runtime closes that handle when it receives
  `DB_IPCE_DETACH_FROM_PROCESS` (`DebuggerRCThread::RightSideDetach` → `CloseIPCHandles` → `RemoteHANDLE::Close`). Then it
  replies, and lets its threads run.
- **At the detach, in mscordbi.** `CordbProcess::Detach` ends with `Neuter`, which deletes the event channel
  (`LocalEventChannel::Delete`). That closes the same value in the target again, from mscordbi's own copy, which no one has
  changed (`RemoteHANDLE::CloseInRemoteProcess`). It does it with `DuplicateHandle` and `DUPLICATE_CLOSE_SOURCE`, which
  closes the source handle "regardless of any error status returned" (Windows' documentation).

So the target's runtime closes the handle first, and mscordbi closes the same value once more a few milliseconds later,
while the target runs again. Windows soon hands out a freed handle value again. A handle the target makes in between can
have that value, and then mscordbi's close is a close of the target's own handle.

Linux and macOS have no such handle: there the runtime talks to the debugger over the transport
(`FEATURE_DBGIPC_TRANSPORT_VM`), and neither close is compiled.

## What was run

[`119-detach-closes-a-handle-smoke.cs`](../119-detach-closes-a-handle-smoke.cs), 40 sessions, each against a fresh
target ([`119-detach-closes-a-handle-target.cs`](../119-detach-closes-a-handle-target.cs)), each borrowed:

- **While it is debugged**, the target finds which of its handle values name the debugger's process. It compares every
  value from 4 to `0x10000` with a handle of its own to that process (`CompareObjectHandles`), and writes a marker. The
  probe waits for the marker, then disposes the session.
- **The moment the debugger has gone**, the target makes 64 events, one after another, and records their values. 300 ms
  later it records which of them are invalid (`GetHandleInformation`). Nothing in the target closes them.
- **Hypothesis:** one of the new handles is invalid, and it has the debugger's handle's value. Refuted by sessions in
  which no new handle is invalid, or one with another value is.

## Results

| Outcome | Sessions |
|---|---|
| exactly one new handle invalid, the one with the debugger's handle's value | **30** |
| a new handle invalid with another value | 0 |
| no new handle invalid | 10 |

- **The debugger's handle.** In every session the target held exactly one handle to the debugger's process: `0x2FC` in
  20, `0x2F4` in 11, and seven other values in the rest.
- **When the first new event took the value.** That happened in 24 sessions, and it was the one closed. In 6 more a later
  event of the 64 took the value, and that one was closed.
- **The 10 with nothing lost.** In each, no event of the 64 took the value: something else in the target did, or
  mscordbi's close came first.
- **No anomaly** from DrHook in any session. `Dispose` took 91 to 226 ms.

The record is [`119-detach-closes-a-handle/omega-windows.json`](119-detach-closes-a-handle/).

## What this establishes

- **The double close.** On Windows, a detach through mscordbi closes a handle value in the target twice. When the target
  has reused the value in between, mscordbi's second close closes one of the target's own handles: in 30 of 40 detaches
  here.
- **What it costs the target.** Whatever that handle was for. Finding 118 shows it as:
  - a write to a file just opened that fails;
  - a thread whose wait is never ended (the held targets);
  - the runtime's own fatal error.

  How often that happens depends on what the target makes in the moment after the detach. A target that makes no handle
  then loses nothing. A busy one can lose any of them.
- **It is not DrHook's to prevent.** Both closes happen inside mscordbi's `Detach` and the target's runtime, and no
  ICorDebug call reaches either. It affects every debugger built on mscordbi on Windows that detaches. DrHook's own
  teardown order does not change it (finding 118: as often after ADR-016 6.6's change as before).

## Not established

- **The runners, x64, and other .NET versions.** Only omega-windows, 10.0.15, has been measured.
- **Whether `main` of dotnet/runtime still has it.** GitHub's code search of `main` finds the same `CloseInRemoteProcess`
  in `localeventchannel.cpp`; it was not read line by line.
- **Whether an upstream fix is simple.** mscordbi could read the target's field before it closes, since the target's
  runtime sets it to null when it closes. That is not tried.

## Next (proposed, not decided)

1. Report it to dotnet/runtime, with this probe as the reproduction. That is an outward act, so it is Martin's decision.
2. Run probe 119 on both Windows runners, x64 included.
3. Take it to ADR-016. "Attaching to a running program: detach and leave it running" (6.1) is not safe on Windows for a
   target that makes handles as the debugger leaves. The platform gate and the MCP tools' descriptions should say so.
