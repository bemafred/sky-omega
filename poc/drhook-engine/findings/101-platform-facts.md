# Finding 101 — platform facts (ADR-016 Step 1): how mscordbi reads a target's memory depends on the target's ancestry on Linux, and on the machine and the target's executable on macOS

**Date:** 2026-10-31; apphost runs 2026-11-01
**Status:** **RECORDED** on our four machines (six runs, probe 101 at `ca4f931` and `73616fe`, the same probe) and on
the six GitHub runners (workflow run 41235525121, `73616fe`); then on omega and the six runners with the target also
started through its own apphost (probe at `9e29407`; workflow run 41284406137 at `f4250a1`). Every run RESULT(0).
elite is excluded (ADR-016 Step 0). The question the first runs left open is answered: on the macOS runners a target
started through its apphost is read over the transport, a `dotnet`-hosted one directly (below).

## What was run

[`101-platform-facts-smoke.cs`](../101-platform-facts-smoke.cs) attaches to an idle target that ends by itself,
records, detaches cleanly (a borrowed session's `Dispose`: synchronize, then detach) and waits for the target's own
end. On macOS and Linux it does this twice, with a target that descends from the debugger (started under bash, which
waits for it) and with one reparented away (bash exits). Nothing is stopped, evaluated, signalled or killed. The
facts of each run are in [`101-platform-facts/`](101-platform-facts/).

The second version (`9e29407`) starts the same target in two ways, through the `dotnet` host (`dotnet <dll>`) and
through its own apphost, each as a descendant and reparented (on Windows, as a direct child), and records how the
host, the apphost and the debugger are signed. The apphost gets `DOTNET_ROOT`, the probe's dotnet root, so every
target runs on the probe's runtime (every run loaded the probe's `mscordbi`). It ran on omega (`omega-apphost.json`)
and on the six runners (`runner-*-apphost.json`). Our Linux and Windows machines were not run again: on the runners
the second version agrees with the first there, and the launcher made no difference.

| Machine | OS, architecture | .NET (probe and target) |
|---------|------------------|-------------------------|
| omega | macOS 27.0.1, arm64, metal | 10.0.9, Microsoft |
| omega-ubuntu | Ubuntu 26.04.1, arm64, Parallels VM | 10.0.14, Ubuntu's (`/usr/lib/dotnet`) |
| omega-ubuntu | same | 10.0.13, Microsoft's (`~/.dotnet`) |
| omega-ubuntu | same | 10.0.14, Microsoft's (`~/.dotnet`, added after Step 0's upgrade, below) |
| zbook | Ubuntu 26.04.1, x64, metal | 10.0.14, Microsoft's (`~/.dotnet`) |
| omega-windows | Windows 11 (26100), arm64, Parallels VM | 10.0.14, Microsoft |
| runner `macos-latest` | macOS 26.6.2, arm64, VM, 5 cores | 10.0.14, Microsoft |
| runner `macos-26-intel` | macOS 26.6.1, x64, VM, 4 cores | 10.0.14, Microsoft |
| runner `ubuntu-24.04` | Ubuntu 24.04.5, x64, VM, 4 cores | 10.0.14, Microsoft |
| runner `ubuntu-24.04-arm` | Ubuntu 24.04.5, arm64, VM, 4 cores | 10.0.14, Microsoft |
| runner `windows-latest` | Windows (26100), x64, 4 cores | 10.0.14, Microsoft |
| runner `windows-11-arm` | Windows 11 (26200), arm64, 4 cores | 10.0.14, Microsoft |

