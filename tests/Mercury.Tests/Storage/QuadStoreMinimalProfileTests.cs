using System;
using System.IO;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-029 Phase 2d completion, with ADR-064 D2: QuadStore dispatch tests for the Minimal profile, Graph without
/// secondary indexes, named graphs or trigram index. Session writes with a log, single and batched; queries via
/// QueryCurrent on the single GSPO index. Named-graph constraints and temporal queries reject at the API boundary
/// with clear ProfileCapabilityException messages. The model's own tests are in FactStatesTests.
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
        Assert.True(store.Schema.HasVersioning);   // ADR-064 D2: each fact's states are numbered.
        Assert.True(File.Exists(Path.Combine(dir, StoreSchema.FileName)));
        // Single index declared in the schema.
        Assert.Single(store.Schema.Indexes);
        Assert.Equal("gspo", store.Schema.Indexes[0]);

        // ADR-064 D2: Minimal has a log, as Graph.
        Assert.True(File.Exists(Path.Combine(dir, "wal.log")));
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
    public void Minimal_NamedGraphInASingleWrite_ThrowsProfileCapability_AndLogsNothing()
    {
        // ADR-064 D2: Minimal takes single writes, as Graph does, and has no named graphs.
        var dir = NewStoreDir("single_write_graph_reject");
        using var store = CreateMinimalStore(dir);
        var (txBefore, _, _) = store.GetWalStatistics();

        var ex = Assert.Throws<ProfileCapabilityException>(() =>
            store.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>", "<http://ex/g1>"));
        Assert.Contains("Minimal", ex.Message);
        Assert.Contains("named graphs", ex.Message);

        var (txAfter, _, _) = store.GetWalStatistics();
        Assert.Equal(txBefore, txAfter);
        Assert.Equal(0, store.GetStatistics().QuadCount);
    }

    [Fact]
    public void Minimal_SingleWrites_AssertAndRetract_WhatAClientSeesFollows()
    {
        var dir = NewStoreDir("single_writes");
        using var store = CreateMinimalStore(dir);

        Assert.True(store.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>"));
        Assert.False(store.AddCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>"));
        int before = 0;
        var e1 = store.QueryCurrent("<http://ex/s>", "", "");
        while (e1.MoveNext()) before++;
        Assert.Equal(1, before);

        Assert.True(store.DeleteCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>"));
        Assert.False(store.DeleteCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>"));
        int after = 0;
        var e2 = store.QueryCurrent("<http://ex/s>", "", "");
        while (e2.MoveNext()) after++;
        Assert.Equal(0, after);

        // The retracted fact is held: deletes are soft (ADR-064).
        Assert.Equal(1, store.GetStatistics().QuadCount);
    }

    [Fact]
    public void Minimal_RollbackBatch_LeavesTheStoreAsItWas()
    {
        // ADR-064 D2: a batch is logged and applied at commit, so a rollback leaves nothing behind. Before the log,
        // what a batch had added was in the index already, and a rollback could not take it out.
        var dir = NewStoreDir("rollback");
        using var store = CreateMinimalStore(dir);

        store.BeginBatch();
        store.AddCurrentBatched("<http://ex/s>", "<http://ex/p>", "<http://ex/o1>");
        store.AddCurrentBatched("<http://ex/s>", "<http://ex/p>", "<http://ex/o2>");
        store.RollbackBatch();

        int count = 0;
        var e = store.QueryCurrent("<http://ex/s>", "", "");
        while (e.MoveNext()) count++;
        Assert.Equal(0, count);
        Assert.Equal(0, store.GetStatistics().QuadCount);
    }

    [Fact]
    public void Minimal_QueryMemories_ThrowsProfileCapability()
    {
        var dir = NewStoreDir("memories_reject");
        using var store = CreateMinimalStore(dir);

        // A Minimal store keeps no times (ADR-064 D2), so it has no memories to give with them (ADR-062 D2)
        var ex = Assert.Throws<ProfileCapabilityException>(() =>
            store.QueryMemories("<http://ex/s>", "<http://ex/p>", "<http://ex/o>",
                memorizedSince: DateTimeOffset.UtcNow.AddDays(-1)));
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
