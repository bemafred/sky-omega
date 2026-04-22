using System;
using System.IO;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// Tests for ReferenceQuadIndex — ADR-029 Phase 2c: 32-byte keys, no temporal
/// dimension, uniqueness-on-insert (Cases A/B collapsed).
/// </summary>
public class ReferenceQuadIndexTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _testPath;
    private ReferenceQuadIndex? _index;

    public ReferenceQuadIndexTests()
    {
        var tempPath = TempPath.Test("refindex");
        tempPath.MarkOwnership();
        _testDir = tempPath;
        _testPath = Path.Combine(_testDir, "ref.rdb");
    }

    public void Dispose()
    {
        _index?.Dispose();
        TempPath.SafeCleanup(_testDir);
    }

    private ReferenceQuadIndex CreateIndex()
    {
        _index?.Dispose();
        // Smaller initial size than production default so test cleanup is cheap.
        _index = new ReferenceQuadIndex(_testPath, sharedAtoms: null, initialSizeBytes: 1L << 20);
        return _index;
    }

    private static int Count(ReferenceQuadIndex idx, long g, long p, long s, long t)
    {
        var e = idx.Query(g, p, s, t);
        int n = 0;
        while (e.MoveNext()) n++;
        return n;
    }

    #region Basic add/query

    [Fact]
    public void Add_SingleQuad_IsQueryable()
    {
        var idx = CreateIndex();
        idx.Add("alice", "knows", "bob", graph: "g1");

        Assert.Equal(1, idx.QuadCount);
        Assert.Equal(1, Count(idx, g: -1, p: -1, s: -1, t: -1));
    }

    [Fact]
    public void Add_MultipleDistinct_CountsCorrect()
    {
        var idx = CreateIndex();
        idx.Add("alice", "knows", "bob", "g1");
        idx.Add("alice", "knows", "carol", "g1");
        idx.Add("bob", "knows", "alice", "g1");

        Assert.Equal(3, idx.QuadCount);
        Assert.Equal(3, Count(idx, -1, -1, -1, -1));
    }

    #endregion

    #region Uniqueness enforcement (ADR-029 Decision 7)

    [Fact]
    public void Add_ExactDuplicate_IsSilentNoOp()
    {
        var idx = CreateIndex();
        idx.Add("alice", "knows", "bob", "g1");
        idx.Add("alice", "knows", "bob", "g1"); // duplicate
        idx.Add("alice", "knows", "bob", "g1"); // duplicate

        Assert.Equal(1, idx.QuadCount);
        Assert.Equal(1, Count(idx, -1, -1, -1, -1));
    }

    [Fact]
    public void AddRaw_ExactDuplicate_IsSilentNoOp()
    {
        var idx = CreateIndex();
        idx.AddRaw(graph: 1, primary: 2, secondary: 3, tertiary: 4);
        idx.AddRaw(graph: 1, primary: 2, secondary: 3, tertiary: 4);
        idx.AddRaw(graph: 1, primary: 2, secondary: 3, tertiary: 4);

        Assert.Equal(1, idx.QuadCount);
    }

    [Fact]
    public void AddRaw_DifferentGraph_SameTriple_KeptSeparate()
    {
        var idx = CreateIndex();
        idx.AddRaw(1, 2, 3, 4);
        idx.AddRaw(2, 2, 3, 4); // different graph — not a duplicate

        Assert.Equal(2, idx.QuadCount);
    }

    #endregion

    #region Wildcard queries

    [Fact]
    public void Query_GraphWildcard_ScansAllGraphs()
    {
        var idx = CreateIndex();
        idx.AddRaw(1, 10, 20, 30);
        idx.AddRaw(2, 10, 20, 30);
        idx.AddRaw(3, 10, 20, 30);

        Assert.Equal(3, Count(idx, g: -1, p: -1, s: -1, t: -1));
    }

    [Fact]
    public void Query_SpecificGraph_NotFoundReturnsEmpty()
    {
        var idx = CreateIndex();
        idx.AddRaw(1, 10, 20, 30);

        // Graph 99 has no entries — result is empty.
        Assert.Equal(0, Count(idx, g: 99, p: -1, s: -1, t: -1));
    }

    [Fact]
    public void Query_MinusTwoSentinel_MatchesNothing()
    {
        // -2 is the "graph specified but atom-id lookup failed" sentinel;
        // query must return zero rows.
        var idx = CreateIndex();
        idx.AddRaw(1, 10, 20, 30);
        Assert.Equal(0, Count(idx, g: -2, p: -1, s: -1, t: -1));
    }

    #endregion

    #region Page split (B+Tree growth)

    [Fact]
    public void Add_ExceedsLeafDegree_SplitsAndPreservesOrder()
    {
        var idx = CreateIndex();

        // LeafDegree is 511; insert enough to force at least one split.
        const int N = 2000;
        for (int i = 0; i < N; i++)
            idx.AddRaw(graph: 1, primary: i, secondary: 0, tertiary: 0);

        Assert.Equal(N, idx.QuadCount);

        // Full scan returns all N entries in ascending primary order.
        var e = idx.Query(graph: 1, primary: -1, secondary: -1, tertiary: -1);
        long expectedPrimary = 0;
        int seen = 0;
        while (e.MoveNext())
        {
            Assert.Equal(expectedPrimary, e.Current.Primary);
            expectedPrimary++;
            seen++;
        }
        Assert.Equal(N, seen);
    }

    [Fact]
    public void Add_ManyInSortedOrder_PreservesUniqueness()
    {
        var idx = CreateIndex();
        const int N = 5000;
        for (int i = 0; i < N; i++)
            idx.AddRaw(1, i, 0, 0);

        // Re-insert every entry — each must be a no-op.
        for (int i = 0; i < N; i++)
            idx.AddRaw(1, i, 0, 0);

        Assert.Equal(N, idx.QuadCount);
    }

    #endregion

    #region Persistence

    [Fact]
    public void Persist_ReopenSameFile_PreservesData()
    {
        // Write session
        using (var first = new ReferenceQuadIndex(_testPath, sharedAtoms: null, initialSizeBytes: 1L << 20))
        {
            for (int i = 0; i < 100; i++)
                first.AddRaw(graph: 1, primary: i, secondary: i * 2, tertiary: i * 3);
            first.Flush();
        }

        // Read session — own AtomStore reopened
        using (var reopened = new ReferenceQuadIndex(_testPath, sharedAtoms: null, initialSizeBytes: 1L << 20))
        {
            Assert.Equal(100, reopened.QuadCount);

            var e = reopened.Query(1, -1, -1, -1);
            int seen = 0;
            while (e.MoveNext())
            {
                Assert.Equal(seen, e.Current.Primary);
                seen++;
            }
            Assert.Equal(100, seen);
        }
    }

    [Fact]
    public void Open_WrongMagicNumber_ThrowsInvalidData()
    {
        // Create a file that looks like a TemporalQuadIndex would have stamped.
        File.WriteAllBytes(_testPath, new byte[32]);
        using (var fs = new FileStream(_testPath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.SetLength(1L << 20);
            fs.Position = 24;
            fs.Write(BitConverter.GetBytes(0x54454D504F52414CL)); // "TEMPORAL"
        }

        Assert.Throws<InvalidDataException>(() =>
            new ReferenceQuadIndex(_testPath, sharedAtoms: null, initialSizeBytes: 1L << 20));
    }

    #endregion

    #region Sort-insert (ADR-032)

    [Fact]
    public void AppendSorted_QueryResultsMatchRandomInsert()
    {
        // Equivalence check: two stores with the same data via different insert paths
        // must produce the same query results. Sort-insert is a fast path, not a new
        // semantic.
        var randomPath = _testPath + "_random";
        var sortedPath = _testPath + "_sorted";

        // Build the same 300 quads via random AddRaw and sorted AppendSorted.
        var keys = new List<ReferenceQuadIndex.ReferenceKey>(300);
        for (int i = 0; i < 300; i++)
        {
            keys.Add(new ReferenceQuadIndex.ReferenceKey
            {
                Graph = 1,
                Primary = i / 10,          // 30 distinct primaries × 10 secondaries
                Secondary = i % 10,
                Tertiary = i,
            });
        }

        using (var random = new ReferenceQuadIndex(randomPath, sharedAtoms: null, initialSizeBytes: 1L << 20))
        {
            for (int i = 0; i < keys.Count; i++)
                random.AddRaw(keys[i].Graph, keys[i].Primary, keys[i].Secondary, keys[i].Tertiary);
        }

        using (var sorted = new ReferenceQuadIndex(sortedPath, sharedAtoms: null, initialSizeBytes: 1L << 20))
        {
            var sortedKeys = keys.ToArray();
            Array.Sort(sortedKeys, static (a, b) => ReferenceQuadIndex.ReferenceKey.Compare(in a, in b));
            sorted.BeginAppendSorted();
            foreach (var k in sortedKeys) sorted.AppendSorted(k);
            sorted.EndAppendSorted();
        }

        using var randomReopened = new ReferenceQuadIndex(randomPath, sharedAtoms: null, initialSizeBytes: 1L << 20);
        using var sortedReopened = new ReferenceQuadIndex(sortedPath, sharedAtoms: null, initialSizeBytes: 1L << 20);

        Assert.Equal(randomReopened.QuadCount, sortedReopened.QuadCount);
        Assert.Equal(300, sortedReopened.QuadCount);

        int randomCount = 0, sortedCount = 0;
        var re = randomReopened.Query(1, 5, -1, -1);
        while (re.MoveNext()) randomCount++;
        var se = sortedReopened.Query(1, 5, -1, -1);
        while (se.MoveNext()) sortedCount++;
        Assert.Equal(randomCount, sortedCount);
        Assert.Equal(10, sortedCount);
    }

    [Fact]
    public void AppendSorted_DuplicateKey_IsSilentNoOp()
    {
        var idx = CreateIndex();
        idx.BeginAppendSorted();
        idx.AppendSorted(new ReferenceQuadIndex.ReferenceKey { Graph = 1, Primary = 1, Secondary = 1, Tertiary = 1 });
        idx.AppendSorted(new ReferenceQuadIndex.ReferenceKey { Graph = 1, Primary = 1, Secondary = 1, Tertiary = 1 });
        idx.AppendSorted(new ReferenceQuadIndex.ReferenceKey { Graph = 1, Primary = 1, Secondary = 1, Tertiary = 1 });
        idx.EndAppendSorted();

        Assert.Equal(1, idx.QuadCount);
    }

    [Fact]
    public void AppendSorted_LargeMonotonicRun_ExceedsLeafAndPromotes()
    {
        // Exercise the tail-full fallback path: far more keys than LeafDegree, all in
        // strictly increasing order. Correct output requires the fallback's split-and-
        // refind-tail logic to work.
        var idx = CreateIndex();

        const int N = 5000; // LeafDegree is 511; this forces ~10 leaf splits.
        idx.BeginAppendSorted();
        for (int i = 0; i < N; i++)
        {
            idx.AppendSorted(new ReferenceQuadIndex.ReferenceKey
            {
                Graph = 1,
                Primary = i,
                Secondary = 0,
                Tertiary = 0,
            });
        }
        idx.EndAppendSorted();

        Assert.Equal(N, idx.QuadCount);

        for (int i = 0; i < N; i++)
        {
            int matched = 0;
            var e = idx.Query(1, i, -1, -1);
            while (e.MoveNext()) matched++;
            Assert.Equal(1, matched);
        }
    }

    #endregion

    #region Clear

    [Fact]
    public void Clear_EmptiesStore()
    {
        var idx = CreateIndex();
        for (int i = 0; i < 200; i++)
            idx.AddRaw(1, i, 0, 0);

        Assert.Equal(200, idx.QuadCount);

        idx.Clear();

        Assert.Equal(0, idx.QuadCount);
        Assert.Equal(0, Count(idx, -1, -1, -1, -1));

        // Post-clear inserts still work.
        idx.AddRaw(1, 999, 0, 0);
        Assert.Equal(1, idx.QuadCount);
    }

    #endregion
}
