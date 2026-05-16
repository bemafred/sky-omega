using System;
using System.IO;
using System.Runtime.InteropServices;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-029 Minimal profile B+Tree storage-layer tests. 24 B key-only entries,
/// single sort order P → S → T, no graph dimension, RDF set semantics enforced
/// at the B+Tree level.
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
    public void Add_ThreeDistinctTriples_QueryReturnsAllThree()
    {
        var idx = CreateIndex();
        idx.AddRaw(100, 200, 300);
        idx.AddRaw(100, 200, 301);
        idx.AddRaw(101, 200, 300);

        Assert.Equal(3, idx.QuadCount);

        int count = 0;
        var e = idx.Query(-1, -1, -1);
        while (e.MoveNext()) count++;
        Assert.Equal(3, count);
    }

    [Fact]
    public void Add_SameTripleTwice_StaysSingleEntry()
    {
        var idx = CreateIndex();
        idx.AddRaw(100, 200, 300);
        idx.AddRaw(100, 200, 300);
        Assert.Equal(1, idx.QuadCount);
    }

    [Fact]
    public void Add_ExceedsLeafDegree_PageSplitsAndAllQueryable()
    {
        // LeafDegree = 681. Insert 2000 entries to force multiple splits.
        var idx = CreateIndex();
        const int N = 2000;
        for (int i = 1; i <= N; i++)
            idx.AddRaw(100, 200, i);

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
        idx1.AddRaw(100, 200, 300);
        idx1.AddRaw(100, 200, 301);
        idx1.AddRaw(100, 200, 302);
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
        idx.AddRaw(100, 200, 300);
        idx.AddRaw(101, 201, 301);

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
        idx.AddRaw(100, 200, 300);
        idx.AddRaw(100, 200, 301);
        idx.AddRaw(100, 200, 302);

        int count = 0;
        long matchedTertiary = 0;
        var e = idx.Query(100, 200, 301);
        while (e.MoveNext()) { matchedTertiary = e.Current.Tertiary; count++; }
        Assert.Equal(1, count);
        Assert.Equal(301, matchedTertiary);
    }
}
