# Finding 101 — platform facts (ADR-016 Step 1): on Linux a debugger reads its descendants directly and other processes over the transport; on macOS it reads over the transport always

**Date:** 2026-10-31
**Status:** **RECORDED** on our four machines (five runs, probe 101 at `ca4f931`, every run RESULT(0)). The six GitHub
runners are not yet recorded: their workflow (`.github/workflows/drhook-probe-101.yml`) awaits review. elite is
excluded (ADR-016 Step 0).

## What was run

[`101-platform-facts-smoke.cs`](../101-platform-facts-smoke.cs) attaches to an idle target that ends by itself,
records, detaches cleanly (a borrowed session's `Dispose`: synchronize, then detach) and waits for the target's own
end. On macOS and Linux it does this twice, with a target that descends from the debugger (started under bash, which
waits for it) and with one reparented away (bash exits). Nothing is stopped, evaluated, signalled or killed. The
facts of each run are in [`101-platform-facts/`](101-platform-facts/).

| Machine | OS, architecture | .NET (probe and target) |
|---------|------------------|-------------------------|
| omega | macOS 27.0.1, arm64, metal | 10.0.9, Microsoft |
| omega-ubuntu | Ubuntu 26.04.1, arm64, Parallels VM | 10.0.14, Ubuntu's (`/usr/lib/dotnet`) |
| omega-ubuntu | same | 10.0.13, Microsoft's (`~/.dotnet`) |
| zbook | Ubuntu 26.04.1, x64, metal | 10.0.14, Microsoft's (`~/.dotnet`) |
| omega-windows | Windows 11 (26100), arm64, Parallels VM | 10.0.14, Microsoft |

Every run: the attach raised the same 21 events (CreateProcess 1, CreateAppDomain 1, CreateThread 3, LoadAssembly 8,
LoadModule 8), no anomaly, the detach took about 0.6 s, and the target ended by itself and wrote its marker.

## How mscordbi reads the target's memory

mscordbi decides once per attach, in the debugger process (`release/10.0` 425ba4b, `pal/src/debug/debug.cpp`,
`PAL_OpenProcessMemory`): directly when `task_for_pid` (macOS) or `open("/proc/<pid>/mem", O_RDONLY)` (Linux)
succeeds, otherwise over the debugger transport. The probe made the same call from the same process.

| Machine | Descendant target | Reparented target |
|---------|-------------------|-------------------|
| omega | transport (`task_for_pid` → 5, KERN_FAILURE) | transport (5) |
| omega-ubuntu, Ubuntu's .NET | **direct** | transport (`EACCES`) |
| omega-ubuntu, Microsoft's .NET | **direct** | transport (`EACCES`) |
| zbook | **direct** | transport (`EACCES`) |
| omega-windows | direct (Windows has no transport) | — |

- **Linux** (`ptrace_scope=1` on both machines): Yama lets a process open the memory of its descendants only. The
  outcome follows the target's ancestry, not the .NET build.
- **macOS**: the target's host (`/usr/local/share/dotnet/dotnet`) carries `get-task-allow`, Developer Mode is on and
  SIP is enabled, but the debugger process (the probe's apphost) has no entitlements and is not root, so
  `task_for_pid` fails for every target. That matches probe 100, where a read waited for a frozen target on omega.

**DrHook's integration tests attach to descendants only.** `TargetSpawn.cs` starts MTP targets under bash and VSTest
targets under `dotnet test` under a shell, and `Launch` starts a direct child. So on Linux the tests' reads are
direct, and finding 100's hang, which is a read over the transport, cannot happen there; on macOS every read goes
over the transport. This fits the cascade having appeared on macOS only (6 of 19 known macOS jobs since `8ed9701`,
none on Linux). It is consistent, not yet shown: the Linux runners' `ptrace_scope` and outcomes come with the
runner run. It also means the Linux tests do not exercise the transport-read path that a production attach to an
unrelated program takes.

## What a debugger process can read of its own threads on Linux

Without ptrace rights beyond its own, from `/proc/self/task/<tid>/`: `comm`, the state in `stat`, `wchan` and the
syscall number in `syscall` are readable for every thread; `stack` is not (`EACCES`; it needs `CAP_SYS_ADMIN`). The
same on both Linux machines and both .NET builds.

Threads that appeared with the attach, and what became of them 500 ms after the detach (target still running):

| Thread (`comm`) | While attached | After the detach | Likely |
|-----------------|----------------|------------------|--------|
| `.NET DebugPipe` (a second one) | `anon_pipe_read`, syscall `read` | **still there**, same | mscordbi's transport thread, reading the target's debug pipe (its debuggee-side counterpart in the probe's own runtime has the same name and existed before) |
| two threads named after the process | `futex_do_wait` | gone | mscordbi's event threads (unnamed native threads take the process's name) |
| `DrHook.Callback` | `futex_do_wait` | gone | DrHook's callback pump |
| `.NET SigHandler`, `.NET Sockets`, `.NET BGC` | — | stay | the probe's own runtime starting threads lazily, not the debugger's |

For the no-capture constructions of Steps 2 to 4 this means a thread reading over the transport can be seen without
a stack: `wchan` `anon_pipe_read` with syscall `read` (0 on x64, 63 on arm64). Probe 101 kept only the syscall
number; the full `syscall` line also carries the arguments, so its file descriptor, resolved through
`/proc/self/fd`, names the pipe and so the target. The event threads cannot be told apart by name.

**Open:** mscordbi's transport thread outlives the detach. Whether it ends when the target exits, and whether such
threads accumulate across sessions in one process, was not recorded (the snapshot was taken while the target ran).
That belongs to Step 2 and to ADR-016's resource budgets.

## The debugger code loaded

dbgshim comes from DrHook's NuGet copy in the build output; mscordbi and the DAC come from the target's runtime
directory. SHA-256, first 16 hex digits:

| Machine, .NET | dbgshim | mscordbi | DAC (mscordaccore) | coreclr |
|---------------|---------|----------|--------------------|---------|
| omega, 10.0.9 | `452a9778afe10f54` | `1aa6a035180b1d9a` | `01227b106cd13885` | `1c5644aec52ccaaf` |
| omega-ubuntu, Ubuntu's 10.0.14 | `d5a795efac5db08c` | `6c3938a983a9753a` | `a333e34abc5e985b` | `2c9c73ebf6ca1118` |
| omega-ubuntu, Microsoft's 10.0.13 | `d5a795efac5db08c` | `2107c18fb7c84027` | `07db8ff1fbd881fe` | `adb1b0f846b793df` |
| zbook, 10.0.14 | `4f45a9caad30619b` | `8cd42da437d93cda` | `3bd386a66f65c077` | `843b66a2b33a5705` |
| omega-windows, 10.0.14 | `fef88cc588634829` | `95e4e044a582f143` | `76c7dff41f6e5d0b` | `c8d6680d8580e60f` |

Microsoft's 10.0.13 on omega-ubuntu matches Step 0's record. Ubuntu's build also loads
`libcoreclrtraceptprovider.so` (LTTng UST) into the debugger process, with a `-ust` thread; Microsoft's does not.

## Ubuntu upgraded its .NET under Step 0

Step 0 recorded both builds on omega-ubuntu at 10.0.13, from one source commit, so their debugger code could be
compared. Seven minutes later, at 13:28:51 UTC, unattended-upgrades raised Ubuntu's packages to 10.0.14
(`/var/log/dpkg.log`: `upgrade dotnet-runtime-10.0:arm64 10.0.13-0ubuntu1~26.04.1 10.0.14-0ubuntu1~26.04.1`; SDK
10.0.114). The two builds now differ in version as well as in build, and a runtime can change under a running series
without notice. Step 0 needs a decision: hold Ubuntu's .NET packages, add Microsoft's 10.0.14 beside them, or both.

## Environment, briefly

`ptrace_scope` 1 and `/bin/sh` dash on both Linux machines; neither Linux runtime links the system libunwind; omega
is metal (`kern.hv_vmm_present` 0), omega-ubuntu Parallels, zbook metal. The debug pipes stay after the detach while
the target runs: they are the target's.
