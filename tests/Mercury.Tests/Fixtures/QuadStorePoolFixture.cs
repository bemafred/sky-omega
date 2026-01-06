using System;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Tests.Fixtures;

/// <summary>
/// xUnit collection fixture providing a shared QuadStorePool.
/// Pool is created once per test collection and disposed after all tests complete.
/// </summary>
/// <remarks>
/// Usage with IClassFixture (per-class pool):
/// <code>
/// public class MyTests : IClassFixture&lt;QuadStorePoolFixture&gt;
/// {
///     private readonly QuadStorePoolFixture _fixture;
///     public MyTests(QuadStorePoolFixture fixture) => _fixture = fixture;
/// }
/// </code>
///
/// Usage with ICollectionFixture (shared across collection):
/// <code>
/// [CollectionDefinition("QuadStore")]
/// public class QuadStoreCollection : ICollectionFixture&lt;QuadStorePoolFixture&gt; { }
///
/// [Collection("QuadStore")]
/// public class MyTests
/// {
///     private readonly QuadStorePoolFixture _fixture;
///     public MyTests(QuadStorePoolFixture fixture) => _fixture = fixture;
/// }
/// </code>
/// </remarks>
public sealed class QuadStorePoolFixture : IDisposable
{
    /// <summary>
    /// The shared pool. Bounded by disk budget and ProcessorCount.
    /// Uses StorageOptions.ForTesting for minimal disk footprint (~320MB per store vs ~5.5GB).
    /// </summary>
    public QuadStorePool Pool { get; }

    public QuadStorePoolFixture()
    {
        // Use ForTesting options for minimal disk footprint:
        // - 64MB per index file (vs 1GB default) = 256MB for 4 indexes
        // - 64MB atom data (vs 1GB default)
        // - 64K atom capacity (vs 1M default)
        // Pool size is min(ProcessorCount, diskBudget / estimatedStoreSize)
        //
        // Cross-process gate enabled: coordinates with other test runner processes
        // (e.g., NCrunch parallel execution) to prevent disk exhaustion when multiple
        // processes create pools simultaneously.
        Pool = new QuadStorePool(
            storageOptions: StorageOptions.ForTesting,
            diskBudgetFraction: QuadStorePool.DefaultDiskBudgetFraction,
            maxConcurrent: Environment.ProcessorCount,
            purpose: "test",
            useCrossProcessGate: true);
    }

    /// <summary>
    /// Rent a store from the pool. Store is cleared and ready to use.
    /// </summary>
    public QuadStore Rent() => Pool.Rent();

    /// <summary>
    /// Return a store to the pool.
    /// </summary>
    public void Return(QuadStore store) => Pool.Return(store);

    /// <summary>
    /// Rent with automatic return on dispose.
    /// </summary>
    public PooledStoreLease RentScoped() => Pool.RentScoped();

    public void Dispose() => Pool.Dispose();
}
