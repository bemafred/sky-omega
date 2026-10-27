using System;
using System.Collections.Generic;
using System.IO;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-054: on a Cognitive store a delete ends a fact's valid time instead of erasing it from every AS OF view; a
/// retraction ("recorded in error") is a separate operation; a re-insert after either creates a new version; counts
/// report what changed; WAL replay is idempotent; and <see cref="QuadStore.RepairDeletes"/> repairs the history that
/// pre-ADR-054 deletes erased. Each test opens its own store directory and states its sequence inline.
/// </summary>
public class DeleteEndsValidTimeTests : IDisposable
{
    private const string S = "<http://ex.org/s>";
    private const string P = "<http://ex.org/status>";

    private readonly string _testDir;

    public DeleteEndsValidTimeTests()
    {
        var tempPath = TempPath.Test("adr054");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    // ---- D1: End -------------------------------------------------------------------------------------------------

    [Fact]
    public void DeleteCurrent_EndsTheVersion_AsOfBeforeTheDeleteStillReturnsIt()
    {
        var dir = Path.Combine(_testDir, "end-asof");
        using var store = new QuadStore(dir);
        var t0 = DateTimeOffset.UtcNow.AddHours(-2);
        store.Add(S, P, "\"open\"", t0, DateTimeOffset.MaxValue);

        var deleted = store.DeleteCurrent(S, P, "\"open\"");
        var afterDelete = DateTimeOffset.UtcNow.AddMilliseconds(1);

        Assert.True(deleted);
        store.AcquireReadLock();
        try
        {
            var before = store.QueryAsOf(S, P, "\"open\"", t0.AddHours(1));
            Assert.True(before.MoveNext(), "AS OF a time before the delete returns the fact");
            before.Dispose();

            var now = store.QueryAsOf(S, P, "\"open\"", afterDelete);
            Assert.False(now.MoveNext(), "AS OF a time after the delete does not");
            now.Dispose();

            var history = store.QueryEvolution(S, P, "\"open\"");
            Assert.True(history.MoveNext());
            Assert.False(history.Current.IsDeleted, "An ended version is not a retracted one");
            Assert.Equal(t0.ToUnixTimeMilliseconds(), history.Current.ValidFrom.ToUnixTimeMilliseconds());
            Assert.True(history.Current.ValidTo < afterDelete, "ValidTo is the delete's time");
            Assert.False(history.MoveNext(), "Ending a version adds no entry");
            history.Dispose();
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void DeleteCurrent_WithAnEarlierClosedVersion_EndsOnlyTheCurrentOne()
    {
        var dir = Path.Combine(_testDir, "end-two-versions");
        using var store = new QuadStore(dir);
        var t0 = DateTimeOffset.UtcNow.AddHours(-3);
        store.Add(S, P, "\"v\"", t0, t0.AddHours(1));
        store.Add(S, P, "\"v\"", t0.AddHours(2), DateTimeOffset.MaxValue);

        Assert.True(store.DeleteCurrent(S, P, "\"v\""));

        store.AcquireReadLock();
        try
        {
            var current = store.QueryCurrent(S, P, "\"v\"");
            Assert.False(current.MoveNext(), "The current version was ended");
            current.Dispose();

            var first = store.QueryAsOf(S, P, "\"v\"", t0.AddMinutes(30));
            Assert.True(first.MoveNext(), "The earlier version is untouched");
            first.Dispose();

            var second = store.QueryAsOf(S, P, "\"v\"", t0.AddMinutes(150));
            Assert.True(second.MoveNext(), "The current version held until the delete");
            second.Dispose();

            var history = store.QueryEvolution(S, P, "\"v\"");
            int versions = 0, retracted = 0;
            while (history.MoveNext())
            {
                versions++;
                if (history.Current.IsDeleted) retracted++;
            }
            history.Dispose();
            Assert.Equal(2, versions);
            Assert.Equal(0, retracted);
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void DeleteCurrent_VersionsSpanningLeafPages_FindsTheCurrentOne()
    {
        // 400 closed versions of one quad fill more than two 185-entry leaf pages; the current version sorts last.
        var dir = Path.Combine(_testDir, "end-leaf-pages");
        using var store = new QuadStore(dir);
        var t0 = DateTimeOffset.UtcNow.AddDays(-30);
        for (int i = 0; i < 400; i++)
            store.Add(S, P, "\"v\"", t0.AddMinutes(i * 2), t0.AddMinutes(i * 2 + 1));
        store.Add(S, P, "\"v\"", t0.AddDays(10), DateTimeOffset.MaxValue);

        Assert.True(store.DeleteCurrent(S, P, "\"v\""));

        store.AcquireReadLock();
        try
        {
            var current = store.QueryCurrent(S, P, "\"v\"");
            Assert.False(current.MoveNext(), "The current version, on the last leaf page, was ended");
            current.Dispose();

            var firstVersion = store.QueryAsOf(S, P, "\"v\"", t0.AddSeconds(30));
            Assert.True(firstVersion.MoveNext(), "The first closed version is untouched");
            firstVersion.Dispose();

            var currentVersion = store.QueryAsOf(S, P, "\"v\"", t0.AddDays(11));
            Assert.True(currentVersion.MoveNext(), "The ended version still holds before its end");
            currentVersion.Dispose();
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void DeleteCurrent_NothingHolds_ReturnsFalse()
    {
        var dir = Path.Combine(_testDir, "end-counts");
        using var store = new QuadStore(dir);
        store.AddCurrent(S, P, "\"a\"");

        Assert.True(store.DeleteCurrent(S, P, "\"a\""));
        Assert.False(store.DeleteCurrent(S, P, "\"a\""), "An ended fact has nothing left to delete");
        Assert.False(store.DeleteCurrent(S, P, "\"never-written\""));
    }

    [Fact]
    public void DeleteCurrentBatched_SameQuadTwiceInOneBatch_CountsOnce()
    {
        var dir = Path.Combine(_testDir, "end-batch-counts");
        using var store = new QuadStore(dir);
        store.AddCurrent(S, P, "\"a\"");

        store.BeginBatch();
        var first = store.DeleteCurrentBatched(S, P, "\"a\"");
        var second = store.DeleteCurrentBatched(S, P, "\"a\"");
        var absent = store.DeleteCurrentBatched(S, P, "\"never-written\"");
        store.CommitBatch();

        Assert.True(first);
        Assert.False(second, "The quad was already deleted in this batch");
        Assert.False(absent);
        store.AcquireReadLock();
        try
        {
            var current = store.QueryCurrent(S, P, "\"a\"");
            Assert.False(current.MoveNext());
            current.Dispose();
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void DeleteCurrentBatched_AfterAnAddInTheSameBatch_EndsTheAddedVersion()
    {
        var dir = Path.Combine(_testDir, "end-batch-add");
        using var store = new QuadStore(dir);

        store.BeginBatch();
        store.AddCurrentBatched(S, P, "\"a\"");
        var deleted = store.DeleteCurrentBatched(S, P, "\"a\"");
        store.CommitBatch();

        Assert.True(deleted, "The version the batch's Add creates holds at the batch's time");
        store.AcquireReadLock();
        try
        {
            var current = store.QueryCurrent(S, P, "\"a\"");
            Assert.False(current.MoveNext());
            current.Dispose();
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    // ---- D2 / D3: Retract, and insert after End or Retract ---------------------------------------------------------

    [Fact]
    public void Retract_AdjacentVersions_MarksOnlyTheExactOne()
    {
        // The first version's ValidTo equals the second's ValidFrom, so both overlap the second's exact interval.
        var dir = Path.Combine(_testDir, "retract-adjacent");
        using var store = new QuadStore(dir);
        var t0 = DateTimeOffset.UtcNow.AddHours(-3);
        var t1 = t0.AddHours(1);
        var t2 = t0.AddHours(2);
        store.Add(S, P, "\"v\"", t0, t1);
        store.Add(S, P, "\"v\"", t1, t2);

        Assert.True(store.Retract(S, P, "\"v\"", t1, t2));

        store.AcquireReadLock();
        try
        {
            var first = store.QueryAsOf(S, P, "\"v\"", t0.AddMinutes(30));
            Assert.True(first.MoveNext(), "The first version is untouched");
            first.Dispose();

            var second = store.QueryAsOf(S, P, "\"v\"", t1.AddMinutes(30));
            Assert.False(second.MoveNext(), "The second version was retracted");
            second.Dispose();

            var history = store.QueryEvolution(S, P, "\"v\"");
            var retractedFrom = new List<long>();
            while (history.MoveNext())
                if (history.Current.IsDeleted)
                {
                    retractedFrom.Add(history.Current.ValidFrom.ToUnixTimeMilliseconds());
                    Assert.NotNull(history.Current.RetractedAt);
                }
            history.Dispose();
            Assert.Equal(new[] { t1.ToUnixTimeMilliseconds() }, retractedFrom);
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void InsertDeleteInsert_ThroughSparql_TheFactIsCurrentAgain()
    {
        var dir = Path.Combine(_testDir, "reinsert-after-end");
        using var store = new QuadStore(dir);

        SparqlEngine.Update(store, $"INSERT DATA {{ {S} {P} \"a\" }}", TestContext.Current.CancellationToken);
        Thread.Sleep(5);
        SparqlEngine.Update(store, $"DELETE DATA {{ {S} {P} \"a\" }}", TestContext.Current.CancellationToken);
        Thread.Sleep(5);
        var reinsert = SparqlEngine.Update(store, $"INSERT DATA {{ {S} {P} \"a\" }}", TestContext.Current.CancellationToken);

        Assert.True(reinsert.Success, reinsert.ErrorMessage);
        store.AcquireReadLock();
        try
        {
            var current = store.QueryCurrent(S, P, "\"a\"");
            Assert.True(current.MoveNext(), "The re-insert is stored, not dropped as a duplicate");
            current.Dispose();

            var history = store.QueryEvolution(S, P, "\"a\"");
            int versions = 0;
            while (history.MoveNext()) versions++;
            history.Dispose();
            Assert.Equal(2, versions);
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void Insert_AfterRetractOfTheCurrentVersion_CreatesANewCurrentVersion()
    {
        var dir = Path.Combine(_testDir, "reinsert-after-retract");
        using var store = new QuadStore(dir);
        var t0 = DateTimeOffset.UtcNow.AddHours(-1);
        store.Add(S, P, "\"a\"", t0, DateTimeOffset.MaxValue);
        Assert.True(store.Retract(S, P, "\"a\"", t0, DateTimeOffset.MaxValue));

        store.AddCurrent(S, P, "\"a\"");

        store.AcquireReadLock();
        try
        {
            var current = store.QueryCurrent(S, P, "\"a\"");
            Assert.True(current.MoveNext(), "A retracted entry does not block the insert");
            current.Dispose();
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    // ---- SPARQL facade -------------------------------------------------------------------------------------------

    [Fact]
    public void StatusRewrite_ThroughSparql_AsOfBeforeReturnsTheOldStatus()
    {
        var dir = Path.Combine(_testDir, "sparql-status");
        using var store = new QuadStore(dir);
        var t0 = DateTimeOffset.UtcNow.AddHours(-2);
        store.Add(S, P, "\"open\"", t0, DateTimeOffset.MaxValue);

        var result = SparqlEngine.Update(store,
            $"DELETE {{ {S} {P} ?old }} INSERT {{ {S} {P} \"fixed\" }} WHERE {{ {S} {P} ?old }}", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.AffectedCount); // one version ended, one inserted
        store.AcquireReadLock();
        try
        {
            var before = store.QueryAsOf(S, P, default, t0.AddHours(1));
            Assert.True(before.MoveNext());
            Assert.Equal("\"open\"", before.Current.Object.ToString());
            Assert.False(before.MoveNext());
            before.Dispose();

            var now = store.QueryCurrent(S, P, default);
            Assert.True(now.MoveNext());
            Assert.Equal("\"fixed\"", now.Current.Object.ToString());
            Assert.False(now.MoveNext());
            now.Dispose();
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void DeleteAndInsertTheSameQuad_InOneModify_LeavesOneUnbrokenVersion()
    {
        var dir = Path.Combine(_testDir, "sparql-noop");
        using var store = new QuadStore(dir);
        var t0 = DateTimeOffset.UtcNow.AddHours(-2);
        store.Add(S, P, "\"open\"", t0, DateTimeOffset.MaxValue);

        var result = SparqlEngine.Update(store,
            $"DELETE {{ {S} {P} ?o }} INSERT {{ {S} {P} ?o }} WHERE {{ {S} {P} ?o }}", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(0, result.AffectedCount);
        store.AcquireReadLock();
        try
        {
            var history = store.QueryEvolution(S, P, "\"open\"");
            Assert.True(history.MoveNext());
            Assert.Equal(t0.ToUnixTimeMilliseconds(), history.Current.ValidFrom.ToUnixTimeMilliseconds());
            Assert.True(history.Current.ValidTo.Year > 9000, "Still current, with no boundary");
            Assert.False(history.MoveNext());
            history.Dispose();
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void DeleteData_OfAnAlreadyDeletedQuad_ReportsZero()
    {
        var dir = Path.Combine(_testDir, "sparql-counts");
        using var store = new QuadStore(dir);
        SparqlEngine.Update(store, $"INSERT DATA {{ {S} {P} \"a\" }}", TestContext.Current.CancellationToken);

        var first = SparqlEngine.Update(store, $"DELETE DATA {{ {S} {P} \"a\" }}", TestContext.Current.CancellationToken);
        var second = SparqlEngine.Update(store, $"DELETE DATA {{ {S} {P} \"a\" }}", TestContext.Current.CancellationToken);
        var deleteWhere = SparqlEngine.Update(store, $"DELETE WHERE {{ {S} {P} ?o }}", TestContext.Current.CancellationToken);

        Assert.Equal(1, first.AffectedCount);
        Assert.Equal(0, second.AffectedCount);
        Assert.Equal(0, deleteWhere.AffectedCount);
    }

    // ---- WAL replay (Q2) -----------------------------------------------------------------------------------------

    [Fact]
    public void Replay_AddAndEndOverIndexesThatHoldThem_ChangesNothing()
    {
        // Session 1 applies Add + End to the indexes; then the same records are appended to the WAL as if the process
        // had crashed after the indexes were flushed but before the checkpoint. Reopening replays them.
        var dir = Path.Combine(_testDir, "replay-add-end");
        var t0 = DateTimeOffset.UtcNow.AddHours(-2);
        long subjectId, predicateId, objectId, addTx, endMs;
        using (var store = new QuadStore(dir))
        {
            store.Add(S, P, "\"open\"", t0, DateTimeOffset.MaxValue);
            Assert.True(store.DeleteCurrent(S, P, "\"open\""));
            subjectId = store.Atoms.GetAtomId(S);
            predicateId = store.Atoms.GetAtomId(P);
            objectId = store.Atoms.GetAtomId("\"open\"");
            store.AcquireReadLock();
            var history = store.QueryEvolution(S, P, "\"open\"");
            Assert.True(history.MoveNext());
            addTx = history.Current.TransactionTime.ToUnixTimeMilliseconds();
            endMs = history.Current.ValidTo.ToUnixTimeMilliseconds();
            history.Dispose();
            store.ReleaseReadLock();
        }
        using (var wal = new WriteAheadLog(Path.Combine(dir, "wal.log")))
        {
            wal.Append(LogRecord.CreateAdd(subjectId, predicateId, objectId, t0, DateTimeOffset.MaxValue,
                transactionTimeTicks: addTx));
            wal.Append(LogRecord.CreateEnd(subjectId, predicateId, objectId, t0,
                DateTimeOffset.FromUnixTimeMilliseconds(endMs), graphId: 0, transactionTimeTicks: endMs));
        }

        // Reopen twice: each open replays the appended records once more.
        for (int open = 0; open < 2; open++)
        {
            using var store = new QuadStore(dir);
            store.AcquireReadLock();
            try
            {
                var history = store.QueryEvolution(S, P, "\"open\"");
                Assert.True(history.MoveNext());
                Assert.Equal(endMs, history.Current.ValidTo.ToUnixTimeMilliseconds());
                Assert.False(history.Current.IsDeleted);
                Assert.False(history.MoveNext(), "The replayed Add did not bring the ended version back");
                history.Dispose();

                var current = store.QueryCurrent(S, P, "\"open\"");
                Assert.False(current.MoveNext());
                current.Dispose();
            }
            finally
            {
                store.ReleaseReadLock();
            }
        }
    }

    [Fact]
    public void Replay_DeleteAndReinsertInTheSameMillisecond_EndsTheOldVersionOnly()
    {
        // The re-insert starts at the delete's time T, so it also "holds at T". The End record names the old version
        // by its ValidFrom, so replay cannot end the re-insert instead.
        var dir = Path.Combine(_testDir, "replay-same-ms");
        var t0 = DateTimeOffset.UtcNow.AddHours(-2);
        long subjectId, predicateId, objectId, firstTx, endMs, secondTx;
        using (var store = new QuadStore(dir))
        {
            store.Add(S, P, "\"a\"", t0, DateTimeOffset.MaxValue);
            Assert.True(store.DeleteCurrent(S, P, "\"a\""));
            subjectId = store.Atoms.GetAtomId(S);
            predicateId = store.Atoms.GetAtomId(P);
            objectId = store.Atoms.GetAtomId("\"a\"");
            store.AcquireReadLock();
            var history = store.QueryEvolution(S, P, "\"a\"");
            Assert.True(history.MoveNext());
            firstTx = history.Current.TransactionTime.ToUnixTimeMilliseconds();
            endMs = history.Current.ValidTo.ToUnixTimeMilliseconds();
            history.Dispose();
            store.ReleaseReadLock();

            store.Add(S, P, "\"a\"", DateTimeOffset.FromUnixTimeMilliseconds(endMs), DateTimeOffset.MaxValue);
            store.AcquireReadLock();
            history = store.QueryEvolution(S, P, "\"a\"");
            Assert.True(history.MoveNext());
            Assert.True(history.MoveNext());
            secondTx = history.Current.TransactionTime.ToUnixTimeMilliseconds();
            history.Dispose();
            store.ReleaseReadLock();
        }
        using (var wal = new WriteAheadLog(Path.Combine(dir, "wal.log")))
        {
            wal.Append(LogRecord.CreateAdd(subjectId, predicateId, objectId, t0, DateTimeOffset.MaxValue,
                transactionTimeTicks: firstTx));
            wal.Append(LogRecord.CreateEnd(subjectId, predicateId, objectId, t0,
                DateTimeOffset.FromUnixTimeMilliseconds(endMs), graphId: 0, transactionTimeTicks: endMs));
            wal.Append(LogRecord.CreateAdd(subjectId, predicateId, objectId,
                DateTimeOffset.FromUnixTimeMilliseconds(endMs), DateTimeOffset.MaxValue, transactionTimeTicks: secondTx));
        }

        using (var store = new QuadStore(dir))
        {
            store.AcquireReadLock();
            try
            {
                var current = store.QueryCurrent(S, P, "\"a\"");
                Assert.True(current.MoveNext(), "The re-inserted version is current after replay");
                current.Dispose();

                var history = store.QueryEvolution(S, P, "\"a\"");
                Assert.True(history.MoveNext());
                Assert.Equal(t0.ToUnixTimeMilliseconds(), history.Current.ValidFrom.ToUnixTimeMilliseconds());
                Assert.Equal(endMs, history.Current.ValidTo.ToUnixTimeMilliseconds());
                Assert.True(history.MoveNext());
                Assert.Equal(endMs, history.Current.ValidFrom.ToUnixTimeMilliseconds());
                Assert.True(history.Current.ValidTo.Year > 9000);
                Assert.False(history.MoveNext());
                history.Dispose();
            }
            finally
            {
                store.ReleaseReadLock();
            }
        }
    }

    [Fact]
    public void Replay_PreAdr054DeleteRecord_KeepsItsOldMeaning()
    {
        // Before ADR-054, DeleteCurrent wrote operation 2 with [MinValue, MaxValue]; replay marks the first overlapping
        // version, as it did then.
        var dir = Path.Combine(_testDir, "replay-legacy");
        long subjectId, predicateId, objectId;
        using (var store = new QuadStore(dir))
        {
            store.Add(S, P, "\"old\"", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.MaxValue);
            subjectId = store.Atoms.GetAtomId(S);
            predicateId = store.Atoms.GetAtomId(P);
            objectId = store.Atoms.GetAtomId("\"old\"");
        }
        using (var wal = new WriteAheadLog(Path.Combine(dir, "wal.log")))
            wal.Append(LogRecord.CreateRetract(subjectId, predicateId, objectId, DateTimeOffset.MinValue, DateTimeOffset.MaxValue,
                transactionTimeTicks: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        using (var store = new QuadStore(dir))
        {
            store.AcquireReadLock();
            try
            {
                var history = store.QueryEvolution(S, P, "\"old\"");
                Assert.True(history.MoveNext());
                Assert.True(history.Current.IsDeleted);
                Assert.True(history.Current.ValidTo.Year > 9000, "The old meaning left ValidTo open");
                history.Dispose();
            }
            finally
            {
                store.ReleaseReadLock();
            }
        }
    }

    [Fact]
    public void Replay_UnknownOperation_Throws()
    {
        var dir = Path.Combine(_testDir, "replay-unknown");
        using (var store = new QuadStore(dir))
            store.AddCurrent(S, P, "\"a\"");
        using (var wal = new WriteAheadLog(Path.Combine(dir, "wal.log")))
        {
            var record = LogRecord.CreateAdd(1, 2, 3, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue,
                transactionTimeTicks: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            record.Operation = (LogOperation)7;
            wal.Append(record);
        }

        var error = Assert.Throws<InvalidDataException>(() => new QuadStore(dir));
        Assert.Contains("operation 7", error.Message);
    }

    // ---- D7: repair ----------------------------------------------------------------------------------------------

    [Fact]
    public void RepairDeletes_EndsFlaggedEntries_AndRestoresTheNamedEvent()
    {
        // Retract flags entries exactly as a pre-ADR-054 delete did (IsDeleted, ModifiedAt = the deletion's time).
        var dir = Path.Combine(_testDir, "repair");
        using var store = new QuadStore(dir);
        var t0 = DateTimeOffset.UtcNow.AddHours(-2);
        store.Add(S, P, "\"ended\"", t0, DateTimeOffset.MaxValue, "<http://ex.org/g1>");
        store.Add(S, P, "\"mistake\"", t0, DateTimeOffset.MaxValue, "<http://ex.org/g2>");
        var future = DateTimeOffset.UtcNow.AddDays(1);
        store.Add(S, P, "\"future\"", future, DateTimeOffset.MaxValue, "<http://ex.org/g1>");
        Assert.True(store.Retract(S, P, "\"ended\"", t0, DateTimeOffset.MaxValue, "<http://ex.org/g1>"));
        Assert.True(store.Retract(S, P, "\"mistake\"", t0, DateTimeOffset.MaxValue, "<http://ex.org/g2>"));
        Assert.True(store.Retract(S, P, "\"future\"", future, DateTimeOffset.MaxValue, "<http://ex.org/g1>"));

        DateTimeOffset mistakeDeletedAt;
        store.AcquireReadLock();
        var mistake = store.QueryEvolution(S, P, "\"mistake\"", "<http://ex.org/g2>");
        Assert.True(mistake.MoveNext());
        mistakeDeletedAt = mistake.Current.RetractedAt!.Value;
        mistake.Dispose();
        store.ReleaseReadLock();
        var restore = new[] { ("<http://ex.org/g2>", mistakeDeletedAt) };

        var dryReport = new StringWriter();
        var dry = store.RepairDeletes(restore, dryRun: true, dryReport);
        Assert.Equal((1, 1, 1), dry);
        Assert.Contains("<http://ex.org/g2>  1 entries  -> restore", dryReport.ToString());
        store.AcquireReadLock();
        var unchanged = store.QueryAsOf(S, P, "\"ended\"", t0.AddHours(1), "<http://ex.org/g1>");
        Assert.False(unchanged.MoveNext(), "The dry run changed nothing");
        unchanged.Dispose();
        store.ReleaseReadLock();

        var repaired = store.RepairDeletes(restore, dryRun: false, new StringWriter());

        Assert.Equal((1, 1, 1), repaired);
        store.AcquireReadLock();
        try
        {
            var ended = store.QueryAsOf(S, P, "\"ended\"", t0.AddHours(1), "<http://ex.org/g1>");
            Assert.True(ended.MoveNext(), "The ended fact is back in the past view");
            ended.Dispose();
            var endedNow = store.QueryCurrent(S, P, "\"ended\"", "<http://ex.org/g1>");
            Assert.False(endedNow.MoveNext(), "and ended at its deletion time");
            endedNow.Dispose();

            var restored = store.QueryCurrent(S, P, "\"mistake\"", "<http://ex.org/g2>");
            Assert.True(restored.MoveNext(), "The restored fact never stopped holding");
            restored.Dispose();

            var left = store.QueryEvolution(S, P, "\"future\"", "<http://ex.org/g1>");
            Assert.True(left.MoveNext());
            Assert.True(left.Current.IsDeleted, "Deleted before its ValidFrom: left as it was");
            left.Dispose();
        }
        finally
        {
            store.ReleaseReadLock();
        }

        var again = store.RepairDeletes(Array.Empty<(string, DateTimeOffset)>(), dryRun: false, new StringWriter());
        Assert.Equal((0, 0, 1), again);
    }
}
