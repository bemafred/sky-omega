using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// End-to-end bulk-load against Reference-profile QuadStore via RdfEngine — the same
/// path the CLI takes. Verifies ADR-029 Phase 2 integration: the batch API dispatches
/// on profile, RdfEngine is unchanged, and the result is a queryable Reference store.
/// </summary>
public class ReferenceBulkLoadE2ETests : IDisposable
{
    private readonly string _testDir;

    public ReferenceBulkLoadE2ETests()
    {
        var tempPath = TempPath.Test("refbulk");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    private static Stream StreamFrom(string text) =>
        new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task LoadStreamingAsync_NTriples_PopulatesBothIndexes()
    {
        var dir = Path.Combine(_testDir, "nt_basic");
        Directory.CreateDirectory(dir);

        const string nt = """
        <http://ex.org/alice> <http://ex.org/knows> <http://ex.org/bob> .
        <http://ex.org/alice> <http://ex.org/knows> <http://ex.org/carol> .
        <http://ex.org/bob> <http://ex.org/knows> <http://ex.org/alice> .
        <http://ex.org/alice> <http://ex.org/age> "42" .
        """;

        using (var store = new QuadStore(dir, null, null,
            new StorageOptions { Profile = StoreProfile.Reference }))
        {
            Assert.Equal(StoreProfile.Reference, store.Schema.Profile);

            var count = await RdfEngine.LoadStreamingAsync(store, StreamFrom(nt), RdfFormat.NTriples);
            Assert.Equal(4, count);
            Assert.Equal(4, store.GetStatistics().QuadCount);
        }
    }

    [Fact]
    public async Task LoadStreamingAsync_NQuads_RespectsNamedGraphs()
    {
        var dir = Path.Combine(_testDir, "nq_graphs");
        Directory.CreateDirectory(dir);

        const string nq = """
        <http://ex.org/alice> <http://ex.org/knows> <http://ex.org/bob> <http://ex.org/graphA> .
        <http://ex.org/alice> <http://ex.org/knows> <http://ex.org/carol> <http://ex.org/graphB> .
        <http://ex.org/alice> <http://ex.org/knows> <http://ex.org/bob> <http://ex.org/graphB> .
        """;

        using var store = new QuadStore(dir, null, null,
            new StorageOptions { Profile = StoreProfile.Reference });

        var count = await RdfEngine.LoadStreamingAsync(store, StreamFrom(nq), RdfFormat.NQuads);
        Assert.Equal(3, count);
        Assert.Equal(3, store.GetStatistics().QuadCount);
    }

    [Fact]
    public async Task LoadStreamingAsync_Duplicate_IsDedupedByRdfSemantics()
    {
        var dir = Path.Combine(_testDir, "nt_dupe");
        Directory.CreateDirectory(dir);

        // Same triple three times — Reference uniqueness invariant (ADR-029 Decision 7)
        // reduces this to a single stored quad.
        const string nt = """
        <http://ex.org/alice> <http://ex.org/knows> <http://ex.org/bob> .
        <http://ex.org/alice> <http://ex.org/knows> <http://ex.org/bob> .
        <http://ex.org/alice> <http://ex.org/knows> <http://ex.org/bob> .
        """;

        using var store = new QuadStore(dir, null, null,
            new StorageOptions { Profile = StoreProfile.Reference });

        await RdfEngine.LoadStreamingAsync(store, StreamFrom(nt), RdfFormat.NTriples);
        Assert.Equal(1, store.GetStatistics().QuadCount);
    }

    [Fact]
    public async Task LoadStreaming_ThenReopen_PersistenceHolds()
    {
        var dir = Path.Combine(_testDir, "nt_persist");
        Directory.CreateDirectory(dir);

        // Create + load
        const string nt = """
        <http://ex.org/s1> <http://ex.org/p> <http://ex.org/o1> .
        <http://ex.org/s2> <http://ex.org/p> <http://ex.org/o2> .
        <http://ex.org/s3> <http://ex.org/p> <http://ex.org/o3> .
        """;

        using (var store = new QuadStore(dir, null, null,
            new StorageOptions { Profile = StoreProfile.Reference }))
        {
            await RdfEngine.LoadStreamingAsync(store, StreamFrom(nt), RdfFormat.NTriples);
            Assert.Equal(3, store.GetStatistics().QuadCount);
        }

        // Reopen — persisted Reference schema wins, data is queryable.
        using (var reopened = new QuadStore(dir))
        {
            Assert.Equal(StoreProfile.Reference, reopened.Schema.Profile);
            Assert.Equal(3, reopened.GetStatistics().QuadCount);
        }
    }

    [Fact]
    public async Task RebuildAfterBulkLoad_IsSilentNoOp()
    {
        // The common CLI pipeline is bulk-load → rebuild-indexes. Reference's rebuild
        // is a no-op so the pipeline works uniformly across profiles.
        var dir = Path.Combine(_testDir, "nt_pipeline");
        Directory.CreateDirectory(dir);

        const string nt = "<http://ex.org/a> <http://ex.org/b> <http://ex.org/c> .";
        using var store = new QuadStore(dir, null, null,
            new StorageOptions { Profile = StoreProfile.Reference, BulkMode = true });

        await RdfEngine.LoadStreamingAsync(store, StreamFrom(nt), RdfFormat.NTriples);
        store.RebuildSecondaryIndexes(); // no-op, no exception
        store.FlushToDisk();

        Assert.Equal(1, store.GetStatistics().QuadCount);
    }
}
