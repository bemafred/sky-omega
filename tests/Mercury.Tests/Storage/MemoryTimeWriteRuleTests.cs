using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-061 M5 and M6: the write rule, as the table of expected results states it. The Cognitive store is a temporal
/// RDF set: <c>M(t)</c> is the set of quads remembered at clock reading <c>t</c>, and a period <c>[m, f)</c> is a
/// longest run of readings at which a quad is in the set. Inside one clock reading only the last operation on a quad
/// counts, as if it were the only one (M2).
///
/// Each row of M6 is one test per write path: one batch, single writes, one SPARQL request. A batch has one clock
/// reading by construction; for the other two the test supplies the clock and holds it still. In every test
/// <c>m</c> &lt; <c>f</c> &lt; <c>t</c> are clock readings, and the quad's periods are read back through each of the
/// four indexes, which must agree (I13).
/// </summary>
public class MemoryTimeWriteRuleTests : IDisposable
{
    private const string S = "<http://ex.org/s>";
    private const string P = "<http://ex.org/p>";
    private const string O = "\"o\"";

    private static readonly DateTimeOffset m = new(2031, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset f = m.AddMinutes(5);
    private static readonly DateTimeOffset t = m.AddMinutes(10);

    /// <summary>A clock whose reading the test sets. It stands still until the test moves it.</summary>
    private sealed class SuppliedClock : TimeProvider
    {
        public DateTimeOffset Reading { get; set; } = m;
        public override DateTimeOffset GetUtcNow() => Reading;
    }

    private readonly string _testDir;

    public MemoryTimeWriteRuleTests()
    {
        var tempPath = TempPath.Test("adr061-write-rule");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    /// <summary>
    /// The periods of the quad (S, P, O), in time order, as each of the four indexes holds them: GSPO by the subject,
    /// GPOS by the predicate, GOSP by the object, TGSP by a time range over all time. An absent forgotten time is
    /// null. The four must agree (ADR-061 I13): the assertion that they do is here, so every test makes it.
    /// </summary>
    private static (DateTimeOffset MemorizedAt, DateTimeOffset? ForgottenAt)[] PeriodsInEveryIndex(QuadStore store)
    {
        var throughEachIndex = new List<(DateTimeOffset, DateTimeOffset?)[]>();
        store.AcquireReadLock();
        try
        {
            for (int index = 0; index < 4; index++)
            {
                var entries = index switch
                {
                    0 => store.QueryEvolution(S, default, default),
                    1 => store.QueryEvolution(default, P, default),
                    2 => store.QueryEvolution(default, default, O),
                    _ => store.QueryChanges(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, default, default, default),
                };
                var periods = new List<(DateTimeOffset, DateTimeOffset?)>();
                while (entries.MoveNext())
                {
                    var entry = entries.Current;
                    if (entry.Subject.SequenceEqual(S) && entry.Predicate.SequenceEqual(P) && entry.Object.SequenceEqual(O))
                        periods.Add((entry.MemorizedAt, entry.ForgottenAt.Year >= 9000 ? null : entry.ForgottenAt));
                }
                entries.Dispose();
                periods.Sort();
                throughEachIndex.Add(periods.ToArray());
            }
        }
        finally { store.ReleaseReadLock(); }

        Assert.Equal(throughEachIndex[0], throughEachIndex[1]);
        Assert.Equal(throughEachIndex[0], throughEachIndex[2]);
        Assert.Equal(throughEachIndex[0], throughEachIndex[3]);
        return throughEachIndex[0];
    }

    // ---- M6, operations in one batch -----------------------------------------------------------------------------

    [Fact]
    public void OneBatch_NotRemembered_Memorize_BeginsAPeriodAtTheReading()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "OneBatch_NotRemembered_Memorize_BeginsAPeriodAtTheReading"), null, null, new StorageOptions { Clock = clock });

