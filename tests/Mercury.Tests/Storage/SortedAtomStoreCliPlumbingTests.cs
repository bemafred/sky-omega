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
/// ADR-034 Phase 1B-5c: the CLI plumbing path. <c>mercury --bulk-load --profile Reference
/// --atom-store Sorted</c> constructs a <see cref="StorageOptions"/> with
/// <see cref="StorageOptions.AtomStore"/> = <see cref="AtomStoreImplementation.Sorted"/>,
/// hands it to <see cref="QuadStore"/>, which writes the schema with the requested
/// AtomStore field and routes the bulk-load through <see cref="SortedAtomBulkBuilder"/>.
/// These tests exercise that path via <see cref="RdfEngine.LoadStreamingAsync"/> — the
/// same call the CLI makes after argument parsing.
/// </summary>
public class SortedAtomStoreCliPlumbingTests : IDisposable
{
    private readonly string _testDir;

    public SortedAtomStoreCliPlumbingTests()
    {
        var tempPath = TempPath.Test("sorted_cli");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    private static Stream StreamFrom(string text) =>
        new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task FreshStore_ReferenceSorted_BulkLoadEndToEnd()
    {
        var dir = Path.Combine(_testDir, "fresh");
        // No schema file pre-written — QuadStore must synthesize it from StorageOptions.

        const string nt = """
        <http://ex.org/alice> <http://ex.org/knows> <http://ex.org/bob> .
        <http://ex.org/alice> <http://ex.org/knows> <http://ex.org/carol> .
        <http://ex.org/bob> <http://ex.org/knows> <http://ex.org/alice> .
        <http://ex.org/alice> <http://ex.org/age> "42" .
        """;

        var bulkOpts = new StorageOptions
        {
            Profile = StoreProfile.Reference,
            AtomStore = AtomStoreImplementation.Sorted,
            BulkMode = true,
        };

        long count;
        using (var store = new QuadStore(dir, null, null, bulkOpts))
        {
            // Schema is synthesized from StorageOptions on first open.
            Assert.Equal(StoreProfile.Reference, store.Schema.Profile);
            Assert.Equal(AtomStoreImplementation.Sorted, store.Schema.AtomStore);

            count = await RdfEngine.LoadStreamingAsync(store, StreamFrom(nt), RdfFormat.NTriples);
            // LoadStreamingAsync calls FlushToDisk internally when IsBulkLoadMode is true,
            // so vocab is built and the placeholder atom store has been replaced.
            Assert.IsType<SortedAtomStore>(store.Atoms);
        }

        Assert.Equal(4, count);

        // Schema must persist on disk with AtomStore=Sorted so subsequent opens dispatch
        // to SortedAtomStore even without re-passing StorageOptions.
        var persisted = StoreSchema.ReadFrom(dir);
        Assert.NotNull(persisted);
        Assert.Equal(AtomStoreImplementation.Sorted, persisted!.AtomStore);

        // Reopen without StorageOptions: the persisted schema is the source of truth.
        using (var store = new QuadStore(dir))
        {
            Assert.IsType<SortedAtomStore>(store.Atoms);
            // 4 distinct IRIs (alice, bob, carol, knows, age) + 1 literal ("42") = 6 atoms,
            // wait — alice/bob/carol are 3, knows is 1, age is 1, "42" is 1 → 6. Recount:
            // <http://ex.org/alice>, <http://ex.org/bob>, <http://ex.org/carol>,
            // <http://ex.org/knows>, <http://ex.org/age>, "42" → 6 atoms.
            Assert.Equal(6, store.Atoms.AtomCount);
        }
    }

    [Fact]
    public void FreshStore_DefaultAtomStore_OpensAsHash()
    {
        // Backward compat: omitting AtomStore (or default Hash) on a fresh store yields
        // a HashAtomStore-backed Cognitive store, exactly as before ADR-034 existed.
        var dir = Path.Combine(_testDir, "default");

        using var store = new QuadStore(dir, null, null, new StorageOptions());
        Assert.IsType<HashAtomStore>(store.Atoms);

        var persisted = StoreSchema.ReadFrom(dir);
        Assert.NotNull(persisted);
        Assert.Equal(AtomStoreImplementation.Hash, persisted!.AtomStore);
    }

    [Fact]
    public void FreshStore_ExplicitHash_PersistsHashInSchema()
    {
        var dir = Path.Combine(_testDir, "explicit_hash");
        var opts = new StorageOptions { AtomStore = AtomStoreImplementation.Hash };

        using var store = new QuadStore(dir, null, null, opts);
        Assert.IsType<HashAtomStore>(store.Atoms);

        var persisted = StoreSchema.ReadFrom(dir);
        Assert.NotNull(persisted);
        Assert.Equal(AtomStoreImplementation.Hash, persisted!.AtomStore);
    }

    [Fact]
    public async Task FreshStore_ReferenceSorted_QueriesAfterReopen()
    {
        // Prove the data is actually queryable end-to-end through the IAtomStore interface
        // after a Sorted-backed bulk load, not just the atom-count check.
        var dir = Path.Combine(_testDir, "queryable");

        const string nt = """
        <http://ex.org/s1> <http://ex.org/p> <http://ex.org/o1> .
        <http://ex.org/s2> <http://ex.org/p> <http://ex.org/o2> .
        <http://ex.org/s3> <http://ex.org/p> <http://ex.org/o3> .
        """;

        using (var store = new QuadStore(dir, null, null, new StorageOptions
        {
            Profile = StoreProfile.Reference,
            AtomStore = AtomStoreImplementation.Sorted,
            BulkMode = true,
        }))
        {
            await RdfEngine.LoadStreamingAsync(store, StreamFrom(nt), RdfFormat.NTriples);
        }

        using (var store = new QuadStore(dir))
        {
            Assert.IsType<SortedAtomStore>(store.Atoms);
            // Sorted byte order: <http://ex.org/o1>, /o2, /o3, /p, /s1, /s2, /s3.
            // Atom IDs assigned 1..7 in that order.
            Assert.Equal(1, store.Atoms.GetAtomId("<http://ex.org/o1>"));
            Assert.Equal(4, store.Atoms.GetAtomId("<http://ex.org/p>"));
            Assert.Equal(5, store.Atoms.GetAtomId("<http://ex.org/s1>"));
            Assert.Equal(7, store.Atoms.GetAtomId("<http://ex.org/s3>"));
        }
    }
}
