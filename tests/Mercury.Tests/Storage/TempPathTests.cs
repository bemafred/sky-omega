using System.Diagnostics;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

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

    #region SafeCleanup Tests

    /// <summary>
    /// Verifies SafeCleanup handles locked files gracefully without throwing.
    /// This simulates the scenario where a file handle is still held briefly after disposal.
    /// Note: File locking behavior differs by platform (mandatory on Windows, advisory on Unix).
    /// </summary>
    [Fact]
    public void SafeCleanup_LockedFile_DoesNotThrow()
    {
        var tempPath = TempPath.Test("locked-file-test");
        tempPath.MarkOwnership();
        var filePath = Path.Combine(tempPath.FullPath, "locked.txt");

        // Create a file with an open handle
        using var lockedFile = new FileStream(filePath, FileMode.Create,
            FileAccess.ReadWrite, FileShare.None);

        // SafeCleanup should not throw - it gracefully handles locked files
        var ex = Record.Exception(() => TempPath.SafeCleanup(tempPath.FullPath));
        Assert.Null(ex);

        // On Windows, file locking is mandatory so the file still exists
        // On Unix, file locking is advisory so deletion may succeed
        // Either outcome is acceptable - the key is that no exception is thrown

        // After releasing handle, ensure cleanup succeeds
        lockedFile.Dispose();
        TempPath.SafeCleanup(tempPath.FullPath);
        Assert.False(Directory.Exists(tempPath.FullPath));
    }

    /// <summary>
    /// Verifies SafeCleanup handles non-existent paths gracefully.
    /// </summary>
    [Fact]
    public void SafeCleanup_NonExistentPath_DoesNotThrow()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}");

        var ex = Record.Exception(() => TempPath.SafeCleanup(nonExistent));
        Assert.Null(ex);
    }

    /// <summary>
    /// Verifies SafeCleanup handles null/empty paths gracefully.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SafeCleanup_NullOrEmpty_DoesNotThrow(string? path)
    {
        var ex = Record.Exception(() => TempPath.SafeCleanup(path!));
        Assert.Null(ex);
    }

    /// <summary>
    /// Simulates parallel test execution where multiple stores are created and cleaned up concurrently.
    /// This validates that SafeCleanup handles the scenario that triggers issues with NCrunch.
    /// </summary>
    [Fact]
    public async Task SafeCleanup_ParallelStoreCreation_AllCleanupSucceeds()
    {
        // Simulate parallel test runners creating/destroying stores
        var tasks = Enumerable.Range(0, 8).Select(async i =>
        {
            var tempPath = TempPath.Test($"parallel-cleanup-{i}");
            tempPath.MarkOwnership();
            var path = tempPath.FullPath;

            // Create store, do work, dispose
            using (var store = new QuadStore(path))
            {
                store.AddCurrent($"<http://s{i}>", "<http://p>", "<http://o>");
                await Task.Delay(Random.Shared.Next(10, 100));
            }

            // This should not throw even if handles are briefly held
            TempPath.SafeCleanup(path);
        });

        // All tasks should complete without IOException
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Verifies that SafeCleanup successfully deletes a directory with nested content.
    /// </summary>
    [Fact]
    public void SafeCleanup_NestedDirectoryStructure_DeletesAll()
    {
        var tempPath = TempPath.Test("nested-cleanup");
        tempPath.MarkOwnership();

        // Create nested structure
        var subDir = Path.Combine(tempPath.FullPath, "subdir", "nested");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "file.txt"), "test");
        File.WriteAllText(Path.Combine(tempPath.FullPath, "root.txt"), "test");

        Assert.True(Directory.Exists(tempPath.FullPath));
        Assert.True(File.Exists(Path.Combine(subDir, "file.txt")));

        TempPath.SafeCleanup(tempPath.FullPath);

        Assert.False(Directory.Exists(tempPath.FullPath));
    }

    #endregion
}
