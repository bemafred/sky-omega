using System;
using System.IO;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-031 Piece 2: <c>QuadStore._sessionMutated</c> tracks whether anything has been
/// written since the last checkpoint. Dispose gates <c>CheckpointInternal</c> on the
/// flag, collapsing the 14-minute <c>CollectPredicateStatistics</c> path for read-only
/// sessions. These tests pin the invariant: every public mutation flips the flag;
/// every pure-query path leaves it false; a mutating session still checkpoints on
/// Dispose so statistics remain current for the next Open.
/// </summary>
public class SessionMutationFlagTests : IDisposable
{
    private readonly string _testDir;

    public SessionMutationFlagTests()
    {
        var tempPath = TempPath.Test("mutflag");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    private string Dir(string name)
    {
        var d = Path.Combine(_testDir, name);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void FreshStore_FlagInitiallyFalse()
    {
        using var store = new QuadStore(Dir("fresh"));
        Assert.False(store.SessionMutated);
    }

    #region Every public mutation sets the flag

    [Fact]
    public void Add_SetsFlag()
    {
        using var store = new QuadStore(Dir("add"));
        Assert.False(store.SessionMutated);
        store.Add("s", "p", "o", DateTimeOffset.UtcNow, DateTimeOffset.MaxValue);
        Assert.True(store.SessionMutated);
    }

    [Fact]
    public void AddCurrent_SetsFlag()
    {
        using var store = new QuadStore(Dir("add_current"));
        store.AddCurrent("s", "p", "o");
        Assert.True(store.SessionMutated);
    }

    [Fact]
    public void Delete_SetsFlag()
    {
        var dir = Dir("delete");
        // Seed, checkpoint explicitly, then reopen so the flag starts false.
        using (var seed = new QuadStore(dir))
        {
            seed.AddCurrent("s", "p", "o");
            seed.Checkpoint();
        }
        using var store = new QuadStore(dir);
        Assert.False(store.SessionMutated);
        store.Delete("s", "p", "o", DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        Assert.True(store.SessionMutated);
    }

    [Fact]
    public void Batch_SetsFlag()
    {
        using var store = new QuadStore(Dir("batch"));
        store.BeginBatch();
        store.AddCurrentBatched("s", "p", "o");
        store.CommitBatch();
        Assert.True(store.SessionMutated);
    }

    [Fact]
    public void Clear_SetsFlag()
    {
        using var store = new QuadStore(Dir("clear"));
        store.Checkpoint(); // resets flag even though nothing changed
        Assert.False(store.SessionMutated);
        store.Clear();
        Assert.True(store.SessionMutated);
    }

    [Fact]
    public void RebuildSecondaryIndexes_SetsFlag()
    {
        using var store = new QuadStore(Dir("rebuild"), null, null, new StorageOptions { BulkMode = true });
        store.BeginBatch();
        store.AddCurrentBatched("s", "p", "o");
        store.CommitBatch();
        store.Checkpoint(); // reset flag before rebuild
        Assert.False(store.SessionMutated);
        store.RebuildSecondaryIndexes();
        Assert.True(store.SessionMutated);
    }

    [Fact]
    public void Reference_BulkLoad_SetsFlag()
    {
        using var store = new QuadStore(Dir("ref_bulk"), null, null,
            new StorageOptions { Profile = StoreProfile.Reference, BulkMode = true });
        Assert.False(store.SessionMutated);
        store.BeginBatch();
        store.AddCurrentBatched("s", "p", "o");
        store.CommitBatch();
        Assert.True(store.SessionMutated);
    }

    #endregion

    #region Pure-query sessions leave the flag false

    [Fact]
    public void ReadOnlySession_PureQueries_FlagStaysFalse()
    {
        var dir = Dir("readonly");
        using (var seed = new QuadStore(dir))
        {
            seed.AddCurrent("<http://ex/alice>", "<http://ex/knows>", "<http://ex/bob>");
            seed.AddCurrent("<http://ex/alice>", "<http://ex/knows>", "<http://ex/carol>");
            seed.AddCurrent("<http://ex/bob>", "<http://ex/knows>", "<http://ex/alice>");
            // Checkpoint inside this Dispose (flag is true at this point).
        }

        // Fresh open — no replay (all records committed), no mutation, flag must stay false.
        using var readOnly = new QuadStore(dir);
        Assert.False(readOnly.SessionMutated);

        var result = SparqlEngine.Query(readOnly, "SELECT ?s ?o WHERE { ?s <http://ex/knows> ?o }");
        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(readOnly.SessionMutated);

        var count = SparqlEngine.Query(readOnly, "SELECT (COUNT(*) AS ?n) WHERE { ?s ?p ?o }");
        Assert.True(count.Success);
        Assert.False(readOnly.SessionMutated);

        var ask = SparqlEngine.Query(readOnly, "ASK { ?s ?p ?o }");
        Assert.True(ask.Success);
        Assert.False(readOnly.SessionMutated);

        // GetStatistics is metadata-only.
        _ = readOnly.GetStatistics();
        Assert.False(readOnly.SessionMutated);
    }

    [Fact]
    public void Reference_ReadOnlySession_FlagStaysFalse()
    {
        var dir = Dir("ref_readonly");

        // Create + bulk-load a Reference store (flag goes true during load).
        using (var seed = new QuadStore(dir, null, null,
            new StorageOptions { Profile = StoreProfile.Reference, BulkMode = true }))
        {
            seed.BeginBatch();
            seed.AddCurrentBatched("alice", "knows", "bob");
            seed.AddCurrentBatched("alice", "knows", "carol");
            seed.CommitBatch();
            // Dispose here — Reference has no WAL so CheckpointInternal short-circuits
            // and the flag stays true. That's fine; the Reference Dispose was always
            // near-zero cost (no WAL, no statistics).
        }

        using var readOnly = new QuadStore(dir);
        // After reopen, no replay happens (Reference has no WAL) — flag is false.
        Assert.False(readOnly.SessionMutated);

        var result = SparqlEngine.Query(readOnly, "SELECT (COUNT(*) AS ?n) WHERE { ?s ?p ?o }");
        Assert.True(result.Success);
        Assert.False(readOnly.SessionMutated);
    }

    #endregion

    #region Checkpoint resets the flag

    [Fact]
    public void Checkpoint_Explicit_ResetsFlag()
    {
        using var store = new QuadStore(Dir("cp_reset"));
        store.AddCurrent("s", "p", "o");
        Assert.True(store.SessionMutated);
        store.Checkpoint();
        Assert.False(store.SessionMutated);
    }

    [Fact]
    public void MutateThenCheckpoint_NextDisposeIsFast()
    {
        // The "fast" side is structural — we can only observe the flag and the
        // behavior, not wall-clock microseconds here.
        var dir = Dir("cp_then_dispose");
        using (var mutating = new QuadStore(dir))
        {
            mutating.AddCurrent("s", "p", "o");
            mutating.Checkpoint();
            Assert.False(mutating.SessionMutated);
            // Dispose below will see flag=false and skip CheckpointInternal.
        }
        // Reopen — data is intact despite skipped Dispose-time checkpoint.
        using var reopened = new QuadStore(dir);
        var stats = reopened.GetStatistics();
        Assert.Equal(1, stats.QuadCount);
    }

    #endregion

    #region Post-mutation Dispose correctness

    [Fact]
    public void PostMutationDispose_PreservesDataAcrossReopen()
    {
        var dir = Dir("post_mut");
        using (var first = new QuadStore(dir))
        {
            first.AddCurrent("<http://ex/alice>", "<http://ex/knows>", "<http://ex/bob>");
            first.AddCurrent("<http://ex/alice>", "<http://ex/knows>", "<http://ex/carol>");
            Assert.Equal(2, first.GetStatistics().QuadCount);
            Assert.True(first.SessionMutated);
            // Dispose runs CheckpointInternal because flag is true.
        }

        using (var second = new QuadStore(dir))
        {
            Assert.False(second.SessionMutated);
            var stats = second.GetStatistics();
            Assert.Equal(2, stats.QuadCount);

            var result = SparqlEngine.Query(second,
                "SELECT ?o WHERE { <http://ex/alice> <http://ex/knows> ?o }");
            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotNull(result.Rows);
            Assert.Equal(2, result.Rows!.Count);
        }
    }

    #endregion
}
