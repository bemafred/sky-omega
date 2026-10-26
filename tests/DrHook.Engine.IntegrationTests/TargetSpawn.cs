// Shared spawn + PID-extraction for integration targets.
//
// MTP: spawn target.exe with `--debug` — MTP prints "Process Id: NNNN" + blocks
// until Debugger.IsAttached.
// VSTest: spawn `dotnet test` with VSTEST_HOST_DEBUG=1 — testhost prints
// "Process Id: NNNN" + blocks until debugger attaches.
//
// Both use the same regex. Phase 8 (ADR-008 Increment 4) integration tests all
// reuse this helper to keep spawn + handshake logic in one place.
//
// TOPOLOGY (finding 87 — the dual-reaper race): the debuggee must NOT be a Process.Start child of
// this test host. Once DrHook attaches, the CoreCLR PAL inside the in-process debug components polls
// wait4(pid, WNOHANG) and reaps the target if it is our child; the BCL also tracks every
// Process.Start child and reaps it on SIGCHLD. Two reapers on one pid: when the PAL wins,
// bootstrap.WaitForExit never sees the exit and the runtime later FailFasts the whole test host
// ("Error while reaping child. errno = 10"). So MTP targets run under a shell parent (bash) — the shell is
// the BCL-tracked child, the target is its child (the PAL's wait4 gets ECHILD, harmless), and the
// shell's `wait` makes its exit mirror the target's. VSTest already has this shape: the attached
// testhost is a child of `dotnet test`, not of this host. It also matches production attach, where
// the debuggee is never the debugger's own child.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DrHook.Engine.IntegrationTests;

internal static class TargetSpawn
{
    /// <summary>Spawn an MTP integration target with --debug. Optionally filters which test
    /// method(s) MTP runs via `--filter FullyQualifiedName~<methodFilter>`. Returns the bootstrap
    /// Process — the bash parent, whose exit mirrors the target's (see TOPOLOGY above); the target
    /// pid comes from <see cref="ExtractPid"/>. Caller is responsible for disposing it (typically via
    /// `using`). The target runs in its own process group, recorded in <see cref="SpawnedProcessGroups"/>
    /// so it is killed even if the test is abandoned at its timeout.</summary>
    public static Process Mtp(string targetExe, string? methodFilter = null)
    {
        // `set -m; "$0" "$@" & echo "DRHOOK_PGID $!"; wait $!` — the target runs as the shell's background
        // job (not exec'd, so it is never this host's child) in its own process group, the shell reports that
        // group, and exits with the target's status. Arguments pass positionally, so no quoting of paths or
        // filters. bash, not /bin/sh: dash refuses `set -m` without a terminal (see Vstest below).
        ProcessStartInfo startInfo = new("/bin/bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("set -m; \"$0\" \"$@\" & echo \"DRHOOK_PGID $!\"; wait $!");
        startInfo.ArgumentList.Add(targetExe);
        startInfo.ArgumentList.Add("--debug");
        if (methodFilter is not null)
        {
            startInfo.ArgumentList.Add("--filter");
            startInfo.ArgumentList.Add($"FullyQualifiedName~{methodFilter}");
        }
        Process bootstrap = new() { StartInfo = startInfo };
        bootstrap.Start();
        string? first = bootstrap.StandardOutput.ReadLine();
        Match m = first is null ? Match.Empty : Regex.Match(first, @"^DRHOOK_PGID (\d+)$");
        if (!m.Success)
        {
            try { bootstrap.Kill(entireProcessTree: true); } catch { }
            bootstrap.Dispose();
            throw new InvalidOperationException($"MTP bootstrap did not report its process group (first line: '{first}').");
        }
        SpawnedProcessGroups.Register(int.Parse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture));
        return bootstrap;
    }

    /// <summary>Spawn `dotnet test` against a Legacy VSTest target with VSTEST_HOST_DEBUG=1, as a job in its OWN
    /// process group. Optionally filters which test method(s) VSTest runs via
    /// `--filter "FullyQualifiedName~<methodFilter>"`. The caller owns the returned <see cref="VstestRun"/> and must
    /// dispose it (typically via `using`) — disposal SIGKILLs the whole group.
    ///
    /// FINDING 90 — why a process group, not <c>Kill(entireProcessTree)</c>: during a run vstest.console can launch a
    /// SECOND testhost, which inherits VSTEST_HOST_DEBUG=1 and parks forever waiting for a debugger nobody attaches.
    /// vstest.console then exits, the waiting testhost is reparented to launchd, and the bootstrap's process TREE no
    /// longer contains it — so the tree kill missed it (~3–4 orphans per suite run, 346 / 17 GB RSS over one day).
    /// A process group survives reparenting (observed: the orphans kept their original pgid), so a group kill reaches
    /// every process the run started. `set -m` gives the background job its own group (pgid = its pid).
    ///
    /// That needs BASH, not /bin/sh: on Ubuntu /bin/sh is dash, which refuses `set -m` without a terminal
    /// ("can't access tty; job control turned off") and leaves the job in the CALLER's group — the group kill
    /// then hits a pgid that does not exist and every VSTest run leaks its tree (observed on zbook, 18 orphans
    /// after one pass). bash gives the job its own group with or without a terminal, on macOS and Linux.
    ///
    /// FINDING 91 — the `Process Id:` line <see cref="ExtractPid"/> parses is printed by vstest.console when it
    /// LAUNCHES the testhost, before the testhost reaches its VSTEST_HOST_DEBUG hold. An attach that lands first makes
    /// the hold return immediately (Debugger.IsAttached) with no Break — so never wait on that Break as the first
    /// stop; force one (Pause).
    ///
    /// RUNNER ISOLATION — the repo-root global.json selects Microsoft.Testing.Platform, under which `dotnet test`
    /// refuses a VSTest project. The target's folder carries its own global.json (SDK pin only, no test runner), and
    /// `dotnet` resolves global.json from the WORKING DIRECTORY — so the run starts in the target's folder, where
    /// `dotnet test` is the legacy VSTest runner this target exists to exercise.</summary>
    public static VstestRun Vstest(string targetProject, string? methodFilter = null)
    {
        ProcessStartInfo startInfo = new("/bin/bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(targetProject)!,
            Environment = { ["VSTEST_HOST_DEBUG"] = "1" },
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("set -m; dotnet \"$@\" & echo \"DRHOOK_PGID $!\"; wait $!");
        startInfo.ArgumentList.Add("sh"); // $0
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(targetProject);
        // The target is a build dependency of this test project (ProjectReference), so it is built in the test's
        // own configuration — read it off the test's bin path (.../bin/<config>/<tfm>/), never assume one.
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name);
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--nologo");
        if (methodFilter is not null)
        {
            startInfo.ArgumentList.Add("--filter");
            startInfo.ArgumentList.Add($"FullyQualifiedName~{methodFilter}");
        }
        Process shell = Process.Start(startInfo)!;
        string? first = shell.StandardOutput.ReadLine();
        Match m = first is null ? Match.Empty : Regex.Match(first, @"^DRHOOK_PGID (\d+)$");
        if (!m.Success)
        {
            try { shell.Kill(entireProcessTree: true); } catch { }
            shell.Dispose();
            throw new InvalidOperationException($"VSTest bootstrap did not report its process group (first line: '{first}').");
        }
        int processGroup = int.Parse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        SpawnedProcessGroups.Register(processGroup);
        return new VstestRun(shell, processGroup);
    }

    /// <summary>Extract the target PID from stdout. Both MTP and VSTest print "Process Id: NNNN".</summary>
    public static int ExtractPid(Process bootstrap, TimeSpan timeout)
    {
        int pid = -1;
        ManualResetEventSlim ready = new(false);
        Thread reader = new(() =>
        {
            string? line;
            while ((line = bootstrap.StandardOutput.ReadLine()) is not null)
            {
                Match m = Regex.Match(line, @"Process Id:\s*(\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPid))
                {
                    Volatile.Write(ref pid, parsedPid);
                    ready.Set();
                }
            }
        }) { IsBackground = true, Name = "target-stdout" };
        reader.Start();
        Thread errDrain = new(() => { while (bootstrap.StandardError.ReadLine() is not null) { } })
        { IsBackground = true, Name = "target-stderr" };
        errDrain.Start();

        Assert.IsTrue(ready.Wait(timeout),
            $"Target did not print 'Process Id: NNNN' within {timeout.TotalSeconds}s — runner handshake failed.");
        return Volatile.Read(ref pid);
    }
}

