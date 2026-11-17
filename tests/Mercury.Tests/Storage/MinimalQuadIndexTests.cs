using System;
using System.IO;
using System.Runtime.InteropServices;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-029 Minimal profile B+Tree storage-layer tests, with the model of ADR-064: 28 B leaf entries of the key and
/// the version, single sort order P → S → T, no graph dimension, and one change, a fact's next version (M5).
/// </summary>
public class MinimalQuadIndexTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _testPath;
    private MinimalQuadIndex? _index;

    public MinimalQuadIndexTests()
    {
        var tempPath = TempPath.Test("minimal");
        tempPath.MarkOwnership();
        _testDir = tempPath;
        _testPath = Path.Combine(_testDir, "test.mdb");
    }

    public void Dispose()
    {
        _index?.Dispose();
        TempPath.SafeCleanup(_testDir);
    }

    private MinimalQuadIndex CreateIndex()
    {
        _index?.Dispose();
        _index = new MinimalQuadIndex(_testPath, null);
        return _index;
    }

    [Fact]
    public void Layout_MinimalKey_Is24Bytes()
    {
        Assert.Equal(24, Marshal.SizeOf<MinimalQuadIndex.MinimalKey>());
    }

    [Fact]
    public void Layout_LeafEntry_HoldsTheKeyAndTheVersion_In28Bytes()
    {
        // ADR-064 D2: a Minimal leaf entry holds the triple and its version, and no time. 584 fit a page.
        Assert.Equal(28, Marshal.SizeOf<MinimalQuadIndex.MinimalLeafEntry>());
        Assert.Equal(28, MinimalQuadIndex.LeafEntryBytes);
        Assert.Equal(584, MinimalQuadIndex.LeafDegree);
    }

    [Fact]
    public void Layout_MinimalKey_FieldOrder_PrimarySecondaryTertiary()
    {
        var a = new MinimalQuadIndex.MinimalKey { Primary = 1, Secondary = 0, Tertiary = 0 };
        var b = new MinimalQuadIndex.MinimalKey { Primary = 0, Secondary = 100, Tertiary = 100 };
        Assert.True(a.CompareTo(b) > 0, "Primary dimension must outweigh later dimensions");

        var c = new MinimalQuadIndex.MinimalKey { Primary = 1, Secondary = 1, Tertiary = 0 };
        var d = new MinimalQuadIndex.MinimalKey { Primary = 1, Secondary = 0, Tertiary = 999 };
        Assert.True(c.CompareTo(d) > 0, "Secondary must outweigh Tertiary");
    }

    [Fact]
    public void ThreeDistinctFacts_QueryReturnsAllThree()
    {
        var idx = CreateIndex();
        idx.NextVersion(100, 200, 300, 0);
        idx.NextVersion(100, 200, 301, 0);
        idx.NextVersion(101, 200, 300, 0);

        Assert.Equal(3, idx.QuadCount);

        int count = 0;
        var e = idx.Query(-1, -1, -1);
        while (e.MoveNext()) count++;
        Assert.Equal(3, count);
    }

    [Fact]
    public void NextVersion_FromZero_Twice_HoldsOneEntryAtVersion1()
    {
        var idx = CreateIndex();
        Assert.True(idx.NextVersion(100, 200, 300, 0));
        Assert.False(idx.NextVersion(100, 200, 300, 0));
        Assert.Equal(1, idx.QuadCount);

        int version = 0;
        var e = idx.Query(100, 200, 300);
        while (e.MoveNext()) version = e.Version;
        Assert.Equal(1, version);
    }

    [Fact]
    public void NextVersion_FromTheVersionHeld_TakesTheFactToTheNext_AndFromAnyOther_ChangesNothing()
    {
        var idx = CreateIndex();
        idx.NextVersion(100, 200, 300, 0);

        // Version 2: retracted. A read of what is asserted does not see it; the index holds it.
        Assert.True(idx.NextVersion(100, 200, 300, 1));
        int asserted = 0;
        var live = idx.Query(100, 200, 300);
        while (live.MoveNext()) asserted++;
        Assert.Equal(0, asserted);

        Assert.False(idx.NextVersion(100, 200, 300, 0));   // the index holds the fact
        Assert.False(idx.NextVersion(100, 200, 300, 1));   // the change is applied already
        Assert.False(idx.NextVersion(100, 200, 300, 3));   // a version the fact has not reached
        Assert.False(idx.NextVersion(100, 200, 999, 1));   // a fact the index does not hold

        int version = 0;
        var held = idx.QueryHeld(-1, -1, -1);
        while (held.MoveNext()) version = held.Version;
        Assert.Equal(2, version);
        Assert.Equal(1, idx.QuadCount);

        // Version 3: asserted again.
        Assert.True(idx.NextVersion(100, 200, 300, 2));
        int again = 0;
        var seen = idx.Query(100, 200, 300);
        while (seen.MoveNext()) { again++; version = seen.Version; }
        Assert.Equal(1, again);
        Assert.Equal(3, version);
    }

    [Fact]
    public void MoreFactsThanALeafHolds_PageSplitsAndAllQueryable()
    {
        // LeafDegree = 584. Insert 2000 entries to force multiple splits.
        var idx = CreateIndex();
        const int N = 2000;
        for (int i = 1; i <= N; i++)
            idx.NextVersion(100, 200, i, 0);

        Assert.Equal(N, idx.QuadCount);

        int found = 0;
        var e = idx.Query(100, 200, -1);
        while (e.MoveNext()) found++;
        Assert.Equal(N, found);
    }

    [Fact]
    public void Persistence_EntriesSurviveDisposeAndReopen()
    {
        var idx1 = CreateIndex();
        idx1.NextVersion(100, 200, 300, 0);
        idx1.NextVersion(100, 200, 301, 0);
        idx1.NextVersion(100, 200, 302, 0);
        idx1.Dispose();
        _index = null;

        var idx2 = CreateIndex();
        Assert.Equal(3, idx2.QuadCount);

        int found = 0;
        var e = idx2.Query(100, 200, -1);
        while (e.MoveNext()) found++;
        Assert.Equal(3, found);
    }

    [Fact]
    public void Clear_EmptiesIndex()
    {
        var idx = CreateIndex();
        idx.NextVersion(100, 200, 300, 0);
        idx.NextVersion(101, 201, 301, 0);

        idx.Clear();
        Assert.Equal(0, idx.QuadCount);

        int count = 0;
        var e = idx.Query(-1, -1, -1);
        while (e.MoveNext()) count++;
        Assert.Equal(0, count);
    }

    [Fact]
    public void Query_SpecificTertiary_ReturnsOnlyMatching()
    {
        var idx = CreateIndex();
        idx.NextVersion(100, 200, 300, 0);
        idx.NextVersion(100, 200, 301, 0);
        idx.NextVersion(100, 200, 302, 0);

        int count = 0;
        long matchedTertiary = 0;
        var e = idx.Query(100, 200, 301);
        while (e.MoveNext()) { matchedTertiary = e.Tertiary; count++; }
        Assert.Equal(1, count);
        Assert.Equal(301, matchedTertiary);
    }
}
