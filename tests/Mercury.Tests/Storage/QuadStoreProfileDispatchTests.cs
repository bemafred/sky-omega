using System;
using System.IO;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-029 Phase 2d: QuadStore dispatches on schema.Profile. Session-API writes and
/// temporal queries against a Reference-profile store raise
/// <see cref="ProfileCapabilityException"/> at the API boundary (Decision 7 / Decision 4).
/// Minimal profile is accepted for schema-write but the index family is deferred.
/// </summary>
public class QuadStoreProfileDispatchTests : IDisposable
{
    private readonly string _testDir;

    public QuadStoreProfileDispatchTests()
    {
        var tempPath = TempPath.Test("profile");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    private string NewStoreDir(string suffix) => Path.Combine(_testDir, suffix);

    #region Reference profile

    [Fact]
    public void Reference_OpensWithPersistedSchema()
    {
        var dir = NewStoreDir("ref");
        Directory.CreateDirectory(dir);

        using var store = new QuadStore(dir, null, null, new StorageOptions { Profile = StoreProfile.Reference });

        Assert.Equal(StoreProfile.Reference, store.Schema.Profile);
        Assert.False(store.Schema.HasTemporal);
        Assert.False(store.Schema.HasVersioning);
        Assert.True(File.Exists(Path.Combine(dir, StoreSchema.FileName)));
        // Reference has no WAL.
        var (txId, _, _) = store.GetWalStatistics();
        Assert.Equal(0, txId);
    }

    [Fact]
    public void Reference_SessionApiAdd_ThrowsProfileCapability()
    {
        var dir = NewStoreDir("ref_add");
        Directory.CreateDirectory(dir);
        using var store = new QuadStore(dir, null, null, new StorageOptions { Profile = StoreProfile.Reference });

        var ex = Assert.Throws<ProfileCapabilityException>(() =>
            store.Add("s", "p", "o", DateTimeOffset.UtcNow, DateTimeOffset.MaxValue));
        Assert.Contains("Reference", ex.Message);
        Assert.Contains("ADR-029", ex.Message);
    }

    [Fact]
    public void Reference_SessionApiBatch_ThrowsProfileCapability()
    {
        var dir = NewStoreDir("ref_batch");
        Directory.CreateDirectory(dir);
        using var store = new QuadStore(dir, null, null, new StorageOptions { Profile = StoreProfile.Reference });

        Assert.Throws<ProfileCapabilityException>(() => store.BeginBatch());
    }

    [Fact]
    public void Reference_Delete_ThrowsProfileCapability()
    {
        var dir = NewStoreDir("ref_delete");
        Directory.CreateDirectory(dir);
        using var store = new QuadStore(dir, null, null, new StorageOptions { Profile = StoreProfile.Reference });

        Assert.Throws<ProfileCapabilityException>(() =>
            store.Delete("s", "p", "o", DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
    }

    [Fact]
    public void Reference_TemporalQuery_ThrowsProfileCapability()
    {
        var dir = NewStoreDir("ref_query");
        Directory.CreateDirectory(dir);
        using var store = new QuadStore(dir, null, null, new StorageOptions { Profile = StoreProfile.Reference });

        var ex = Assert.Throws<ProfileCapabilityException>(() =>
            store.QueryCurrent(ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty));
        Assert.Contains("temporal", ex.Message);
    }

    [Fact]
    public void Reference_RebuildSecondaryIndexes_ThrowsProfileCapability()
    {
        var dir = NewStoreDir("ref_rebuild");
        Directory.CreateDirectory(dir);
        using var store = new QuadStore(dir, null, null, new StorageOptions { Profile = StoreProfile.Reference });

        Assert.Throws<ProfileCapabilityException>(() => store.RebuildSecondaryIndexes());
    }

    [Fact]
    public void Reference_ReopenPreservesProfile()
    {
        var dir = NewStoreDir("ref_reopen");
        Directory.CreateDirectory(dir);

        using (var first = new QuadStore(dir, null, null, new StorageOptions { Profile = StoreProfile.Reference }))
        {
            Assert.Equal(StoreProfile.Reference, first.Schema.Profile);
        }

        // Reopen with default (Cognitive) options — persisted Reference still wins.
        using (var reopened = new QuadStore(dir))
        {
            Assert.Equal(StoreProfile.Reference, reopened.Schema.Profile);
            Assert.Throws<ProfileCapabilityException>(() =>
                reopened.Add("s", "p", "o", DateTimeOffset.UtcNow, DateTimeOffset.MaxValue));
        }
    }

    [Fact]
    public void Reference_DisposeDoesNotThrow()
    {
        var dir = NewStoreDir("ref_dispose");
        Directory.CreateDirectory(dir);

        var store = new QuadStore(dir, null, null, new StorageOptions { Profile = StoreProfile.Reference });
        store.Dispose(); // Must not touch nullable temporal fields or WAL.
    }

    [Fact]
    public void Reference_GetStatistics_ReportsZeroInitialQuads()
    {
        var dir = NewStoreDir("ref_stats");
        Directory.CreateDirectory(dir);
        using var store = new QuadStore(dir, null, null, new StorageOptions { Profile = StoreProfile.Reference });

        var (quads, _, totalBytes) = store.GetStatistics();
        Assert.Equal(0, quads);
        // WAL bytes contribute 0 for Reference (no WAL).
        Assert.True(totalBytes >= 0);
    }

    #endregion

    #region Minimal profile

    [Fact]
    public void Minimal_OpenThrowsNotSupported()
    {
        var dir = NewStoreDir("minimal");
        Directory.CreateDirectory(dir);

        // Schema creation happens before index construction, so the schema file is
        // written before the dispatch throws. That's fine — the error tells the caller
        // what's going on. Subsequent attempts to open will also throw for the same reason.
        var ex = Assert.Throws<NotSupportedException>(() =>
            new QuadStore(dir, null, null, new StorageOptions { Profile = StoreProfile.Minimal }));
        Assert.Contains("Minimal", ex.Message);
    }

    #endregion

    #region Cognitive profile regression

    [Fact]
    public void Cognitive_DefaultStillWorks()
    {
        var dir = NewStoreDir("cognitive");
        Directory.CreateDirectory(dir);

        using var store = new QuadStore(dir);
        Assert.Equal(StoreProfile.Cognitive, store.Schema.Profile);

        // Session API works normally.
        store.AddCurrent("alice", "knows", "bob", graph: "g1");
        Assert.Equal(1, store.GetStatistics().QuadCount);
    }

    [Fact]
    public void Cognitive_ExplicitlyRequestedStillWorks()
    {
        var dir = NewStoreDir("cognitive_explicit");
        Directory.CreateDirectory(dir);

        using var store = new QuadStore(dir, null, null, new StorageOptions { Profile = StoreProfile.Cognitive });
        Assert.Equal(StoreProfile.Cognitive, store.Schema.Profile);
        store.AddCurrent("alice", "knows", "bob");
        Assert.Equal(1, store.GetStatistics().QuadCount);
    }

    #endregion
}
