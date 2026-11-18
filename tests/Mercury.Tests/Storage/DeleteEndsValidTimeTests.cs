using System;
using System.Collections.Generic;
using System.IO;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-054, as ADR-061 left it: on a Cognitive store a delete ends a fact's period instead of erasing it, so what was
/// remembered before the delete still holds the fact; a re-insert after a delete begins a new period; counts report
/// what changed; WAL replay is idempotent; and <see cref="QuadStore.RepairDeletes"/> repairs the history that
/// pre-ADR-054 deletes erased. Retract is gone (ADR-061 D2): a retraction reaches a store now only from the log of a
/// Mercury before ADR-061 Step 6, which a test appends and the store replays when it opens. Each test supplies the
/// store's clock and states its sequence inline. What was remembered at a time t is two reads (ADR-061 Q10): what was
/// memorized before t + 1 ms and is remembered, and what was memorized before it and forgotten since it.
/// </summary>
public class DeleteEndsValidTimeTests : IDisposable
{
    private const string S = "<http://ex.org/s>";
    private const string P = "<http://ex.org/status>";

    private static DateTimeOffset Reading(int minutes) => new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

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
    public void DeleteCurrent_EndsThePeriod_WhatWasRememberedBeforeTheDeleteStillHoldsIt()
    {
        var clock = new SuppliedClock { Reading = Reading(0) };
        using var store = new QuadStore(Path.Combine(_testDir, "end-asof"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent(S, P, "\"open\"");
        clock.Reading = Reading(60);

        Assert.True(store.DeleteCurrent(S, P, "\"open\""));

        store.AcquireReadLock();
        try
        {
            // At minute 30 it was remembered: memorized before 30:00.001 and forgotten since
            var justAfter30 = Reading(30).AddMilliseconds(1);
            var at30 = store.QueryMemories(S, P, "\"open\"", memorizedBefore: justAfter30, forgottenSince: justAfter30);
            Assert.True(at30.MoveNext(), "What was remembered before the delete holds the fact");
            at30.Dispose();

            // At minute 60, the delete's reading, it was not: forgotten then, not since then
            var justAfter60 = Reading(60).AddMilliseconds(1);
            var remembered = store.QueryMemories(S, P, "\"open\"", memorizedBefore: justAfter60);
            Assert.False(remembered.MoveNext(), "What is remembered after the delete does not");
            remembered.Dispose();
            var forgottenSince = store.QueryMemories(S, P, "\"open\"", memorizedBefore: justAfter60, forgottenSince: justAfter60);
            Assert.False(forgottenSince.MoveNext());
            forgottenSince.Dispose();

            var history = store.QueryMemories(S, P, "\"open\"", includingForgotten: true);
            Assert.True(history.MoveNext());
            Assert.Equal(Reading(0), history.Current.MemorizedAt);
            Assert.Equal(Reading(60), history.Current.ForgottenAt);
            Assert.False(history.MoveNext(), "Ending a period adds no entry");
            history.Dispose();
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void DeleteCurrent_WithAnEarlierForgottenPeriod_EndsOnlyTheOneThatHolds()
    {
        var clock = new SuppliedClock { Reading = Reading(0) };
        using var store = new QuadStore(Path.Combine(_testDir, "end-two-periods"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent(S, P, "\"v\"");
        clock.Reading = Reading(60);
        store.DeleteCurrent(S, P, "\"v\"");
        clock.Reading = Reading(120);
        store.AddCurrent(S, P, "\"v\"");
        clock.Reading = Reading(180);

        Assert.True(store.DeleteCurrent(S, P, "\"v\""));

        store.AcquireReadLock();
        try
        {
            var current = store.QueryCurrent(S, P, "\"v\"");
            Assert.False(current.MoveNext(), "The period that held was ended");
            current.Dispose();

            var history = store.QueryMemories(S, P, "\"v\"", includingForgotten: true);
            var periods = new List<(DateTimeOffset, DateTimeOffset?)>();
            while (history.MoveNext())
                periods.Add((history.Current.MemorizedAt, history.Current.ForgottenAt));
            history.Dispose();
            periods.Sort();
            Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (Reading(0), Reading(60)), (Reading(120), Reading(180)) }, periods);
        }
        finally
        {
            store.ReleaseReadLock();
        }
    }

    [Fact]
    public void DeleteCurrent_PeriodsSpanningLeafPages_FindsTheOneThatHolds()
    {
        // 200 forgotten periods of one quad and the one that holds are more than one 185-entry leaf page; the one that
        // holds sorts last.
        var clock = new SuppliedClock { Reading = Reading(0) };
        using var store = new QuadStore(Path.Combine(_testDir, "end-leaf-pages"), null, null, new StorageOptions { Clock = clock });
        for (int i = 0; i < 200; i++)
        {
            clock.Reading = Reading(i * 2);
            store.AddCurrent(S, P, "\"v\"");
            clock.Reading = Reading(i * 2 + 1);
            store.DeleteCurrent(S, P, "\"v\"");
        }
        clock.Reading = Reading(1000);
        store.AddCurrent(S, P, "\"v\"");
        clock.Reading = Reading(1001);

        Assert.True(store.DeleteCurrent(S, P, "\"v\""));

        store.AcquireReadLock();
        try
        {
            var current = store.QueryCurrent(S, P, "\"v\"");
            Assert.False(current.MoveNext(), "The period that held, on the last leaf page, was ended");
            current.Dispose();

            var history = store.QueryMemories(S, P, "\"v\"", includingForgotten: true);
            var periods = new List<(DateTimeOffset, DateTimeOffset?)>();
            while (history.MoveNext())
                periods.Add((history.Current.MemorizedAt, history.Current.ForgottenAt));
            history.Dispose();
            periods.Sort();
            Assert.Equal(201, periods.Count);
            Assert.Equal((Reading(0), (DateTimeOffset?)Reading(1)), periods[0]);
            Assert.Equal((Reading(1000), (DateTimeOffset?)Reading(1001)), periods[200]);
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
    // Since ADR-061 (M2) an add and a delete of one quad in one batch leave no period: the quad was remembered at no
    // representable time. MemoryTimeWriteRuleTests holds that. What this test holds still stands: the delete reports
    // a change, and nothing is remembered.
    public void DeleteCurrentBatched_AfterAnAddInTheSameBatch_ReportsAChangeAndLeavesNothingRemembered()
    {
        var dir = Path.Combine(_testDir, "end-batch-add");
        using var store = new QuadStore(dir);

        store.BeginBatch();
        store.AddCurrentBatched(S, P, "\"a\"");
        var deleted = store.DeleteCurrentBatched(S, P, "\"a\"");
        store.CommitBatch();

        Assert.True(deleted, "The quad the batch memorized was remembered when the delete was asked");
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

    // ---- D3: insert after End, or after a retraction an older Mercury logged -------------------------------------

    [Fact]
    public void InsertDeleteInsert_ThroughSparql_TheFactIsCurrentAgain()
    {
        // Three requests in three clock readings: the test supplies them. In one reading the delete and the second
        // insert would continue the first period, and one period would be left (ADR-061 M2).
        var dir = Path.Combine(_testDir, "reinsert-after-end");
        var clock = new SuppliedClock { Reading = new DateTimeOffset(2031, 3, 4, 5, 6, 7, TimeSpan.Zero) };
        using var store = new QuadStore(dir, null, null, new StorageOptions { Clock = clock });

        SparqlEngine.Update(store, $"INSERT DATA {{ {S} {P} \"a\" }}", TestContext.Current.CancellationToken);
        clock.Reading = clock.Reading.AddMinutes(1);
        SparqlEngine.Update(store, $"DELETE DATA {{ {S} {P} \"a\" }}", TestContext.Current.CancellationToken);
        clock.Reading = clock.Reading.AddMinutes(1);
        var reinsert = SparqlEngine.Update(store, $"INSERT DATA {{ {S} {P} \"a\" }}", TestContext.Current.CancellationToken);

        Assert.True(reinsert.Success, reinsert.ErrorMessage);
        store.AcquireReadLock();
        try
        {
            var current = store.QueryCurrent(S, P, "\"a\"");
            Assert.True(current.MoveNext(), "The re-insert is stored, not dropped as a duplicate");
            current.Dispose();

            var history = store.QueryMemories(S, P, "\"a\"", includingForgotten: true);
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
    public void Insert_AfterAReplayedRetractOfThePeriodThatHeld_BeginsANewPeriod()
    {
        var dir = Path.Combine(_testDir, "reinsert-after-retract");
        var clock = new SuppliedClock { Reading = Reading(0) };
        var options = new StorageOptions { Clock = clock };
        long subjectId, predicateId, objectId;
        using (var store = new QuadStore(dir, null, null, options))
        {
            store.AddCurrent(S, P, "\"a\"");
            subjectId = store.Atoms.GetAtomId(S);
            predicateId = store.Atoms.GetAtomId(P);
            objectId = store.Atoms.GetAtomId("\"a\"");
        }
        using (var wal = new WriteAheadLog(Path.Combine(dir, "wal.log")))
            wal.Append(LogRecord.CreateRetract(subjectId, predicateId, objectId, Reading(0), DateTimeOffset.MaxValue,
                transactionTimeTicks: Reading(30).ToUnixTimeMilliseconds()));

        clock.Reading = Reading(60);
        using (var store = new QuadStore(dir, null, null, options))
        {
            store.AddCurrent(S, P, "\"a\"");

            store.AcquireReadLock();
            try
            {
                var current = store.QueryMemories(S, P, "\"a\"");
                Assert.True(current.MoveNext(), "A retracted entry does not block the insert");
                Assert.Equal(Reading(60), current.Current.MemorizedAt);
                Assert.False(current.MoveNext());
                current.Dispose();
            }
            finally
            {
                store.ReleaseReadLock();
            }
        }
    }

    // ---- SPARQL facade -------------------------------------------------------------------------------------------

    [Fact]
    public void StatusRewrite_ThroughSparql_WhatWasRememberedBeforeHoldsTheOldStatus()
    {
        var clock = new SuppliedClock { Reading = Reading(0) };
        using var store = new QuadStore(Path.Combine(_testDir, "sparql-status"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent(S, P, "\"open\"");
        clock.Reading = Reading(60);

        var result = SparqlEngine.Update(store,
            $"DELETE {{ {S} {P} ?old }} INSERT {{ {S} {P} \"fixed\" }} WHERE {{ {S} {P} ?old }}", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.AffectedCount); // one period ended, one begun
        store.AcquireReadLock();
        try
        {
            // At minute 30: "open", memorized before then and forgotten since; "fixed" was not yet memorized
            var justAfter30 = Reading(30).AddMilliseconds(1);
            var before = store.QueryMemories(S, P, default, memorizedBefore: justAfter30, forgottenSince: justAfter30);
            Assert.True(before.MoveNext());
            Assert.Equal("\"open\"", before.Current.Object.ToString());
            Assert.False(before.MoveNext());
            before.Dispose();
            var rememberedThen = store.QueryMemories(S, P, default, memorizedBefore: justAfter30);
            Assert.False(rememberedThen.MoveNext());
            rememberedThen.Dispose();

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
    public void DeleteAndInsertTheSameQuad_InOneModify_LeavesOneUnbrokenPeriod()
    {
        var clock = new SuppliedClock { Reading = Reading(0) };
        using var store = new QuadStore(Path.Combine(_testDir, "sparql-noop"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent(S, P, "\"open\"");
        clock.Reading = Reading(60);

        var result = SparqlEngine.Update(store,
            $"DELETE {{ {S} {P} ?o }} INSERT {{ {S} {P} ?o }} WHERE {{ {S} {P} ?o }}", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(0, result.AffectedCount);
        store.AcquireReadLock();
        try
        {
            var history = store.QueryMemories(S, P, "\"open\"", includingForgotten: true);
            Assert.True(history.MoveNext());
            Assert.Equal(Reading(0), history.Current.MemorizedAt);
            Assert.Null(history.Current.ForgottenAt); // still remembered, with no boundary
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

    // ---- WAL replay (Q2): the records a Mercury before ADR-061 wrote -------------------------------------------

    [Fact]
    public void Replay_AddAndEndOverIndexesThatHoldThem_ChangesNothing()
    {
        // Session 1 memorizes the fact at minute 0 and forgets it at 60. Then the records a Mercury before ADR-061 wrote
        // for the same two changes, an Add and an End, are appended to the WAL, as if the process had crashed after
        // the indexes were flushed but before the checkpoint. Reopening replays them.
        var dir = Path.Combine(_testDir, "replay-add-end");
        var clock = new SuppliedClock { Reading = Reading(0) };
        var options = new StorageOptions { Clock = clock };
        long subjectId, predicateId, objectId;
        using (var store = new QuadStore(dir, null, null, options))
        {
            store.AddCurrent(S, P, "\"open\"");
            clock.Reading = Reading(60);
            Assert.True(store.DeleteCurrent(S, P, "\"open\""));
            subjectId = store.Atoms.GetAtomId(S);
            predicateId = store.Atoms.GetAtomId(P);
            objectId = store.Atoms.GetAtomId("\"open\"");
        }
        using (var wal = new WriteAheadLog(Path.Combine(dir, "wal.log")))
        {
            wal.Append(LogRecord.CreateAdd(subjectId, predicateId, objectId, Reading(0), DateTimeOffset.MaxValue,
                transactionTimeTicks: Reading(0).ToUnixTimeMilliseconds()));
            wal.Append(LogRecord.CreateEnd(subjectId, predicateId, objectId, Reading(0), Reading(60), graphId: 0,
                transactionTimeTicks: Reading(60).ToUnixTimeMilliseconds()));
        }

        // Reopen twice: each open replays the appended records once more.
        for (int open = 0; open < 2; open++)
        {
            using var store = new QuadStore(dir, null, null, options);
            store.AcquireReadLock();
            try
            {
                var history = store.QueryMemories(S, P, "\"open\"", includingForgotten: true);
                Assert.True(history.MoveNext());
                Assert.Equal(Reading(0), history.Current.MemorizedAt);
                Assert.Equal(Reading(60), history.Current.ForgottenAt);
                Assert.False(history.MoveNext(), "The replayed Add did not bring the ended period back");
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
        // A Mercury before ADR-061 could write a version that starts at the delete's time T, which "holds at T" too:
        // an Add with a stated time. The End record names the old version by its MemorizedAt, so replay cannot end the
        // re-insert instead. The three records are replayed twice: once into indexes that hold none of them, and once
        // more into indexes that hold all three.
        var dir = Path.Combine(_testDir, "replay-same-ms");
        long subjectId, predicateId, objectId;
        using (var store = new QuadStore(dir))
        {
            subjectId = store.Atoms.Intern(S);
            predicateId = store.Atoms.Intern(P);
            objectId = store.Atoms.Intern("\"a\"");
        }
        var t0 = Reading(0);
        var end = Reading(60);
        for (int replay = 0; replay < 2; replay++)
        {
            using (var wal = new WriteAheadLog(Path.Combine(dir, "wal.log")))
            {
                wal.Append(LogRecord.CreateAdd(subjectId, predicateId, objectId, t0, DateTimeOffset.MaxValue,
                    transactionTimeTicks: t0.ToUnixTimeMilliseconds()));
                wal.Append(LogRecord.CreateEnd(subjectId, predicateId, objectId, t0, end, graphId: 0,
                    transactionTimeTicks: end.ToUnixTimeMilliseconds()));
                wal.Append(LogRecord.CreateAdd(subjectId, predicateId, objectId, end, DateTimeOffset.MaxValue,
                    transactionTimeTicks: end.ToUnixTimeMilliseconds() + 1));
            }

            using var store = new QuadStore(dir);
            store.AcquireReadLock();
            try
            {
                var current = store.QueryCurrent(S, P, "\"a\"");
                Assert.True(current.MoveNext(), "The re-inserted version is current after replay");
                current.Dispose();

                var history = store.QueryMemories(S, P, "\"a\"", includingForgotten: true);
                var periods = new List<(DateTimeOffset, DateTimeOffset?)>();
                while (history.MoveNext())
                    periods.Add((history.Current.MemorizedAt, history.Current.ForgottenAt));
                history.Dispose();
                periods.Sort();
                Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (t0, end), (end, null) }, periods);
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
        // version, as it did then. A marked version is in no read: it is neither remembered nor forgotten.
        var dir = Path.Combine(_testDir, "replay-legacy");
        var clock = new SuppliedClock { Reading = Reading(0) };
        var options = new StorageOptions { Clock = clock };
        long subjectId, predicateId, objectId;
        using (var store = new QuadStore(dir, null, null, options))
        {
            store.AddCurrent(S, P, "\"old\"");
            subjectId = store.Atoms.GetAtomId(S);
            predicateId = store.Atoms.GetAtomId(P);
            objectId = store.Atoms.GetAtomId("\"old\"");
        }
        using (var wal = new WriteAheadLog(Path.Combine(dir, "wal.log")))
            wal.Append(LogRecord.CreateRetract(subjectId, predicateId, objectId, DateTimeOffset.MinValue, DateTimeOffset.MaxValue,
                transactionTimeTicks: Reading(60).ToUnixTimeMilliseconds()));

        using (var store = new QuadStore(dir, null, null, options))
        {
            store.AcquireReadLock();
            try
            {
                var history = store.QueryMemories(S, P, "\"old\"", includingForgotten: true);
                Assert.False(history.MoveNext(), "The old meaning marks the version; it does not end it");
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
            // 200 is no operation of this Mercury. (7 was used here until ADR-061 gave 6 to 9 to the decided changes.)
            record.Operation = (LogOperation)200;
            wal.Append(record);
        }

        // The store does not open, and its error names the cause (ADR-061 M7): the log is kept for the Mercury that
        // knows the operation.
        var error = Assert.Throws<SkyOmega.Mercury.Abstractions.CommittedWriteNotAppliedException>(() => new QuadStore(dir));
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Contains("operation 200", error.Message);
    }

    // ---- D7: repair ----------------------------------------------------------------------------------------------

    [Fact]
    public void RepairDeletes_EndsFlaggedEntries_AndRestoresTheNamedEvent()
    {
        // A Retract record flags an entry exactly as a pre-ADR-054 delete did (LegacyIsDeleted, LegacyModifiedAt = the
        // deletion's time). "ended" and "mistake" are memorized at minute 0, "future" at minute 300; all three are
        // flagged at minute 120, "future" before its MemorizedAt.
        var dir = Path.Combine(_testDir, "repair");
        var clock = new SuppliedClock { Reading = Reading(0) };
        var options = new StorageOptions { Clock = clock };
        var flagged = new List<(long Graph, long Subject, long Predicate, long Object, DateTimeOffset MemorizedAt)>();
        using (var store = new QuadStore(dir, null, null, options))
        {
            store.AddCurrent(S, P, "\"ended\"", "<http://ex.org/g1>");
            store.AddCurrent(S, P, "\"mistake\"", "<http://ex.org/g2>");
            clock.Reading = Reading(300);
            store.AddCurrent(S, P, "\"future\"", "<http://ex.org/g1>");
            foreach (var (graph, value, memorizedAt) in new[] { ("<http://ex.org/g1>", "\"ended\"", Reading(0)),
                         ("<http://ex.org/g2>", "\"mistake\"", Reading(0)), ("<http://ex.org/g1>", "\"future\"", Reading(300)) })
                flagged.Add((store.Atoms.GetAtomId(graph), store.Atoms.GetAtomId(S), store.Atoms.GetAtomId(P),
                    store.Atoms.GetAtomId(value), memorizedAt));
        }
        var deletedAt = Reading(120);
        using (var wal = new WriteAheadLog(Path.Combine(dir, "wal.log")))
            foreach (var quad in flagged)
                wal.Append(LogRecord.CreateRetract(quad.Subject, quad.Predicate, quad.Object, quad.MemorizedAt,
                    DateTimeOffset.MaxValue, quad.Graph, transactionTimeTicks: deletedAt.ToUnixTimeMilliseconds()));

        clock.Reading = Reading(400);
        using var repairing = new QuadStore(dir, null, null, options);
        var restore = new[] { ("<http://ex.org/g2>", deletedAt) };
        var justAfter60 = Reading(60).AddMilliseconds(1);

        var dryReport = new StringWriter();
        var dry = repairing.RepairDeletes(restore, dryRun: true, dryReport);
        Assert.Equal((1, 1, 1), dry);
        Assert.Contains("<http://ex.org/g2>  1 entries  -> restore", dryReport.ToString());
        repairing.AcquireReadLock();
        var unchanged = repairing.QueryMemories(S, P, "\"ended\"", "<http://ex.org/g1>", includingForgotten: true);
        Assert.False(unchanged.MoveNext(), "The dry run changed nothing");
        unchanged.Dispose();
        repairing.ReleaseReadLock();

        var repaired = repairing.RepairDeletes(restore, dryRun: false, new StringWriter());

        Assert.Equal((1, 1, 1), repaired);
        repairing.AcquireReadLock();
        try
        {
            var ended = repairing.QueryMemories(S, P, "\"ended\"", "<http://ex.org/g1>", memorizedBefore: justAfter60, forgottenSince: justAfter60);
            Assert.True(ended.MoveNext(), "The ended fact was remembered at minute 60");
            Assert.Equal(deletedAt, ended.Current.ForgottenAt);
            ended.Dispose();
            var endedNow = repairing.QueryCurrent(S, P, "\"ended\"", "<http://ex.org/g1>");
            Assert.False(endedNow.MoveNext(), "and ended at its deletion time");
            endedNow.Dispose();

            var restored = repairing.QueryCurrent(S, P, "\"mistake\"", "<http://ex.org/g2>");
            Assert.True(restored.MoveNext(), "The restored fact never stopped holding");
            restored.Dispose();

            var left = repairing.QueryMemories(S, P, "\"future\"", "<http://ex.org/g1>", includingForgotten: true);
            Assert.False(left.MoveNext(), "Deleted before its MemorizedAt: left as it was, flagged");
            left.Dispose();
        }
        finally
        {
            repairing.ReleaseReadLock();
        }

        var again = repairing.RepairDeletes(Array.Empty<(string, DateTimeOffset)>(), dryRun: false, new StringWriter());
        Assert.Equal((0, 0, 1), again);
    }
}
