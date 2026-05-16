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
/// semantics (no-op on duplicate, un-delete on re-add, single sort order) are exercised
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

    // ===== Basic Add and Query =====

    [Fact]
    public void Add_ThreeDistinctTriples_QueryReturnsAllThree()
    {
        var idx = CreateIndex();
        idx.AddRaw(0, 100, 200, 300);
        idx.AddRaw(0, 100, 200, 301);
        idx.AddRaw(0, 101, 200, 300);

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
        idx.AddRaw(0, 100, 200, 300);
        idx.AddRaw(0, 100, 200, 301);
        idx.AddRaw(0, 100, 200, 302);

        int count = 0;
        long matchedTertiary = 0;
        var e = idx.Query(0, 100, 200, 301);
        while (e.MoveNext()) { matchedTertiary = e.Current.Tertiary; count++; }
        Assert.Equal(1, count);
        Assert.Equal(301, matchedTertiary);
    }

    // ===== Duplicate-add semantics (no-op) =====

    [Fact]
    public void Add_SameTripleTwice_QuadCountStaysAtOne()
    {
        // RDF set semantics: re-adding a live triple is a no-op. Version does NOT
        // advance — the default in this profile is "no-op-on-re-add" (the alternative
        // "explicit touch" would be a separate API).
        var idx = CreateIndex();
        idx.AddRaw(0, 100, 200, 300);
        idx.AddRaw(0, 100, 200, 300);

        Assert.Equal(1, idx.QuadCount);

        int count = 0;
        int version = -1;
        var e = idx.Query(0, 100, 200, 300);
        while (e.MoveNext()) { version = e.Current.Version; count++; }
        Assert.Equal(1, count);
        Assert.Equal(1, version);
    }

    // ===== Soft-delete semantics =====

    [Fact]
    public void DeleteRaw_LiveEntry_ReturnsTrueAndMarksDeleted()
    {
        var idx = CreateIndex();
        idx.AddRaw(0, 100, 200, 300);

        Assert.True(idx.DeleteRaw(0, 100, 200, 300));

        // Live query excludes deleted entries.
        int liveCount = 0;
        var live = idx.Query(0, 100, 200, 300);
        while (live.MoveNext()) liveCount++;
        Assert.Equal(0, liveCount);

        // Audit query includes deleted entries.
        int auditCount = 0;
        bool auditIsDeleted = false;
        int auditVersion = 0;
        var audit = idx.QueryAllVersions(0, 100, 200, 300);
        while (audit.MoveNext())
        {
            auditCount++;
            auditIsDeleted = audit.Current.IsDeleted;
            auditVersion = audit.Current.Version;
        }
        Assert.Equal(1, auditCount);
        Assert.True(auditIsDeleted);
        Assert.Equal(2, auditVersion); // 1 from Add, then bumped on Delete
    }

    [Fact]
    public void DeleteRaw_MissingEntry_ReturnsFalse()
    {
        var idx = CreateIndex();
        idx.AddRaw(0, 100, 200, 300);

        Assert.False(idx.DeleteRaw(0, 100, 200, 999));
    }

    [Fact]
    public void DeleteRaw_AlreadyDeleted_ReturnsFalse()
    {
        var idx = CreateIndex();
        idx.AddRaw(0, 100, 200, 300);
        Assert.True(idx.DeleteRaw(0, 100, 200, 300));
        Assert.False(idx.DeleteRaw(0, 100, 200, 300));
    }

    // ===== Re-add un-deletes =====

    [Fact]
    public void AddRaw_AfterDelete_UnDeletesAndBumpsVersion()
    {
        var idx = CreateIndex();
        idx.AddRaw(0, 100, 200, 300);    // Version 1, live
        Assert.True(idx.DeleteRaw(0, 100, 200, 300)); // Version 2, deleted
        idx.AddRaw(0, 100, 200, 300);    // Should un-delete, bump to Version 3

        int count = 0;
        int version = 0;
        bool isDeleted = true;
        var e = idx.Query(0, 100, 200, 300);
        while (e.MoveNext())
        {
            count++;
            version = e.Current.Version;
            isDeleted = e.Current.IsDeleted;
        }
        Assert.Equal(1, count);
        Assert.False(isDeleted);
        Assert.Equal(3, version);
    }

    // ===== Page split =====

    [Fact]
    public void Add_ExceedsNodeDegree_PageSplits_AllQueryable()
    {
        // NodeDegree = 255. Insert 1000 entries to force multiple page splits.
        var idx = CreateIndex();
        const int N = 1000;
        for (int i = 1; i <= N; i++)
        {
            idx.AddRaw(0, 100, 200, i);
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
        idx1.AddRaw(0, 100, 200, 300);
        idx1.AddRaw(0, 100, 200, 301);
        idx1.AddRaw(0, 100, 200, 302);
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
        idx.AddRaw(0, 100, 200, 300);
        idx.AddRaw(0, 101, 201, 301);

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
        idx.AddRaw(graph: 1, primary: 100, secondary: 200, tertiary: 300);
        idx.AddRaw(graph: 2, primary: 100, secondary: 200, tertiary: 300);

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
        idx.AddRaw(0, 100, 200, 300);
        idx.AddRaw(1, 101, 201, 301);
        idx.AddRaw(2, 102, 202, 302);

        int count = 0;
        var e = idx.Query(-1, -1, -1, -1);
        while (e.MoveNext()) count++;
        Assert.Equal(3, count);
    }
}