Every run: the attach raised the same 21 events (CreateProcess 1, CreateAppDomain 1, CreateThread 3, LoadAssembly 8,
LoadModule 8), no anomaly, and the target ended by itself and wrote its marker. From the attached snapshot to the
after-detach snapshot took 0.58 to 0.95 s, which includes the probe's 500 ms wait after `Dispose`; so `Dispose`
itself took at most about 0.45 s, and the probe does not time it alone. (An earlier version of this finding called
the whole interval the detach.) One exception in the first runs shows a flaw in the probe: on the macOS x64 runner the
descendant's attach took 4.7 s, and the probe's 500 ms quiet window counted from before the attach, so its "attached"
snapshot was taken before any event had arrived (0 events). The memory-open result does not depend on it. The second
version waits for the first event after the attach returns: each of its runs took the snapshot after all 21 events,
and that runner's slowest attach took 1.4 s.

## How mscordbi reads the target's memory

mscordbi decides once per attach, in the debugger process (`release/10.0` 425ba4b, `pal/src/debug/debug.cpp`,
`PAL_OpenProcessMemory`): directly when `task_for_pid` (macOS) or `open("/proc/<pid>/mem", O_RDONLY)` (Linux)
succeeds, otherwise over the debugger transport. The probe made the same call from the same process.

| Machine | Target started through | Descendant target | Reparented target |
|---------|------------------------|-------------------|-------------------|
| omega | `dotnet` host, apphost | transport (`task_for_pid` → 5, KERN_FAILURE) | transport (5) |
| omega-ubuntu, Ubuntu's .NET | `dotnet` host | **direct** | transport (`EACCES`) |
| omega-ubuntu, Microsoft's .NET (10.0.13 and 10.0.14) | `dotnet` host | **direct** | transport (`EACCES`) |
| zbook | `dotnet` host | **direct** | transport (`EACCES`) |
| runners `ubuntu-24.04`, `ubuntu-24.04-arm` | `dotnet` host, apphost | **direct** | transport (`EACCES`) |
| omega-windows | `dotnet` host | direct (Windows has no transport) | — |
| runners `windows-latest`, `windows-11-arm` | `dotnet` host, apphost | direct | — |
| runners `macos-latest`, `macos-26-intel` | `dotnet` host | **direct** (`task_for_pid` → 0) | **direct** (0) |
| runners `macos-latest`, `macos-26-intel` | apphost | **transport** (5) | **transport** (5) |

- **Linux** (`ptrace_scope=1` on every Linux machine and runner): Yama lets a process open the memory of its
  descendants only. The outcome follows the target's ancestry, not the .NET build or the machine.
