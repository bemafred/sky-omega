# Finding 109 — The diagnostics-port pause holds and spreads on macOS and Windows as on Linux; how a held target ends, and the startup handshake, differ

**Date:** 2026-11-10
**Status:** **Observed** with probe 109 rev 2 on three platforms, all exit 0:
- omega: macOS 27.0.1 arm64, runtime 10.0.9;
- omega-windows: Windows 11 26200 arm64, runtime 10.0.14;
- omega-ubuntu: Ubuntu 26.04.1 arm64, distro runtime 10.0.14.

Every run used dbgshim 9.0.661903, and every prediction held except one: the Windows re-dial timing. This answers
[ADR-017](../../../docs/adrs/drhook/ADR-017-debugging-a-process-tree.md)'s Q1 (Step 3). Nothing in the engine changed.

## The question

ADR-017's lead candidate for holding a supervisor's children is the runtime's own pause,
`DOTNET_DiagnosticPorts=<address>,connect,suspend`. [Finding 107](107-diagnostics-port-hold-is-inherited.md) measured
it on Linux x64 only, and [finding 108](108-rider-holds-children-with-the-diagnostics-port.md) saw Rider rely on it on
macOS. Q1 asks whether it holds and spreads the same way on macOS and Windows, measured by DrHook.

## What the source says

Read in runtime release/10.0 and dbgshim v9.0.661903; the probe's header has the details.

- **Every platform:** the runtime pauses for the diagnostics monitor before it starts its debugger. The protocol's wire
  layouts are the same everywhere.
- **The port on Windows** is a named pipe, `\\.\pipe\<address>`. Each dial is one `CreateFileA`, with no
  `WaitNamedPipe`.
- **The startup handshake** is how a registered debugger and the runtime meet at startup:
  - **Linux:** the semaphores `/dev/shm/sem.clrst…` and `sem.clrco…` (finding 107).
  - **macOS:** two FIFOs first, `clr-debug-pipe-<pid>-<start>-st` and `-co` in the temp folder, with the semaphores as
    the fallback. The source gives the reason: "On macOS 26, sem_open fails if debugger and debugee are signed with
    different team ids". dbgshim 9.0.661903 creates both FIFOs and both semaphores.
  - **Windows:** a named event, `TelestoStartupEvent_<pid as %08x>`, and a continue event the runtime creates once it
    is past its pause. dbgshim retries while that continue event does not exist yet: 25 times, 100 ms apart. It then
    waits on the startup event.
- **After an S_OK startup callback:**
  - **Linux and macOS:** the runtime marks a debugger attached (`MarkDebuggerAttachedInternal`). With no debugger
    attached, it stops at its first debug event, before main.
  - **Windows:** nothing marks a debugger attached, so the runtime runs on to main.

## What was run

`109-diagport-hold-smoke.cs`, with `109-diagport-hold-target.cs` and `109-diagport-hold-hook.cs`. It runs probe 107
rev 5's five modes unchanged, through an observation layer per platform:
- **Linux:** `/proc`.
- **macOS:** libproc and sysctl, using DrHook.Engine's verified offsets.
- **Windows:** `System.Diagnostics.Process` and `GetThreadDescription`.

The layer is checked against the probe's own process before any mode runs. Two more changes from rev 5:
- **A named-pipe server on Windows**, in place of the Unix socket.
- **The startup-hook arm is live.** The target builds with `PublishAot=false`; rev 5's file-based target had startup
  hooks switched off (finding 107).

On Linux, probe 109 is the control for the port itself.

| Mode | Runs | What it does |
|------|------|--------------|
| hold | 10 | spawn paused; wait 2 s; `RegisterForRuntimeStartup`; hold 2 s; Q6 in runs 1–5; `ResumeRuntime` |
| hold-control | 10 | no pause; register 2 s after the spawn |
| child-listener | 3 | the target starts a child; the child's dial is held 5 s, then resumed |
| child-no-listener | 3 | the listener is closed before the child starts; the child is watched 30 s, then the address is bound again |
| runtimeconfig-property | 1 | `DOTNET_DiagnosticPorts` as a runtimeconfig property, not a variable |

