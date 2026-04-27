using System;
using System.IO;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-034 Phase 1B-3: QuadStore profile dispatch on <see cref="StoreSchema.AtomStore"/>.
/// Existing stores written before the field was introduced default to <see cref="AtomStoreImplementation.Hash"/>;
/// new stores opt into <see cref="AtomStoreImplementation.Sorted"/> via the schema flag,
/// and the open path constructs the matching <see cref="IAtomStore"/> implementation.
/// </summary>
public class AtomStoreProfileDispatchTests : IDisposable
{
    private readonly string _testDir;

    public AtomStoreProfileDispatchTests()
    {
        var tempPath = TempPath.Test("atom_dispatch");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    [Fact]
    public void DefaultStoreOpens_AsHashAtomStore()
    {
        // No schema field present -> default Hash. Existing wiki-21b-ref stores rely
        // on this for backward compat.
        var dir = Path.Combine(_testDir, "default");
        Directory.CreateDirectory(dir);
        using var store = new QuadStore(dir);

        store.AddCurrent("subj", "pred", "obj");
        var (count, _, _) = store.Atoms.GetStatistics();
        Assert.True(count > 0);
        Assert.IsType<HashAtomStore>(store.Atoms);
    }

    [Fact]
    public void ExplicitlyHashSchema_OpensAsHashAtomStore()
    {
        var dir = Path.Combine(_testDir, "hash_explicit");
        Directory.CreateDirectory(dir);
        // Pre-write a schema with explicit Hash atomStore.
        var schema = StoreSchema.ForProfile(StoreProfile.Cognitive)
            with { AtomStore = AtomStoreImplementation.Hash };
        schema.WriteTo(dir);

        using var store = new QuadStore(dir);
        store.AddCurrent("a", "b", "c");
        Assert.IsType<HashAtomStore>(store.Atoms);
    }

    [Fact]
    public void SortedSchemaWithBuiltAtoms_OpensAsSortedAtomStore()
    {
        // Manually construct a SortedAtomStore-backed store: build atom files via the
        // builder, write the schema with AtomStore=Sorted, open via QuadStore.
        var dir = Path.Combine(_testDir, "sorted");
        Directory.CreateDirectory(dir);
        var atomPath = Path.Combine(dir, "atoms");
        SortedAtomStoreBuilder.Build(atomPath, new[] { "alpha", "beta", "gamma" });

        var schema = StoreSchema.ForProfile(StoreProfile.Reference)
            with { AtomStore = AtomStoreImplementation.Sorted };
        schema.WriteTo(dir);

        using var store = new QuadStore(dir);
        Assert.IsType<SortedAtomStore>(store.Atoms);
        Assert.Equal(3, store.Atoms.AtomCount);

        // Query the existing atoms via the polymorphic IAtomStore surface.
        Assert.Equal(1, store.Atoms.GetAtomId("alpha"));
        Assert.Equal(2, store.Atoms.GetAtomId("beta"));
        Assert.Equal(3, store.Atoms.GetAtomId("gamma"));
    }

    [Fact]
    public void SortedSchema_SecondBulkLoadAfterPopulated_Throws()
    {
        // ADR-034 Decision 7: SortedAtomStore-backed stores accept exactly ONE bulk-load —
        // the build that populates the vocab files. A second bulk-load against a populated
        // Sorted store violates the single-bulk-load contract. Phase 1B-5b implementation:
        // the gate fires only when AtomCount > 0.
        var dir = Path.Combine(_testDir, "sorted_second_bulk_throws");
        Directory.CreateDirectory(dir);
        var atomPath = Path.Combine(dir, "atoms");
        // Pre-populate with non-empty vocab.
        SortedAtomStoreBuilder.Build(atomPath, new[] { "x", "y", "z" });
        var schema = StoreSchema.ForProfile(StoreProfile.Reference)
            with { AtomStore = AtomStoreImplementation.Sorted };
        schema.WriteTo(dir);

        var bulkOpts = new StorageOptions { BulkMode = true };
        Assert.Throws<ProfileCapabilityException>(() => new QuadStore(dir, null, null, bulkOpts));
    }

    [Fact]
    public void SortedSchema_FirstBulkLoadOnEmptyStore_Allowed()
    {
        // ADR-034 Phase 1B-5b: the FIRST bulk-load against a fresh Sorted store is the
        // build that populates the vocab files. Open with BulkMode=true must succeed.
        var dir = Path.Combine(_testDir, "sorted_first_bulk_ok");
        Directory.CreateDirectory(dir);
        var schema = StoreSchema.ForProfile(StoreProfile.Reference)
            with { AtomStore = AtomStoreImplementation.Sorted };
        schema.WriteTo(dir);

        var bulkOpts = new StorageOptions { BulkMode = true, Profile = StoreProfile.Reference };
        using var store = new QuadStore(dir, null, null, bulkOpts);
        Assert.IsType<SortedAtomStore>(store.Atoms);
        Assert.Equal(0, store.Atoms.AtomCount);
    }

    [Fact]
    public void Schema_RoundTripsAtomStoreField()
    {
        var schema = StoreSchema.ForProfile(StoreProfile.Reference)
            with { AtomStore = AtomStoreImplementation.Sorted };
        var json = schema.ToJson();
        Assert.Contains("\"atomStore\": \"Sorted\"", json);

        var roundTripped = StoreSchema.FromJson(json);
        Assert.Equal(AtomStoreImplementation.Sorted, roundTripped.AtomStore);
    }

    [Fact]
    public void Schema_LegacyJsonWithoutField_DefaultsToHash()
    {
        // Schema files written before the atomStore field existed must continue to load
        // as Hash. Mercury's open path can't refuse legacy stores; ADR-034 explicitly
        // makes the field additive.
        var legacyJson = """
            {
              "profile": "Reference",
              "indexes": ["gspo", "gpos"],
              "hasGraph": true,
              "hasTemporal": false,
              "hasVersioning": false,
              "keyLayoutVersion": 1
            }
            """;
        var schema = StoreSchema.FromJson(legacyJson);
        Assert.Equal(AtomStoreImplementation.Hash, schema.AtomStore);
    }
}
