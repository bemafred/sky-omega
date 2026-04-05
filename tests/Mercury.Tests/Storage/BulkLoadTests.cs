using System;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// Tests for bulk load mode — WAL without fsync, GSPO-only indexing.
/// Verifies correctness equivalence with the cognitive (normal) write path.
/// </summary>
public class BulkLoadTests : IDisposable
{
    private readonly string _testPath;
    private QuadStore? _store;

    public BulkLoadTests()
    {
        var tempPath = TempPath.Test("bulk-load");
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

    [Fact]
    public void BulkMode_IsReportedCorrectly()
    {
        var store = CreateStore(bulkMode: true);
        Assert.True(store.IsBulkLoadMode);
    }

    [Fact]
    public void CognitiveMode_IsDefault()
    {
        var store = CreateStore(bulkMode: false);
        Assert.False(store.IsBulkLoadMode);
    }

    [Fact]
    public void BulkLoad_SingleTriple_QueryableViaPrimaryIndex()
    {
        var store = CreateStore(bulkMode: true);

        store.BeginBatch();
        store.AddCurrentBatched("<http://ex.org/s>", "<http://ex.org/p>", "<http://ex.org/o>");
        store.CommitBatch();
        store.FlushToDisk();

        store.AcquireReadLock();
        try
        {
            // Subject query uses GSPO (primary) — must work in bulk mode
            var results = store.QueryCurrent("<http://ex.org/s>", ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
            try
            {
                Assert.True(results.MoveNext());
                Assert.Equal("<http://ex.org/s>", results.Current.Subject.ToString());
                Assert.Equal("<http://ex.org/p>", results.Current.Predicate.ToString());
                Assert.Equal("<http://ex.org/o>", results.Current.Object.ToString());
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void BulkLoad_ChunkedBatches_AllTriplesQueryable()
    {
        var store = CreateStore(bulkMode: true);
        const int chunkSize = 100;
        const int chunks = 10;

        // Load 1,000 triples in 10 chunks of 100
        for (int chunk = 0; chunk < chunks; chunk++)
        {
            store.BeginBatch();
            for (int i = 0; i < chunkSize; i++)
            {
                var id = chunk * chunkSize + i;
                store.AddCurrentBatched(
                    $"<http://ex.org/s{id}>",
                    "<http://ex.org/p>",
                    $"\"value-{id}\"");
            }
            store.CommitBatch();
        }
        store.FlushToDisk();

        // Verify all 1,000 triples are queryable via GSPO (subject scan)
        store.AcquireReadLock();
        try
        {
            int count = 0;
            // Query each subject individually — GSPO handles subject lookups
            for (int i = 0; i < chunkSize * chunks; i++)
            {
                var results = store.QueryCurrent(
                    $"<http://ex.org/s{i}>", ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
                try
                {
                    Assert.True(results.MoveNext(), $"Triple s{i} not found");
                    count++;
                }
                finally
                {
                    results.Dispose();
                }
            }
            Assert.Equal(chunkSize * chunks, count);
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void BulkLoad_MatchesCognitivePath_SameQueryResults()
    {
        // Load same data via both paths, verify identical query results
        var triples = new (string s, string p, string o)[]
        {
            ("<http://ex.org/alice>", "<http://ex.org/name>", "\"Alice\""),
            ("<http://ex.org/alice>", "<http://ex.org/age>", "\"30\"^^<http://www.w3.org/2001/XMLSchema#integer>"),
            ("<http://ex.org/bob>", "<http://ex.org/name>", "\"Bob\""),
            ("<http://ex.org/bob>", "<http://ex.org/knows>", "<http://ex.org/alice>"),
        };

        // Bulk load path
        var store = CreateStore(bulkMode: true);
        store.BeginBatch();
        foreach (var (s, p, o) in triples)
            store.AddCurrentBatched(s, p, o);
        store.CommitBatch();
        store.FlushToDisk();

        store.AcquireReadLock();
        int bulkCount;
        try
        {
            bulkCount = CountResults(store, "<http://ex.org/alice>",
                ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
        }
        finally
        {
            store.ReleaseReadLock();
        }
        store.Dispose();
        TempPath.SafeCleanup(_testPath);

        // Cognitive path (same data)
        store = CreateStore(bulkMode: false);
        _store = store;
        store.BeginBatch();
        foreach (var (s, p, o) in triples)
            store.AddCurrentBatched(s, p, o);
        store.CommitBatch();

        store.AcquireReadLock();
        int cognitiveCount;
        try
        {
            cognitiveCount = CountResults(store, "<http://ex.org/alice>",
                ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
        }
        finally
        {
            store.ReleaseReadLock();
        }

        Assert.Equal(cognitiveCount, bulkCount);
        Assert.Equal(2, bulkCount); // Alice has 2 triples
    }

    [Fact]
    public void BulkLoad_BatchBufferClearedBetweenChunks()
    {
        var store = CreateStore(bulkMode: true);

        // First chunk
        store.BeginBatch();
        for (int i = 0; i < 500; i++)
            store.AddCurrentBatched($"<http://ex.org/s{i}>", "<http://ex.org/p>", $"\"v{i}\"");
        store.CommitBatch();

        // Second chunk — if buffer wasn't cleared, store would have duplicates
        store.BeginBatch();
        for (int i = 500; i < 1000; i++)
            store.AddCurrentBatched($"<http://ex.org/s{i}>", "<http://ex.org/p>", $"\"v{i}\"");
        store.CommitBatch();
        store.FlushToDisk();

        // Verify all 1,000 triples exist — query by subject (GSPO)
        store.AcquireReadLock();
        try
        {
            // Spot-check first, last, and boundary triples
            Assert.Equal(1, CountResults(store, "<http://ex.org/s0>", ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty));
            Assert.Equal(1, CountResults(store, "<http://ex.org/s499>", ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty));
            Assert.Equal(1, CountResults(store, "<http://ex.org/s500>", ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty));
            Assert.Equal(1, CountResults(store, "<http://ex.org/s999>", ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty));
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void BulkLoad_FlushToDisk_MakesDataDurable()
    {
        var storePath = _testPath;

        // Load data and flush
        var store = CreateStore(bulkMode: true);
        store.BeginBatch();
        store.AddCurrentBatched("<http://ex.org/s>", "<http://ex.org/p>", "\"durable\"");
        store.CommitBatch();
        store.FlushToDisk();
        store.Dispose();

        // Reopen in cognitive mode — data should survive
        _store = new QuadStore(storePath);
        _store.AcquireReadLock();
        try
        {
            var results = _store.QueryCurrent("<http://ex.org/s>", "<http://ex.org/p>", ReadOnlySpan<char>.Empty);
            try
            {
                Assert.True(results.MoveNext());
                Assert.Equal("\"durable\"", results.Current.Object.ToString());
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            _store.ReleaseReadLock();
        }
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
