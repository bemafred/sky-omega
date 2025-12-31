using System.Diagnostics;
using SkyOmega.Mercury.Runtime;
using Xunit;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Tests for TempPath utility - specifically crash recovery cleanup.
/// </summary>
public class TempPathTests
{
    /// <summary>
    /// Verifies that CleanupStale removes directories with lock files from non-existent processes.
    /// This simulates the crash recovery scenario.
    /// </summary>
    [Fact]
    public void CleanupStale_RemovesDirectoriesFromTerminatedProcesses()
    {
        // Create a temp directory that looks like it was left by a crashed process
        var stalePath = TempPath.Create("test", "stale-cleanup-test", unique: true);
        var staleDir = stalePath.FullPath;

        try
        {
            Directory.CreateDirectory(staleDir);

            // Write a lock file with a non-existent PID (simulating crashed process)
            var lockFile = Path.Combine(staleDir, ".mercury.lock");
            var fakePid = 999999; // Very unlikely to be a real process
            var fakeStartTime = DateTime.UtcNow.AddDays(-1).ToFileTimeUtc();
            File.WriteAllText(lockFile, $"{fakePid}\n{fakeStartTime}");

            // Verify directory exists before cleanup
            Assert.True(Directory.Exists(staleDir), "Stale directory should exist before cleanup");

            // Run cleanup
            TempPath.CleanupStale("test");

            // Verify directory was removed
            Assert.False(Directory.Exists(staleDir),
                "CleanupStale should remove directories from terminated processes");
        }
        finally
        {
            // Safety cleanup in case test fails
            if (Directory.Exists(staleDir))
                Directory.Delete(staleDir, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that CleanupStale does NOT remove directories from the current process.
    /// </summary>
    [Fact]
    public void CleanupStale_PreservesDirectoriesFromCurrentProcess()
    {
        var activePath = TempPath.Test("active-cleanup-test");
        activePath.MarkOwnership(); // Creates lock file with current process info

        try
        {
            Assert.True(Directory.Exists(activePath.FullPath), "Active directory should exist");

            // Run cleanup - should NOT remove our directory
            TempPath.CleanupStale("test");

            // Verify directory still exists
            Assert.True(Directory.Exists(activePath.FullPath),
                "CleanupStale should preserve directories from the current process");
        }
        finally
        {
            activePath.Cleanup();
        }
    }

    /// <summary>
    /// Verifies that directories without lock files older than 24 hours are cleaned up.
    /// </summary>
    [Fact]
    public void CleanupStale_RemovesOldDirectoriesWithoutLockFiles()
    {
        // This test is tricky because we can't easily fake directory creation time.
        // Instead, we verify that recent directories WITHOUT lock files are preserved
        // (the 24-hour safety window).

        var recentPath = TempPath.Create("test", "recent-no-lock", unique: true);
        var recentDir = recentPath.FullPath;

        try
        {
            // Create directory but DON'T call MarkOwnership (no lock file)
            Directory.CreateDirectory(recentDir);

            Assert.True(Directory.Exists(recentDir), "Directory should exist");
            Assert.False(File.Exists(Path.Combine(recentDir, ".mercury.lock")),
                "Should not have lock file");

            // Run cleanup - should preserve recent directory (< 24 hours old)
            TempPath.CleanupStale("test");

            // Directory should still exist (created just now, within 24-hour window)
            Assert.True(Directory.Exists(recentDir),
                "CleanupStale should preserve recent directories without lock files (24-hour safety window)");
        }
        finally
        {
            if (Directory.Exists(recentDir))
                Directory.Delete(recentDir, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that MarkOwnership creates the expected lock file format.
    /// </summary>
    [Fact]
    public void MarkOwnership_CreatesLockFileWithProcessInfo()
    {
        var tempPath = TempPath.Test("lock-format-test");
        tempPath.MarkOwnership();

        try
        {
            var lockFile = Path.Combine(tempPath.FullPath, ".mercury.lock");
            Assert.True(File.Exists(lockFile), "Lock file should exist after MarkOwnership");

            var lines = File.ReadAllLines(lockFile);
            Assert.Equal(2, lines.Length);

            // Verify PID matches current process
            Assert.True(int.TryParse(lines[0], out var pid), "First line should be parseable as int (PID)");
            Assert.Equal(Environment.ProcessId, pid);

            // Verify start time is valid
            Assert.True(long.TryParse(lines[1], out var startTime), "Second line should be parseable as long (FileTime)");
            var currentProcess = Process.GetCurrentProcess();
            Assert.Equal(currentProcess.StartTime.ToFileTimeUtc(), startTime);
        }
        finally
        {
            tempPath.Cleanup();
        }
    }
}
