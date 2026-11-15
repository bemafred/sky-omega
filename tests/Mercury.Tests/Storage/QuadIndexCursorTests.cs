using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-062 Step 4: the cursor beside the enumerators. Each of the four index classes moves a
/// <see cref="QuadIndexCursor"/> with its own <c>Advance</c>, and for every query its cursor must give the rows its
/// enumerator gives, in the same order, with the same values.
///
/// Each test fills an index with enough entries for several leaf pages, by atom numbers, and asks the same queries
/// through both. The cursor is made from the enumerator, so both start at the same position with the same bounds.
/// </summary>
public class QuadIndexCursorTests : IDisposable
{
    private readonly string _testDir;

    public QuadIndexCursorTests()
    {
        var tempPath = TempPath.Test("index-cursor");
        tempPath.MarkOwnership();
        _testDir = tempPath;
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose() => TempPath.SafeCleanup(_testDir);

    [Fact]
    public void EveryIndexsScanState_HoldsNoReference_AndFitsTheCursor()
    {
        // The cursor keeps the state as bytes. A reference in it would be invisible to the garbage collector.
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<TemporalQuadIndex.ScanState>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<VersionedQuadIndex.ScanState>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<ReferenceQuadIndex.ScanState>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<MinimalQuadIndex.ScanState>());

        // The widest state is the Cognitive index's, and the cursor's block is exactly that wide.
        Assert.Equal(QuadIndexCursor.StateBytes, Unsafe.SizeOf<TemporalQuadIndex.ScanState>());
        Assert.True(Unsafe.SizeOf<VersionedQuadIndex.ScanState>() <= QuadIndexCursor.StateBytes);
        Assert.True(Unsafe.SizeOf<ReferenceQuadIndex.ScanState>() <= QuadIndexCursor.StateBytes);
        Assert.True(Unsafe.SizeOf<MinimalQuadIndex.ScanState>() <= QuadIndexCursor.StateBytes);

        // The index, the state and the row. The enumerator that holds all four profiles' enumerators is 632 bytes.
        Assert.Equal(8 + QuadIndexCursor.StateBytes + 64, Unsafe.SizeOf<QuadIndexCursor>());
    }

