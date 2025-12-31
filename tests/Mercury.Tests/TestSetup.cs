using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Runtime;

namespace SkyOmega.Mercury.Tests;

/// <summary>
/// Assembly-level initialization for Mercury tests.
/// </summary>
internal static class TestSetup
{
    /// <summary>
    /// Runs once when the test assembly is loaded, before any tests execute.
    /// Cleans up stale temp directories from previous crashed test runs.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Clean up test directories from terminated processes.
        // This is safe to call - it only removes directories whose owning process
        // has terminated, never directories from running tests or benchmarks.
        TempPath.CleanupStale("test");
    }
}
