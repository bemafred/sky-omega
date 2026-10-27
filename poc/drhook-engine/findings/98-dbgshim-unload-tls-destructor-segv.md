# Finding 98 — freeing libdbgshim crashed the host when a thread that had used it exited (Linux)

**Date:** 2026-10-26
**Status:** Mechanism **PROVEN** (probe 65, deterministic on Linux arm64 and x64). **Fixed**: libdbgshim stays loaded
for the process's lifetime. A **second, separate** host crash on Linux arm64 (below) became
[finding 99](99-attach-before-debugger-started-segv.md), also fixed.

## Symptom

The DrHook integration test host on omega-ubuntu (Linux arm64, Ubuntu 26.04, distro .NET 10.0.13) died with SIGSEGV
(exit 139) in 1 of 8 runs of the unchanged 1.8.19 suite, about 40 s after its last reported test. Earlier rigs had
run the suite 2–3 times per machine, too few to see it.

## Root cause (proven)

From the core (`DOTNET_DbgEnableMiniDump`, read with gdb):

- The crashing thread was native, exiting: `start_thread` → `__nptl_deallocate_tsd` (pthread TLS destructors).
- glibc's key table held a destructor at an **unmapped** address, at the same low bits (`…7e80`) as six live keys
  whose destructors lie in the loaded `libdbgshim.so`: the same function in an **earlier copy** of libdbgshim.
- `libdbgshim.so` from that earlier load was no longer mapped.

libdbgshim embeds its own PAL. The PAL registers pthread keys with destructors inside the library and gives every
thread that calls into it a value for them. `DbgShim.Dispose` called `NativeLibrary.Free` at the end of every session.
The library was unmapped, but its keys stayed registered. When a thread that had once called dbgshim exited later (a
retiring thread-pool thread, hence the delay), glibc called a destructor in unmapped memory.

**Probe 65** (`65-dbgshim-unload-tls-smoke.cs`) isolates it. A worker thread calls one dbgshim export, the library is
freed (or kept), then the worker exits:

| | freed | kept |
|---|---|---|
| Linux arm64 (omega-ubuntu) | 5/5 SIGSEGV | 0/5 |
| Linux x64 (elite) | 3/3 SIGSEGV | 0/3 |
| macOS (omega) | 0/3 | 0/3 |
| Windows arm64 (omega-windows) | 0/3 | 0/3 |

## Fix

`DbgShim.Load` loads the library once per process and returns sessions that share it; `Dispose` frees nothing. A
debugger host keeps its debugging shim loaded for its lifetime. Freeing it served only to avoid a double `dlclose`
(finding 54, T7), which cannot happen now.

`DbgShimLifetimeTests` checks the library is still mapped after `Dispose` and that `Load` reuses it. On omega-ubuntu
it passes with the fix and **fails** with `Dispose` freeing the library again.

## Validation

- The new cores show no unmapped TLS destructor, so this mechanism is gone.
- DrHook.Engine.Tests 184 (1 skipped) on omega-ubuntu and omega.

## A second crash in an mscordbi thread (now finding 99)

With the fix, omega-ubuntu still crashed in **2 of 12** integration runs (and one run timed out
`AttachAndOwn_VstestTestHost_PauseDuringStoppingFlood_PauseStopSurfaces` at 120 s). Both cores have the **same
signature**, different from the one above: a thread started by `libmscordbi.so` faults (signal frame directly above
frames at `libmscordbi.so+0x24a18`, `+0x24a30`, `+0x2ce98`, `+0x24898`, `+0x5338`, `+0x123dc`, with
`libmscordaccore.so` frames below). The last tests reported before each crash were `SpanLengthCondition_…` and
`AttachAndOwn_MtpTarget_DisposeDuringFlood_…`. (Those offsets were measured from the start of mscordbi's code
mapping, not its load address; with Ubuntu's debug symbols the frames are `SendAttachProcessWorkItem::Do` →
`CordbRCEventThread::SendIPCEvent`.) [Finding 99](99-attach-before-debugger-started-segv.md) has the mechanism: an
attach whose transport session opens before the target's runtime has marked its debugger initialized.
