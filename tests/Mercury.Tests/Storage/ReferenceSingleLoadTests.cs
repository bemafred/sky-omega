using System;
using System.IO;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-064 D10: a Reference store takes one bulk load, whatever its atom store; it is read-only by design (ADR-007).
/// Its one write path is a batch (ADR-029 D7), so a store that holds a load refuses a second one both ways: opened in
/// bulk mode, and a batch begun on it when it is open to be queried. The store is left as it was.
/// </summary>
public class ReferenceSingleLoadTests : IDisposable
{
    private const string P = "<urn:reference:p>";

    private readonly string _root;

    public ReferenceSingleLoadTests()
    {
        var tempPath = TempPath.Test("reference-single-load");
        tempPath.MarkOwnership();
        _root = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_root);

    /// <summary>A Reference store with the given atom store, loaded once with two triples, and closed.</summary>
    private string LoadedOnce(AtomStoreImplementation atoms)
    {
        var dir = Path.Combine(_root, atoms.ToString());
        Directory.CreateDirectory(dir);
        (StoreSchema.ForProfile(StoreProfile.Reference) with { AtomStore = atoms }).WriteTo(dir);
        using var store = new QuadStore(dir, null, null,
            new StorageOptions { BulkMode = true, IndexInitialSizeBytes = 1L << 20, AtomDataInitialSizeBytes = 1L << 20 });
        store.BeginBatch();
        store.AddCurrentBatched("<urn:reference:s1>", P, "\"one\"");
        store.AddCurrentBatched("<urn:reference:s2>", P, "\"two\"");
        store.CommitBatch();
        store.FlushToDisk();
        store.RebuildSecondaryIndexes();
        return dir;
    }

    [Theory]
    [InlineData(AtomStoreImplementation.Hash)]
    [InlineData(AtomStoreImplementation.Sorted)]
    public void AReferenceStoreThatHoldsALoad_IsRefused_WhenItIsOpenedInBulkMode(AtomStoreImplementation atoms)
    {
        var dir = LoadedOnce(atoms);

        var refusal = Assert.Throws<ProfileCapabilityException>(() => new QuadStore(dir, null, null, new StorageOptions { BulkMode = true }));
        Assert.Contains("ADR-064 D10", refusal.Message);

        using var store = new QuadStore(dir);
        Assert.Equal(2, store.GetStatistics().QuadCount);
    }

    [Theory]
    [InlineData(AtomStoreImplementation.Hash)]
    [InlineData(AtomStoreImplementation.Sorted)]
    public void AReferenceStoreThatHoldsALoad_RefusesABatch_WhenItIsOpenToBeQueried(AtomStoreImplementation atoms)
    {
        var dir = LoadedOnce(atoms);

        using (var store = new QuadStore(dir))
        {
            var refusal = Assert.Throws<ProfileCapabilityException>(() => store.BeginBatch());
            Assert.Contains("ADR-064 D10", refusal.Message);

            // The store answers as before, and a refused batch leaves nothing held: the next read takes the lock.
            int bySubject = 0, byPredicate = 0;
            var gspo = store.QueryCurrent("<urn:reference:s1>", default, default);
            while (gspo.MoveNext()) bySubject++;
            gspo.Dispose();
            var gpos = store.QueryCurrent(default, P, default);
            while (gpos.MoveNext()) byPredicate++;
            gpos.Dispose();
            Assert.Equal(1, bySubject);
            Assert.Equal(2, byPredicate);
        }

        using var reopened = new QuadStore(dir);
        Assert.Equal(2, reopened.GetStatistics().QuadCount);
    }

    [Fact]
    public void AReferenceStore_TakesItsOneLoad_InSeveralBatches_WhenItIsOpenedEmpty()
    {
        // One load can be several batches of one session: a parser commits every so many triples.
        var dir = Path.Combine(_root, "batches");
        Directory.CreateDirectory(dir);
        (StoreSchema.ForProfile(StoreProfile.Reference) with { AtomStore = AtomStoreImplementation.Hash }).WriteTo(dir);
        using var store = new QuadStore(dir, null, null,
            new StorageOptions { BulkMode = true, IndexInitialSizeBytes = 1L << 20, AtomDataInitialSizeBytes = 1L << 20 });
        store.BeginBatch();
        store.AddCurrentBatched("<urn:reference:s1>", P, "\"one\"");
        store.CommitBatch();
        store.BeginBatch();
        store.AddCurrentBatched("<urn:reference:s2>", P, "\"two\"");
        store.CommitBatch();
        store.FlushToDisk();
        Assert.Equal(2, store.GetStatistics().QuadCount);
    }
}
