using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Runtime;
using Xunit;

// Limit test parallelization to reduce stack accumulation.
// QueryResults is ~90KB on stack, and parallel tests can exhaust the 1MB Windows stack.
// MaxParallelThreads = 1 runs tests sequentially, eliminating stack accumulation from parallelism.
// See ADR-011 for details on the stack overflow issue.
[assembly: CollectionBehavior(MaxParallelThreads = 1)]

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