/// <summary>A `dotnet test` run started by <see cref="TargetSpawn.Vstest"/>: the <see cref="Shell"/> whose exit mirrors
/// `dotnet test`'s (read its stdout with <see cref="TargetSpawn.ExtractPid"/>, wait on it for natural exit), and the
/// <see cref="ProcessGroup"/> holding every process the run started — including a testhost orphaned to launchd while
/// waiting for a debugger (finding 90). <see cref="KillGroup"/> / <see cref="Dispose"/> SIGKILL the whole group.</summary>
internal sealed class VstestRun : IDisposable
{
    private const int SIGKILL = 9;

    public VstestRun(Process shell, int processGroup)
    {
        Shell = shell;
        ProcessGroup = processGroup;
    }

    public Process Shell { get; }
    public int ProcessGroup { get; }

    /// <summary>SIGKILL every process in the run's group. Idempotent — an emptied group yields ESRCH, ignored.</summary>
    public void KillGroup()
    {
        if (OperatingSystem.IsWindows()) { try { Shell.Kill(entireProcessTree: true); } catch { } return; }
        _ = kill(-ProcessGroup, SIGKILL);
    }

    public void Dispose()
    {
        KillGroup();
        try { if (!Shell.HasExited) Shell.Kill(); } catch { }
        Shell.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
}

/// <summary>Every process group the harness started, killed at the end of the run. A test's own `using` cleanup
/// does not run when MSTest abandons it at its timeout (testconfig.json): the test thread is left behind and the
/// target it spawned — an MTP target with `--debug` or a VSTest testhost under VSTEST_HOST_DEBUG, both waiting for
/// a debugger forever — would outlive the run (observed: an abandoned VSTest test left vstest.console + testhost
/// running). [AssemblyCleanup] still runs after an abandoned test, so it reaps them. A group already gone yields
/// ESRCH, ignored.</summary>
[TestClass]
public static class SpawnedProcessGroups
{
    private const int SIGKILL = 9;
    private static readonly object s_lock = new();
    private static readonly List<int> s_groups = new();

    internal static void Register(int processGroup)
    {
        lock (s_lock) s_groups.Add(processGroup);
    }

    [AssemblyCleanup]
    public static void KillAll()
    {
        if (OperatingSystem.IsWindows()) return;
        int[] groups;
        lock (s_lock) { groups = s_groups.ToArray(); s_groups.Clear(); }
        foreach (int group in groups)
            _ = kill(-group, SIGKILL);
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
}