    [Theory]
    [InlineData(TemporalQuadIndex.KeySortOrder.EntityFirst)]
    [InlineData(TemporalQuadIndex.KeySortOrder.TimeFirst)]
    internal void TheCognitiveIndex_GivesTheSameRowsThroughItsCursorAsThroughItsEnumerator(TemporalQuadIndex.KeySortOrder order)
    {
        using var index = new TemporalQuadIndex(Path.Combine(_testDir, "cognitive.tdb"), null, 4L << 20, order);

        // 3 graphs, 40 first, 5 second and 5 third atoms: 3,000 quads, each memorized at its own time from 1,000 on.
        // Every third quad is forgotten 4,000 later, and every ninth is memorized again after that.
        int n = 0;
        for (long graph = 0; graph < 3; graph++)
        for (long primary = 1; primary <= 40; primary++)
        for (long secondary = 101; secondary <= 105; secondary++)
        for (long tertiary = 201; tertiary <= 205; tertiary++)
        {
            long memorizedAt = 1000 + n;
            Assert.True(index.BeginPeriod(graph, primary, secondary, tertiary, memorizedAt));
            if (n % 3 == 0)
            {
                Assert.True(index.EndPeriod(graph, primary, secondary, tertiary, memorizedAt, memorizedAt + 4000));
                if (n % 9 == 0)
                    Assert.True(index.BeginPeriod(graph, primary, secondary, tertiary, memorizedAt + 9000));
            }
            n++;
        }

        var selections = new[]
        {
            new TemporalQuery { Type = TemporalQueryType.AsOf, AsOfTime = 500 },        // before anything
            new TemporalQuery { Type = TemporalQueryType.AsOf, AsOfTime = 2500 },       // half of it memorized
            new TemporalQuery { Type = TemporalQueryType.AsOf, AsOfTime = 6500 },       // some forgotten
            new TemporalQuery { Type = TemporalQueryType.AsOf, AsOfTime = 50_000 },     // after everything
            new TemporalQuery { Type = TemporalQueryType.Range, RangeStart = 0, RangeEnd = 2000 },
            new TemporalQuery { Type = TemporalQueryType.Range, RangeStart = 4500, RangeEnd = 7000 },
            new TemporalQuery { Type = TemporalQueryType.AllTime },
        };
        // -1 leaves a dimension unbound.
        var patterns = new (long Graph, long Primary, long Secondary, long Tertiary)[]
        {
            (-1, -1, -1, -1), (0, -1, -1, -1), (2, -1, -1, -1), (1, 7, -1, -1), (1, 7, 103, -1), (1, 7, 103, 204),
            (0, -1, 102, -1), (2, -1, -1, 205), (-1, 40, -1, -1), (-1, -1, 105, 201), (1, 99, -1, -1), (7, -1, -1, -1),
        };

        long rowsCompared = 0;
        int longestResult = 0;
        foreach (var selection in selections)
        foreach (var pattern in patterns)
        {
            var enumerator = index.Query(pattern.Graph, pattern.Primary, pattern.Secondary, pattern.Tertiary, selection);
            var cursor = index.Query(pattern.Graph, pattern.Primary, pattern.Secondary, pattern.Tertiary, selection).ToCursor();

            int rows = 0;
            while (enumerator.MoveNext())
            {
                var expected = enumerator.Current;
                Assert.True(cursor.MoveNext(), $"the cursor ended after {rows} rows of {selection.Type} {pattern}");
                Assert.Equal(expected.Graph, cursor.Graph);
                Assert.Equal(expected.Primary, cursor.Primary);
                Assert.Equal(expected.Secondary, cursor.Secondary);
                Assert.Equal(expected.Tertiary, cursor.Tertiary);
                Assert.Equal(expected.MemorizedAt, cursor.MemorizedAt);
                Assert.Equal(expected.ForgottenAt, cursor.ForgottenAt);
                Assert.Equal(expected.LegacyTransactionTime, cursor.LegacyTransactionTime);
                Assert.Equal(expected.LegacyIsDeleted, cursor.LegacyIsDeleted);
                rows++;
            }
            Assert.False(cursor.MoveNext(), $"the cursor has a row more than the enumerator's {rows} of {selection.Type} {pattern}");
            Assert.False(cursor.MoveNext());                        // and stays at its end

            rowsCompared += rows;
            longestResult = Math.Max(longestResult, rows);
        }

        // The comparison compared something, and at least one result was longer than a leaf page holds (185 entries).
        Assert.True(rowsCompared > 10_000, $"only {rowsCompared} rows were compared");
        Assert.True(longestResult > 1000, $"the longest result had {longestResult} rows");
    }

