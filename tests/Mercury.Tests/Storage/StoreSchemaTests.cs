using System;
using System.IO;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// Tests for ADR-029 store-schema.json: round-trip, validation, and QuadStore integration.
/// </summary>
public class StoreSchemaTests : IDisposable
{
    private readonly string _testDir;

    public StoreSchemaTests()
    {
        var tempPath = TempPath.Test("schema");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose()
    {
        TempPath.SafeCleanup(_testDir);
    }

    #region Canonical profiles

    [Theory]
    [InlineData(StoreProfile.Cognitive, true, true, true, 4)]
    [InlineData(StoreProfile.Graph, true, false, true, 4)]
    [InlineData(StoreProfile.Reference, true, false, false, 2)]
    [InlineData(StoreProfile.Minimal, false, false, false, 1)]
    public void ForProfile_ReturnsCanonicalShape(
        StoreProfile profile, bool expectGraph, bool expectTemporal, bool expectVersioning, int expectedIndexCount)
    {
        var schema = StoreSchema.ForProfile(profile);

        Assert.Equal(profile, schema.Profile);
        Assert.Equal(expectGraph, schema.HasGraph);
        Assert.Equal(expectTemporal, schema.HasTemporal);
        Assert.Equal(expectVersioning, schema.HasVersioning);
        Assert.Equal(expectedIndexCount, schema.Indexes.Count);
        Assert.Equal(StoreSchema.CurrentKeyLayoutVersion, schema.KeyLayoutVersion);
    }

    [Fact]
    public void ForProfile_UnknownProfile_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StoreSchema.ForProfile((StoreProfile)999));
    }

    #endregion

    #region JSON round-trip

    [Theory]
    [InlineData(StoreProfile.Cognitive)]
    [InlineData(StoreProfile.Graph)]
    [InlineData(StoreProfile.Reference)]
    [InlineData(StoreProfile.Minimal)]
    public void Json_RoundTrip_PreservesAllFields(StoreProfile profile)
    {
        var original = StoreSchema.ForProfile(profile);

        var json = original.ToJson();
        var parsed = StoreSchema.FromJson(json);

        Assert.Equal(original.Profile, parsed.Profile);
        Assert.Equal(original.HasGraph, parsed.HasGraph);
        Assert.Equal(original.HasTemporal, parsed.HasTemporal);
        Assert.Equal(original.HasVersioning, parsed.HasVersioning);
        Assert.Equal(original.KeyLayoutVersion, parsed.KeyLayoutVersion);
        Assert.Equal(original.Indexes, parsed.Indexes);
    }

    [Fact]
    public void Json_SameInput_ProducesIdenticalBytes()
    {
        // Canonical serialization: two calls for the same schema must produce byte-equal output
        // so store-schema.json content-hashes are stable.
        var a = StoreSchema.ForProfile(StoreProfile.Reference).ToJson();
        var b = StoreSchema.ForProfile(StoreProfile.Reference).ToJson();
        Assert.Equal(a, b);
    }

    [Fact]
    public void FromJson_Malformed_ThrowsInvalidStoreSchemaException()
    {
        Assert.Throws<InvalidStoreSchemaException>(() => StoreSchema.FromJson("{ not json"));
    }

    [Fact]
    public void FromJson_UnknownProfile_ThrowsWithBuildKnownList()
    {
        var json = """
        {
          "profile": "Quantum",
          "indexes": ["gspo"],
          "hasGraph": false,
          "hasTemporal": false,
          "hasVersioning": false,
          "keyLayoutVersion": 1
        }
        """;

        var ex = Assert.Throws<InvalidStoreSchemaException>(() => StoreSchema.FromJson(json));
        Assert.Contains("Quantum", ex.Message);
        Assert.Contains("Cognitive", ex.Message);
    }

    [Fact]
    public void FromJson_FutureKeyLayoutVersion_RejectsWithClearMessage()
    {
        var json = """
        {
          "profile": "Cognitive",
          "indexes": ["gspo", "gpos", "gosp", "tgsp"],
          "hasGraph": true,
          "hasTemporal": true,
          "hasVersioning": true,
          "keyLayoutVersion": 99
        }
        """;

        var ex = Assert.Throws<InvalidStoreSchemaException>(() => StoreSchema.FromJson(json));
        Assert.Contains("99", ex.Message);
        Assert.Contains("Upgrade Mercury", ex.Message);
    }

    [Fact]
    public void FromJson_MissingRequiredField_Throws()
    {
        var json = """
        {
          "profile": "Cognitive",
          "indexes": ["gspo"],
          "hasGraph": true,
          "hasTemporal": true,
          "hasVersioning": true
        }
        """;

        var ex = Assert.Throws<InvalidStoreSchemaException>(() => StoreSchema.FromJson(json));
        Assert.Contains("keyLayoutVersion", ex.Message);
    }

    [Fact]
    public void FromJson_UnknownFutureFields_Tolerated()
    {
        // Forward-compat: unknown fields in a schema with a known KeyLayoutVersion are ignored.
        var json = """
        {
          "profile": "Cognitive",
          "indexes": ["gspo", "gpos", "gosp", "tgsp"],
          "hasGraph": true,
          "hasTemporal": true,
          "hasVersioning": true,
          "keyLayoutVersion": 1,
          "futureOpaqueField": "ignored-by-design"
        }
        """;

        var schema = StoreSchema.FromJson(json);
        Assert.Equal(StoreProfile.Cognitive, schema.Profile);
    }

    #endregion

    #region File I/O

    [Fact]
    public void WriteThenRead_FromDirectory_RoundTrip()
    {
        Directory.CreateDirectory(_testDir);
        var original = StoreSchema.ForProfile(StoreProfile.Reference);

        original.WriteTo(_testDir);
        var read = StoreSchema.ReadFrom(_testDir);

        Assert.NotNull(read);
        Assert.Equal(original.Profile, read!.Profile);
        Assert.True(File.Exists(Path.Combine(_testDir, StoreSchema.FileName)));
    }

    [Fact]
    public void ReadFrom_MissingFile_ReturnsNull()
    {
        Directory.CreateDirectory(_testDir);
        var read = StoreSchema.ReadFrom(_testDir);
        Assert.Null(read);
    }

    #endregion

    #region QuadStore integration

    [Fact]
    public void QuadStore_BrandNewStore_WritesSchemaForRequestedProfile()
    {
        Directory.CreateDirectory(_testDir);
        var options = new StorageOptions { Profile = StoreProfile.Reference };

        using var store = new QuadStore(_testDir, null, null, options);

        Assert.Equal(StoreProfile.Reference, store.Schema.Profile);
        var persisted = StoreSchema.ReadFrom(_testDir);
        Assert.NotNull(persisted);
        Assert.Equal(StoreProfile.Reference, persisted!.Profile);
    }

    [Fact]
    public void QuadStore_LegacyStoreNoSchema_BackfillsCognitive()
    {
        // Simulate a pre-ADR-029 store: create one, then delete its schema file.
        Directory.CreateDirectory(_testDir);
        using (var store = new QuadStore(_testDir))
        {
            // normal create — Cognitive is written
        }
        File.Delete(Path.Combine(_testDir, StoreSchema.FileName));
        Assert.False(File.Exists(Path.Combine(_testDir, StoreSchema.FileName)));
        Assert.True(File.Exists(Path.Combine(_testDir, "gspo.tdb")));

        using (var reopened = new QuadStore(_testDir))
        {
            Assert.Equal(StoreProfile.Cognitive, reopened.Schema.Profile);
            Assert.True(File.Exists(Path.Combine(_testDir, StoreSchema.FileName)));
        }
    }

    [Fact]
    public void QuadStore_ReopenWithSamePersistedProfile_UsesPersisted()
    {
        Directory.CreateDirectory(_testDir);
        using (var first = new QuadStore(_testDir, null, null, new StorageOptions { Profile = StoreProfile.Reference }))
        {
            Assert.Equal(StoreProfile.Reference, first.Schema.Profile);
        }

        // Reopen without specifying profile (defaults to Cognitive). Persisted Reference should win.
        using (var reopened = new QuadStore(_testDir))
        {
            Assert.Equal(StoreProfile.Reference, reopened.Schema.Profile);
        }
    }

    [Fact]
    public void QuadStore_CorruptedSchemaFile_ThrowsOnOpen()
    {
        Directory.CreateDirectory(_testDir);
        File.WriteAllText(Path.Combine(_testDir, StoreSchema.FileName), "{{ broken");

        Assert.Throws<InvalidStoreSchemaException>(() => new QuadStore(_testDir));
    }

    #endregion
}
