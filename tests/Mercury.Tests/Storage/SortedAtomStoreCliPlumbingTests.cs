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
/// ADR-034 profile-derived AtomStore dispatch. <c>mercury --bulk-load --profile Reference</c>
/// constructs <see cref="StorageOptions"/> with <see cref="StoreProfile.Reference"/>;
/// <see cref="StoreSchema.ForProfile"/> derives <see cref="AtomStoreImplementation.Sorted"/>
/// from the profile and persists it; <see cref="QuadStore"/> dispatches construction to
/// <see cref="SortedAtomStore"/>. There is no separate <c>--atom-store</c> override —
/// profile is the single source of truth, since Reference+Hash and Cognitive+Sorted are
/// both invalid combinations.
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
    public async Task FreshStore_ReferenceProfile_BulkLoadDispatchesToSorted()
    {
        var dir = Path.Combine(_testDir, "fresh");

        const string nt = """
        <http://ex.org/alice> <http://ex.org/knows> <http://ex.org/bob> .
        <http://ex.org/alice> <http://ex.org/knows> <http://ex.org/carol> .
        <http://ex.org/bob> <http://ex.org/knows> <http://ex.org/alice> .
        <http://ex.org/alice> <http://ex.org/age> "42" .
        """;

        var bulkOpts = new StorageOptions
        {
            Profile = StoreProfile.Reference,
            BulkMode = true,
        };

        long count;
        using (var store = new QuadStore(dir, null, null, bulkOpts))
        {
            // Profile=Reference must derive AtomStore=Sorted via StoreSchema.ForProfile.
            Assert.Equal(StoreProfile.Reference, store.Schema.Profile);
            Assert.Equal(AtomStoreImplementation.Sorted, store.Schema.AtomStore);

            count = await RdfEngine.LoadStreamingAsync(store, StreamFrom(nt), RdfFormat.NTriples);
            Assert.IsType<SortedAtomStore>(store.Atoms);
        }

        Assert.Equal(4, count);

        var persisted = StoreSchema.ReadFrom(dir);
        Assert.NotNull(persisted);
        Assert.Equal(AtomStoreImplementation.Sorted, persisted!.AtomStore);

        using (var store = new QuadStore(dir))
        {
            Assert.IsType<SortedAtomStore>(store.Atoms);
            // 6 atoms: alice, bob, carol, knows, age, "42"
            Assert.Equal(6, store.Atoms.AtomCount);
        }
    }

    [Fact]
    public void FreshStore_CognitiveProfile_DispatchesToHash()
    {
        var dir = Path.Combine(_testDir, "cognitive");

        using var store = new QuadStore(dir, null, null, new StorageOptions { Profile = StoreProfile.Cognitive });
        Assert.IsType<HashAtomStore>(store.Atoms);

        var persisted = StoreSchema.ReadFrom(dir);
        Assert.NotNull(persisted);
        Assert.Equal(AtomStoreImplementation.Hash, persisted!.AtomStore);
    }

    [Fact]
    public void FreshStore_DefaultOptions_DispatchesToHash()
    {
        // Default profile is Cognitive, which derives Hash. No way to construct an
        // illegal Cognitive+Sorted combination from outside.
        var dir = Path.Combine(_testDir, "default");

        using var store = new QuadStore(dir, null, null, new StorageOptions());
        Assert.IsType<HashAtomStore>(store.Atoms);

        var persisted = StoreSchema.ReadFrom(dir);
        Assert.NotNull(persisted);
        Assert.Equal(AtomStoreImplementation.Hash, persisted!.AtomStore);
    }

    [Fact]
    public async Task FreshStore_ReferenceProfile_QueriesAfterReopen()
    {
        var dir = Path.Combine(_testDir, "queryable");

        const string nt = """
        <http://ex.org/s1> <http://ex.org/p> <http://ex.org/o1> .
        <http://ex.org/s2> <http://ex.org/p> <http://ex.org/o2> .
        <http://ex.org/s3> <http://ex.org/p> <http://ex.org/o3> .
        """;

        using (var store = new QuadStore(dir, null, null, new StorageOptions
        {
            Profile = StoreProfile.Reference,
            BulkMode = true,
        }))
        {
            await RdfEngine.LoadStreamingAsync(store, StreamFrom(nt), RdfFormat.NTriples);
        }

        using (var store = new QuadStore(dir))
        {
            Assert.IsType<SortedAtomStore>(store.Atoms);
            // Sorted byte order: <http://ex.org/o1>, /o2, /o3, /p, /s1, /s2, /s3.
            Assert.Equal(1, store.Atoms.GetAtomId("<http://ex.org/o1>"));
            Assert.Equal(4, store.Atoms.GetAtomId("<http://ex.org/p>"));
            Assert.Equal(5, store.Atoms.GetAtomId("<http://ex.org/s1>"));
            Assert.Equal(7, store.Atoms.GetAtomId("<http://ex.org/s3>"));
        }
    }
}