    [Fact]
    public void TheGraphIndex_GivesTheSameRowsThroughItsCursorAsThroughItsEnumerator()
    {
        using var index = new VersionedQuadIndex(Path.Combine(_testDir, "graph.tdb"), null, 4L << 20);

        // 3,000 quads; every fourth is deleted, which leaves its entry with a deleted mark.
        int n = 0;
        for (long graph = 0; graph < 3; graph++)
        for (long primary = 1; primary <= 40; primary++)
        for (long secondary = 101; secondary <= 105; secondary++)
        for (long tertiary = 201; tertiary <= 205; tertiary++)
        {
            index.AddRaw(graph, primary, secondary, tertiary);
            if (n % 4 == 0) Assert.True(index.DeleteRaw(graph, primary, secondary, tertiary));
            n++;
        }

        var patterns = new (long Graph, long Primary, long Secondary, long Tertiary)[]
        {
            (-1, -1, -1, -1), (0, -1, -1, -1), (2, -1, -1, -1), (1, 7, -1, -1), (1, 7, 103, -1), (1, 7, 103, 204),
            (0, 1, 101, 201), (1, 99, -1, -1), (7, -1, -1, -1),
        };

        long rowsCompared = 0, deletedRowsSeen = 0;
        int longestResult = 0;
        foreach (bool withDeleted in new[] { false, true })
        foreach (var pattern in patterns)
        {
            var enumerator = withDeleted
                ? index.QueryAllVersions(pattern.Graph, pattern.Primary, pattern.Secondary, pattern.Tertiary)
                : index.Query(pattern.Graph, pattern.Primary, pattern.Secondary, pattern.Tertiary);
            var cursor = (withDeleted
                ? index.QueryAllVersions(pattern.Graph, pattern.Primary, pattern.Secondary, pattern.Tertiary)
                : index.Query(pattern.Graph, pattern.Primary, pattern.Secondary, pattern.Tertiary)).ToCursor();

            int rows = 0;
            while (enumerator.MoveNext())
            {
                var expected = enumerator.Current;
                Assert.True(cursor.MoveNext(), $"the cursor ended after {rows} rows of {pattern}, deleted included: {withDeleted}");
                Assert.Equal(expected.Graph, cursor.Graph);
                Assert.Equal(expected.Primary, cursor.Primary);
                Assert.Equal(expected.Secondary, cursor.Secondary);
                Assert.Equal(expected.Tertiary, cursor.Tertiary);
                Assert.Equal(expected.IsDeleted, cursor.LegacyIsDeleted);
                if (expected.IsDeleted) deletedRowsSeen++;
                rows++;
            }
            Assert.False(cursor.MoveNext(), $"the cursor has a row more than the enumerator's {rows} of {pattern}, deleted included: {withDeleted}");

            rowsCompared += rows;
            longestResult = Math.Max(longestResult, rows);
        }

        Assert.True(rowsCompared > 5000, $"only {rowsCompared} rows were compared");
        Assert.True(longestResult > 2000, $"the longest result had {longestResult} rows");
        Assert.True(deletedRowsSeen > 500, $"only {deletedRowsSeen} deleted rows were seen");
    }

    [Fact]
    public void TheReferenceIndex_GivesTheSameRowsThroughItsCursorAsThroughItsEnumerator()
    {
        using var index = new ReferenceQuadIndex(Path.Combine(_testDir, "reference.tdb"), sharedAtoms: null, initialSizeBytes: 4L << 20);

        for (long graph = 0; graph < 3; graph++)
        for (long primary = 1; primary <= 40; primary++)
        for (long secondary = 101; secondary <= 105; secondary++)
        for (long tertiary = 201; tertiary <= 205; tertiary++)
            index.AddRaw(graph, primary, secondary, tertiary);

        var patterns = new (long Graph, long Primary, long Secondary, long Tertiary)[]
        {
            (-1, -1, -1, -1), (0, -1, -1, -1), (2, -1, -1, -1), (1, 7, -1, -1), (1, 7, 103, -1), (1, 7, 103, 204),
            (0, 1, 101, 201), (2, 40, 105, 205), (1, 99, -1, -1), (7, -1, -1, -1),
        };

        long rowsCompared = 0;
        int longestResult = 0;
        foreach (var pattern in patterns)
        {
            var enumerator = index.Query(pattern.Graph, pattern.Primary, pattern.Secondary, pattern.Tertiary);
            var cursor = index.Query(pattern.Graph, pattern.Primary, pattern.Secondary, pattern.Tertiary).ToCursor();

            int rows = 0;
            while (enumerator.MoveNext())
            {
                var expected = enumerator.Current;
                Assert.True(cursor.MoveNext(), $"the cursor ended after {rows} rows of {pattern}");
                Assert.Equal(expected.Graph, cursor.Graph);
                Assert.Equal(expected.Primary, cursor.Primary);
                Assert.Equal(expected.Secondary, cursor.Secondary);
                Assert.Equal(expected.Tertiary, cursor.Tertiary);
                rows++;
            }
            Assert.False(cursor.MoveNext(), $"the cursor has a row more than the enumerator's {rows} of {pattern}");

            rowsCompared += rows;
            longestResult = Math.Max(longestResult, rows);
        }

        // 3,000 entries are more than five leaf pages of 511.
        Assert.True(rowsCompared > 5000, $"only {rowsCompared} rows were compared");
        Assert.Equal(3000, longestResult);
    }

