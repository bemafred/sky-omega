using System;
using System.Collections.Generic;
using System.IO;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// <see cref="QuadStore.QueryMemories"/>: the store read of the words of Cognitive SPARQL (ADR-061 D6, D10), each
/// memory with the two times of its period (ADR-062 D2). Its bounds are the words' bounds: half-open ranges
/// <c>[since, before)</c>, a bound between two milliseconds taken at the next one, a bound on the forgotten time
/// excluding what is remembered. Each test supplies the store's clock and states its sequence inline.
/// </summary>
public class QueryMemoriesTests : IDisposable
{
    private const string S = "<urn:memories:s>";
    private const string P = "<urn:memories:p>";

    private static readonly DateTimeOffset Hour0 = new(2031, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Hour1 = Hour0.AddHours(1);
    private static readonly DateTimeOffset Hour2 = Hour0.AddHours(2);
    private static readonly DateTimeOffset Hour3 = Hour0.AddHours(3);

    private readonly string _testDir;

    public QueryMemoriesTests()
    {
        var tempPath = TempPath.Test("adr062-query-memories");
        tempPath.MarkOwnership();
        _testDir = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    [Fact]
    public void MemorizedSinceAndBefore_KeepTheMemoriesMemorizedInTheHalfOpenRange()
    {
        var clock = new SuppliedClock { Reading = Hour0 };
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent(S, P, "\"a\"");
        clock.Reading = Hour1;
        store.AddCurrent(S, P, "\"b\"");
        clock.Reading = Hour2;
        store.AddCurrent(S, P, "\"c\"");

        store.AcquireReadLock();
        try
        {
            // [Hour1, Hour2): "b", memorized at Hour1; not "a", before it; not "c", at Hour2
            var memories = store.QueryMemories(S, P, default, memorizedSince: Hour1, memorizedBefore: Hour2);
            var found = new List<string>();
            while (memories.MoveNext())
                found.Add(memories.Current.Object.ToString() + " " + memories.Current.MemorizedAt.ToString("HH:mm"));
            memories.Dispose();
            Assert.Equal(new[] { "\"b\" 01:00" }, found);
        }
        finally { store.ReleaseReadLock(); }
    }

    [Fact]
    public void ABoundBetweenTwoMilliseconds_IsTakenAtTheNextOne()
    {
        // Every stored time is a whole millisecond. SINCE Hour0 + 1 tick is SINCE Hour0 + 1 ms, so a memory at Hour0 is
        // before it; BEFORE Hour0 + 1 tick is BEFORE Hour0 + 1 ms, so a memory at Hour0 is before it too.
        var clock = new SuppliedClock { Reading = Hour0 };
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent(S, P, "\"a\"");
        var oneTickAfter = Hour0.AddTicks(1);

        store.AcquireReadLock();
        try
        {
            var since = store.QueryMemories(S, P, "\"a\"", memorizedSince: oneTickAfter);
            Assert.False(since.MoveNext(), "Memorized at Hour0, before Hour0 + 1 ms");
            since.Dispose();
            var before = store.QueryMemories(S, P, "\"a\"", memorizedBefore: oneTickAfter);
            Assert.True(before.MoveNext(), "Memorized at Hour0, before Hour0 + 1 ms");
            before.Dispose();
        }
        finally { store.ReleaseReadLock(); }
    }

    [Fact]
    public void ABoundOnTheForgottenTime_KeepsTheMemoriesForgottenInTheRange_AndNoneThatIsRemembered()
    {
        var clock = new SuppliedClock { Reading = Hour0 };
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent(S, P, "\"remembered\"");
        store.AddCurrent(S, P, "\"early\"");
        store.AddCurrent(S, P, "\"late\"");
        clock.Reading = Hour1;
        store.DeleteCurrent(S, P, "\"early\"");
        clock.Reading = Hour2;
        store.DeleteCurrent(S, P, "\"late\"");

        store.AcquireReadLock();
        try
        {
            var since = store.QueryMemories(S, P, default, forgottenSince: Hour2);
            var found = new List<string>();
            while (since.MoveNext())
                found.Add(since.Current.Object.ToString() + " " + since.Current.ForgottenAt!.Value.ToString("HH:mm"));
            since.Dispose();
            Assert.Equal(new[] { "\"late\" 02:00" }, found);

            var before = store.QueryMemories(S, P, default, forgottenBefore: Hour2);
            found.Clear();
            while (before.MoveNext())
                found.Add(before.Current.Object.ToString());
            before.Dispose();
            Assert.Equal(new[] { "\"early\"" }, found);

            // A range open at both ends but for the forgotten time still keeps none that is remembered
            var forgotten = store.QueryMemories(S, P, default, forgottenSince: DateTimeOffset.MinValue);
            int count = 0;
            while (forgotten.MoveNext())
            {
                Assert.NotNull(forgotten.Current.ForgottenAt);
                count++;
            }
            forgotten.Dispose();
            Assert.Equal(2, count);
        }
        finally { store.ReleaseReadLock(); }
    }

    [Fact]
    public void WithoutABound_TheReadGivesWhatIsRemembered_AndIncludingForgottenEveryPeriod()
    {
        var clock = new SuppliedClock { Reading = Hour0 };
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent(S, P, "\"a\"");
        store.AddCurrent(S, P, "\"b\"");
        clock.Reading = Hour1;
        store.DeleteCurrent(S, P, "\"b\"");
        clock.Reading = Hour3;

        store.AcquireReadLock();
        try
        {
            var remembered = store.QueryMemories(S, P, default);
            Assert.True(remembered.MoveNext());
            Assert.Equal("\"a\"", remembered.Current.Object.ToString());
            Assert.Equal(Hour0, remembered.Current.MemorizedAt);
            Assert.Null(remembered.Current.ForgottenAt); // absent, not a far-future value (ADR-061 I8)
            Assert.False(remembered.MoveNext());
            remembered.Dispose();

            var every = store.QueryMemories(S, P, default, includingForgotten: true);
            var periods = new List<(string, DateTimeOffset, DateTimeOffset?)>();
            while (every.MoveNext())
                periods.Add((every.Current.Object.ToString(), every.Current.MemorizedAt, every.Current.ForgottenAt));
            every.Dispose();
            periods.Sort();
            Assert.Equal(new (string, DateTimeOffset, DateTimeOffset?)[] { ("\"a\"", Hour0, null), ("\"b\"", Hour0, Hour1) }, periods);
        }
        finally { store.ReleaseReadLock(); }
    }

    [Fact]
    public void ANamedGraph_IsReadAsTheWordsReadIt()
    {
        var clock = new SuppliedClock { Reading = Hour0 };
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Clock = clock });
        store.AddCurrent(S, P, "\"in g\"", "<urn:memories:g>");
        store.AddCurrent(S, P, "\"in the default graph\"");

        store.AcquireReadLock();
        try
        {
            var memories = store.QueryMemories(S, P, default, "<urn:memories:g>");
            Assert.True(memories.MoveNext());
            Assert.Equal("\"in g\"", memories.Current.Object.ToString());
            Assert.Equal("<urn:memories:g>", memories.Current.Graph.ToString());
            Assert.False(memories.MoveNext());
            memories.Dispose();
        }
        finally { store.ReleaseReadLock(); }
    }

    [Theory]
    [InlineData(StoreProfile.Graph)]
    [InlineData(StoreProfile.Minimal)]
    [InlineData(StoreProfile.Reference)]
    public void AStoreThatKeepsNoTimes_RefusesTheRead(StoreProfile profile)
    {
        using var store = new QuadStore(Path.Combine(_testDir, "store"), null, null, new StorageOptions { Profile = profile });

        var refused = Assert.Throws<SkyOmega.Mercury.Abstractions.ProfileCapabilityException>(() =>
            store.QueryMemories(S, P, default));
        Assert.Contains(profile.ToString(), refused.Message);
    }
}
