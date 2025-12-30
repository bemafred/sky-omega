namespace SkyOmega.Mercury.Utilities;

/// <summary>
/// Provides standardized temporary path generation for Mercury projects.
/// All paths use the "mercury-" prefix for easy identification and cleanup.
/// </summary>
/// <remarks>
/// To clean up all Mercury temp files manually:
/// <code>rm -rf /tmp/mercury-*</code>
/// Or programmatically via <see cref="CleanupAll"/>.
/// </remarks>
public readonly record struct TempPath
{
    private const string Prefix = "mercury";

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
    /// Removes all Mercury temp files and directories from the system temp folder.
    /// </summary>
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