    [Fact]
    public void TheMinimalIndex_GivesTheSameRowsThroughItsCursorAsThroughItsEnumerator()
    {
        using var index = new MinimalQuadIndex(Path.Combine(_testDir, "minimal.tdb"), null, 4L << 20);

        for (long primary = 1; primary <= 120; primary++)
        for (long secondary = 101; secondary <= 105; secondary++)
        for (long tertiary = 201; tertiary <= 205; tertiary++)
            index.AddRaw(primary, secondary, tertiary);

        var patterns = new (long Primary, long Secondary, long Tertiary)[]
        {
            (-1, -1, -1), (7, -1, -1), (7, 103, -1), (7, 103, 204), (1, 101, 201), (120, 105, 205), (999, -1, -1),
        };

        long rowsCompared = 0;
        int longestResult = 0;
        foreach (var pattern in patterns)
        {
            var enumerator = index.Query(pattern.Primary, pattern.Secondary, pattern.Tertiary);
            var cursor = index.Query(pattern.Primary, pattern.Secondary, pattern.Tertiary).ToCursor();

            int rows = 0;
            while (enumerator.MoveNext())
            {
                var expected = enumerator.Current;
                Assert.True(cursor.MoveNext(), $"the cursor ended after {rows} rows of {pattern}");
                Assert.Equal(0, cursor.Graph);                      // this index has no graph: the default graph
                Assert.Equal(expected.Primary, cursor.Primary);
                Assert.Equal(expected.Secondary, cursor.Secondary);
                Assert.Equal(expected.Tertiary, cursor.Tertiary);
                rows++;
            }
            Assert.False(cursor.MoveNext(), $"the cursor has a row more than the enumerator's {rows} of {pattern}");

            rowsCompared += rows;
            longestResult = Math.Max(longestResult, rows);
        }

        // 3,000 entries are more than four leaf pages of 681.
        Assert.True(rowsCompared > 3000, $"only {rowsCompared} rows were compared");
        Assert.Equal(3000, longestResult);
    }

    [Fact]
    public void ACursorCopiedInTheMiddleOfAScan_GoesOnFromThere_AndTheOriginalToo()
    {
        // The engine holds scans by value and copies them. A copy is the same scan at the same row.
        using var index = new ReferenceQuadIndex(Path.Combine(_testDir, "copied.tdb"), sharedAtoms: null, initialSizeBytes: 4L << 20);
        for (long primary = 1; primary <= 1500; primary++)
            index.AddRaw(0, primary, 101, 201);

        var cursor = index.Query(0, -1, -1, -1).ToCursor();
        for (int row = 0; row < 600; row++) Assert.True(cursor.MoveNext());     // past the first leaf page of 511
        Assert.Equal(600, cursor.Primary);

        var copy = cursor;
        var fromTheOriginal = new List<long>();
        while (cursor.MoveNext()) fromTheOriginal.Add(cursor.Primary);
        var fromTheCopy = new List<long>();
        while (copy.MoveNext()) fromTheCopy.Add(copy.Primary);

        Assert.Equal(900, fromTheOriginal.Count);
        Assert.Equal(601, fromTheOriginal[0]);
        Assert.Equal(1500, fromTheOriginal[^1]);
        Assert.Equal(fromTheOriginal, fromTheCopy);
    }
}
