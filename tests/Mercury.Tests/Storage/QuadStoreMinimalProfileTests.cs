using System;
using System.IO;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-029 Phase 2d completion: QuadStore dispatch tests for the Minimal profile.
/// Session-API writes via BeginBatch / AddCurrentBatched / CommitBatch (same shape
/// as Reference's bulk-load entry); queries via QueryCurrent on the single GSPO
/// index. Named-graph constraints, temporal queries, and direct Add() all reject
/// at the API boundary with clear ProfileCapabilityException messages.
/// </summary>
public class QuadStoreMinimalProfileTests : IDisposable
{
    private readonly string _testDir;

    public QuadStoreMinimalProfileTests()
    {
        var tempPath = TempPath.Test("minimal_profile");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    private string NewStoreDir(string suffix) => Path.Combine(_testDir, suffix);

    private static QuadStore CreateMinimalStore(string dir)
    {
        Directory.CreateDirectory(dir);
        return new QuadStore(dir, null, null, new StorageOptions { Profile = StoreProfile.Minimal });
    }

    [Fact]
    public void Minimal_OpensWithPersistedSchema()
    {
        var dir = NewStoreDir("open");
        using var store = CreateMinimalStore(dir);

        Assert.Equal(StoreProfile.Minimal, store.Schema.Profile);
        Assert.False(store.Schema.HasGraph);
        Assert.False(store.Schema.HasTemporal);
        Assert.False(store.Schema.HasVersioning);
        Assert.True(File.Exists(Path.Combine(dir, StoreSchema.FileName)));
        // Single index declared in the schema.
        Assert.Single(store.Schema.Indexes);
        Assert.Equal("gspo", store.Schema.Indexes[0]);

        // No WAL.
        var (txId, _, _) = store.GetWalStatistics();
        Assert.Equal(0, txId);
    }

    [Fact]
    public void Minimal_BatchedAddRoundTripsThroughQueryCurrent()
    {
        var dir = NewStoreDir("batched");
        using var store = CreateMinimalStore(dir);

        store.BeginBatch();
        store.AddCurrentBatched("<http://ex/s1>", "<http://ex/p>", "<http://ex/o1>");
        store.AddCurrentBatched("<http://ex/s1>", "<http://ex/p>", "<http://ex/o2>");
        store.AddCurrentBatched("<http://ex/s2>", "<http://ex/p>", "<http://ex/o1>");
        store.CommitBatch();

        int s1Count = 0;
        var s1 = store.QueryCurrent("<http://ex/s1>", "", "");
        while (s1.MoveNext()) s1Count++;
        Assert.Equal(2, s1Count);

        int allCount = 0;
        var all = store.QueryCurrent("", "", "");
        while (all.MoveNext()) allCount++;
        Assert.Equal(3, allCount);
    }

    [Fact]
    public void Minimal_AddSameTripleTwice_RemainsSingleEntry()
    {
        var dir = NewStoreDir("dedup");
        using var store = CreateMinimalStore(dir);

        store.BeginBatch();
        store.AddCurrentBatched("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");
        store.AddCurrentBatched("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");
        store.CommitBatch();

        int count = 0;
        var e = store.QueryCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");
        while (e.MoveNext()) count++;
        Assert.Equal(1, count);
    }

    [Fact]
    public void Minimal_NamedGraphInAdd_ThrowsProfileCapability()
    {
        // ADR-029: Minimal's schema declares hasGraph=false. Adding with a non-empty
        // graph fails at the API boundary.
        var dir = NewStoreDir("graph_add_reject");
        using var store = CreateMinimalStore(dir);

        store.BeginBatch();
        try
        {
            var ex = Assert.Throws<ProfileCapabilityException>(() =>
                store.AddCurrentBatched("<http://ex/s>", "<http://ex/p>", "<http://ex/o>", "<http://ex/g1>"));
            Assert.Contains("Minimal", ex.Message);
            Assert.Contains("named graphs", ex.Message);
        }
        finally
        {
            store.RollbackBatch();
        }
    }

    [Fact]
    public void Minimal_NamedGraphInQuery_ThrowsProfileCapability()
    {
        var dir = NewStoreDir("graph_query_reject");
        using var store = CreateMinimalStore(dir);

        var ex = Assert.Throws<ProfileCapabilityException>(() =>
            store.QueryCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>", "<http://ex/g1>"));
        Assert.Contains("Minimal", ex.Message);
    }

    [Fact]
    public void Minimal_DirectAdd_ThrowsProfileCapability()
    {
        // Public Add() is the WAL-durable single-triple path; Minimal has no WAL,
        // so it rejects via RequireWriteCapableProfile's HasVersioning check —
        // same stance as Reference (Decision 7: use BeginBatch as the bulk-load entry).
        var dir = NewStoreDir("direct_add_reject");
        using var store = CreateMinimalStore(dir);

        var ex = Assert.Throws<ProfileCapabilityException>(() =>
            store.Add("s", "p", "o", DateTimeOffset.UtcNow, DateTimeOffset.MaxValue));
        Assert.Contains("Minimal", ex.Message);
        Assert.Contains("ADR-029", ex.Message);
    }

    [Fact]
    public void Minimal_AsOfQuery_ThrowsProfileCapability()
    {
        var dir = NewStoreDir("asof_reject");
        using var store = CreateMinimalStore(dir);

        var ex = Assert.Throws<ProfileCapabilityException>(() =>
            store.QueryAsOf("<http://ex/s>", "<http://ex/p>", "<http://ex/o>",
                DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.Contains("Minimal", ex.Message);
    }

    [Fact]
    public void Minimal_Persistence_TriplesPersistAcrossReopen()
    {
        var dir = NewStoreDir("persist");
        using (var store1 = CreateMinimalStore(dir))
        {
            store1.BeginBatch();
            store1.AddCurrentBatched("<http://ex/s>", "<http://ex/p>", "<http://ex/o1>");
            store1.AddCurrentBatched("<http://ex/s>", "<http://ex/p>", "<http://ex/o2>");
            store1.CommitBatch();
            store1.FlushToDisk();
        }

        using var store2 = new QuadStore(dir, null, null, new StorageOptions { Profile = StoreProfile.Minimal });
        Assert.Equal(StoreProfile.Minimal, store2.Schema.Profile);

        int count = 0;
        var e = store2.QueryCurrent("<http://ex/s>", "<http://ex/p>", "");
        while (e.MoveNext()) count++;
        Assert.Equal(2, count);
    }

    [Fact]
    public void Minimal_RebuildSecondaryIndexes_NoOp()
    {
        // Minimal has only one index — no secondaries to rebuild. RebuildSecondaryIndexes
        // should silently transition state to Ready, not throw.
        var dir = NewStoreDir("rebuild_noop");
        using var store = CreateMinimalStore(dir);

        store.BeginBatch();
        store.AddCurrentBatched("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");
        store.CommitBatch();

        // Should not throw.
        store.RebuildSecondaryIndexes();

        // Triples still queryable.
        int count = 0;
        var e = store.QueryCurrent("", "", "");
        while (e.MoveNext()) count++;
        Assert.Equal(1, count);
    }
}
