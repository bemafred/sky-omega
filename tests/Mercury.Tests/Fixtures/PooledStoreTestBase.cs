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

    /// <summary>
    /// Extracts the numeric value from a typed literal string.
    /// Handles both plain values ("30") and typed literals ("30"^^&lt;xsd:integer&gt;).
    /// </summary>
    /// <param name="value">The literal value, possibly with type annotation</param>
    /// <returns>The numeric portion of the literal</returns>
    protected static string ExtractNumericValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        // If it starts with a quote, it's a typed literal: "30"^^<...>
        if (value.StartsWith('"'))
        {
            // Find the closing quote
            int endQuote = value.IndexOf('"', 1);
            if (endQuote > 1)
                return value.Substring(1, endQuote - 1);
        }

        // Plain numeric value
        return value;
    }
}
