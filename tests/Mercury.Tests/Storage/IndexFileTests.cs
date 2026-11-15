using System;
using System.IO;
using System.Threading;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// The file a B+Tree index lives in (<see cref="BTreeFile"/>), in two respects.
///
/// Readers at the same time: readers of a store hold a lock they share, so several of them ask the index file for
/// pages at once. Each must be given the page it asked for.
///
/// Growth: an index file starts at an initial size and must hold a tree that outgrows it. A page never moves, so a
/// pointer taken before the file grew reaches its page afterwards.
///
/// The first tests use the file alone and mark each page with its own number. The last ones use a store whose
/// index files start small.
/// </summary>
public unsafe class IndexFileTests : IDisposable
{
    private const long Magic = 0x494E44455846494CL;                 // not the magic number of any index of Mercury
    private const int LongsInAPage = BTreeFile.PageSize / sizeof(long);

    private readonly string _testDir;

    public IndexFileTests()
    {
        var tempPath = TempPath.Test("index-file");
        tempPath.MarkOwnership();
        _testDir = tempPath;
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    [Fact]
    public void ReadersAtTheSameTime_AreEachGivenThePageTheyAskedFor()
    {
        const int Pages = 4000;
        const int Readers = 8;
        var path = Path.Combine(_testDir, "readers.tdb");

        using (var written = new BTreeFile(path, 128L << 20, bulkMode: true, bulkModeFloor: 1, magicNumber: Magic))
        {
            *written.GetPagePointer<long>(1) = 1;
            while (written.NextPageId <= Pages)
            {
                var pageId = written.AllocatePage();
                *written.GetPagePointer<long>(pageId) = pageId;
            }
        }

        // Opened again, so that no page has been asked for yet.
        using var file = new BTreeFile(path, 128L << 20, bulkMode: true, bulkModeFloor: 1, magicNumber: Magic);
        long pagesThatWereNotTheOneAskedFor = 0;
        using var start = new Barrier(Readers);
        var readers = new Thread[Readers];
        for (int reader = 0; reader < Readers; reader++)
        {
            int seed = reader;
            readers[reader] = new Thread(() =>
            {
                var random = new Random(seed);
                start.SignalAndWait();
                for (int asked = 0; asked < 20_000; asked++)
                {
                    long pageId = random.Next(1, Pages + 1);
                    if (*file.GetPagePointer<long>(pageId) != pageId)
                        Interlocked.Increment(ref pagesThatWereNotTheOneAskedFor);
                }
            });
            readers[reader].Start();
        }
        foreach (var reader in readers) reader.Join();

        Assert.Equal(0, pagesThatWereNotTheOneAskedFor);
    }

    [Fact]
    public void AFileThatOutgrowsItsInitialSize_HoldsEveryPage_AlsoWhenOpenedAgain()
    {
        const int Pages = 1000;                                     // 16 MB of pages; the file starts at 1 MB
        var path = Path.Combine(_testDir, "grows.tdb");

        using (var file = new BTreeFile(path, 1L << 20, bulkMode: true, bulkModeFloor: 1, magicNumber: Magic))
        {
            var first = file.GetPagePointer<long>(1);
            first[0] = 1;
            first[LongsInAPage - 1] = -1;
            while (file.NextPageId <= Pages)
            {
                var pageId = file.AllocatePage();
                var page = file.GetPagePointer<long>(pageId);
                page[0] = pageId;                                   // the first eight bytes of the page
                page[LongsInAPage - 1] = -pageId;                   // and the last eight
            }

            for (long pageId = 1; pageId <= Pages; pageId++)
            {
                var page = file.GetPagePointer<long>(pageId);
                Assert.Equal(pageId, page[0]);
                Assert.Equal(-pageId, page[LongsInAPage - 1]);
            }
        }

        Assert.True(new FileInfo(path).Length >= (Pages + 1L) * BTreeFile.PageSize);

        using var reopened = new BTreeFile(path, 1L << 20, bulkMode: true, bulkModeFloor: 1, magicNumber: Magic);
        Assert.Equal(Pages + 1, reopened.NextPageId);
        for (long pageId = 1; pageId <= Pages; pageId++)
        {
            var page = reopened.GetPagePointer<long>(pageId);
            Assert.Equal(pageId, page[0]);
            Assert.Equal(-pageId, page[LongsInAPage - 1]);
        }
    }

    [Fact]
    public void APointerTakenBeforeTheFileGrew_ReachesItsPageAfterwards()
    {
        var path = Path.Combine(_testDir, "pointer.tdb");
        using var file = new BTreeFile(path, 1L << 20, bulkMode: true, bulkModeFloor: 1, magicNumber: Magic);

        var takenBefore = file.GetPagePointer<long>(1);
        *takenBefore = 11;

        while (file.NextPageId <= 1000) file.AllocatePage();        // the file grows several times

        // A tree that splits a page writes through the pointer it held when it asked for the new page.
        *takenBefore = 12;

        Assert.Equal(12, *file.GetPagePointer<long>(1));
        Assert.True(takenBefore == file.GetPagePointer<long>(1));
    }

    [Fact]
    public void AnInitialSizeThatIsNotAWholeNumberOfPages_LosesNoPageWhereTheFileGrew()
    {
        // 1,000,000 bytes hold 61 whole pages (0 to 60). Page 61 would lie across the end of the file.
        var path = Path.Combine(_testDir, "uneven.tdb");

        using (var file = new BTreeFile(path, 1_000_000, bulkMode: true, bulkModeFloor: 1, magicNumber: Magic))
        {
            while (file.NextPageId <= 200) file.AllocatePage();
            for (long pageId = 58; pageId <= 64; pageId++)
            {
                var page = file.GetPagePointer<long>(pageId);
                for (int i = 0; i < LongsInAPage; i++) page[i] = pageId;
            }
            for (long pageId = 58; pageId <= 64; pageId++)
            {
                var page = file.GetPagePointer<long>(pageId);
                for (int i = 0; i < LongsInAPage; i++) Assert.Equal(pageId, page[i]);
            }
        }

        using var reopened = new BTreeFile(path, 1_000_000, bulkMode: true, bulkModeFloor: 1, magicNumber: Magic);
        for (long pageId = 58; pageId <= 64; pageId++)
        {
            var page = reopened.GetPagePointer<long>(pageId);
            for (int i = 0; i < LongsInAPage; i++) Assert.Equal(pageId, page[i]);
        }
    }

    [Theory]
    [InlineData(StoreProfile.Cognitive)]
    [InlineData(StoreProfile.Graph)]
    public void AStoreThatOutgrowsItsIndexFiles_HoldsEveryQuad_AlsoWhenOpenedAgain(StoreProfile profile)
    {
        // Index files of four pages: the header, the root and two more. The tree asks for its fourth page after a
        // few hundred quads, and 1,500 quads make the file grow several times. The numbers are small because a
        // write flushes to disk, which is slow on Windows.
        const int Quads = 1500;
        var options = new StorageOptions { Profile = profile, IndexInitialSizeBytes = 4 * BTreeFile.PageSize };

        using (var store = new QuadStore(_testDir, null, null, options))
        {
            for (int batch = 0; batch < Quads / 500; batch++)
            {
                store.BeginBatch();
                for (int i = 0; i < 500; i++)
                    store.AddCurrentBatched($"<http://ex.org/s{batch * 500 + i}>", "<http://ex.org/p>", $"\"{batch * 500 + i}\"");
                store.CommitBatch();
            }

            // Grown at least twice, from four pages to eight and from eight to sixteen.
            Assert.True(new FileInfo(Path.Combine(_testDir, "gspo.tdb")).Length > 8 * BTreeFile.PageSize);
            Assert.Equal(Quads, store.GetStatistics().QuadCount);
            store.AcquireReadLock();
            try
            {
                for (int n = 0; n < Quads; n++)
                {
                    var bySubject = store.QueryCurrent($"<http://ex.org/s{n}>", "<http://ex.org/p>", default);
                    Assert.True(bySubject.MoveNext(), $"subject {n} is not found through its subject");
                    bySubject.Dispose();
                    var byObject = store.QueryCurrent(default, default, $"\"{n}\"");
                    Assert.True(byObject.MoveNext(), $"subject {n} is not found through its object");
                    byObject.Dispose();
                }
            }
            finally { store.ReleaseReadLock(); }
        }

        using var reopened = new QuadStore(_testDir, null, null, options);
        Assert.Equal(Quads, reopened.GetStatistics().QuadCount);
        reopened.AcquireReadLock();
        try
        {
            for (int n = 0; n < Quads; n++)
            {
                var bySubject = reopened.QueryCurrent($"<http://ex.org/s{n}>", "<http://ex.org/p>", default);
                Assert.True(bySubject.MoveNext(), $"subject {n} is not found through its subject after opening again");
                bySubject.Dispose();
                var byObject = reopened.QueryCurrent(default, default, $"\"{n}\"");
                Assert.True(byObject.MoveNext(), $"subject {n} is not found through its object after opening again");
                byObject.Dispose();
            }
        }
        finally { reopened.ReleaseReadLock(); }
    }

    [Fact]
    public void ReadersOfAStoreAtTheSameTime_FindEveryQuad()
    {
        // The quads are few because a write flushes to disk, which is slow on Windows. Readers disturb each other
        // only while pages are asked for the first time, so the store is opened again for every round.
        const int Quads = 4000;
        const int Readers = 8;
        const int Rounds = 25;
        var options = new StorageOptions { IndexInitialSizeBytes = 4L << 20, AtomDataInitialSizeBytes = 4L << 20, AtomOffsetInitialCapacity = 1L << 14 };

        using (var written = new QuadStore(_testDir, null, null, options))
        {
            for (int batch = 0; batch < Quads / 500; batch++)
            {
                written.BeginBatch();
                for (int i = 0; i < 500; i++)
                    written.AddCurrentBatched($"<http://ex.org/s{batch * 500 + i}>", "<http://ex.org/p>", $"\"{batch * 500 + i}\"");
                written.CommitBatch();
            }
        }

        long quadsNotFound = 0;
        long readersThatFinished = 0;
        for (int round = 0; round < Rounds; round++)
        {
            using var store = new QuadStore(_testDir, null, null, options);
            using var start = new Barrier(Readers);
            var readers = new Thread[Readers];
            for (int reader = 0; reader < Readers; reader++)
            {
                int seed = round * Readers + reader;
                // A background thread: a reader that is given a wrong page can walk the leaves in a circle without
                // end, and must then not keep the test process alive.
                readers[reader] = new Thread(() =>
                {
                    var random = new Random(seed);
                    start.SignalAndWait();
                    for (int asked = 0; asked < 300; asked++)
                    {
                        int n = random.Next(Quads);
                        store.AcquireReadLock();
                        try
                        {
                            var found = store.QueryCurrent($"<http://ex.org/s{n}>", "<http://ex.org/p>", default);
                            if (!found.MoveNext()) Interlocked.Increment(ref quadsNotFound);
                            found.Dispose();
                        }
                        finally { store.ReleaseReadLock(); }
                    }
                    Interlocked.Increment(ref readersThatFinished);
                }) { IsBackground = true };
                readers[reader].Start();
            }

            var until = DateTime.UtcNow.AddSeconds(60);
            foreach (var reader in readers) reader.Join(until > DateTime.UtcNow ? until - DateTime.UtcNow : TimeSpan.Zero);

            // A reader that did not finish still holds the read lock: the store cannot be closed, so the test ends here.
            Assert.Equal((round + 1L) * Readers, Interlocked.Read(ref readersThatFinished));
        }

        Assert.Equal(0, Interlocked.Read(ref quadsNotFound));
    }
}
