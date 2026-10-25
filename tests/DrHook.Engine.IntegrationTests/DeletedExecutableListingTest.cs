// Layer 1 — INTEGRATION TEST for finding 93: a .NET process whose executable was deleted while it ran stays in
// both process listings, marked ExecutableDeleted.
//
// Before the fix, on Linux, ProcessInspector.ListDotnetProcesses threw FileNotFoundException on such a process
// ("<path> (deleted)"), and drhook_processes (ProcessAttacher) silently dropped it, hiding a rebuilt orphan.
//
// The target is a private copy of the snapshot target's output directory, run through its apphost so the apphost is
// the main module, then deleted. It is not debugged, so a plain Process.Start child is fine here (finding 87 only
// concerns a target that is both our child and attached).

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkyOmega.DrHook.Engine.Diagnostics;
using SkyOmega.DrHook.Engine.Observation;

namespace DrHook.Engine.IntegrationTests;

[TestClass]
public sealed class DeletedExecutableListingTest
{
    [TestMethod]
    public void ProcessWithDeletedExecutable_StaysListed_MarkedDeleted()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("Windows locks a running executable, so it cannot be deleted.");

        string sourceDir = Path.GetDirectoryName(IntegrationTargetPaths.SnapshotTargetDll())!;
        string copyDir = Path.Combine(Path.GetTempPath(), $"drhook-f93-{Guid.NewGuid():N}");
        CopyDirectory(sourceDir, copyDir);
        string apphost = Path.Combine(copyDir, "DrHookSnapshotTarget");
        Assert.IsTrue(File.Exists(apphost), $"Snapshot target apphost missing: {apphost}");

        using Process target = Process.Start(new ProcessStartInfo(apphost)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        try
        {
            WaitUntilPublished(target.Id, TimeSpan.FromSeconds(20));

            File.Delete(apphost);
            Assert.IsFalse(target.HasExited, "Target exited before the listing was checked.");

            // Both listings must keep the process (no throw, not dropped) on every platform.
            var listed = ProcessInspector.ListDotnetProcesses();
            DotnetProcess entry = listed.SingleOrDefault(p => p.Pid == target.Id)
                ?? throw new AssertFailedException($"Target {target.Id} missing from ListDotnetProcesses.");

            string json = new ProcessAttacher().ListDotNetProcessesAsync(CancellationToken.None).GetAwaiter().GetResult().ToJson();
            JsonNode? row = JsonNode.Parse(json)!["processes"]!.AsArray()
                .FirstOrDefault(p => p!["pid"]!.GetValue<int>() == target.Id);
            Assert.IsNotNull(row, $"Target {target.Id} missing from drhook_processes:\n{json}");

            // Linux names the deletion in /proc/<pid>/exe, so the marking is exact there. What macOS reports for a
            // deleted main module is unverified (finding 93, Open), so it is not asserted there yet.
            if (OperatingSystem.IsLinux())
            {
                Assert.IsTrue(entry.ExecutableDeleted, $"Expected ExecutableDeleted for {entry}");
                Assert.IsNull(entry.FileVersion);
                Assert.AreEqual(apphost, entry.MainModulePath, "Path should be where the file was, without a ' (deleted)' suffix.");
                Assert.IsTrue(row!["executableDeleted"]!.GetValue<bool>());
                Assert.AreEqual("unknown", row["version"]!.GetValue<string>());
            }
        }
        finally
        {
            try { target.Kill(entireProcessTree: true); target.WaitForExit(5000); } catch { }
            try { Directory.Delete(copyDir, recursive: true); } catch { }
        }
    }

    /// <summary>The runtime publishes its diagnostic port shortly after start; listing before that would miss it.</summary>
    private static void WaitUntilPublished(int pid, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (ProcessInspector.IsDotnetProcess(pid)) return;
            Thread.Sleep(100);
        }
        Assert.Fail($"Target {pid} never published a diagnostic port within {timeout.TotalSeconds:0} s.");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
        {
            string to = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, to);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(to, File.GetUnixFileMode(file));
        }
    }
}
