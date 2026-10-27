# Operational runbook: run Sky Omega on Microsoft's .NET build on Linux

**Status:** In force since 2026-10-27 ([validation record](../validations/ubuntu-dotnet-libunwind-2026-10-27.md))
**Audience:** Anyone preparing a Linux machine, container or server for Sky Omega: developer machines, test machines, and production deployments (VGR included)

## TL;DR

On Linux, install .NET from Microsoft, not from the distribution's package manager. Ubuntu's own .NET 10 build (`dotnet-runtime-10.0` 10.0.13 on Ubuntu 26.04) links the system `libunwind`. Under parallel load it intermittently kills the process with:

```
Fatal error.
Internal CLR error. (0x80131506)
```

Microsoft's build of the same version (10.0.13) bundles its own unwinder. In a 20 + 20 run A/B on the same machine it never crashed, while Ubuntu's build crashed 5 times. Ubuntu's build also threw exceptions about 32% slower.

Check every machine and image before you trust it:

```bash
for d in $(dotnet --list-runtimes | awk '/NETCore.App/{gsub(/[][]/,"",$3); print $3"/"$2}'); do
  echo "$d"; ldd "$d/libcoreclr.so" | grep -i unwind || echo "  ok: no system libunwind"
done
```

Any line naming `libunwind.so.8` (or `libunwind-x86_64.so.8`, `libunwind-aarch64.so.8`) means that runtime is the affected kind. Don't run Sky Omega on it.

## What it looks like

- The process prints the two lines above and dies with exit code 134 (SIGABRT). There is no managed exception or stack trace.
- It's intermittent. The same command passes when re-run. It shows up most under parallel work that throws exceptions: `dotnet build` / `dotnet pack` with parallel MSBuild nodes, the `dotnet test` run across the solution, and multi-threaded services.
- Seen on the Linux test machine on 2026-10-27: `dotnet pack SkyOmega.sln` inside `tools/install-tools.sh`, and the `DrHook.Viz.Tests` test host during a full `dotnet test`. That project then reported zero tests run, while every other project passed.
- It isn't a Sky Omega defect. Any .NET process on that runtime can hit it, including the `dotnet` CLI itself.

The native stack is always the same: `IL_Throw` → `SoftwareExceptionFrame::InitAndLink` → `SoftwareExceptionFrame::Init` → `EEPolicy::HandleFatalError`. If a core dump shows that path, this runbook applies.

## Preparing a developer or test machine

### 1. Install Microsoft's SDK

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
bash dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"
```

The repo's `global.json` pins SDK 10.0.100 with `rollForward: latestMinor`, so any 10.0.x SDK works.

### 2. Point both `PATH` and `DOTNET_ROOT` at it

Put this in `~/.profile`, not only `~/.bashrc`. Desktop sessions and anything started from a launcher, Claude Code included, read the login environment:

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
```

Both are needed, because two different things pick the runtime:

| What runs | Picks its runtime from |
|---|---|
| The `dotnet` command (build, test, pack, `dotnet run`) | Whichever `dotnet` comes first on `PATH` |
| Apphost executables: the global tools (`mercury`, `mercury-mcp`, `drhook-mcp`, …) and the Microsoft.Testing.Platform test executables | `DOTNET_ROOT`, then `/etc/dotnet/install_location` |

Ubuntu's packages write `/etc/dotnet/install_location` → `/usr/lib/dotnet`. If you set only `PATH`, `dotnet --version` reports Microsoft's SDK while the MCP servers and test hosts still run on Ubuntu's runtime. Verified with `COREHOST_TRACE=1` on 2026-10-27.

Log out and back in (or re-source `~/.profile` and restart the terminal) so the new environment reaches everything.

### 3. Reinstall the tools and restart the MCP servers

```bash
dotnet build-server shutdown     # old MSBuild/Roslyn servers still run on the old runtime
./tools/install-tools.sh
```

Then restart Claude Code, so `mercury-mcp` and `drhook-mcp` start again under the new runtime.

### 4. Verify what is actually running

```bash
command -v dotnet                      # the CLI
dotnet --list-runtimes | grep NETCore  # the runtimes that CLI sees
for p in $(pgrep -f 'mercury-mcp|DrHook.Mcp'); do   # the running MCP servers
  m=$(grep -m1 -o '/[^ ]*libcoreclr\.so' /proc/$p/maps 2>/dev/null) &&
    echo "$p $(tr '\0' ' ' </proc/$p/cmdline | cut -c1-60) -> $m"
done
```

Every path must point into `~/.dotnet`, never `/usr/lib/dotnet`.

### Optional: remove Ubuntu's packages

With `PATH` and `DOTNET_ROOT` set, the two installs coexist safely, and this machine kept Ubuntu's packages for the A/B. Removing them takes away the fallback path entirely:

```bash
sudo apt remove 'dotnet*' 'aspnet*' 'netstandard*'
```

If something still needs a system-wide default after that, write the Microsoft install's path to `/etc/dotnet/install_location`.

## Preparing production (servers, containers, VGR)

- **Don't install the distro package** (`apt install dotnet-sdk-10.0` / `dotnet-runtime-10.0`) on a server that will run Sky Omega.
- **Bare-metal or VM:** install Microsoft's runtime with `dotnet-install.sh --runtime dotnet --channel 10.0 --install-dir /opt/dotnet`, or unpack Microsoft's tarball there. Set `DOTNET_ROOT=/opt/dotnet` for the service, for example `Environment=DOTNET_ROOT=/opt/dotnet` in the systemd unit.
- **Containers:** build on Microsoft's images (`mcr.microsoft.com/dotnet/…`). Images that install .NET from the distribution's own packages carry the same build as the package. Check inside the image rather than trusting its name.
- **Self-contained publish:** this bundles a runtime into the application. Publish from a Microsoft SDK, then run the `ldd` check on the published `libcoreclr.so`.
- **Make the check a deployment gate:** run the `ldd` check from the TL;DR in the image build or the provisioning script, and fail if it finds a system `libunwind`.

## Scope and limits of the evidence

- Verified on one machine: Ubuntu 26.04.1 LTS, x86_64, `dotnet-runtime-10.0` 10.0.13-0ubuntu1~26.04.1, `libunwind8` 1.8.3-0ubuntu1. Other distributions, versions and arm64 haven't been tested. Treat any runtime that links the system `libunwind` as suspect until it passes `tools/throwstress.cs` 20 times in a row.
- The A/B proves Ubuntu's build is at fault, not Sky Omega and not .NET 10.0.13 itself. Linking the system `libunwind` is the most likely cause, since that's the difference the crash path runs through. It also matches the upstream report ([Launchpad bug 2143357](https://bugs.launchpad.net/bugs/2143357), which expired in May 2026 without a maintainer response). The mechanism wasn't isolated further.
- macOS and Windows installs come from Microsoft. This runbook is about Linux distribution packages.

## Reproducing

```bash
dotnet run tools/throwstress.cs -- 30   # exit 0 = survived; the last line names the runtime that ran
```

One clean run proves little: on the affected build about 1 run in 4 crashed. Run it 20 times. The [validation record](../validations/ubuntu-dotnet-libunwind-2026-10-27.md) has the full A/B method, the crash-dump analysis and the numbers.
