// Shared spawn + PID-extraction for integration targets.
//
// MTP: spawn target.exe with `--debug` — MTP prints "Process Id: NNNN" + blocks
// until Debugger.IsAttached.
// VSTest: spawn `dotnet test` with VSTEST_HOST_DEBUG=1 — testhost prints
// "Process Id: NNNN" + blocks until debugger attaches.
//
// Both use the same regex. Phase 8 (ADR-008 Increment 4) integration tests all
// reuse this helper to keep spawn + handshake logic in one place.

using System;
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
    /// Process; caller is responsible for disposing it (typically via `using`).</summary>
    public static Process Mtp(string targetExe, string? methodFilter = null)
    {
        string args = methodFilter is null
            ? "--debug"
            : $"--debug --filter \"FullyQualifiedName~{methodFilter}\"";
        Process bootstrap = new()
        {
            StartInfo = new ProcessStartInfo(targetExe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };
        bootstrap.Start();
        return bootstrap;
    }

    /// <summary>Spawn `dotnet test` against a Legacy VSTest target with VSTEST_HOST_DEBUG=1.
    /// Optionally filters which test method(s) VSTest runs via
    /// `--filter "FullyQualifiedName~<methodFilter>"`.</summary>
    public static Process Vstest(string targetProject, string? methodFilter = null)
    {
        string filterArg = methodFilter is null
            ? string.Empty
            : $" --filter \"FullyQualifiedName~{methodFilter}\"";
        Process dotnetTest = new()
        {
            StartInfo = new ProcessStartInfo("dotnet", $"test \"{targetProject}\" -c Release --no-build --nologo{filterArg}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                Environment = { ["VSTEST_HOST_DEBUG"] = "1" },
            }
        };
        dotnetTest.Start();
        return dotnetTest;
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
