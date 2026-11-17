using System;
using System.IO;
using System.Threading;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-064 D1, D2, D3, D8 and G9: a Graph entry holds the quad, the child pointer and the version, and a Minimal leaf
/// entry the triple and the version; nothing in either is read from a clock. Graph keeps GSPO, GPOS and GOSP, and no
/// TGSP. Each index file's magic number changes with its layout, so a file in the earlier layout is refused when the
/// store opens.
/// </summary>
public class FactStatesLayoutTests : IDisposable
{
    private const string P = "<urn:layout:p>";
    private const string G = "<urn:layout:g>";

    private readonly string _root;

    public FactStatesLayoutTests()
    {
        var tempPath = TempPath.Test("fact-states-layout");
        tempPath.MarkOwnership();
        _root = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_root);

    [Theory]
    [InlineData(StoreProfile.Graph)]
    [InlineData(StoreProfile.Minimal)]
    public void TheSameWrites_AtOtherTimes_LeaveTheSameIndexFiles(StoreProfile profile)
    {
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        var yGraph = profile == StoreProfile.Graph ? G : "";

        // The first store's clock moves a minute at each write. The second's stands still in another year, and every
        // write to it reads a later wall clock than every write to the first.
        foreach (var dir in new[] { first, second })
        {
            if (dir == second)
                Thread.Sleep(5);
            var clock = new SuppliedClock { Reading = new DateTimeOffset(dir == first ? 2031 : 2045, 1, 1, 0, 0, 0, TimeSpan.Zero) };
            void Tick() { if (dir == first) clock.Reading = clock.Reading.AddMinutes(1); }

            using var store = new QuadStore(dir, null, null,
                new StorageOptions { Profile = profile, Clock = clock, IndexInitialSizeBytes = 1L << 20, AtomDataInitialSizeBytes = 1L << 20 });
            store.AddCurrent("<urn:layout:x>", P, "\"x\"");
            Tick();
            store.DeleteCurrent("<urn:layout:x>", P, "\"x\"");
            Tick();
            store.AddCurrent("<urn:layout:x>", P, "\"x\"");
            Tick();
            store.AddCurrent("<urn:layout:y>", P, "\"y\"", yGraph);
            Tick();
            store.DeleteCurrent("<urn:layout:y>", P, "\"y\"", yGraph);
            Tick();
            store.BeginBatch();
            store.AddCurrentBatched("<urn:layout:z>", P, "\"z\"");
            store.DeleteCurrentBatched("<urn:layout:z>", P, "\"z\"");
            store.CommitBatch();
        }

        var indexes = profile == StoreProfile.Graph ? new[] { "gspo.tdb", "gpos.tdb", "gosp.tdb" } : new[] { "gspo.tdb" };
        foreach (var index in indexes)
        {
            var inFirst = File.ReadAllBytes(Path.Combine(first, index));
            var inSecond = File.ReadAllBytes(Path.Combine(second, index));
            Assert.True(inFirst.AsSpan().SequenceEqual(inSecond), $"{index} differs between the two stores");
        }
    }

    [Fact]
    public void AGraphStore_KeepsGspoGposAndGosp_AndNoTgsp()
    {
        var dir = Path.Combine(_root, "store");
        using (var store = new QuadStore(dir, null, null,
            new StorageOptions { Profile = StoreProfile.Graph, IndexInitialSizeBytes = 1L << 20, AtomDataInitialSizeBytes = 1L << 20 }))
        {
            store.AddCurrent("<urn:layout:x>", P, "\"x\"");
            Assert.Equal(["gspo", "gpos", "gosp"], store.Schema.Indexes);
        }

        Assert.True(File.Exists(Path.Combine(dir, "gosp.tdb")));
        Assert.False(File.Exists(Path.Combine(dir, "tgsp.tdb")));
    }

    /// <summary>
    /// The earlier layouts' magic numbers, at byte 24 of the header (ADR-029, 2026-05-16): "GRAPHIDX", whose entries
    /// held two wall-clock times and a deleted mark, and "MINIMAL\0", whose leaf entries held the key only.
    /// </summary>
    [Theory]
    [InlineData(StoreProfile.Graph, "gpos.tdb", 0x4752415048494458L)]
    [InlineData(StoreProfile.Minimal, "gspo.tdb", 0x4D494E494D414C00L)]
    public void AnIndexFileInTheEarlierLayout_IsRefusedWhenTheStoreOpens(StoreProfile profile, string index, long earlierMagic)
    {
        var dir = Path.Combine(_root, "store");
        var options = new StorageOptions { Profile = profile, IndexInitialSizeBytes = 1L << 20, AtomDataInitialSizeBytes = 1L << 20 };
        using (var store = new QuadStore(dir, null, null, options))
            store.AddCurrent("<urn:layout:x>", P, "\"x\"");

        using (var file = new FileStream(Path.Combine(dir, index), FileMode.Open, FileAccess.ReadWrite))
        {
            file.Position = 24;
            file.Write(BitConverter.GetBytes(earlierMagic));
        }

        var refusal = Assert.Throws<InvalidDataException>(() => new QuadStore(dir, null, null, options));
        Assert.Contains(index, refusal.Message);
    }
}
