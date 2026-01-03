using System;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Tests.Fixtures;

/// <summary>
/// Base class for tests that need a pooled QuadStore.
/// Provides a clean store per test with automatic return to pool.
/// </summary>
/// <remarks>
/// Usage:
/// <code>
/// [Collection("QuadStore")]
/// public class MyTests : PooledStoreTestBase
/// {
///     public MyTests(QuadStorePoolFixture fixture) : base(fixture) { }
///
///     [Fact]
///     public void MyTest()
///     {
///         Store.AddCurrent("&lt;http://ex/s&gt;", "&lt;http://ex/p&gt;", "&lt;http://ex/o&gt;");
///         // ...
///     }
/// }
/// </code>
/// </remarks>
public abstract class PooledStoreTestBase : IDisposable
{
    private readonly PooledStoreLease _lease;

    /// <summary>
    /// The pooled store, cleared and ready for use.
    /// </summary>
    protected QuadStore Store => _lease.Store;

    protected PooledStoreTestBase(QuadStorePoolFixture fixture)
    {
        _lease = fixture.RentScoped();
    }

    public virtual void Dispose()
    {
        _lease.Dispose();
    }
}
