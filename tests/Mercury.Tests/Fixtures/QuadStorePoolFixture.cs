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
    /// The shared pool. Bounded to ProcessorCount concurrent stores.
    /// </summary>
    public QuadStorePool Pool { get; }

    public QuadStorePoolFixture()
    {
        // Use ProcessorCount as default - balances parallelism with disk usage
        Pool = new QuadStorePool(
            maxConcurrent: Environment.ProcessorCount,
            purpose: "test");
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
