using System;
using System.IO;
using System.Runtime.InteropServices;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Runtime;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// Tests for <see cref="VersionedQuadIndex"/> — Graph profile B+Tree (versioned, no temporal).
/// Mirrors the TemporalQuadIndex test surface where applicable; the Graph-specific
/// semantics (a fact's next version, ADR-064 M5, and the single sort order) are exercised
/// directly.
/// </summary>
public class VersionedQuadIndexTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _testPath;
    private VersionedQuadIndex? _index;

    public VersionedQuadIndexTests()
    {
        var tempPath = TempPath.Test("versioned");
        tempPath.MarkOwnership();
        _testDir = tempPath;
        _testPath = Path.Combine(_testDir, "test.gdb");
    }

    public void Dispose()
    {
        _index?.Dispose();
        TempPath.SafeCleanup(_testDir);
    }

    private VersionedQuadIndex CreateIndex()
    {
        _index?.Dispose();
        _index = new VersionedQuadIndex(_testPath);
        return _index;
    }

    // ===== Layout invariants (ADR-029 contract) =====

    [Fact]
    public void Layout_VersionedKey_Is32Bytes()
    {
        // ADR-029 Graph profile contract: VersionedKey occupies exactly 32 bytes
        // (4 × long; Pack=1). Reference profile uses the same shape; the entry-size
        // win for Graph (64 B vs Cognitive's 88 B) comes from dropping the three
        // temporal fields from the key.
        Assert.Equal(32, Marshal.SizeOf<VersionedQuadIndex.VersionedKey>());
    }

    [Fact]
    public void Layout_VersionedKey_FieldOrder_GraphPrimarySecondaryTertiary()
    {
        // Lexicographic comparison depends on field order. Confirm Graph leads, then
        // Primary/Secondary/Tertiary in declaration order — matches ReferenceKey.
        var a = new VersionedQuadIndex.VersionedKey { Graph = 1, Primary = 0, Secondary = 0, Tertiary = 0 };
        var b = new VersionedQuadIndex.VersionedKey { Graph = 0, Primary = 100, Secondary = 100, Tertiary = 100 };
        Assert.True(a.CompareTo(b) > 0, "Graph dimension must outweigh later dimensions");

        var c = new VersionedQuadIndex.VersionedKey { Graph = 1, Primary = 1, Secondary = 0, Tertiary = 0 };
        var d = new VersionedQuadIndex.VersionedKey { Graph = 1, Primary = 0, Secondary = 999, Tertiary = 999 };
        Assert.True(c.CompareTo(d) > 0, "Primary must outweigh Secondary/Tertiary");
    }

    // ===== Basic insert and Query =====

    [Fact]
    public void ThreeDistinctFacts_QueryReturnsAllThree()
    {
        var idx = CreateIndex();
        idx.NextVersion(0, 100, 200, 300, 0);
        idx.NextVersion(0, 100, 200, 301, 0);
        idx.NextVersion(0, 101, 200, 300, 0);

        Assert.Equal(3, idx.QuadCount);

        int count = 0;
        var e = idx.Query(0, -1, -1, -1);
        while (e.MoveNext()) count++;
        Assert.Equal(3, count);
    }

    [Fact]
    public void Query_SpecificTertiary_ReturnsOnlyMatching()
    {
        var idx = CreateIndex();
        idx.NextVersion(0, 100, 200, 300, 0);
        idx.NextVersion(0, 100, 200, 301, 0);
        idx.NextVersion(0, 100, 200, 302, 0);

        int count = 0;
        long matchedTertiary = 0;
        var e = idx.Query(0, 100, 200, 301);
        while (e.MoveNext()) { matchedTertiary = e.Tertiary; count++; }
        Assert.Equal(1, count);
        Assert.Equal(301, matchedTertiary);
    }

    // ===== A fact's next version (ADR-064 M5) =====

    [Fact]
    public void NextVersion_FromZero_HoldsTheFactAtVersion1_AndAgain_ChangesNothing()
    {
        var idx = CreateIndex();
        Assert.True(idx.NextVersion(0, 100, 200, 300, 0));
        Assert.False(idx.NextVersion(0, 100, 200, 300, 0));

        Assert.Equal(1, idx.QuadCount);
        int count = 0;
        int version = -1;
        var e = idx.Query(0, 100, 200, 300);
        while (e.MoveNext()) { version = e.Version; count++; }
        Assert.Equal(1, count);
        Assert.Equal(1, version);
    }

    [Fact]
    public void NextVersion_FromTheVersionHeld_TakesTheFactToTheNext_AnEvenVersionIsRetracted()
    {
        var idx = CreateIndex();
        idx.NextVersion(0, 100, 200, 300, 0);

        // Version 2: retracted. A read of what is asserted does not see it; the index holds it.
        Assert.True(idx.NextVersion(0, 100, 200, 300, 1));
        int liveCount = 0;
        var live = idx.Query(0, 100, 200, 300);
        while (live.MoveNext()) liveCount++;
        Assert.Equal(0, liveCount);

        int heldCount = 0;
        int heldVersion = 0;
        bool heldRetracted = false;
        var held = idx.QueryAllVersions(0, 100, 200, 300);
        while (held.MoveNext()) { heldCount++; heldVersion = held.Version; heldRetracted = held.LegacyIsDeleted; }
        Assert.Equal(1, heldCount);
        Assert.Equal(2, heldVersion);
        Assert.True(heldRetracted);
        Assert.Equal(1, idx.QuadCount);

        // Version 3: asserted again.
        Assert.True(idx.NextVersion(0, 100, 200, 300, 2));
        int version = 0;
        bool retracted = true;
        var again = idx.Query(0, 100, 200, 300);
        while (again.MoveNext()) { version = again.Version; retracted = again.LegacyIsDeleted; }
        Assert.Equal(3, version);
        Assert.False(retracted);
    }

    [Fact]
    public void NextVersion_FromAVersionTheIndexDoesNotHold_ChangesNothing()
    {
        var idx = CreateIndex();
        idx.NextVersion(0, 100, 200, 300, 0);
        idx.NextVersion(0, 100, 200, 300, 1);

        Assert.False(idx.NextVersion(0, 100, 200, 300, 0));   // the index holds the fact
        Assert.False(idx.NextVersion(0, 100, 200, 300, 1));   // the change is applied already
        Assert.False(idx.NextVersion(0, 100, 200, 300, 3));   // a version the fact has not reached
        Assert.False(idx.NextVersion(0, 100, 200, 999, 1));   // a fact the index does not hold

        int version = 0;
        var held = idx.QueryAllVersions(-1, -1, -1, -1);
        while (held.MoveNext()) version = held.Version;
        Assert.Equal(2, version);
        Assert.Equal(1, idx.QuadCount);
    }

    // ===== Page split =====

    [Fact]
    public void MoreFactsThanANodeHolds_PageSplits_AllQueryable()
    {
        // NodeDegree = 255. Insert 1000 entries to force multiple page splits.
        var idx = CreateIndex();
        const int N = 1000;
        for (int i = 1; i <= N; i++)
        {
            idx.NextVersion(0, 100, 200, i, 0);
        }

        Assert.Equal(N, idx.QuadCount);

        int found = 0;
        var e = idx.Query(0, 100, 200, -1);
        while (e.MoveNext()) found++;
        Assert.Equal(N, found);
    }

    // ===== Persistence =====

    [Fact]
    public void Persistence_EntriesSurviveDisposeAndReopen()
    {
        var idx1 = CreateIndex();
        idx1.NextVersion(0, 100, 200, 300, 0);
        idx1.NextVersion(0, 100, 200, 301, 0);
        idx1.NextVersion(0, 100, 200, 302, 0);
        idx1.Dispose();
        _index = null;

        var idx2 = CreateIndex();
        Assert.Equal(3, idx2.QuadCount);

        int found = 0;
        var e = idx2.Query(0, 100, 200, -1);
        while (e.MoveNext()) found++;
        Assert.Equal(3, found);
    }

    // ===== Clear =====

    [Fact]
    public void Clear_EmptiesIndex_QueryReturnsNothing()
    {
        var idx = CreateIndex();
        idx.NextVersion(0, 100, 200, 300, 0);
        idx.NextVersion(0, 101, 201, 301, 0);

        idx.Clear();

        Assert.Equal(0, idx.QuadCount);

        int count = 0;
        var e = idx.Query(-1, -1, -1, -1);
        while (e.MoveNext()) count++;
        Assert.Equal(0, count);
    }

    // ===== Graph dimension isolation =====

    [Fact]
    public void Query_GraphIsolation_SeparateGraphsDoNotLeak()
    {
        var idx = CreateIndex();
        idx.NextVersion(graph: 1, primary: 100, secondary: 200, tertiary: 300, fromVersion: 0);
        idx.NextVersion(graph: 2, primary: 100, secondary: 200, tertiary: 300, fromVersion: 0);

        int g1 = 0;
        var e1 = idx.Query(1, 100, 200, 300);
        while (e1.MoveNext()) g1++;

        int g2 = 0;
        var e2 = idx.Query(2, 100, 200, 300);
        while (e2.MoveNext()) g2++;

        Assert.Equal(1, g1);
        Assert.Equal(1, g2);
        Assert.Equal(2, idx.QuadCount);
    }

    // ===== Wildcard semantics =====

    [Fact]
    public void Query_AllWildcards_ReturnsEverything()
    {
        var idx = CreateIndex();
        idx.NextVersion(0, 100, 200, 300, 0);
        idx.NextVersion(1, 101, 201, 301, 0);
        idx.NextVersion(2, 102, 202, 302, 0);

        int count = 0;
        var e = idx.Query(-1, -1, -1, -1);
        while (e.MoveNext()) count++;
        Assert.Equal(3, count);
    }
}
