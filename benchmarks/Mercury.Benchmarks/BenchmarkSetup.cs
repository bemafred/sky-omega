using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Runtime;

namespace SkyOmega.Mercury.Benchmarks;

/// <summary>
/// Assembly-level initialization for Mercury benchmarks.
/// </summary>
internal static class BenchmarkSetup
{
    /// <summary>
    /// Runs once when the benchmark assembly is loaded, before any benchmarks execute.
    /// Cleans up stale temp directories from previous crashed benchmark runs.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Clean up benchmark directories from terminated processes.
        // This is safe to call - it only removes directories whose owning process
        // has terminated, never directories from running tests or benchmarks.
        TempPath.CleanupStale("bench");
    }
}
