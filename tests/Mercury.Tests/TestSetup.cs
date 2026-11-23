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
    /// Removes the temporary directories that processes which have ended left behind.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Every Mercury temporary directory, whatever its category. A temporary pool's directory is named by its
        // purpose, so a sweep of the "test" category kept the directories that a refused
        // StoreSession.OpenTemporary("store-session-test") left, from run to run (elite, 2026-11-22). The sweep
        // removes only a directory whose owner has ended, never one that a running test, benchmark or tool uses.
        TempPath.CleanupAllStale();
    }
}
