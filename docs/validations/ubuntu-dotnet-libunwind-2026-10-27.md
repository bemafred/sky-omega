# Ubuntu's distro .NET vs Microsoft's build: intermittent CLR fatal error, 2026-10-27

**Sky Omega version:** 1.8.25 (commit 84efa22)
**Machine:** Linux test machine: Ubuntu 26.04.1 LTS, x86_64, 8 logical CPUs, kernel 7.0.0-38-generic
**Outcome:** Ubuntu's `dotnet-runtime-10.0` is at fault. On Linux, use Microsoft's .NET build. The operator steps are in [runbook-linux-dotnet-runtime.md](../operations/runbook-linux-dotnet-runtime.md).

## Summary

Two unrelated processes died on the Linux test machine on the same day with `Fatal error. Internal CLR error. (0x80131506)`. Both crash dumps show the same native path through the exception-throw code. A same-version A/B between Ubuntu's .NET 10.0.13 and Microsoft's .NET 10.0.13 reproduced the crash on Ubuntu's build only:

| Runtime (both 10.0.13) | Stress probe crashes | Exceptions thrown per 30 s (clean runs) | Full build + test crashes |
|---|---|---|---|
| Ubuntu `dotnet-runtime-10.0` 10.0.13-0ubuntu1~26.04.1 | **5 of 20** | 4.41 M | 0 of 20 steps |
| Microsoft (`dotnet-install.sh`, SDK 10.0.113) | 0 of 20 | 6.51 M | 0 of 20 steps |

One-sided Fisher exact test on the stress crashes: p ≈ 0.024.

## The two builds

| | Ubuntu package | Microsoft build |
|---|---|---|
| Install | `apt`, under `/usr/lib/dotnet` | `dotnet-install.sh`, under `~/.dotnet-ms-10.0.13` |
| SDK / runtime | 10.0.113 / 10.0.13 | 10.0.113 / 10.0.13 |
| `ldd libcoreclr.so \| grep unwind` | `libunwind.so.8`, `libunwind-x86_64.so.8` (system `libunwind8` 1.8.3-0ubuntu1) | nothing (bundled unwinder) |
| `libcoreclr.so` build ID | `bcaf5dead37fa204aeeda8a85ec920e0229ff8ae` | — |

## The incidents

1. **18:37, `dotnet pack SkyOmega.sln`** inside `tools/install-tools.sh`. A retry after `dotnet build-server shutdown` succeeded.
2. **19:38, the `DrHook.Viz.Tests` test host** during a full `dotnet test` at 84efa22. That project reported zero tests run, while every other project passed (5052 passed, 8 skipped). The same project then passed 40 of 40 runs on its own.

## Crash analysis

The apport core of incident 1 was symbolized against Ubuntu's debug info. The debug files came from debuginfod and were read with `addr2line`, because gdb crashed on this DWARF. The crashing thread:

```
IL_Throw                                  libcoreclr+0x4c553c
SoftwareExceptionFrame::InitAndLink       libcoreclr+0x392b16
SoftwareExceptionFrame::Init              libcoreclr+0x392a58   (excep.cpp:11439)
EEPolicy::HandleFatalError                libcoreclr+0x49d9ab
```

At the same moment, another thread was JIT-compiling (`GcInfoEncoder::Build`) and a third was in `ExternalMethodFixupWorker`.

A `createdump` core of one stress-probe crash (`DOTNET_DbgEnableMiniDump=1`) has **identical offsets** on the crashing thread, so the stress probe and the real incidents hit the same defect. `SoftwareExceptionFrame::Init` captures the throwing thread's context and unwinds to the caller. That's the step where Ubuntu's build calls into the system `libunwind`.

## A/B 1: full build + test, 10 rounds per runtime

Each round ran a full Release rebuild (`dotnet build SkyOmega.sln -c Release --no-incremental`) and then a full `dotnet test`. A sampler logged every `libcoreclr.so` mapped by a running process, to confirm which runtime each step used. The runtime was selected with `PATH` and `DOTNET_ROOT`.

- 0 CLR crashes on either runtime in 20 steps each. Normal workloads hit the defect rarely, so this A/B was inconclusive.
- The only failures were the timing assertion in `ParallelBZip2ThroughputMeasurement`: 4 of 10 test runs on Ubuntu's build and 1 of 10 on Microsoft's. That's a separate, timing-sensitive test on an 8-thread machine, and not a runtime crash.

## A/B 2: targeted stress, 20 interleaved pairs of 30 s

The probe is [`tools/throwstress.cs`](../../tools/throwstress.cs). It runs one thrower thread per core, each throwing through recursive stacks 1–40 frames deep. Alongside them, `max(2, cores/2)` threads each compile a fresh `DynamicMethod` per iteration (one in three throws from the new code), and one thread allocates and forces gen-0/gen-2 GCs. One build of the probe ran under both runtimes, alternating Ubuntu then Microsoft within each pair, selected with `DOTNET_ROOT`.

- Ubuntu's build crashed in pairs 2, 5, 6, 7 and 16, each time with exit 134 and the CLR error.
- Microsoft's build survived all 20.
- Ubuntu's build was slower at throwing in every clean run: 4.41 M against 6.51 M exceptions per 30 s, about 32% fewer. That's consistent with a costlier unwinder on the throw path.

## What this does and doesn't show

- **Shown:** on this machine, at the same .NET version, Ubuntu's build crashes under exception-heavy parallel load and Microsoft's doesn't. The crash path matches the real incidents.
- **Most likely cause, not isolated:** Ubuntu's build is source-built and differs from Microsoft's in more than the unwinder. Linking the system `libunwind` is the difference the crash path runs through. It also matches [Launchpad bug 2143357](https://bugs.launchpad.net/bugs/2143357) ("dotnet-runtime-10.0 crashes intermittently (0x80131506) due to system libunwind 1.8.1 incompatibility", which expired in May 2026 without a maintainer response).
- **Not tested:** other distributions, other .NET versions and arm64.
- **Not a Sky Omega defect:** no Sky Omega code is on the crashing stack, and the CLI itself crashed.

## Reproducing

```bash
dotnet build tools/throwstress.cs -o /tmp/throwstress
for i in $(seq 1 20); do
  DOTNET_ROOT=/path/to/runtime-under-test /tmp/throwstress/throwstress 30; echo "exit $?"
done
```

The last line of each clean run names the runtime directory that ran. Without `DOTNET_ROOT`, the apphost falls back to `/etc/dotnet/install_location`, which is `/usr/lib/dotnet` on Ubuntu.
