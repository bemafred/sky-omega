using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-061 I15 and M7: recovery changes nothing. The log holds the change that was decided at each write (begin a
/// period, end it, remove it, continue it), and replaying the log onto indexes that have each applied some first part
/// of it gives the periods an uninterrupted run gives.
///
/// A separate process (tests/Mercury.CrashTarget, the two periods modes) writes and is killed, so the files are what
/// a killed process leaves. Its first run memorizes four quads at clock reading 1 and exits cleanly: the test copies
/// that store, whose index files have applied the log up to there. Its second run makes each of the four decided
/// changes at readings 2 to 6 and is killed: those index files have applied all of the log, and the log is not
/// truncated. The test then opens stores in which some index files are the earlier ones and some the later ones.
/// </summary>
public class MemoryTimeRecoveryTests : IDisposable
{
    private const string S = "<urn:periods:s>";
    private const string P = "<urn:periods:p>";

    private static DateTimeOffset Reading(int n) => new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(n);

    private sealed class SuppliedClock : TimeProvider
    {
        public DateTimeOffset Reading { get; set; }
        public override DateTimeOffset GetUtcNow() => Reading;
    }

    private readonly string _root;

    public MemoryTimeRecoveryTests()
    {
        var tempPath = TempPath.Test("adr061-recovery");
        tempPath.MarkOwnership();
        _root = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_root);

    /// <summary>
    /// The periods of the quad (S, P, <paramref name="o"/>), in time order, as each of the four indexes holds them.
    /// An absent forgotten time is null. The four must agree (ADR-061 I13): the assertion that they do is here.
    /// </summary>
    private static (DateTimeOffset MemorizedAt, DateTimeOffset? ForgottenAt)[] PeriodsInEveryIndex(QuadStore store, string o)
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
                    2 => store.QueryEvolution(default, default, o),
                    _ => store.QueryChanges(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, default, default, default),
                };
                var periods = new List<(DateTimeOffset, DateTimeOffset?)>();
                while (entries.MoveNext())
                {
                    var entry = entries.Current;
                    if (entry.Subject.SequenceEqual(S) && entry.Predicate.SequenceEqual(P) && entry.Object.SequenceEqual(o))
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

    [Theory]
    [InlineData("")]                                    // every index has applied all of the log
    [InlineData("gspo gpos gosp tgsp")]                 // every index is at the earlier point
    [InlineData("gpos gosp tgsp")]                      // GSPO is ahead of the other three
    [InlineData("gspo gpos gosp")]                      // TGSP is ahead of the other three
    [InlineData("gspo")]                                // GSPO is behind the other three
    [InlineData("tgsp")]                                // TGSP is behind the other three
    public void ReplayingTheLog_OntoIndexesAtDifferentPoints_GivesThePeriodsOfAnUninterruptedRun(string indexesAtTheEarlierPoint)
    {
        var killed = Path.Combine(_root, "killed");
        var earlier = Path.Combine(_root, "earlier");

        CrashTarget.Run("periods-first", killed, "unused", 0).AssertExitedCleanly();
        Directory.CreateDirectory(earlier);
        foreach (var file in Directory.GetFiles(killed))
            File.Copy(file, Path.Combine(earlier, Path.GetFileName(file)));

        CrashTarget.Run("periods-killed", killed, "unused", 0).AssertKilledAfter("changed");

        foreach (var index in indexesAtTheEarlierPoint.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            File.Copy(Path.Combine(earlier, index + ".tdb"), Path.Combine(killed, index + ".tdb"), overwrite: true);

        var clock = new SuppliedClock { Reading = Reading(7) };
        using var store = new QuadStore(killed, null, null, new StorageOptions { Clock = clock });

        // K: memorized at 1 and never touched again.
        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (Reading(1), null) }, PeriodsInEveryIndex(store, "\"K\""));
        // A0: forgotten and memorized again inside reading 2. Its period continues.
        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (Reading(1), null) }, PeriodsInEveryIndex(store, "\"A0\""));
        // A1: forgotten at 3, memorized again at 4. Two periods.
        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (Reading(1), Reading(3)), (Reading(4), null) }, PeriodsInEveryIndex(store, "\"A1\""));
        // B: memorized and forgotten inside reading 4. No period.
        Assert.Empty(PeriodsInEveryIndex(store, "\"B\""));
        // C: forgotten in the batch at 5.
        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (Reading(1), Reading(5)) }, PeriodsInEveryIndex(store, "\"C\""));
        // D: memorized in the batch at 5, forgotten at 6.
        Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (Reading(5), Reading(6)) }, PeriodsInEveryIndex(store, "\"D\""));

        // Six entries: K, A0, A1 twice, C, D. B left none.
        Assert.Equal(6, store.GetStatistics().QuadCount);
    }

    // An index file's header holds its entry count and the transaction that count is exact for (ADR-058: EntryCount at
    // byte 16, CountTxId at byte 32). A killed process can leave the count behind the pages; a power loss can leave it
    // ahead of them. The two tests below build each case by moving those two fields between the earlier file and the
    // later one, and read the count back from the header after a clean close.

    [Theory]
    [InlineData("gspo")]
    [InlineData("tgsp")]
    public void ACountSavedBeforeTheChanges_OnPagesThatHaveThem_IsBroughtToTheRightCount(string index)
    {
        var killed = Path.Combine(_root, "killed");
        var earlier = Path.Combine(_root, "earlier");
        CrashTarget.Run("periods-first", killed, "unused", 0).AssertExitedCleanly();
        Directory.CreateDirectory(earlier);
        foreach (var file in Directory.GetFiles(killed))
            File.Copy(file, Path.Combine(earlier, Path.GetFileName(file)));
        CrashTarget.Run("periods-killed", killed, "unused", 0).AssertKilledAfter("changed");

        // The later pages, with the earlier count: four entries, exact for the first run's transaction.
        var earlierHeader = File.ReadAllBytes(Path.Combine(earlier, index + ".tdb")).AsSpan(0, 40).ToArray();
        Assert.Equal(4, BitConverter.ToInt64(earlierHeader, 16));
        using (var file = new FileStream(Path.Combine(killed, index + ".tdb"), FileMode.Open, FileAccess.ReadWrite))
        {
            file.Position = 16;
            file.Write(earlierHeader, 16, 8);
            file.Position = 32;
            file.Write(earlierHeader, 32, 8);
        }

        var clock = new SuppliedClock { Reading = Reading(7) };
        using (var store = new QuadStore(killed, null, null, new StorageOptions { Clock = clock }))
            Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (Reading(1), Reading(3)), (Reading(4), null) }, PeriodsInEveryIndex(store, "\"A1\""));

        Assert.Equal(6, BitConverter.ToInt64(File.ReadAllBytes(Path.Combine(killed, index + ".tdb")), 16));
    }

    [Theory]
    [InlineData("gspo")]
    [InlineData("tgsp")]
    public void ACountSavedAfterTheChanges_OnPagesThatLackThem_StaysTheRightCount(string index)
    {
        var killed = Path.Combine(_root, "killed");
        var earlier = Path.Combine(_root, "earlier");
        CrashTarget.Run("periods-first", killed, "unused", 0).AssertExitedCleanly();
        Directory.CreateDirectory(earlier);
        foreach (var file in Directory.GetFiles(killed))
            File.Copy(file, Path.Combine(earlier, Path.GetFileName(file)));
        CrashTarget.Run("periods-killed", killed, "unused", 0).AssertKilledAfter("changed");

        // The earlier pages, with the later count: six entries, exact for the second run's last transaction.
        var laterHeader = File.ReadAllBytes(Path.Combine(killed, index + ".tdb")).AsSpan(0, 40).ToArray();
        Assert.Equal(6, BitConverter.ToInt64(laterHeader, 16));
        File.Copy(Path.Combine(earlier, index + ".tdb"), Path.Combine(killed, index + ".tdb"), overwrite: true);
        using (var file = new FileStream(Path.Combine(killed, index + ".tdb"), FileMode.Open, FileAccess.ReadWrite))
        {
            file.Position = 16;
            file.Write(laterHeader, 16, 8);
            file.Position = 32;
            file.Write(laterHeader, 32, 8);
        }

        var clock = new SuppliedClock { Reading = Reading(7) };
        using (var store = new QuadStore(killed, null, null, new StorageOptions { Clock = clock }))
            Assert.Equal(new (DateTimeOffset, DateTimeOffset?)[] { (Reading(1), Reading(3)), (Reading(4), null) }, PeriodsInEveryIndex(store, "\"A1\""));

        Assert.Equal(6, BitConverter.ToInt64(File.ReadAllBytes(Path.Combine(killed, index + ".tdb")), 16));
    }
}