Evidence, in [`109-diagnostics-port-pause-on-every-platform/`](109-diagnostics-port-pause-on-every-platform/).

| Log | Platform | Run (UTC, from the log) | Lines | sha256 (first 16) |
|-----|----------|-------------------------|------:|-------------------|
| `omega-rev1.txt` | macOS | 18:51:53 – 19:46:57, ended by SIGTERM (see Rev 1) | 41 | `091da9838531fe21` |
| `omega-rev2.txt` | macOS | 19:47:17 – 19:53:29 | 382 | `5574d00cf68da5aa` |
| `windows-rev2.txt` | Windows | 20:07:51 – 20:12:27 | 248 | `b5bc5c168f466cb0` |
| `ubuntu-rev2.txt` | Linux | 20:17:17 – 20:23:25 | 391 | `50ac4e5c4eb7f1bd` |

The Windows log is the VM's file with carriage returns and the byte-order mark removed. Probe files (rev 2):
- `109-diagport-hold-smoke.cs` `14362a25667fd5d5`
- `-target.cs` `43a60fed384462fb`
- `-hook.cs` `9005c5b430a04f33`

On omega-windows and omega-ubuntu, the probe files and the repo's `global.json` (the SDK 10 pin) were copied to a
scratch folder, and the checkouts were left as they were.

## What was observed

| | Linux (omega-ubuntu) | macOS (omega) | Windows (omega-windows) |
|---|---|---|---|
| **Q1** hold: a registration 2 s into the pause catches the startup | 10/10 | 10/10 | 10/10 |
| How a held run ended | stopped before main, killed at the 15 s bound: 10/10 | the same: 10/10 | **reached main, exit 0**: 10/10 |
| **Q5** a callback during the pause | none: 10/10 | none: 10/10 | none: 10/10 |
| **Q6** the default port's `ResumeRuntime` releases the pause | no: 5/5 | no: 5/5 | no: 5/5 |
| **Q2** managed code before the resume | none | none | none |
| Marks after the resume | none (stopped first) | none (stopped first) | hook, modinit, main, exit |
| The handshake after Register | `sem.clrst`, `sem.clrco`: 10/10 | both FIFOs and both semaphores: 10/10 | `TelestoStartupEvent_<pid>`: 10/10 |
| The handshake after Unregister | gone: 20/20 | gone: 20/20 | gone: 20/20 |
| hold-control: the registration misses the startup | 10/10 | 10/10 | 10/10 |
| Spawn to main, hold-control | 24–31 ms | 31–38 ms | 40–53 ms |
| **Q3** a child inherits the pause | 3/3 | 3/3 | 3/3 |
| Its variable during the pause | the same value (`/proc`) | the same value (`KERN_PROCARGS2`) | not read; its own view after the resume shows the same value |
| A child with nobody listening | waits 30 s, no mark: 3/3 | the same: 3/3 | the same: 3/3 |
| Its re-dial after the address returns | 195, 145, 105 ms | 428, 415, 449 ms | **865, 729, 159 ms** |
| **Q3b** a runtimeconfig property | no dial | no dial | no dial |
| Containment | 33 tracked, 0 alive, 30 files removed, all from the 10 killed targets | the same | 33 tracked, 0 alive, nothing to remove |

- **Threads at the bound** of a held target that is not attached:
  - **Linux:** `.NET DebugPipe` in `wait_for_partner` and `.NET Debugger` in `futex_do_wait`, as on zbook
    (finding 107).
  - **macOS:** every thread waiting, `.NET DebugPipe` and `.NET Debugger` among them.
- **The startup hook ran first** wherever managed code ran: hook, module initializer, main.
- **The Linux control reproduces finding 107** in every outcome, in the threads at the bound and in containment. The
  port changed nothing.

## Reading