        clock.Reading = t;
        store.BeginBatch();
        store.AddCurrentBatched(S, P, O);
        store.CommitBatch();

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (t, null) }, PeriodsInEveryIndex(store));
    }

    [Fact]
    public void OneBatch_RememberedSinceEarlier_Memorize_ChangesNothing()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "OneBatch_RememberedSinceEarlier_Memorize_ChangesNothing"), null, null, new StorageOptions { Clock = clock });
        clock.Reading = m;
        store.AddCurrent(S, P, O);

        clock.Reading = t;
        store.BeginBatch();
        store.AddCurrentBatched(S, P, O);
        store.CommitBatch();

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (m, null) }, PeriodsInEveryIndex(store));
    }

    [Fact]
    public void OneBatch_RememberedSinceEarlier_Forget_EndsThePeriodAtTheReading()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "OneBatch_RememberedSinceEarlier_Forget_EndsThePeriodAtTheReading"), null, null, new StorageOptions { Clock = clock });
        clock.Reading = m;
        store.AddCurrent(S, P, O);

        clock.Reading = t;
        store.BeginBatch();
        store.DeleteCurrentBatched(S, P, O);
        store.CommitBatch();

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (m, t) }, PeriodsInEveryIndex(store));
    }

    [Fact]
    public void OneBatch_NotRemembered_Forget_ChangesNothing()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "OneBatch_NotRemembered_Forget_ChangesNothing"), null, null, new StorageOptions { Clock = clock });

        clock.Reading = t;
        store.BeginBatch();
        store.DeleteCurrentBatched(S, P, O);
        store.CommitBatch();

        Assert.Empty(PeriodsInEveryIndex(store));
    }

    [Fact]
    public void OneBatch_NotRemembered_MemorizeForget_LeavesNoPeriod()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "OneBatch_NotRemembered_MemorizeForget_LeavesNoPeriod"), null, null, new StorageOptions { Clock = clock });

        clock.Reading = t;
        store.BeginBatch();
        store.AddCurrentBatched(S, P, O);
        store.DeleteCurrentBatched(S, P, O);
        store.CommitBatch();

        Assert.Empty(PeriodsInEveryIndex(store));
    }

    [Fact]
    public void OneBatch_RememberedSinceEarlier_ForgetMemorize_LeavesThePeriodUnbroken()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "OneBatch_RememberedSinceEarlier_ForgetMemorize_LeavesThePeriodUnbroken"), null, null, new StorageOptions { Clock = clock });
        clock.Reading = m;
        store.AddCurrent(S, P, O);

        clock.Reading = t;
        store.BeginBatch();
        store.DeleteCurrentBatched(S, P, O);
        store.AddCurrentBatched(S, P, O);
        store.CommitBatch();

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (m, null) }, PeriodsInEveryIndex(store));
    }

    [Fact]
    public void OneBatch_NotRemembered_MemorizeForgetMemorize_BeginsOnePeriodAtTheReading()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "OneBatch_NotRemembered_MemorizeForgetMemorize_BeginsOnePeriodAtTheReading"), null, null, new StorageOptions { Clock = clock });

        clock.Reading = t;
        store.BeginBatch();
        store.AddCurrentBatched(S, P, O);
        store.DeleteCurrentBatched(S, P, O);
        store.AddCurrentBatched(S, P, O);
        store.CommitBatch();

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (t, null) }, PeriodsInEveryIndex(store));
    }

    [Fact]
    public void OneBatch_RememberedSinceEarlier_ForgetMemorizeForget_EndsThePeriodAtTheReading()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "OneBatch_RememberedSinceEarlier_ForgetMemorizeForget_EndsThePeriodAtTheReading"), null, null, new StorageOptions { Clock = clock });
        clock.Reading = m;
        store.AddCurrent(S, P, O);

        clock.Reading = t;
        store.BeginBatch();
        store.DeleteCurrentBatched(S, P, O);
        store.AddCurrentBatched(S, P, O);
        store.DeleteCurrentBatched(S, P, O);
        store.CommitBatch();

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (m, t) }, PeriodsInEveryIndex(store));
    }

    [Fact]
    public void OneBatch_ForgottenEarlier_Memorize_BeginsASecondPeriod()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "OneBatch_ForgottenEarlier_Memorize_BeginsASecondPeriod"), null, null, new StorageOptions { Clock = clock });
        clock.Reading = m;
        store.AddCurrent(S, P, O);
        clock.Reading = f;
        store.DeleteCurrent(S, P, O);

        clock.Reading = t;
        store.BeginBatch();
        store.AddCurrentBatched(S, P, O);
        store.CommitBatch();

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (m, f), (t, null) }, PeriodsInEveryIndex(store));
    }

    [Fact]
    public void OneBatch_ForgottenEarlier_MemorizeForget_LeavesOnlyTheEarlierPeriod()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "OneBatch_ForgottenEarlier_MemorizeForget_LeavesOnlyTheEarlierPeriod"), null, null, new StorageOptions { Clock = clock });
        clock.Reading = m;
        store.AddCurrent(S, P, O);
        clock.Reading = f;
        store.DeleteCurrent(S, P, O);

        clock.Reading = t;
        store.BeginBatch();
        store.AddCurrentBatched(S, P, O);
        store.DeleteCurrentBatched(S, P, O);
        store.CommitBatch();

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (m, f) }, PeriodsInEveryIndex(store));
    }

    // ---- M6, operations in single writes inside one clock reading ------------------------------------------------

    [Fact]
    public void SingleWrites_NotRemembered_Memorize_BeginsAPeriodAtTheReading()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "SingleWrites_NotRemembered_Memorize_BeginsAPeriodAtTheReading"), null, null, new StorageOptions { Clock = clock });

        clock.Reading = t;
        store.AddCurrent(S, P, O);

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (t, null) }, PeriodsInEveryIndex(store));
    }

    [Fact]
    public void SingleWrites_RememberedSinceEarlier_Memorize_ChangesNothing()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "SingleWrites_RememberedSinceEarlier_Memorize_ChangesNothing"), null, null, new StorageOptions { Clock = clock });
        clock.Reading = m;
        store.AddCurrent(S, P, O);

        clock.Reading = t;
        store.AddCurrent(S, P, O);

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (m, null) }, PeriodsInEveryIndex(store));
    }

    [Fact]
    public void SingleWrites_RememberedSinceEarlier_Forget_EndsThePeriodAtTheReading()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "SingleWrites_RememberedSinceEarlier_Forget_EndsThePeriodAtTheReading"), null, null, new StorageOptions { Clock = clock });
        clock.Reading = m;
        store.AddCurrent(S, P, O);

        clock.Reading = t;
        store.DeleteCurrent(S, P, O);

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (m, t) }, PeriodsInEveryIndex(store));
    }

    [Fact]
    public void SingleWrites_NotRemembered_Forget_ChangesNothing()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "SingleWrites_NotRemembered_Forget_ChangesNothing"), null, null, new StorageOptions { Clock = clock });

        clock.Reading = t;
        store.DeleteCurrent(S, P, O);

        Assert.Empty(PeriodsInEveryIndex(store));
    }

    [Fact]
    public void SingleWrites_NotRemembered_MemorizeForget_LeavesNoPeriod()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "SingleWrites_NotRemembered_MemorizeForget_LeavesNoPeriod"), null, null, new StorageOptions { Clock = clock });

        clock.Reading = t;
        store.AddCurrent(S, P, O);
        store.DeleteCurrent(S, P, O);

        Assert.Empty(PeriodsInEveryIndex(store));
    }

    [Fact]
    public void SingleWrites_RememberedSinceEarlier_ForgetMemorize_LeavesThePeriodUnbroken()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "SingleWrites_RememberedSinceEarlier_ForgetMemorize_LeavesThePeriodUnbroken"), null, null, new StorageOptions { Clock = clock });
        clock.Reading = m;
        store.AddCurrent(S, P, O);

        clock.Reading = t;
        store.DeleteCurrent(S, P, O);
        store.AddCurrent(S, P, O);

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (m, null) }, PeriodsInEveryIndex(store));
    }

    [Fact]
    public void SingleWrites_NotRemembered_MemorizeForgetMemorize_BeginsOnePeriodAtTheReading()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "SingleWrites_NotRemembered_MemorizeForgetMemorize_BeginsOnePeriodAtTheReading"), null, null, new StorageOptions { Clock = clock });

        clock.Reading = t;
        store.AddCurrent(S, P, O);
        store.DeleteCurrent(S, P, O);
        store.AddCurrent(S, P, O);

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (t, null) }, PeriodsInEveryIndex(store));
    }

    [Fact]
    public void SingleWrites_RememberedSinceEarlier_ForgetMemorizeForget_EndsThePeriodAtTheReading()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "SingleWrites_RememberedSinceEarlier_ForgetMemorizeForget_EndsThePeriodAtTheReading"), null, null, new StorageOptions { Clock = clock });
        clock.Reading = m;
        store.AddCurrent(S, P, O);

        clock.Reading = t;
        store.DeleteCurrent(S, P, O);
        store.AddCurrent(S, P, O);
        store.DeleteCurrent(S, P, O);

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (m, t) }, PeriodsInEveryIndex(store));
    }

    [Fact]
    public void SingleWrites_ForgottenEarlier_Memorize_BeginsASecondPeriod()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "SingleWrites_ForgottenEarlier_Memorize_BeginsASecondPeriod"), null, null, new StorageOptions { Clock = clock });
        clock.Reading = m;
        store.AddCurrent(S, P, O);
        clock.Reading = f;
        store.DeleteCurrent(S, P, O);

        clock.Reading = t;
        store.AddCurrent(S, P, O);

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (m, f), (t, null) }, PeriodsInEveryIndex(store));
    }

    [Fact]
    public void SingleWrites_ForgottenEarlier_MemorizeForget_LeavesOnlyTheEarlierPeriod()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "SingleWrites_ForgottenEarlier_MemorizeForget_LeavesOnlyTheEarlierPeriod"), null, null, new StorageOptions { Clock = clock });
        clock.Reading = m;
        store.AddCurrent(S, P, O);
        clock.Reading = f;
        store.DeleteCurrent(S, P, O);

        clock.Reading = t;
        store.AddCurrent(S, P, O);
        store.DeleteCurrent(S, P, O);

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (m, f) }, PeriodsInEveryIndex(store));
    }

    // ---- M6, operations in one SPARQL request inside one clock reading -------------------------------------------

    [Fact]
    public void OneSparqlRequest_NotRemembered_Memorize_BeginsAPeriodAtTheReading()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "OneSparqlRequest_NotRemembered_Memorize_BeginsAPeriodAtTheReading"), null, null, new StorageOptions { Clock = clock });

        clock.Reading = t;
        var update = SparqlEngine.Update(store, "INSERT DATA { <http://ex.org/s> <http://ex.org/p> \"o\" }", TestContext.Current.CancellationToken);
        Assert.True(update.Success, update.ErrorMessage);

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (t, null) }, PeriodsInEveryIndex(store));
    }

    [Fact]
    public void OneSparqlRequest_RememberedSinceEarlier_Memorize_ChangesNothing()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "OneSparqlRequest_RememberedSinceEarlier_Memorize_ChangesNothing"), null, null, new StorageOptions { Clock = clock });
        clock.Reading = m;
        store.AddCurrent(S, P, O);

        clock.Reading = t;
        var update = SparqlEngine.Update(store, "INSERT DATA { <http://ex.org/s> <http://ex.org/p> \"o\" }", TestContext.Current.CancellationToken);
        Assert.True(update.Success, update.ErrorMessage);

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (m, null) }, PeriodsInEveryIndex(store));
    }

    [Fact]
    public void OneSparqlRequest_RememberedSinceEarlier_Forget_EndsThePeriodAtTheReading()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "OneSparqlRequest_RememberedSinceEarlier_Forget_EndsThePeriodAtTheReading"), null, null, new StorageOptions { Clock = clock });
        clock.Reading = m;
        store.AddCurrent(S, P, O);

        clock.Reading = t;
        var update = SparqlEngine.Update(store, "DELETE DATA { <http://ex.org/s> <http://ex.org/p> \"o\" }", TestContext.Current.CancellationToken);
        Assert.True(update.Success, update.ErrorMessage);

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (m, t) }, PeriodsInEveryIndex(store));
    }

    [Fact]
    public void OneSparqlRequest_NotRemembered_Forget_ChangesNothing()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "OneSparqlRequest_NotRemembered_Forget_ChangesNothing"), null, null, new StorageOptions { Clock = clock });

        clock.Reading = t;
        var update = SparqlEngine.Update(store, "DELETE DATA { <http://ex.org/s> <http://ex.org/p> \"o\" }", TestContext.Current.CancellationToken);
        Assert.True(update.Success, update.ErrorMessage);

        Assert.Empty(PeriodsInEveryIndex(store));
    }

    [Fact]
    public void OneSparqlRequest_NotRemembered_MemorizeForget_LeavesNoPeriod()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "OneSparqlRequest_NotRemembered_MemorizeForget_LeavesNoPeriod"), null, null, new StorageOptions { Clock = clock });

        clock.Reading = t;
        var update = SparqlEngine.Update(store,
            "INSERT DATA { <http://ex.org/s> <http://ex.org/p> \"o\" } ; " +
            "DELETE DATA { <http://ex.org/s> <http://ex.org/p> \"o\" }",
            TestContext.Current.CancellationToken);
        Assert.True(update.Success, update.ErrorMessage);

        Assert.Empty(PeriodsInEveryIndex(store));
    }

    [Fact]
    public void OneSparqlRequest_RememberedSinceEarlier_ForgetMemorize_LeavesThePeriodUnbroken()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "OneSparqlRequest_RememberedSinceEarlier_ForgetMemorize_LeavesThePeriodUnbroken"), null, null, new StorageOptions { Clock = clock });
        clock.Reading = m;
        store.AddCurrent(S, P, O);

        clock.Reading = t;
        var update = SparqlEngine.Update(store,
            "DELETE DATA { <http://ex.org/s> <http://ex.org/p> \"o\" } ; " +
            "INSERT DATA { <http://ex.org/s> <http://ex.org/p> \"o\" }",
            TestContext.Current.CancellationToken);
        Assert.True(update.Success, update.ErrorMessage);

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (m, null) }, PeriodsInEveryIndex(store));
    }

    [Fact]
    public void OneSparqlRequest_NotRemembered_MemorizeForgetMemorize_BeginsOnePeriodAtTheReading()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "OneSparqlRequest_NotRemembered_MemorizeForgetMemorize_BeginsOnePeriodAtTheReading"), null, null, new StorageOptions { Clock = clock });

        clock.Reading = t;
        var update = SparqlEngine.Update(store,
            "INSERT DATA { <http://ex.org/s> <http://ex.org/p> \"o\" } ; " +
            "DELETE DATA { <http://ex.org/s> <http://ex.org/p> \"o\" } ; " +
            "INSERT DATA { <http://ex.org/s> <http://ex.org/p> \"o\" }",
            TestContext.Current.CancellationToken);
        Assert.True(update.Success, update.ErrorMessage);

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (t, null) }, PeriodsInEveryIndex(store));
    }

    [Fact]
    public void OneSparqlRequest_RememberedSinceEarlier_ForgetMemorizeForget_EndsThePeriodAtTheReading()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "OneSparqlRequest_RememberedSinceEarlier_ForgetMemorizeForget_EndsThePeriodAtTheReading"), null, null, new StorageOptions { Clock = clock });
        clock.Reading = m;
        store.AddCurrent(S, P, O);

        clock.Reading = t;
        var update = SparqlEngine.Update(store,
            "DELETE DATA { <http://ex.org/s> <http://ex.org/p> \"o\" } ; " +
            "INSERT DATA { <http://ex.org/s> <http://ex.org/p> \"o\" } ; " +
            "DELETE DATA { <http://ex.org/s> <http://ex.org/p> \"o\" }",
            TestContext.Current.CancellationToken);
        Assert.True(update.Success, update.ErrorMessage);

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (m, t) }, PeriodsInEveryIndex(store));
    }

    [Fact]
    public void OneSparqlRequest_ForgottenEarlier_Memorize_BeginsASecondPeriod()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "OneSparqlRequest_ForgottenEarlier_Memorize_BeginsASecondPeriod"), null, null, new StorageOptions { Clock = clock });
        clock.Reading = m;
        store.AddCurrent(S, P, O);
        clock.Reading = f;
        store.DeleteCurrent(S, P, O);

        clock.Reading = t;
        var update = SparqlEngine.Update(store, "INSERT DATA { <http://ex.org/s> <http://ex.org/p> \"o\" }", TestContext.Current.CancellationToken);
        Assert.True(update.Success, update.ErrorMessage);

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (m, f), (t, null) }, PeriodsInEveryIndex(store));
    }

    [Fact]
    public void OneSparqlRequest_ForgottenEarlier_MemorizeForget_LeavesOnlyTheEarlierPeriod()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "OneSparqlRequest_ForgottenEarlier_MemorizeForget_LeavesOnlyTheEarlierPeriod"), null, null, new StorageOptions { Clock = clock });
        clock.Reading = m;
        store.AddCurrent(S, P, O);
        clock.Reading = f;
        store.DeleteCurrent(S, P, O);

        clock.Reading = t;
        var update = SparqlEngine.Update(store,
            "INSERT DATA { <http://ex.org/s> <http://ex.org/p> \"o\" } ; " +
            "DELETE DATA { <http://ex.org/s> <http://ex.org/p> \"o\" }",
            TestContext.Current.CancellationToken);
        Assert.True(update.Success, update.ErrorMessage);

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (m, f) }, PeriodsInEveryIndex(store));
    }

    // ---- M6, across two clock readings: nothing is merged ---------------------------------------------------------

    [Fact]
    public void MemorizedAtOneReading_ForgottenAtTheNext_IsARealPeriodOfOneMillisecond()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "one-millisecond"), null, null, new StorageOptions { Clock = clock });

        clock.Reading = t;
        store.AddCurrent(S, P, O);
        clock.Reading = t.AddMilliseconds(1);
        store.DeleteCurrent(S, P, O);

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (t, t.AddMilliseconds(1)) }, PeriodsInEveryIndex(store));
    }

    [Fact]
    public void ForgottenAtOneReading_MemorizedAtTheNext_IsTwoPeriods()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "two-periods"), null, null, new StorageOptions { Clock = clock });
        clock.Reading = m;
        store.AddCurrent(S, P, O);

        clock.Reading = t;
        store.DeleteCurrent(S, P, O);
        clock.Reading = t.AddMilliseconds(1);
        store.AddCurrent(S, P, O);

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (m, t), (t.AddMilliseconds(1), null) }, PeriodsInEveryIndex(store));
    }

    [Fact]
    public void OneSparqlRequest_WhoseOperationsFallInTwoReadings_GivesEachReadingItsOwnResult()
    {
        // A request is not a moment (I17). Here the clock moves between the request's two operations, so the insert
        // and the delete are in different readings and a period of that length is left. With the clock still, the
        // same request leaves none (OneSparqlRequest_NotRemembered_MemorizeForget_LeavesNoPeriod).
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "request-two-readings"), null, null, new StorageOptions { Clock = clock });

        clock.Reading = t;
        var insert = SparqlEngine.Update(store, "INSERT DATA { <http://ex.org/s> <http://ex.org/p> \"o\" }", TestContext.Current.CancellationToken);
        Assert.True(insert.Success, insert.ErrorMessage);
        clock.Reading = t.AddMilliseconds(7);
        var delete = SparqlEngine.Update(store, "DELETE DATA { <http://ex.org/s> <http://ex.org/p> \"o\" }", TestContext.Current.CancellationToken);
        Assert.True(delete.Success, delete.ErrorMessage);

        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (t, t.AddMilliseconds(7)) }, PeriodsInEveryIndex(store));
    }

    // ---- M11's fourth row: a fact told again while it is remembered ----------------------------------------------------

    [Fact]
    public void MemorizeAgain_WhileRemembered_WithAnotherEntryBetweenInTimeOrder_AddsNoEntryToAnyIndex()
    {
        // The other quad is written in the same request as the first telling, after it: it has the same memorized
        // time and a larger atom, so it lies between the two tellings in the time-first index. Before ADR-061 that
        // index alone gained an open entry, and after a delete a read by time still returned the fact.
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "told-again"), null, null, new StorageOptions { Clock = clock });

        clock.Reading = m;
        var first = SparqlEngine.Update(store,
            "INSERT DATA { <http://ex.org/s> <http://ex.org/p> \"o\" . <http://ex.org/s> <http://ex.org/p> \"other\" }",
            TestContext.Current.CancellationToken);
        Assert.True(first.Success, first.ErrorMessage);

        clock.Reading = f;
        Assert.False(store.AddCurrent(S, P, O));
        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (m, null) }, PeriodsInEveryIndex(store));

        clock.Reading = t;
        Assert.True(store.DeleteCurrent(S, P, O));
        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (m, t) }, PeriodsInEveryIndex(store));
    }

    // ---- D2: what an operation counts as affected ----------------------------------------------------------------------

    [Fact]
    public void AnOperation_ReportsAChange_OnlyWhenItChangesWhetherTheQuadIsRemembered()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "counts-single"), null, null, new StorageOptions { Clock = clock });

        clock.Reading = m;
        Assert.False(store.DeleteCurrent(S, P, O));   // not remembered: nothing to forget
        Assert.True(store.AddCurrent(S, P, O));       // becomes remembered
        Assert.False(store.AddCurrent(S, P, O));      // already remembered
        Assert.True(store.DeleteCurrent(S, P, O));    // memorized in this same reading: remembered, and now not
        Assert.False(store.DeleteCurrent(S, P, O));   // not remembered any more

        Assert.Empty(PeriodsInEveryIndex(store));
    }

    [Fact]
    public void InABatch_EachOperation_ReportsAgainstWhatTheBatchHasAskedSoFar()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "counts-batch"), null, null, new StorageOptions { Clock = clock });
        clock.Reading = m;
        store.AddCurrent(S, P, O);

        clock.Reading = t;
        store.BeginBatch();
        Assert.False(store.AddCurrentBatched(S, P, O));     // remembered since m
        Assert.True(store.DeleteCurrentBatched(S, P, O));   // remembered, and now not
        Assert.False(store.DeleteCurrentBatched(S, P, O));
        Assert.True(store.AddCurrentBatched(S, P, O));      // not remembered, and now remembered again
        store.CommitBatch();

        // The last operation of the reading is a memorize of a quad the store held as remembered: nothing changed.
        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (m, null) }, PeriodsInEveryIndex(store));
    }

    [Fact]
    public void InsertData_OfRememberedFacts_AffectsNone_AndDeleteData_OfAFactMemorizedInTheSameReading_AffectsOne()
    {
        var clock = new SuppliedClock();
        using var store = new QuadStore(Path.Combine(_testDir, "counts-sparql"), null, null, new StorageOptions { Clock = clock });

        clock.Reading = t;
        var first = SparqlEngine.Update(store, "INSERT DATA { <http://ex.org/s> <http://ex.org/p> \"o\" . <http://ex.org/s> <http://ex.org/p> \"other\" }", TestContext.Current.CancellationToken);
        Assert.True(first.Success, first.ErrorMessage);
        Assert.Equal(2, first.AffectedCount);

        var again = SparqlEngine.Update(store, "INSERT DATA { <http://ex.org/s> <http://ex.org/p> \"o\" . <http://ex.org/s> <http://ex.org/p> \"other\" }", TestContext.Current.CancellationToken);
        Assert.True(again.Success, again.ErrorMessage);
        Assert.Equal(0, again.AffectedCount);

        var delete = SparqlEngine.Update(store, "DELETE DATA { <http://ex.org/s> <http://ex.org/p> \"o\" }", TestContext.Current.CancellationToken);
        Assert.True(delete.Success, delete.ErrorMessage);
        Assert.Equal(1, delete.AffectedCount);

        Assert.Empty(PeriodsInEveryIndex(store));
    }

    // ---- I15: what a store holds after it is closed and opened again ----------------------------------------------------

    [Fact]
    public void AfterReopening_TheStoreHoldsThePeriodsAndTheEntryCountItHadBefore()
    {
        var clock = new SuppliedClock();
        var dir = Path.Combine(_testDir, "reopen");
        using (var store = new QuadStore(dir, null, null, new StorageOptions { Clock = clock }))
        {
            clock.Reading = m;
            store.AddCurrent(S, P, O);
            store.AddCurrent(S, P, "\"kept\"");
            clock.Reading = f;
            store.DeleteCurrent(S, P, O);
            clock.Reading = t;
            store.AddCurrent(S, P, O);                    // a second period
            store.AddCurrent(S, P, "\"gone\"");
            store.DeleteCurrent(S, P, "\"gone\"");         // memorized and forgotten at t: no period, and no entry
            Assert.Equal(3, store.GetStatistics().QuadCount);
        }

        using var reopened = new QuadStore(dir, null, null, new StorageOptions { Clock = clock });
        Assert.Equal(3, reopened.GetStatistics().QuadCount);
        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (m, f), (t, null) }, PeriodsInEveryIndex(reopened));
    }
}
