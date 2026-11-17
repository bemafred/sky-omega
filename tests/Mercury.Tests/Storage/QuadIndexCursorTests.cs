using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-062: the one read of each index. Each of the four index classes moves a <see cref="QuadIndexCursor"/> with
/// its own <c>Advance</c>, and for every query the cursor must give exactly the entries the query selects, in the
/// index's key order, with their values.
///
/// Each test fills an index with enough entries for several leaf pages, by atom numbers, and keeps what it wrote
/// beside the index. What a query must give is computed from that, not read from the index. (Until ADR-062 Step 6 the
/// oracle was each index's own enumerator; Step 6 removed them.)
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

        // The index, the state and the row. The enumerator that held all four profiles' enumerators was 632 bytes.
        Assert.Equal(8 + QuadIndexCursor.StateBytes + 72, Unsafe.SizeOf<QuadIndexCursor>());
    }

    [Theory]
    [InlineData(TemporalQuadIndex.KeySortOrder.EntityFirst)]
    [InlineData(TemporalQuadIndex.KeySortOrder.TimeFirst)]
    internal void TheCognitiveIndexsCursor_GivesEveryPeriodItsQuerySelects_InTheIndexsOrder(TemporalQuadIndex.KeySortOrder order)
    {
        using var index = new TemporalQuadIndex(Path.Combine(_testDir, "cognitive.tdb"), null, 4L << 20, order);
        var open = TemporalQuadIndex.OpenForgottenAt;

        // 3 graphs, 40 first, 5 second and 5 third atoms: 3,000 quads, each memorized at its own time from 1,000 on.
        // Every third quad is forgotten 4,000 later, and every ninth is memorized again after that. The periods
        // written are kept beside the index: they are what a scan must give.
        var periods = new List<(long Graph, long Primary, long Secondary, long Tertiary, long MemorizedAt, long ForgottenAt)>();
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
                periods.Add((graph, primary, secondary, tertiary, memorizedAt, memorizedAt + 4000));
                if (n % 9 == 0)
                {
                    Assert.True(index.BeginPeriod(graph, primary, secondary, tertiary, memorizedAt + 9000));
                    periods.Add((graph, primary, secondary, tertiary, memorizedAt + 9000, open));
                }
            }
            else
            {
                periods.Add((graph, primary, secondary, tertiary, memorizedAt, open));
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
        // -1 leaves a dimension unbound. Some patterns bind a dimension behind an unbound one, which the key range
        // cannot select by.
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
            // What the scan must give: the periods with the bound dimensions that the time selection selects, in
            // this index's key order (entity first: the quad, then the memorized time; time first: the memorized
            // time, then the quad).
            var selected = periods.Where(p =>
                (pattern.Graph < 0 || p.Graph == pattern.Graph) &&
                (pattern.Primary < 0 || p.Primary == pattern.Primary) &&
                (pattern.Secondary < 0 || p.Secondary == pattern.Secondary) &&
                (pattern.Tertiary < 0 || p.Tertiary == pattern.Tertiary) &&
                selection.Type switch
                {
                    TemporalQueryType.AsOf => p.MemorizedAt <= selection.AsOfTime && p.ForgottenAt > selection.AsOfTime,
                    TemporalQueryType.Range => p.MemorizedAt < selection.RangeEnd && p.ForgottenAt > selection.RangeStart,
                    _ => true,
                });
            var expected = (order == TemporalQuadIndex.KeySortOrder.TimeFirst
                ? selected.OrderBy(p => p.MemorizedAt).ThenBy(p => p.Graph).ThenBy(p => p.Primary).ThenBy(p => p.Secondary).ThenBy(p => p.Tertiary)
                : selected.OrderBy(p => p.Graph).ThenBy(p => p.Primary).ThenBy(p => p.Secondary).ThenBy(p => p.Tertiary).ThenBy(p => p.MemorizedAt))
                .ToList();

            var cursor = index.Query(pattern.Graph, pattern.Primary, pattern.Secondary, pattern.Tertiary, selection);
            foreach (var period in expected)
            {
                Assert.True(cursor.MoveNext(), $"the cursor ended before {period} of {selection.Type} {pattern}");
                Assert.Equal(period.Graph, cursor.Graph);
                Assert.Equal(period.Primary, cursor.Primary);
                Assert.Equal(period.Secondary, cursor.Secondary);
                Assert.Equal(period.Tertiary, cursor.Tertiary);
                Assert.Equal(period.MemorizedAt, cursor.MemorizedAt);
                Assert.Equal(period.ForgottenAt, cursor.ForgottenAt);
                Assert.False(cursor.LegacyIsDeleted);
                Assert.True(cursor.HasMemoryTimes);
            }
            Assert.False(cursor.MoveNext(), $"the cursor has a row more than the {expected.Count} of {selection.Type} {pattern}");
            Assert.False(cursor.MoveNext());                        // and stays at its end

            rowsCompared += expected.Count;
            longestResult = Math.Max(longestResult, expected.Count);
        }

        // The comparison compared something, and at least one result was longer than a leaf page holds (185 entries).
        Assert.True(rowsCompared > 10_000, $"only {rowsCompared} rows were compared");
        Assert.True(longestResult > 1000, $"the longest result had {longestResult} rows");
    }

    [Fact]
    public void TheGraphIndexsCursor_GivesEveryEntryItsQuerySelects_WithItsVersion_InKeyOrder()
    {
        using var index = new VersionedQuadIndex(Path.Combine(_testDir, "graph.tdb"), null, 4L << 20);

        // 3,000 quads; every fourth is retracted, which takes its entry to version 2, an even one. The entries
        // written are kept beside the index, in key order: graph, then the three dimensions.
        var entries = new List<(long Graph, long Primary, long Secondary, long Tertiary, int Version, bool Retracted)>();
        int n = 0;
        for (long graph = 0; graph < 3; graph++)
        for (long primary = 1; primary <= 40; primary++)
        for (long secondary = 101; secondary <= 105; secondary++)
        for (long tertiary = 201; tertiary <= 205; tertiary++)
        {
            index.NextVersion(graph, primary, secondary, tertiary, 0);
            bool retracted = n % 4 == 0;
            if (retracted) Assert.True(index.NextVersion(graph, primary, secondary, tertiary, 1));
            entries.Add((graph, primary, secondary, tertiary, retracted ? 2 : 1, retracted));
            n++;
        }

        var patterns = new (long Graph, long Primary, long Secondary, long Tertiary)[]
        {
            (-1, -1, -1, -1), (0, -1, -1, -1), (2, -1, -1, -1), (1, 7, -1, -1), (1, 7, 103, -1), (1, 7, 103, 204),
            (0, 1, 101, 201), (1, 99, -1, -1), (7, -1, -1, -1),
        };

        long rowsCompared = 0, retractedRowsSeen = 0;
        int longestResult = 0;
        foreach (bool withRetracted in new[] { false, true })
        foreach (var pattern in patterns)
        {
            var expected = entries.Where(e =>
                (withRetracted || !e.Retracted) &&
                (pattern.Graph < 0 || e.Graph == pattern.Graph) &&
                (pattern.Primary < 0 || e.Primary == pattern.Primary) &&
                (pattern.Secondary < 0 || e.Secondary == pattern.Secondary) &&
                (pattern.Tertiary < 0 || e.Tertiary == pattern.Tertiary)).ToList();

            var cursor = withRetracted
                ? index.QueryAllVersions(pattern.Graph, pattern.Primary, pattern.Secondary, pattern.Tertiary)
                : index.Query(pattern.Graph, pattern.Primary, pattern.Secondary, pattern.Tertiary);
            foreach (var entry in expected)
            {
                Assert.True(cursor.MoveNext(), $"the cursor ended before {entry} of {pattern}, retracted included: {withRetracted}");
                Assert.Equal(entry.Graph, cursor.Graph);
                Assert.Equal(entry.Primary, cursor.Primary);
                Assert.Equal(entry.Secondary, cursor.Secondary);
                Assert.Equal(entry.Tertiary, cursor.Tertiary);
                Assert.Equal(entry.Version, cursor.Version);
                Assert.Equal(entry.Retracted, cursor.LegacyIsDeleted);
                if (entry.Retracted) retractedRowsSeen++;
            }
            Assert.False(cursor.MoveNext(), $"the cursor has a row more than the {expected.Count} of {pattern}, retracted included: {withRetracted}");

            rowsCompared += expected.Count;
            longestResult = Math.Max(longestResult, expected.Count);
        }

        Assert.True(rowsCompared > 5000, $"only {rowsCompared} rows were compared");
        Assert.True(longestResult > 2000, $"the longest result had {longestResult} rows");
        Assert.True(retractedRowsSeen > 500, $"only {retractedRowsSeen} retracted rows were seen");
    }

    [Fact]
    public void TheReferenceIndexsCursor_GivesEveryEntryItsQuerySelects_InKeyOrder()
    {
        using var index = new ReferenceQuadIndex(Path.Combine(_testDir, "reference.tdb"), sharedAtoms: null, initialSizeBytes: 4L << 20);

        // The entries written, in key order: graph, then the three dimensions.
        var entries = new List<(long Graph, long Primary, long Secondary, long Tertiary)>();
        for (long graph = 0; graph < 3; graph++)
        for (long primary = 1; primary <= 40; primary++)
        for (long secondary = 101; secondary <= 105; secondary++)
        for (long tertiary = 201; tertiary <= 205; tertiary++)
        {
            index.AddRaw(graph, primary, secondary, tertiary);
            entries.Add((graph, primary, secondary, tertiary));
        }

        var patterns = new (long Graph, long Primary, long Secondary, long Tertiary)[]
        {
            (-1, -1, -1, -1), (0, -1, -1, -1), (2, -1, -1, -1), (1, 7, -1, -1), (1, 7, 103, -1), (1, 7, 103, 204),
            (0, 1, 101, 201), (2, 40, 105, 205), (1, 99, -1, -1), (7, -1, -1, -1),
        };

        long rowsCompared = 0;
        int longestResult = 0;
        foreach (var pattern in patterns)
        {
            var expected = entries.Where(e =>
                (pattern.Graph < 0 || e.Graph == pattern.Graph) &&
                (pattern.Primary < 0 || e.Primary == pattern.Primary) &&
                (pattern.Secondary < 0 || e.Secondary == pattern.Secondary) &&
                (pattern.Tertiary < 0 || e.Tertiary == pattern.Tertiary)).ToList();

            var cursor = index.Query(pattern.Graph, pattern.Primary, pattern.Secondary, pattern.Tertiary);
            foreach (var entry in expected)
            {
                Assert.True(cursor.MoveNext(), $"the cursor ended before {entry} of {pattern}");
                Assert.Equal(entry.Graph, cursor.Graph);
                Assert.Equal(entry.Primary, cursor.Primary);
                Assert.Equal(entry.Secondary, cursor.Secondary);
                Assert.Equal(entry.Tertiary, cursor.Tertiary);
            }
            Assert.False(cursor.MoveNext(), $"the cursor has a row more than the {expected.Count} of {pattern}");

            rowsCompared += expected.Count;
            longestResult = Math.Max(longestResult, expected.Count);
        }

        // 3,000 entries are more than five leaf pages of 511.
        Assert.True(rowsCompared > 5000, $"only {rowsCompared} rows were compared");
        Assert.Equal(3000, longestResult);
    }

    [Fact]
    public void TheMinimalIndexsCursor_GivesEveryEntryItsQuerySelects_InKeyOrder()
    {
        using var index = new MinimalQuadIndex(Path.Combine(_testDir, "minimal.tdb"), null, 4L << 20);

        // The entries written, in key order: the three dimensions; this index has no graph.
        var entries = new List<(long Primary, long Secondary, long Tertiary)>();
        for (long primary = 1; primary <= 120; primary++)
        for (long secondary = 101; secondary <= 105; secondary++)
        for (long tertiary = 201; tertiary <= 205; tertiary++)
        {
            index.AddRaw(primary, secondary, tertiary);
            entries.Add((primary, secondary, tertiary));
        }

        var patterns = new (long Primary, long Secondary, long Tertiary)[]
        {
            (-1, -1, -1), (7, -1, -1), (7, 103, -1), (7, 103, 204), (1, 101, 201), (120, 105, 205), (999, -1, -1),
        };

        long rowsCompared = 0;
        int longestResult = 0;
        foreach (var pattern in patterns)
        {
            var expected = entries.Where(e =>
                (pattern.Primary < 0 || e.Primary == pattern.Primary) &&
                (pattern.Secondary < 0 || e.Secondary == pattern.Secondary) &&
                (pattern.Tertiary < 0 || e.Tertiary == pattern.Tertiary)).ToList();

            var cursor = index.Query(pattern.Primary, pattern.Secondary, pattern.Tertiary);
            foreach (var entry in expected)
            {
                Assert.True(cursor.MoveNext(), $"the cursor ended before {entry} of {pattern}");
                Assert.Equal(0, cursor.Graph);                      // this index has no graph: the default graph
                Assert.Equal(entry.Primary, cursor.Primary);
                Assert.Equal(entry.Secondary, cursor.Secondary);
                Assert.Equal(entry.Tertiary, cursor.Tertiary);
            }
            Assert.False(cursor.MoveNext(), $"the cursor has a row more than the {expected.Count} of {pattern}");

            rowsCompared += expected.Count;
            longestResult = Math.Max(longestResult, expected.Count);
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

        var cursor = index.Query(0, -1, -1, -1);
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
