# Finding 119 — a detach closes one of a Windows target's own handles (mscordbi): 30 of 40 times, always the one that took the debugger's handle's value

**Date:** 2026-11-24
**Status:** **RECORDED** on omega-windows (Windows 11 26200 arm64, 4 cores, .NET 10.0.15): one run of probe 119 at
`30b8da9`, 40 sessions, RESULT(1); and on 2026-11-28 one run of probe 119 under a handle trace at `6e4b0c3`, 20 sessions,
RESULT(0): both closes observed, the second made by mscordbi. Read in dotnet/runtime `release/10.0` and `main`. Not run:
the Windows runners, x64, other .NET versions.

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

Nothing refreshes mscordbi's copy between the two closes. `CordbProcess::UpdateRightSideDCB`, which reads the target's
control block into that copy, runs only when a wait for the target times out or fails (`CheckForUnrecoverableError`) and
in calls a detach does not make; the reply to the detach is read from the target's receive buffer, not from the control
block (`LocalEventChannel::GetReplyFromLeftSide`). `main` has the same code (read 2026-11-28).

### Who owns the handle

By Windows' rules, the target: the handle is an entry in the target's handle table. By the runtime's design, also the
target: `RemoteHANDLE`'s comment (`dbgappdomain.h`) describes it as a handle *injected* into another process. The debugger
makes it in the target's table, the target imports it, and from then on it is "a local handle in the *other* process",
used "the same way as a regular HANDLE". The target's runtime does use it as its own:

- **It waits on it.** The runtime's debugger thread puts it in its wait set. When it is signalled, because the
  debugger's process has died while attached, the target terminates itself (`DebuggerRCThread::MainLoop`,
  `EEPOLICY_HANDLE_FATAL_ERROR(0)`). That is a second way, besides kill on exit, in which a Windows target dies with its
  debugger (ADR-016 6.5).
- **It closes it** at the detach, and sets its field in the control block to NULL.

mscordbi never gives the handle up. `LocalEventChannel::Delete` closes it again from mscordbi's own copy, at the end of
every session, and then writes NULL into the target's field itself. It does not read that field first, so it cannot see
that the target has already closed the handle. There are two owners, and each one closes it.

A close from mscordbi is needed only when the target never took the handle over, as after an attach that failed
(`CordbProcess::CleanupHalfBakedLeftSide`). Why the debugger injects the handle, instead of the target opening the
debugger's process itself, is not written anywhere read. A guess: a target may not have the right to open a debugger
that is more privileged than itself, and the injection gives it that right, with `SYNCHRONIZE` access only.

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

## Who makes each close: observed in a handle trace (2026-11-28)

Probe 119 sees that a handle is lost, but not who closes it.
[`119-detach-closes-a-handle-trace.cs`](../119-detach-closes-a-handle-trace.cs) runs probe 119 under Windows'
Performance Recorder, with its built-in Handle profile. That profile records every handle create, close and duplicate in
the system, each with its call stack. The probe then reads the trace with TraceEvent. For each session it takes the
events on the debugger's handle value in the target's handle table, in time order.

- **Hypothesis:** in every session that lost a handle, these four steps happen in this order:
  1. a thread of the debugger duplicates a handle into the target with that value;
  2. a thread of the target closes it;
  3. a thread of the target makes a new handle with the same value;
  4. a thread of the debugger duplicates that new handle out of the target, with a frame of `mscordbi.dll` in its stack.
- **Refuted by** a lost handle missing a step, a step 4 by another process or without that frame, or a close of the new
  handle by the target itself.

