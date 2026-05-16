using System;
using System.IO;
using System.Linq;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-029 Phase 2d: QuadStore dispatch tests for the Graph profile. Session-API
/// writes route through the new <see cref="VersionedQuadIndex"/> family;
/// queries return live entries (soft-deleted entries filtered); temporal
/// queries against Graph profile fail with <see cref="ProfileCapabilityException"/>
/// per Decision 4.
/// </summary>
public class QuadStoreGraphProfileTests : IDisposable
{
    private readonly string _testDir;

    public QuadStoreGraphProfileTests()
    {
        var tempPath = TempPath.Test("graph_profile");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    private string NewStoreDir(string suffix) => Path.Combine(_testDir, suffix);

    private static QuadStore CreateGraphStore(string dir)
    {
        Directory.CreateDirectory(dir);
        return new QuadStore(dir, null, null, new StorageOptions { Profile = StoreProfile.Graph });
    }

    [Fact]
    public void Graph_OpensWithPersistedSchema()
    {
        var dir = NewStoreDir("open");
        using var store = CreateGraphStore(dir);

        Assert.Equal(StoreProfile.Graph, store.Schema.Profile);
        Assert.True(store.Schema.HasGraph);
        Assert.False(store.Schema.HasTemporal);   // ADR-029: Graph has no time dimension.
        Assert.True(store.Schema.HasVersioning);  // ADR-029: Graph has versioning + soft-delete.
        Assert.True(File.Exists(Path.Combine(dir, StoreSchema.FileName)));

        // Graph has a WAL (unlike Reference).
        var (txId, _, _) = store.GetWalStatistics();
        Assert.True(txId >= 0);
    }

    [Fact]
    public void Graph_Add_RoundTripsThroughQueryCurrent()
    {
        var dir = NewStoreDir("add_query");
        using var store = CreateGraphStore(dir);

        store.Add("<http://ex/s>", "<http://ex/p>", "<http://ex/o>",
            DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);

        int count = 0;
        var e = store.QueryCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");
        while (e.MoveNext()) count++;
        Assert.Equal(1, count);
    }

    [Fact]
    public void Graph_AddSameTripleTwice_RemainsSingleEntry()
    {
        // ADR-029 Graph profile: RDF set semantics. Re-adding a live triple is a
        // no-op — no version bump, no duplicate row. VersionedQuadIndex enforces
        // this at the leaf level.
        var dir = NewStoreDir("dedup");
        using var store = CreateGraphStore(dir);

        store.Add("<http://ex/s>", "<http://ex/p>", "<http://ex/o>",
            DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
        store.Add("<http://ex/s>", "<http://ex/p>", "<http://ex/o>",
            DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);

        int count = 0;
        var e = store.QueryCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");
        while (e.MoveNext()) count++;
        Assert.Equal(1, count);
    }

    [Fact]
    public void Graph_BatchedAddAndCommit_PopulatesAllIndexes()
    {
        // Session API: BeginBatch / AddBatched / CommitBatch. All four indexes
        // (GSPO/GPOS/GOSP/TGSP) populated by the non-bulk-mode ApplyToIndexesById path.
        var dir = NewStoreDir("batched");
        using var store = CreateGraphStore(dir);

        store.BeginBatch();
        store.AddBatched("<http://ex/s1>", "<http://ex/p1>", "<http://ex/o1>",
            DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
        store.AddBatched("<http://ex/s2>", "<http://ex/p1>", "<http://ex/o2>",
            DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
        store.AddBatched("<http://ex/s1>", "<http://ex/p2>", "<http://ex/o3>",
            DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
        store.CommitBatch();

        // Query by subject (uses GSPO).
        int s1Count = 0;
        var s1 = store.QueryCurrent("<http://ex/s1>", "", "");
        while (s1.MoveNext()) s1Count++;
        Assert.Equal(2, s1Count);

        // Query by predicate (uses GPOS).
        int pCount = 0;
        var pQuery = store.QueryCurrent("", "<http://ex/p1>", "");
        while (pQuery.MoveNext()) pCount++;
        Assert.Equal(2, pCount);

        // Query by object (uses GOSP).
        int oCount = 0;
        var oQuery = store.QueryCurrent("", "", "<http://ex/o1>");
        while (oQuery.MoveNext()) oCount++;
        Assert.Equal(1, oCount);
    }

    [Fact]
    public void Graph_Delete_SoftDeletesAndQueryHidesEntry()
    {
        var dir = NewStoreDir("delete");
        using var store = CreateGraphStore(dir);

        store.Add("<http://ex/s>", "<http://ex/p>", "<http://ex/o>",
            DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
        // Pre-delete: 1 live entry.
        int beforeCount = 0;
        var before = store.QueryCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");
        while (before.MoveNext()) beforeCount++;
        Assert.Equal(1, beforeCount);

        var deleted = store.Delete("<http://ex/s>", "<http://ex/p>", "<http://ex/o>",
            DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
        Assert.True(deleted);

        // Post-delete: live query returns 0 (soft-deleted filtered out).
        int afterCount = 0;
        var after = store.QueryCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");
        while (after.MoveNext()) afterCount++;
        Assert.Equal(0, afterCount);
    }

    [Fact]
    public void Graph_ReAddAfterDelete_UnDeletes()
    {
        var dir = NewStoreDir("undelete");
        using var store = CreateGraphStore(dir);

        store.Add("<http://ex/s>", "<http://ex/p>", "<http://ex/o>",
            DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
        store.Delete("<http://ex/s>", "<http://ex/p>", "<http://ex/o>",
            DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
        store.Add("<http://ex/s>", "<http://ex/p>", "<http://ex/o>",
            DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);

        // After un-delete: 1 live entry queryable again.
        int count = 0;
        var e = store.QueryCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>");
        while (e.MoveNext()) count++;
        Assert.Equal(1, count);
    }

    [Fact]
    public void Graph_Persistence_TriplesPersistAcrossReopen()
    {
        var dir = NewStoreDir("persist");
        using (var store1 = CreateGraphStore(dir))
        {
            store1.Add("<http://ex/s>", "<http://ex/p>", "<http://ex/o1>",
                DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
            store1.Add("<http://ex/s>", "<http://ex/p>", "<http://ex/o2>",
                DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
            store1.FlushToDisk();
        }

        using var store2 = new QuadStore(dir, null, null, new StorageOptions { Profile = StoreProfile.Graph });
        Assert.Equal(StoreProfile.Graph, store2.Schema.Profile);

        int count = 0;
        var e = store2.QueryCurrent("<http://ex/s>", "<http://ex/p>", "");
        while (e.MoveNext()) count++;
        Assert.Equal(2, count);
    }

    [Fact]
    public void Graph_AsOfQuery_ThrowsProfileCapability()
    {
        // ADR-029 Decision 4: temporal queries against a non-temporal profile fail
        // loudly at the API boundary. AsOf with explicit time + Graph profile = throw.
        var dir = NewStoreDir("asof_reject");
        using var store = CreateGraphStore(dir);

        store.Add("<http://ex/s>", "<http://ex/p>", "<http://ex/o>",
            DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);

        var ex = Assert.Throws<ProfileCapabilityException>(() =>
            store.QueryAsOf("<http://ex/s>", "<http://ex/p>", "<http://ex/o>",
                DateTimeOffset.UtcNow.AddDays(-1)));
        // The rejection message mentions Graph and the temporal-dimension absence.
        Assert.Contains("Graph", ex.Message);
    }

    [Fact]
    public void Graph_RangeQuery_ThrowsProfileCapability()
    {
        var dir = NewStoreDir("range_reject");
        using var store = CreateGraphStore(dir);

        var ex = Assert.Throws<ProfileCapabilityException>(() =>
            store.Query("<http://ex/s>", "<http://ex/p>", "<http://ex/o>",
                TemporalQueryType.Range,
                rangeStart: DateTimeOffset.UtcNow.AddDays(-1),
                rangeEnd: DateTimeOffset.UtcNow));
        Assert.Contains("Graph", ex.Message);
        Assert.Contains("temporal", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Graph_BulkLoadAndRebuild_PopulatesAllSecondaryIndexes()
    {
        // ADR-029 Graph profile Commit 3: bulk-load + RebuildSecondaryIndexes.
        // Opens the store in bulk mode (only the primary GSPO index is written
        // inline by ApplyToIndexesById; secondaries deferred), populates triples,
        // then runs RebuildSecondaryIndexes to fill GPOS/GOSP/TGSP + trigram.
        var dir = NewStoreDir("bulk_rebuild");
        Directory.CreateDirectory(dir);

        using var store = new QuadStore(dir, null, null,
            new StorageOptions { Profile = StoreProfile.Graph, BulkMode = true });

        store.BeginBatch();
        store.AddCurrentBatched("<http://ex/s1>", "<http://ex/p1>", "\"hello world\"@en");
        store.AddCurrentBatched("<http://ex/s2>", "<http://ex/p1>", "\"hello galaxy\"@en");
        store.AddCurrentBatched("<http://ex/s1>", "<http://ex/p2>", "<http://ex/o>");
        store.CommitBatch();

        // Pre-rebuild: only GSPO populated. Secondary-bound queries (by predicate
        // alone) will fall through to GSPO scan, but the result-set should still
        // be correct (just slower than after rebuild).
        store.RebuildSecondaryIndexes();

        // Predicate-bound query — relies on GPOS index post-rebuild.
        int pCount = 0;
        var pQuery = store.QueryCurrent("", "<http://ex/p1>", "");
        while (pQuery.MoveNext()) pCount++;
        Assert.Equal(2, pCount);

        // Object-bound query — relies on GOSP post-rebuild.
        int oCount = 0;
        var oQuery = store.QueryCurrent("", "", "<http://ex/o>");
        while (oQuery.MoveNext()) oCount++;
        Assert.Equal(1, oCount);

        // Subject-bound query — primary GSPO, always works.
        int sCount = 0;
        var sQuery = store.QueryCurrent("<http://ex/s1>", "", "");
        while (sQuery.MoveNext()) sCount++;
        Assert.Equal(2, sCount);
    }

    [Fact]
    public void Graph_GraphIsolation_NamedGraphsDoNotLeak()
    {
        var dir = NewStoreDir("graph_iso");
        using var store = CreateGraphStore(dir);

        store.Add("<http://ex/s>", "<http://ex/p>", "<http://ex/o>",
            DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, "<http://ex/g1>");
        store.Add("<http://ex/s>", "<http://ex/p>", "<http://ex/o>",
            DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, "<http://ex/g2>");

        int g1Count = 0;
        var g1 = store.QueryCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>", "<http://ex/g1>");
        while (g1.MoveNext()) g1Count++;

        int g2Count = 0;
        var g2 = store.QueryCurrent("<http://ex/s>", "<http://ex/p>", "<http://ex/o>", "<http://ex/g2>");
        while (g2.MoveNext()) g2Count++;

        Assert.Equal(1, g1Count);
        Assert.Equal(1, g2Count);
    }
}
