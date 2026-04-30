using System;
using System.IO;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-034 Phase 1B-5b: end-to-end bulk-load against a SortedAtomStore-backed Reference
/// store via the QuadStore.BeginBatch / AddCurrentBatched / CommitBatch surface.
/// Verifies that the deferred-resolution flow produces the same store contents as the
/// HashAtomStore-backed Reference path would, queried through the public APIs.
/// </summary>
public class SortedAtomStoreBulkLoadTests : IDisposable
{
    private readonly string _testDir;

    public SortedAtomStoreBulkLoadTests()
    {
        var tempPath = TempPath.Test("sorted_bulk_e2e");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    [Fact]
    public void FirstBulkLoad_PopulatesSortedAtomStore_AndQueriesWork()
    {
        var dir = Path.Combine(_testDir, "first_bulk");
        Directory.CreateDirectory(dir);

        // Pre-write the schema so QuadStore.Open uses the Sorted-backed dispatch.
        var schema = StoreSchema.ForProfile(StoreProfile.Reference)
            with { AtomStore = AtomStoreImplementation.Sorted };
        schema.WriteTo(dir);

        var bulkOpts = new StorageOptions { BulkMode = true, Profile = StoreProfile.Reference };
        using (var store = new QuadStore(dir, null, null, bulkOpts))
        {
            Assert.IsType<SortedAtomStore>(store.Atoms);
            Assert.Equal(0, store.Atoms.AtomCount);  // placeholder

            // Bulk-load 5 triples through the standard surface; the dispatch routes
            // through SortedAtomBulkBuilder rather than _atoms.Intern.
            store.BeginBatch();
            store.AddCurrentBatched("<http://ex/alice>", "<http://ex/knows>", "<http://ex/bob>");
            store.AddCurrentBatched("<http://ex/bob>", "<http://ex/knows>", "<http://ex/carol>");
            store.AddCurrentBatched("<http://ex/alice>", "<http://ex/age>", "\"42\"");
            store.AddCurrentBatched("<http://ex/bob>", "<http://ex/age>", "\"35\"");
            store.AddCurrentBatched("<http://ex/carol>", "<http://ex/age>", "\"28\"");
            store.CommitBatch();

            // After CommitBatch: vocab is built, _atoms re-opened over fresh files,
            // _bulkSorter populated with 5 ReferenceKey records. FlushToDisk drains
            // the sorter into the GSPO index via AppendSorted.
            store.FlushToDisk();

            // Vocabulary is now real. Distinct atoms: alice, bob, carol, knows, age,
            // "42", "35", "28" = 8 atoms.
            Assert.Equal(8, store.Atoms.AtomCount);
            Assert.IsType<SortedAtomStore>(store.Atoms);

            // Spot-check the dense ID assignment via byte-sorted order.
            // Alphabetical: "28", "35", "42", <ex/age>, <ex/alice>, <ex/bob>, <ex/carol>, <ex/knows>
            // Wait — bytes start with '"' (0x22) for literals vs '<' (0x3C) for IRIs.
            // So literal strings sort BEFORE IRIs. Sorted: "28", "35", "42", <ex/age>, <ex/alice>, <ex/bob>, <ex/carol>, <ex/knows>.
            Assert.Equal(1, store.Atoms.GetAtomId("\"28\""));
            Assert.Equal(2, store.Atoms.GetAtomId("\"35\""));
            Assert.Equal(3, store.Atoms.GetAtomId("\"42\""));
            Assert.Equal(4, store.Atoms.GetAtomId("<http://ex/age>"));
            Assert.Equal(5, store.Atoms.GetAtomId("<http://ex/alice>"));
            Assert.Equal(6, store.Atoms.GetAtomId("<http://ex/bob>"));
            Assert.Equal(7, store.Atoms.GetAtomId("<http://ex/carol>"));
            Assert.Equal(8, store.Atoms.GetAtomId("<http://ex/knows>"));
        }

        // Reopen without bulk mode for query: vocab persists, atom IDs stable.
        using (var store = new QuadStore(dir))
        {
            Assert.IsType<SortedAtomStore>(store.Atoms);
            Assert.Equal(8, store.Atoms.AtomCount);
            Assert.Equal(5, store.Atoms.GetAtomId("<http://ex/alice>"));
        }
    }

    [Fact]
    public void EmptyBulkLoad_ProducesEmptyStore()
    {
        var dir = Path.Combine(_testDir, "empty_bulk");
        Directory.CreateDirectory(dir);
        var schema = StoreSchema.ForProfile(StoreProfile.Reference)
            with { AtomStore = AtomStoreImplementation.Sorted };
        schema.WriteTo(dir);

        var bulkOpts = new StorageOptions { BulkMode = true, Profile = StoreProfile.Reference };
        using var store = new QuadStore(dir, null, null, bulkOpts);

        store.BeginBatch();
        store.CommitBatch();
        store.FlushToDisk();

        Assert.Equal(0, store.Atoms.AtomCount);
    }

    [Fact]
    public void MultipleBatches_AccumulateAtomsAcrossSession()
    {
        // Regression test for the production-path bug discovered during the Phase 7c
        // Round 1 step 3 gradient: at 1M Wikidata triples, the parser fires
        // BeginBatch/CommitBatch every 100K triples (chunk-flush boundary). The original
        // SortedAtomBulkBuilder lifecycle finalized the vocabulary on every CommitBatch,
        // overwriting atoms.atoms each time — so only the LAST chunk's atoms survived.
        // The Hash-backed Reference path doesn't have this problem because Intern() is
        // incremental.
        //
        // Fix: SortedAtomBulkBuilder is now session-scoped. It accumulates atoms across
        // every BeginBatch/CommitBatch and is finalized once at FlushToDisk. This test
        // exercises that lifecycle with three distinct batches, each contributing
        // distinct atoms, and verifies all atoms appear in the final store.
        var dir = Path.Combine(_testDir, "multi_batch");
        Directory.CreateDirectory(dir);
        var schema = StoreSchema.ForProfile(StoreProfile.Reference)
            with { AtomStore = AtomStoreImplementation.Sorted };
        schema.WriteTo(dir);

        var bulkOpts = new StorageOptions { BulkMode = true, Profile = StoreProfile.Reference };
        using (var store = new QuadStore(dir, null, null, bulkOpts))
        {
            // Batch 1: alice, bob, knows
            store.BeginBatch();
            store.AddCurrentBatched("<http://ex/alice>", "<http://ex/knows>", "<http://ex/bob>");
            store.CommitBatch();

            // Batch 2: carol, dave, likes — distinct atoms, different batch
            store.BeginBatch();
            store.AddCurrentBatched("<http://ex/carol>", "<http://ex/likes>", "<http://ex/dave>");
            store.CommitBatch();

            // Batch 3: eve, frank, follows — yet more distinct atoms
            store.BeginBatch();
            store.AddCurrentBatched("<http://ex/eve>", "<http://ex/follows>", "<http://ex/frank>");
            store.CommitBatch();

            // Session-end: this finalizes the bulk-builder and writes atoms.atoms.
            store.FlushToDisk();

            // 9 distinct atoms must be present (alice, bob, carol, dave, eve, frank,
            // knows, likes, follows). The pre-fix code would only show 3 — atoms from
            // the last batch.
            Assert.Equal(9, store.Atoms.AtomCount);
            Assert.NotEqual(0, store.Atoms.GetAtomId("<http://ex/alice>"));
            Assert.NotEqual(0, store.Atoms.GetAtomId("<http://ex/bob>"));
            Assert.NotEqual(0, store.Atoms.GetAtomId("<http://ex/carol>"));
            Assert.NotEqual(0, store.Atoms.GetAtomId("<http://ex/dave>"));
            Assert.NotEqual(0, store.Atoms.GetAtomId("<http://ex/eve>"));
            Assert.NotEqual(0, store.Atoms.GetAtomId("<http://ex/frank>"));
            Assert.NotEqual(0, store.Atoms.GetAtomId("<http://ex/knows>"));
            Assert.NotEqual(0, store.Atoms.GetAtomId("<http://ex/likes>"));
            Assert.NotEqual(0, store.Atoms.GetAtomId("<http://ex/follows>"));
        }

        // Reopen for query — vocab persists, all 9 atoms retrievable.
        using (var store = new QuadStore(dir))
        {
            Assert.Equal(9, store.Atoms.AtomCount);
        }
    }

    [Fact]
    public void RollbackBatch_LeavesPlaceholderIntact()
    {
        var dir = Path.Combine(_testDir, "rollback");
        Directory.CreateDirectory(dir);
        var schema = StoreSchema.ForProfile(StoreProfile.Reference)
            with { AtomStore = AtomStoreImplementation.Sorted };
        schema.WriteTo(dir);

        var bulkOpts = new StorageOptions { BulkMode = true, Profile = StoreProfile.Reference };
        using var store = new QuadStore(dir, null, null, bulkOpts);

        store.BeginBatch();
        store.AddCurrentBatched("<a>", "<b>", "<c>");
        store.AddCurrentBatched("<d>", "<e>", "<f>");
        store.RollbackBatch();

        // Placeholder still in place; no vocab written. Subsequent queries see empty store.
        Assert.Equal(0, store.Atoms.AtomCount);
    }
}
