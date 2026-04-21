using System;
using System.Collections.Generic;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// Tests for deferred secondary indexing — bulk load builds GSPO only,
/// then RebuildSecondaryIndexes populates GPOS, GOSP, TGSP, and trigram.
/// </summary>
public class DeferredIndexTests : IDisposable
{
    private readonly string _testPath;
    private QuadStore? _store;

    public DeferredIndexTests()
    {
        var tempPath = TempPath.Test("deferred-idx");
        tempPath.MarkOwnership();
        _testPath = tempPath;
    }

    public void Dispose()
    {
        _store?.Dispose();
        TempPath.SafeCleanup(_testPath);
    }

    private QuadStore CreateStore(bool bulkMode = false)
    {
        _store?.Dispose();
        var options = new StorageOptions
        {
            BulkMode = bulkMode,
            IndexInitialSizeBytes = 64L << 20,
            AtomDataInitialSizeBytes = 64L << 20,
            AtomOffsetInitialCapacity = 64L << 10,
            MinimumFreeDiskSpace = 512L << 20
        };
        _store = new QuadStore(_testPath, null, null, options);
        return _store;
    }

    private void LoadTestData(QuadStore store)
    {
        store.BeginBatch();
        store.AddCurrentBatched("<http://ex.org/alice>", "<http://ex.org/name>", "\"Alice\"");
        store.AddCurrentBatched("<http://ex.org/alice>", "<http://ex.org/age>", "\"30\"^^<http://www.w3.org/2001/XMLSchema#integer>");
        store.AddCurrentBatched("<http://ex.org/bob>", "<http://ex.org/name>", "\"Bob\"");
        store.AddCurrentBatched("<http://ex.org/bob>", "<http://ex.org/knows>", "<http://ex.org/alice>");
        store.AddCurrentBatched("<http://ex.org/carol>", "<http://ex.org/name>", "\"Carol\"");
        store.AddCurrentBatched("<http://ex.org/carol>", "<http://ex.org/knows>", "<http://ex.org/bob>");
        store.CommitBatch();
    }

    [Fact]
    public void BulkLoad_IndexState_IsPrimaryOnly()
    {
        var store = CreateStore(bulkMode: true);
        Assert.Equal(StoreIndexState.PrimaryOnly, store.IndexState);
    }

    [Fact]
    public void CognitiveMode_IndexState_IsReady()
    {
        var store = CreateStore(bulkMode: false);
        Assert.Equal(StoreIndexState.Ready, store.IndexState);
    }

