using System;
using System.IO;
using System.Linq;
using SkyOmega.Mercury;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-061 D4, I11, I12: a store has one clock (<see cref="StorageOptions.Clock"/>). Every memorized and forgotten
/// time is a reading of it, NOW() is a reading of it, and "now" in a read is a reading of it. No writer states a
/// time: a test that needs two operations inside one clock reading, or in different ones, supplies the readings.
/// Each test opens its own store directory and states its sequence inline.
/// </summary>
public class StoreClockTests : IDisposable
{
    private const string S = "<http://ex.org/s>";
    private const string P = "<http://ex.org/p>";
    private const string O = "\"o\"";

    /// <summary>A clock whose reading the test sets. It stands still until the test moves it.</summary>
    private sealed class SuppliedClock : TimeProvider
    {
        public DateTimeOffset Reading { get; set; }
        public override DateTimeOffset GetUtcNow() => Reading;
    }

    private readonly string _testDir;

    public StoreClockTests()
    {
        var tempPath = TempPath.Test("adr061-clock");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    [Fact]
    public void AddCurrent_StampsTheMemorizedTimeAndTheTransactionTime_WithOneReadingOfTheStoresClock()
    {
        var reading = new DateTimeOffset(2031, 3, 4, 5, 6, 7, 890, TimeSpan.Zero);
        var clock = new SuppliedClock { Reading = reading };
        using var store = new QuadStore(Path.Combine(_testDir, "add"), null, null, new StorageOptions { Clock = clock });

        store.AddCurrent(S, P, O);

        store.AcquireReadLock();
        try
        {
            var entries = store.QueryEvolution(S, P, O);
            Assert.True(entries.MoveNext());
            Assert.Equal(reading, entries.Current.MemorizedAt);
            Assert.Equal(reading, entries.Current.LegacyTransactionTime);
            Assert.False(entries.MoveNext());
            entries.Dispose();
        }
        finally { store.ReleaseReadLock(); }
    }

    [Fact]
    public void ABatch_StampsEveryFact_WithTheReadingTakenWhenItBegan()
    {
        var began = new DateTimeOffset(2031, 3, 4, 5, 6, 7, 0, TimeSpan.Zero);
        var clock = new SuppliedClock { Reading = began };
        using var store = new QuadStore(Path.Combine(_testDir, "batch"), null, null, new StorageOptions { Clock = clock });

        store.BeginBatch();
        store.AddCurrentBatched(S, P, "\"first\"");
        clock.Reading = began.AddSeconds(30);
        store.AddCurrentBatched(S, P, "\"second\"");
        store.CommitBatch();

        store.AcquireReadLock();
        try
        {
            foreach (var value in new[] { "\"first\"", "\"second\"" })
            {
                var entries = store.QueryEvolution(S, P, value);
                Assert.True(entries.MoveNext());
                Assert.Equal(began, entries.Current.MemorizedAt);
                entries.Dispose();
            }
        }
        finally { store.ReleaseReadLock(); }
    }

    [Fact]
    public void DeleteCurrent_StampsTheForgottenTime_WithTheStoresClock()
    {
        var memorized = new DateTimeOffset(2031, 3, 4, 5, 6, 7, 0, TimeSpan.Zero);
        var forgotten = memorized.AddMinutes(10);
        var clock = new SuppliedClock { Reading = memorized };
        using var store = new QuadStore(Path.Combine(_testDir, "delete"), null, null, new StorageOptions { Clock = clock });

        store.AddCurrent(S, P, O);
        clock.Reading = forgotten;
        Assert.True(store.DeleteCurrent(S, P, O));

        store.AcquireReadLock();
        try
        {
            var entries = store.QueryEvolution(S, P, O);
            Assert.True(entries.MoveNext());
            Assert.Equal(memorized, entries.Current.MemorizedAt);
            Assert.Equal(forgotten, entries.Current.ForgottenAt);
            entries.Dispose();
        }
        finally { store.ReleaseReadLock(); }
    }

    [Fact]
    public void Now_InAQuery_IsAReadingOfTheStoresClock()
    {
        var clock = new SuppliedClock { Reading = new DateTimeOffset(2031, 3, 4, 5, 6, 7, 0, TimeSpan.Zero) };
        using var store = new QuadStore(Path.Combine(_testDir, "now"), null, null, new StorageOptions { Clock = clock });

        var result = SparqlEngine.Query(store, "SELECT (NOW() AS ?n) WHERE {}", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("2031-03-04T05:06:07", result.Rows!.Single().Values.Single());
    }

    [Fact]
    public void Now_InAnUpdate_IsAReadingOfTheStoresClock()
    {
        var clock = new SuppliedClock { Reading = new DateTimeOffset(2031, 3, 4, 5, 6, 7, 0, TimeSpan.Zero) };
        using var store = new QuadStore(Path.Combine(_testDir, "now-update"), null, null, new StorageOptions { Clock = clock });

        var update = SparqlEngine.Update(store, "INSERT { <http://ex.org/s> <http://ex.org/at> ?n } WHERE { BIND(NOW() AS ?n) }", TestContext.Current.CancellationToken);
        Assert.True(update.Success, update.ErrorMessage);

        var result = SparqlEngine.Query(store, "SELECT ?n WHERE { <http://ex.org/s> <http://ex.org/at> ?n }", TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("2031-03-04T05:06:07", result.Rows!.Single().Values.Single());
    }

    [Fact]
    public void ACurrentRead_UsesTheStoresClock_SoAFactMemorizedAtItsReadingIsRemembered()
    {
        // The supplied reading is far later than the system clock. A read that compared with the system clock would
        // take the fact for one that is not memorized yet.
        var clock = new SuppliedClock { Reading = new DateTimeOffset(2190, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        using var store = new QuadStore(Path.Combine(_testDir, "read"), null, null, new StorageOptions { Clock = clock });

        store.AddCurrent(S, P, O, "<http://ex.org/g>");

        var facts = SparqlEngine.Query(store, "SELECT ?o WHERE { GRAPH <http://ex.org/g> { <http://ex.org/s> <http://ex.org/p> ?o } }", TestContext.Current.CancellationToken);
        Assert.True(facts.Success, facts.ErrorMessage);
        Assert.Single(facts.Rows!);

        var graphs = SparqlEngine.Query(store, "SELECT DISTINCT ?g WHERE { GRAPH ?g { ?s ?p ?o } }", TestContext.Current.CancellationToken);
        Assert.True(graphs.Success, graphs.ErrorMessage);
        Assert.Equal("<http://ex.org/g>", graphs.Rows!.Single().Values.Single());
    }
}