**Q1 is answered.** On Linux, macOS and Windows the pause holds a target before its first managed code. A
registration made during it catches the startup. Every .NET child inherits it unchanged, and a child with nobody
listening waits until a listener returns. Only the suspending port's resume releases it. The lead candidate is one
mechanism on all three platforms. Two things differ, and DrHook must handle both:

- **How a target ends after an S_OK startup callback without an attach.**
  - **Linux and macOS:** the target stops before main and waits for a debugger forever.
  - **Windows:** it runs on.
  - This bears on ADR-017's Q3. A child that DrHook does not want must be released by `ResumeRuntime` alone, never by
    registering and then not attaching. On Linux and macOS that would leave the child stopped.
- **The startup handshake:** semaphores on Linux, FIFOs on macOS, a named event on Windows. The handshake is dbgshim's
  business. What DrHook can observe and clean up after a crash differs per platform:
  - a killed runtime on Linux or macOS leaves files named by its pid;
  - on Windows nothing is left.

**For Q4 (a re-bound listener releases held children):** a child re-dialled within 0.45 s on Linux and macOS, and
within 0.9 s on Windows.

## Rev 1

Rev 1 hung on omega at the end of hold run 1, after that run's target was killed. The run itself had matched every
macOS prediction.
- **Where it hung:** `drhook_snapshot` of the probe showed the main thread spinning in `Socket.Dispose()`
  (`SafeSocketHandle.CloseAsIs`), inside the reverse server's disposal. The accept thread was still in a synchronous
  `Socket.Accept()`. On macOS, closing a listening socket does not wake a thread blocked in `accept()`.
- **A known fact, missed:** DrHook.Engine's `DebugStateServer` already accepts with `AcceptAsync` and a cancelled token
  for exactly this hang ("the macOS Socket.Close-while-Accept-in-flight hang"). Rev 1 kept rev 5's synchronous accept,
  which is fine on Linux, without that being recalled.
- **The stop:** after 55 minutes the probe was ended with SIGTERM (exit 143). The seven files it would have removed
  were removed by exact name:
  - the killed target's and the probe's debug pipes and diagnostic sockets;
  - the probe's own socket.
- **The fix:** rev 2 accepts with `AcceptAsync` and cancels before the close.

## Side findings

- **Finding 108's unexplained descriptor is the macOS handshake.** fd 16, open for reading on
  `clr-debug-pipe-71044-1794334391-co` in worker 2, is the runtime's end of the continue FIFO.
  `PAL_NotifyRuntimeStarted` opens it for reading and blocks on it until the debugger's dbgshim writes Continue. Why
  the file was missing from the temp-folder listing at that moment is still not established.
- **The re-dial delay is the phase within the runtime's 500 ms cycle, and is steady per machine:**

  | Machine | Re-dials |
  |---------|----------|
  | zbook (finding 107) | 380–387 ms |
  | omega-ubuntu | 105–195 ms |
  | omega | 415–449 ms |
  | omega-windows | 159–865 ms |

  Only Windows goes beyond the cycle. The retry loop has no Windows branch (`ds-ipc.c`); the wait is spent in
  `ds_ipc_poll` on the child's default listen port, which is per platform.
- **A .NET 10 file-based app gets its startup hooks back with `#:property PublishAot=false`.** With the property, the
  generated runtimeconfig has no feature switches (checked on omega).
- **The probe exercised neither fallback:** the semaphore fallback on macOS, and dbgshim's retry-then-wait path on
  Windows. On macOS the FIFOs always worked. On Windows the resume came within dbgshim's 2.5 s of retries, or after
  them; the log does not say which.

## Not established

- **Why Windows re-dials take up to 0.9 s.**
- **A full attach after the release** (ADR-017 Q2, Step 4). The probe stops at the startup callback.
- **x64 on macOS and Windows.** Only arm64 was run there; Linux x64 is finding 107.
- **Probe 109 on zbook.** That would be the exact control for finding 107. omega-ubuntu stands in: arm64, the distro
  runtime, the same version.
