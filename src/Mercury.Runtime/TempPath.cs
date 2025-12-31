using System.Diagnostics;

namespace SkyOmega.Mercury.Runtime;

/// <summary>
/// Provides standardized temporary path generation for Mercury projects.
/// All paths use the "mercury-" prefix for easy identification and cleanup.
/// </summary>
/// <remarks>
/// <para>
/// To clean up all Mercury temp files manually:
/// <code>rm -rf /tmp/mercury-*</code>
/// Or programmatically via <see cref="CleanupAll"/>.
/// </para>
/// <para>
/// For crash-safe cleanup, use <see cref="MarkOwnership"/> after creating a temp path,
/// then call <see cref="CleanupStale"/> at startup. This ensures only directories from
/// terminated processes are cleaned up, never active ones.
/// </para>
/// </remarks>
public readonly record struct TempPath
{
    private const string Prefix = "mercury";
    private const string LockFileName = ".mercury.lock";

    /// <summary>
    /// The full path to the temporary file or directory.
    /// </summary>
    public string FullPath { get; }

    private TempPath(string category, string name, bool unique, string? extension)
    {
        var suffix = unique ? $"-{Guid.NewGuid():N}" : "";
        var path = Path.Combine(Path.GetTempPath(), $"{Prefix}-{category}-{name}{suffix}");
        FullPath = extension != null ? path + extension : path;
    }

    /// <summary>
    /// Creates a temp path for test isolation.
    /// Pattern: mercury-test-{name}-{guid}
    /// </summary>
    public static TempPath Test(string name) => new("test", name, unique: true, extension: null);

    /// <summary>
    /// Creates a temp path for benchmark isolation.
    /// Pattern: mercury-bench-{name}-{guid}
    /// </summary>
    public static TempPath Benchmark(string name) => new("bench", name, unique: true, extension: null);

    /// <summary>
    /// Creates a temp path for examples (deterministic, no guid).
    /// Pattern: mercury-example-{name}
    /// </summary>
    public static TempPath Example(string name) => new("example", name, unique: false, extension: null);

    /// <summary>
    /// Creates a temp path for CLI runtime sessions.
    /// Pattern: mercury-cli-{name}-{guid}
    /// </summary>
    public static TempPath Cli(string name) => new("cli", name, unique: true, extension: null);

    /// <summary>
    /// Creates a temp path with a custom category.
    /// Pattern: mercury-{category}-{name}[-{guid}]
    /// </summary>
    /// <param name="category">Category prefix (e.g., "custom", "cache")</param>
    /// <param name="name">Descriptive name for the temp path</param>
    /// <param name="unique">If true, appends a GUID for isolation</param>
    public static TempPath Create(string category, string name, bool unique = true) =>
        new(category, name, unique, extension: null);

    /// <summary>
    /// Returns a new TempPath with the specified file extension appended.
    /// </summary>
    public TempPath WithExtension(string extension) =>
        new(this.FullPath + extension);

    // Private constructor for WithExtension
    private TempPath(string fullPath) => FullPath = fullPath;

    /// <summary>
    /// Ensures the path is clean by deleting any existing file or directory.
    /// </summary>
    public void EnsureClean()
    {
        if (Directory.Exists(FullPath))
            Directory.Delete(FullPath, recursive: true);
        if (File.Exists(FullPath))
            File.Delete(FullPath);
    }

    /// <summary>
    /// Cleans up the file or directory at this path if it exists.
    /// </summary>
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(FullPath))
                Directory.Delete(FullPath, recursive: true);
            else if (File.Exists(FullPath))
                File.Delete(FullPath);
        }
        catch (IOException)
        {
            // Ignore cleanup failures (file in use, etc.)
        }
    }

    /// <summary>
    /// Creates the directory and writes a lock file marking ownership by the current process.
    /// The lock file contains the process ID and start time, enabling <see cref="CleanupStale"/>
    /// to safely identify and remove directories from terminated processes.
    /// </summary>
    /// <remarks>
    /// Call this immediately after obtaining a TempPath to establish ownership.
    /// This enables bulletproof cleanup that never deletes active directories,
    /// even during long-running benchmarks or parallel test executions.
    /// </remarks>
    public void MarkOwnership()
    {
        Directory.CreateDirectory(FullPath);
        var lockFile = Path.Combine(FullPath, LockFileName);
        var process = Process.GetCurrentProcess();
        var content = $"{Environment.ProcessId}\n{process.StartTime.ToFileTimeUtc()}";
        File.WriteAllText(lockFile, content);
    }

    /// <summary>
    /// Cleans up temp directories for a category whose owning process has terminated.
    /// Safe to call at any time - will never delete directories from running processes.
    /// </summary>
    /// <param name="category">Category to clean (e.g., "test", "bench")</param>
    /// <remarks>
    /// <para>
    /// This method checks each directory's lock file to determine if the owning process
    /// is still running. Only directories from terminated processes are deleted.
    /// </para>
    /// <para>
    /// The check uses both process ID and start time to handle PID reuse by the OS.
    /// If a new process has the same PID but different start time, the directory is
    /// considered stale and will be cleaned up.
    /// </para>
    /// <para>
    /// Directories without lock files (legacy or crashed during creation) fall back to
    /// a 24-hour age check for safety.
    /// </para>
    /// </remarks>
    public static void CleanupStale(string category)
    {
        var tempDir = Path.GetTempPath();
        var pattern = $"{Prefix}-{category}-*";

        foreach (var dir in Directory.GetDirectories(tempDir, pattern))
        {
            if (IsDirectoryInUse(dir))
                continue;

            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Also clean up any stale files (not directories) with the pattern
        foreach (var file in Directory.GetFiles(tempDir, pattern))
        {
            var lockFile = file + LockFileName;
            if (File.Exists(lockFile) && IsLockFileActive(lockFile))
                continue;

            // No lock file for plain files - use age-based fallback
            try
            {
                if ((DateTime.UtcNow - File.GetCreationTimeUtc(file)) > TimeSpan.FromHours(24))
                    File.Delete(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Cleans up all stale temp directories across all categories.
    /// </summary>
    public static void CleanupAllStale()
    {
        var tempDir = Path.GetTempPath();
        var pattern = $"{Prefix}-*";

        foreach (var dir in Directory.GetDirectories(tempDir, pattern))
        {
            if (IsDirectoryInUse(dir))
                continue;

            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsDirectoryInUse(string dir)
    {
        var lockFile = Path.Combine(dir, LockFileName);

        if (!File.Exists(lockFile))
        {
            // No lock file - legacy format or crashed during creation.
            // Conservative fallback: only consider stale if older than 24 hours.
            return (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir)) < TimeSpan.FromHours(24);
        }

        return IsLockFileActive(lockFile);
    }

    private static bool IsLockFileActive(string lockFile)
    {
        try
        {
            var lines = File.ReadAllLines(lockFile);
            if (lines.Length < 2)
                return false; // Corrupt lock file → stale

            if (!int.TryParse(lines[0], out var pid))
                return false; // Invalid PID → stale

            if (!long.TryParse(lines[1], out var startTimeFileTime))
                return false; // Invalid start time → stale

            // Check if process with that PID is still running
            Process process;
            try
            {
                process = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                return false; // Process not found → stale
            }

            // Verify it's the same process (not PID reuse) by comparing start time
            try
            {
                var actualStartTime = process.StartTime.ToFileTimeUtc();
                return actualStartTime == startTimeFileTime;
            }
            catch (Exception)
            {
                // Can't get start time (permission issue, process exited, etc.)
                // Be conservative - assume it might be active
                return true;
            }
        }
        catch (IOException)
        {
            // Can't read lock file - might be in use, be conservative
            return true;
        }
    }

    /// <summary>
    /// Removes all Mercury temp files and directories from the system temp folder.
    /// </summary>
    /// <remarks>
    /// WARNING: This removes ALL Mercury temp files regardless of whether they are
    /// in use. For safe cleanup, use <see cref="CleanupStale"/> instead.
    /// </remarks>
    public static void CleanupAll()
    {
        var tempDir = Path.GetTempPath();

        foreach (var dir in Directory.GetDirectories(tempDir, $"{Prefix}-*"))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }

        foreach (var file in Directory.GetFiles(tempDir, $"{Prefix}-*"))
        {
            try { File.Delete(file); }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// Removes all temp files and directories for a specific category.
    /// </summary>
    public static void CleanupCategory(string category)
    {
        var tempDir = Path.GetTempPath();
        var pattern = $"{Prefix}-{category}-*";

        foreach (var dir in Directory.GetDirectories(tempDir, pattern))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }

        foreach (var file in Directory.GetFiles(tempDir, pattern))
        {
            try { File.Delete(file); }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// Implicit conversion to string for seamless use with APIs expecting paths.
    /// </summary>
    public static implicit operator string(TempPath path) => path.FullPath;

    /// <inheritdoc />
    public override string ToString() => FullPath;
}