    [Fact]
    public void BulkLoad_SubjectQuery_WorksBeforeRebuild()
    {
        var store = CreateStore(bulkMode: true);
        LoadTestData(store);
        store.FlushToDisk();

        store.AcquireReadLock();
        try
        {
            // Subject query uses GSPO — works in PrimaryOnly state
            int count = CountResults(store, "<http://ex.org/alice>",
                ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
            Assert.Equal(2, count);
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void BulkLoad_PredicateQuery_FallsBackToGSPO()
    {
        var store = CreateStore(bulkMode: true);
        LoadTestData(store);
        store.FlushToDisk();

        // In PrimaryOnly state, predicate-only queries fall back to GSPO.
        // GSPO can't filter by predicate when subject is unbound — returns all entries.
        // SPARQL engine filters at a higher level. Verify via SPARQL:
        var result = SparqlEngine.Query(store,
            "SELECT ?s WHERE { ?s <http://ex.org/name> ?o }");
        Assert.True(result.Success);
        Assert.NotNull(result.Rows);
        Assert.Equal(3, result.Rows!.Count); // Alice, Bob, Carol
    }

    [Fact]
    public void RebuildSecondaryIndexes_TransitionsToReady()
    {
        var store = CreateStore(bulkMode: true);
        LoadTestData(store);
        store.FlushToDisk();

        Assert.Equal(StoreIndexState.PrimaryOnly, store.IndexState);

        store.RebuildSecondaryIndexes();

        Assert.Equal(StoreIndexState.Ready, store.IndexState);
        Assert.False(store.IsBulkLoadMode);
    }

    [Fact]
    public void RebuildSecondaryIndexes_PredicateQueryUsesGPOS()
    {
        var store = CreateStore(bulkMode: true);
        LoadTestData(store);
        store.FlushToDisk();
        store.RebuildSecondaryIndexes();

        store.AcquireReadLock();
        try
        {
            // After rebuild, predicate queries use GPOS (optimal)
            int count = CountResults(store, ReadOnlySpan<char>.Empty,
                "<http://ex.org/knows>", ReadOnlySpan<char>.Empty);
            Assert.Equal(2, count); // Bob knows Alice, Carol knows Bob
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void RebuildSecondaryIndexes_ObjectQueryUsesGOSP()
    {
        var store = CreateStore(bulkMode: true);
        LoadTestData(store);
        store.FlushToDisk();
        store.RebuildSecondaryIndexes();

        store.AcquireReadLock();
        try
        {
            // After rebuild, object queries use GOSP (optimal)
            int count = CountResults(store, ReadOnlySpan<char>.Empty,
                ReadOnlySpan<char>.Empty, "<http://ex.org/alice>");
            Assert.Equal(1, count); // Bob knows Alice
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void RebuildSecondaryIndexes_MatchesEagerLoad()
    {
        // Bulk load + rebuild
        var store = CreateStore(bulkMode: true);
        LoadTestData(store);
        store.FlushToDisk();
        store.RebuildSecondaryIndexes();

        store.AcquireReadLock();
        int bulkSubject, bulkPredicate, bulkObject;
        try
        {
            bulkSubject = CountResults(store, "<http://ex.org/bob>", ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
            bulkPredicate = CountResults(store, ReadOnlySpan<char>.Empty, "<http://ex.org/name>", ReadOnlySpan<char>.Empty);
            bulkObject = CountResults(store, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty, "\"Bob\"");
        }
        finally
        {
            store.ReleaseReadLock();
        }
        store.Dispose();
        TempPath.SafeCleanup(_testPath);

        // Eager (cognitive) load
        store = CreateStore(bulkMode: false);
        _store = store;
        LoadTestData(store);

        store.AcquireReadLock();
        int eagerSubject, eagerPredicate, eagerObject;
        try
        {
            eagerSubject = CountResults(store, "<http://ex.org/bob>", ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
            eagerPredicate = CountResults(store, ReadOnlySpan<char>.Empty, "<http://ex.org/name>", ReadOnlySpan<char>.Empty);
            eagerObject = CountResults(store, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty, "\"Bob\"");
        }
        finally
        {
            store.ReleaseReadLock();
        }

        Assert.Equal(eagerSubject, bulkSubject);
        Assert.Equal(eagerPredicate, bulkPredicate);
        Assert.Equal(eagerObject, bulkObject);
    }

    [Fact]
    public void RebuildSecondaryIndexes_ProgressReporting()
    {
        var store = CreateStore(bulkMode: true);
        LoadTestData(store);
        store.FlushToDisk();

        // ADR-030 Phase 2 parallel rebuild: onProgress is called from consumer threads
        // in non-deterministic order. Use a thread-safe collection and assert the set.
        var reported = new System.Collections.Concurrent.ConcurrentBag<(string Name, long Count)>();
        store.RebuildSecondaryIndexes((name, count) => reported.Add((name, count)));

        Assert.Equal(4, reported.Count);
        var byName = reported.ToDictionary(r => r.Name, r => r.Count);
        Assert.True(byName.ContainsKey("GPOS"));
        Assert.True(byName.ContainsKey("GOSP"));
        Assert.True(byName.ContainsKey("TGSP"));
        Assert.True(byName.ContainsKey("Trigram"));

        // All B+Tree indexes should have 6 entries (6 triples loaded)
        Assert.Equal(6, byName["GPOS"]);
        Assert.Equal(6, byName["GOSP"]);
        Assert.Equal(6, byName["TGSP"]);
    }

    [Fact]
    public void StoreState_PersistsAcrossReopen()
    {
        var store = CreateStore(bulkMode: true);
        LoadTestData(store);
        store.FlushToDisk();
        Assert.Equal(StoreIndexState.PrimaryOnly, store.IndexState);
        store.Dispose();

        // Reopen without bulk mode — state should be read from file
        _store = new QuadStore(_testPath, null, null, new StorageOptions
        {
            IndexInitialSizeBytes = 64L << 20,
            AtomDataInitialSizeBytes = 64L << 20,
            AtomOffsetInitialCapacity = 64L << 10,
            MinimumFreeDiskSpace = 512L << 20
        });
        Assert.Equal(StoreIndexState.PrimaryOnly, _store.IndexState);
    }

    [Fact]
    public void StoreState_ReadyAfterRebuild_PersistsAcrossReopen()
    {
        var store = CreateStore(bulkMode: true);
        LoadTestData(store);
        store.FlushToDisk();
        store.RebuildSecondaryIndexes();
        Assert.Equal(StoreIndexState.Ready, store.IndexState);
        store.Dispose();

        // Reopen — should be Ready
        _store = new QuadStore(_testPath, null, null, new StorageOptions
        {
            IndexInitialSizeBytes = 64L << 20,
            AtomDataInitialSizeBytes = 64L << 20,
            AtomOffsetInitialCapacity = 64L << 10,
            MinimumFreeDiskSpace = 512L << 20
        });
        Assert.Equal(StoreIndexState.Ready, _store.IndexState);
    }

    private static int CountResults(QuadStore store,
        ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> @object)
    {
        int count = 0;
        var results = store.QueryCurrent(subject, predicate, @object);
        try
        {
            while (results.MoveNext())
                count++;
        }
        finally
        {
            results.Dispose();
        }
        return count;
    }
}