Result: 20 sessions, 15 lost a handle, **H held in all 15**, refuted in none. Every session showed the same stacks (the
kernel's frames left out):

| Step | Thread | Stack |
|---|---|---|
| 1, attach | debugger's | `ntdll!NtDuplicateObject` ← `mscordbi!LocalEventChannel::Init` ← `CordbProcess::FinishInitializeIPCChannelWorker` ← `CordbProcess::Init` |
| 2, first close | the target runtime's debugger thread | `ntdll!NtClose` ← `coreclr!Debugger::HandleIPCEvent` ← `HandleIPCEventWrapper` ← `DebuggerRCThread::HandleRSEA` ← `DebuggerRCThread::MainLoop` |
| 3, reuse | the target's main thread | `ntdll!NtCreateEvent` ← … ← the target's `Main` (its `new ManualResetEvent(false)`) |
| 4, second close | debugger's | `ntdll!NtDuplicateObject` ← `mscordbi!LocalEventChannel::Delete` ← `CordbProcess::Neuter` ← `CordbProcess::Detach` ← `DebugSession.Detach` (DrHook.Engine) |

- **The object.** Step 1's object is the debugger's process. Step 4's object is the target's new Event: the one the
  target then lost. In the same instant the trace logs a close of that Event in the target's handle table, made by the
  debugger's thread.
- **Timing.** The target reused the value 0.08 to 0.26 ms after its own close. mscordbi's close came 32 to 93 ms after
  the target's, so the target had that long to reuse the value.
- **The 5 that lost nothing.** No step 4 was logged. The value was not reused before mscordbi's close. A
  `DuplicateHandle` of a value not in use fails, and the trace shows no event for it. In two of the five the target
  reused the value about 300 ms later, after mscordbi's close, for a file.

The records are `119-detach-closes-a-handle/omega-windows-trace.json` (the sessions' events, with stacks) and
`omega-windows-trace-probe119.json` (probe 119's own facts from the same run). The trace stays on omega-windows:
`C:\Users\bemafred\diag\p119t-6e4b0c39-out\probe119-handles.etl`, 195,035,136 bytes, sha256
`e5bf1884bf72a3f02c4b7bd7e5acf763dd17d9a60115a885582aa875b6e9cee2`.

## What this establishes

- **The double close.** On Windows, a detach through mscordbi closes a handle value in the target twice. When the target
  has reused the value in between, mscordbi's second close closes one of the target's own handles: in 30 of 40 detaches
  in the first run, and 15 of 20 in the traced one. The trace shows both closes and who makes them: the target's
  runtime first, then mscordbi's `LocalEventChannel::Delete`, from the debugger's thread.
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
- **Whether `main` of dotnet/runtime has it in practice.** Its code is the same, read 2026-11-28; no build of `main` was
  run.
- **Whether an upstream fix is simple.** mscordbi could read the target's field before it closes, since the target's
  runtime sets it to null when it closes. That is not tried.
- **Why in 5 of 20 traced sessions the target's new events did not take the freed value.** How Windows chooses a handle
  value was not read.
- **Whether it has been reported.** A search of dotnet/runtime's issues and pull requests on 2026-11-28 (for
  `m_rightSideProcessHandle`, `CloseInRemoteProcess`, `DUPLICATE_CLOSE_SOURCE`, `LocalEventChannel`, and the symptoms)
  found nothing on it.

## Decided (2026-11-28)

Martin: accepted, with no change to DrHook. It matters only for a detach from a program that must keep running
afterwards, such as a debug build in production, which is already a special debugging run, and the chance in it is
small. It is to be reported to dotnet/runtime, politely, so that its maintainers can judge whether it is worth a fix.
The issue's text is shown to Martin before it is filed. A debugger already has full access to its target, so it is
not a security report. Not pursued, because none of them changes what DrHook does: why 5 of 20 traced sessions lost
nothing, a build of `main`, and trying the fix.

## Next

1. Report it to dotnet/runtime, with probe 119 as the reproduction and the trace's stacks as the evidence.
2. Run probe 119 on both Windows runners, x64 included (`drhook-probe.yml`, 2026-11-28).
3. Take it to ADR-016. "Attaching to a running program: detach and leave it running" (6.1) is not safe on Windows for a
   target that makes handles as the debugger leaves. The platform gate and the MCP tools' descriptions should say so.
