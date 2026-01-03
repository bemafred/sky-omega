using Xunit;

namespace SkyOmega.Mercury.Tests.Fixtures;

/// <summary>
/// Collection definition for tests sharing a QuadStorePool.
/// Apply [Collection("QuadStore")] to test classes to share the pool.
/// </summary>
[CollectionDefinition("QuadStore")]
public class QuadStoreCollection : ICollectionFixture<QuadStorePoolFixture>
{
    // This class has no code - it's just a marker for xUnit
}