- **macOS**: Developer Mode is on everywhere. The debugger process (the probe's apphost) has no entitlements and is
  not root, on omega and on the runners alike. On omega, with System Integrity Protection enabled, `task_for_pid`
  fails for every target (as in probe 100, where a read waited for a frozen target). On the runners, with it
  disabled, the target's executable decides: it succeeds for the `dotnet` host and fails for the apphost, whatever the
  ancestry. The two are signed differently (`codesign -dv` and `codesign -d --entitlements -`):

  | Executable | Signed by | Hardened runtime | Entitlements |
  |------------|-----------|------------------|--------------|
  | `dotnet` host on the runners (10.0.14) | Microsoft (team `UBF8T346G9`) | yes (`flags=0x10000(runtime)`) | `get-task-allow`, `cs.debugger`, `cs.allow-jit`, `cs.disable-library-validation`, `cs.allow-dyld-environment-variables` |
  | `dotnet` host on omega (the newest installed, .NET 11 preview 5) | Microsoft (team `UBF8T346G9`) | yes | the same without `cs.debugger` |
  | the target's apphost, the probe (the debugger), on omega and both runners; `DrHook.Engine.IntegrationTargets.Mtp` (checked on omega) | ad hoc (`Identifier=apphost`, `flags=0x2(adhoc)`, no team) | no | none |

  The probe changes signer, hardened runtime and entitlements at once, so it does not show which of them lets
  `task_for_pid` succeed.

**DrHook's integration tests attach to descendants only.** `TargetSpawn.cs` starts MTP targets under bash and VSTest
targets under `dotnet test` under a shell, and `Launch` starts a direct child. So on Linux the tests' reads are
direct, and finding 100's hang, which is a read over the transport, cannot happen there; the Linux runners confirm
`ptrace_scope=1` and direct reads of a descendant. This fits the cascade having appeared on macOS only (7 of 23
known macOS jobs since `8ed9701` by 2026-11-01, none on Linux). It also means the Linux tests never exercise the transport-read path
that a production attach to an unrelated program takes.

**The macOS runners: only the MTP sessions read over the transport.** The first runs read the probe's
`dotnet`-hosted targets directly, yet the hang watcher's sample of the test host in run 41230567697 (macOS arm64)
shows the stuck read going over the transport (`ShimRemoteDataTarget::ReadVirtual` → `DbgTransportSession::ReadMemory`).
Finding 100 places that read in the previous session, the MTP test's, whose target is the apphost
`DrHook.Engine.IntegrationTargets.Mtp`; the VSTest targets are `testhost` under `dotnet` (the sampled target, pid
3055, is `dotnet`). The second version tested the hypothesis this raised: with SIP disabled, `task_for_pid` fails for
an apphost the SDK signs ad hoc (the MTP target is signed the same way) and succeeds for the `dotnet` host. Both
runners confirm it, for descendants and reparented targets alike. So in CI the VSTest sessions on the macOS runners
read directly and the MTP sessions over the transport, the path finding 100 hangs in; on omega every session reads
over the transport.

**Not established:** which difference in the signatures decides (above). Why omega fails for every target: omega
and the runners differ in System Integrity Protection, and also in macOS version (27.0.1 against 26.6), metal against
VM, and the `dotnet` host, so SIP is the likely difference but not an isolated one. Whether a debugger signed with
`com.apple.security.cs.debugger` would get the task port on omega, where DrHook, an unentitled apphost, reads every
target over the transport.

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
| omega-ubuntu, Microsoft's 10.0.14 | `d5a795efac5db08c` | `475bbda9e39495ec` | `170c3a19bd2b5c39` | `5eaed8176e960fa1` |
| zbook, 10.0.14 | `4f45a9caad30619b` | `8cd42da437d93cda` | `3bd386a66f65c077` | `843b66a2b33a5705` |
| omega-windows, 10.0.14 | `fef88cc588634829` | `95e4e044a582f143` | `76c7dff41f6e5d0b` | `c8d6680d8580e60f` |

Microsoft's 10.0.13 on omega-ubuntu matches Step 0's record. Ubuntu's build also loads
`libcoreclrtraceptprovider.so` (LTTng UST) into the debugger process, with a `-ust` thread; Microsoft's does not.

## Ubuntu upgraded its .NET under Step 0

Step 0 recorded both builds on omega-ubuntu at 10.0.13, from one source commit, so their debugger code could be
compared. Seven minutes later, at 13:28:51 UTC, unattended-upgrades raised Ubuntu's packages to 10.0.14
(`/var/log/dpkg.log`: `upgrade dotnet-runtime-10.0:arm64 10.0.13-0ubuntu1~26.04.1 10.0.14-0ubuntu1~26.04.1`; SDK
10.0.114). The two builds now differ in version as well as in build, and a runtime can change under a running series
without notice. Martin's decision: both. Microsoft's SDK 10.0.114 (runtime 10.0.14) is now in `~/.dotnet` beside
10.0.113; both 10.0.14 builds name source commit `0427b9a`, neither links the system libunwind, and Ubuntu's stays the
default. Their debugger code differs by builder from one source (the table above; Ubuntu's binaries are larger), and
both read memory the same way. Martin then held Ubuntu's ten .NET packages at 10.0.14 (`apt-mark hold`; checked
2026-11-01), so they no longer change under a series.

## Environment, briefly

`ptrace_scope` 1 and `/bin/sh` dash on both Linux machines; neither Linux runtime links the system libunwind; omega
is metal (`kern.hv_vmm_present` 0), omega-ubuntu Parallels, zbook metal. The debug pipes stay after the detach while
the target runs: they are the target's.
