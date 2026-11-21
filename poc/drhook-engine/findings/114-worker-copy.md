# Finding 114 — what a worker's own copy of the build costs (ADR-016 6.7): 4 to 33 ms at the median, small beside the start-up on every machine

**Date:** runs and record 2026-11-21
**Amended 2026-11-21:** the copy was not built. Martin dropped it from ADR-016 6.7 the same day: using DrHook while it is
developed is handled by disconnect, rebuild, reconnect, so a copy that protects a running worker from a rebuild is
unwarranted. The worker runs from `worker/` beside drhook-mcp. This finding stays as the measurement.
**Status:** **RECORDED** on omega (macOS arm64), omega-ubuntu (Linux arm64) and omega-windows (Windows 11 arm64).
Probe 114 ran at `dbd336d1`, 20 copies on each machine. Every copy held the source's files and bytes, and was deleted
(RESULT(0)). Not run: zbook, macbook and the six runners, so Linux x64, macOS x64 and Windows x64 are not covered.

ADR-016 6.7's design, accepted 2026-11-21, has the supervisor copy the worker's directory at each start, so that a
rebuild cannot replace the files of a running worker. It said: "To measure first: what the copy costs, on finding 113's
three machines. If it is not small beside the start-up, the copy is made once per build instead." This is that
measurement.

## What was run

[`114-worker-copy.cs`](../114-worker-copy.cs) builds drhook-mcp (Debug, as `.mcp.json` runs it from source) in a clone of
its own, then copies its build output 20 times, the way the supervisor would: each directory created, each file copied
with `File.Copy`. The copies go into DrHook's directory, where the rendezvous socket lives and where the design puts the
copies: `~/Library/SkyOmega/drhook` on macOS, `~/.local/share/sky-omega/drhook` elsewhere. Each copy is timed, then
checked against the source (files, directories and bytes, untimed), then deleted, and the delete is timed.

- **What was copied.** The worker's directory does not exist yet. It will hold the engine and its dependencies, without
  the MCP SDK and the Generic Host, so drhook-mcp's own output is an upper bound on its files and bytes: 92 files in 34
  directories, 37.1 to 37.2 MB.
- **Small, set before the run:** a copy is small beside the start-up when its median is at most the start-up that
  finding 113 measured for a worker per session on that machine (Debug, median): omega 95 ms, omega-ubuntu 50 ms,
  omega-windows 128 ms.
- **The hypothesis:** the copy is small on every machine. Refuted on a machine where its median is larger than that
  start-up.

| Machine | Filesystem of DrHook's directory | .NET | Notes |
|---------|----------------------------------|------|-------|
| omega | APFS | 10.0.9 (SDK 10.0.301) | |
| omega-ubuntu | ext4 (`/tmp` is tmpfs there, so a temporary directory would not have stood for it) | 10.0.14, Microsoft's (`~/.dotnet`) | also run once with Ubuntu's .NET 10.0.14 |
| omega-windows | NTFS | 10.0.15 (SDK 10.0.404) | Defender's real-time protection on |

The machines ran one after another, omega first, and nothing else ran on the VMs (sky-zbook confirmed it ran nothing
on omega or its VMs). The logs are in [`114-worker-copy/`](114-worker-copy/).

- **omega-ubuntu was run twice.** The first run took Ubuntu's .NET from `/usr/bin`, because ssh with a command does not
  put `~/.dotnet` on the path. It is kept, in `omega-ubuntu-ubuntus-dotnet.txt`. The run with Microsoft's .NET, as in
  finding 113, is `omega-ubuntu.txt`.
- **omega-windows' log.** The probe printed the per-copy lines and the line for copy 1 in the machine's Swedish
  culture, a decimal comma and non-breaking spaces between thousands. That was fixed after the runs, so that every
  number prints in the invariant culture; what is measured is unchanged. PowerShell also re-encoded the output, so
  the en dash reads `ΓÇô`, as in finding 113.

## Results

Median [min–max] ms over copies 2 to 20, with copy 1 apart:

| Machine | Copy | Delete | Copy 1: copy / delete | Start-up (finding 113) | The copy against the start-up |
|---------|------|--------|-----------------------|------------------------|-------------------------------|
| omega | 12.8 [11.9–13.1] | 5.5 [4.8–6.2] | 15.0 / 5.0 | 95 | 13 % |
| omega-ubuntu | 3.9 [3.7–4.2] | 1.1 [1.0–1.4] | 5.3 / 1.7 | 50 | 8 % |
| omega-windows | 33.4 [31.1–45.6] | 6.5 [5.9–14.5] | 36.0 / 7.3 | 128 | 26 % |

With Ubuntu's .NET, omega-ubuntu's copy took 3.9 [3.8–4.2] ms and its delete 1.1 [1.0–1.4] ms, the same as with
Microsoft's.

**The hypothesis holds on all three machines.** The copy's median is 8 to 26 % of the start-up, and on each machine
its slowest copy is below that machine's start-up median. Copy 1 is at most 2.6 ms slower than the median.

The delete comes after the worker has ended, so it is not on a session's path. It took at most 14.5 ms.

## Not established

- The worker's own directory, which does not exist yet. It will be smaller than drhook-mcp's output.
- Release, what the installed tool runs: it was not built.
- Linux x64, macOS x64 and Windows x64, and the runners.
- A copy on a loaded machine, or while a build writes the same files.
- Why the copy is faster on omega-ubuntu's ext4, in a VM on omega, than on omega's own APFS. How `File.Copy` copies on
  each platform was not read.
- What Defender adds on omega-windows. It was on for every copy.

## What this establishes

On these three machines, the worker's own copy of the build costs 4 to 33 ms at the median, and at most 46 ms,
including copying drhook-mcp's whole output. That is 8 to 26 % of what a worker per session adds at its start (finding
113). So, by the rule the design set, the copy is made at each worker's start, not once per build.
