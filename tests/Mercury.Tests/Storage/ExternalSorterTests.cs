using System;
using System.Collections.Generic;
using System.IO;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// Correctness tests for the external merge-sort introduced by ADR-032 Phase 2.
/// The sorter chunks input, radix-sorts each chunk in memory, spills to a temp
/// file, and k-way-merges via binary heap during the consume phase.
/// </summary>
public class ExternalSorterTests
{
    [Fact]
    public void ReferenceKey_SingleChunkInput_ProducesSortedOutput()
    {
        string tempDir = TempPath.Test("external-sort-rk-single");
        try
        {
            var rng = new Random(42);
            var input = GenerateRandomReferenceKeys(rng, 500);
            var expected = SortedCopyReferenceKey(input);

            using var sorter = new ExternalSorter<ReferenceQuadIndex.ReferenceKey, ReferenceKeyChunkSorter>(
                tempDir: tempDir,
                chunkSize: 1000); // larger than input → single chunk
            foreach (var k in input) sorter.Add(in k);
            sorter.Complete();

            Assert.Equal(1, sorter.ChunkCount);
            var actual = DrainAll(sorter);
            AssertReferenceKeysEqual(expected, actual);
        }
        finally { TempPath.SafeCleanup(tempDir); }
    }

    [Fact]
    public void ReferenceKey_MultipleChunks_ProducesMergedSortedOutput()
    {
        string tempDir = TempPath.Test("external-sort-rk-multi");
        try
        {
            var rng = new Random(2026);
            var input = GenerateRandomReferenceKeys(rng, 10_000);
            var expected = SortedCopyReferenceKey(input);

            using var sorter = new ExternalSorter<ReferenceQuadIndex.ReferenceKey, ReferenceKeyChunkSorter>(
                tempDir: tempDir,
                chunkSize: 1000); // 10 chunks
            foreach (var k in input) sorter.Add(in k);
            sorter.Complete();

            Assert.Equal(10, sorter.ChunkCount);
            var actual = DrainAll(sorter);
            AssertReferenceKeysEqual(expected, actual);
        }
        finally { TempPath.SafeCleanup(tempDir); }
    }

    [Fact]
    public void ReferenceKey_ChunkBoundaryAtEdges_HandledCorrectly()
    {
        string tempDir = TempPath.Test("external-sort-rk-edge");
        try
        {
            var rng = new Random(7);
            // Exactly N chunks of exactly chunkSize entries with no remainder
            var input = GenerateRandomReferenceKeys(rng, 5000);
            var expected = SortedCopyReferenceKey(input);

            using var sorter = new ExternalSorter<ReferenceQuadIndex.ReferenceKey, ReferenceKeyChunkSorter>(
                tempDir: tempDir,
                chunkSize: 500); // exactly 10 chunks, no partial trailing chunk
            foreach (var k in input) sorter.Add(in k);
            sorter.Complete();

            Assert.Equal(10, sorter.ChunkCount);
            var actual = DrainAll(sorter);
            AssertReferenceKeysEqual(expected, actual);
        }
        finally { TempPath.SafeCleanup(tempDir); }
    }

    [Fact]
    public void ReferenceKey_EmptyInput_DrainReturnsFalseImmediately()
    {
        string tempDir = TempPath.Test("external-sort-empty");
        try
        {
            using var sorter = new ExternalSorter<ReferenceQuadIndex.ReferenceKey, ReferenceKeyChunkSorter>(
                tempDir: tempDir,
                chunkSize: 100);
            sorter.Complete();

            Assert.Equal(0, sorter.ChunkCount);
            Assert.False(sorter.TryDrainNext(out _));
        }
        finally { TempPath.SafeCleanup(tempDir); }
    }

    [Fact]
    public void ReferenceKey_AddAfterComplete_Throws()
    {
        string tempDir = TempPath.Test("external-sort-add-after-complete");
        try
        {
            using var sorter = new ExternalSorter<ReferenceQuadIndex.ReferenceKey, ReferenceKeyChunkSorter>(
                tempDir: tempDir,
                chunkSize: 100);
            var k = new ReferenceQuadIndex.ReferenceKey { Graph = 1, Primary = 2, Secondary = 3, Tertiary = 4 };
            sorter.Add(in k);
            sorter.Complete();

            Assert.Throws<InvalidOperationException>(() => sorter.Add(in k));
        }
        finally { TempPath.SafeCleanup(tempDir); }
    }

    [Fact]
    public void Dispose_RemovesTempDirectory()
    {
        string tempDir = TempPath.Test("external-sort-cleanup");
        try
        {
            var rng = new Random(99);
            var sorter = new ExternalSorter<ReferenceQuadIndex.ReferenceKey, ReferenceKeyChunkSorter>(
                tempDir: tempDir,
                chunkSize: 100);
            for (int i = 0; i < 250; i++)
            {
                var k = new ReferenceQuadIndex.ReferenceKey { Graph = rng.NextInt64(0, 1000), Primary = 0, Secondary = 0, Tertiary = 0 };
                sorter.Add(in k);
            }
            sorter.Complete();
            Assert.True(Directory.Exists(tempDir));
            Assert.NotEmpty(Directory.GetFiles(tempDir));

            sorter.Dispose();
            Assert.False(Directory.Exists(tempDir));
        }
        finally { TempPath.SafeCleanup(tempDir); }
    }

    [Fact]
    public void Constructor_OrphanTempDirFromPriorCrash_IsWipedAndRecreated()
    {
        string tempDir = TempPath.Test("external-sort-orphan");
        try
        {
            // Simulate a prior crashed session: tempDir exists with stray content
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "orphan-chunk-000000.bin"), "leftover bytes");
            Assert.True(File.Exists(Path.Combine(tempDir, "orphan-chunk-000000.bin")));

            // Constructing a new sorter against the same path must wipe it
            using var sorter = new ExternalSorter<ReferenceQuadIndex.ReferenceKey, ReferenceKeyChunkSorter>(
                tempDir: tempDir,
                chunkSize: 100);

            Assert.True(Directory.Exists(tempDir));
            Assert.False(File.Exists(Path.Combine(tempDir, "orphan-chunk-000000.bin")));
        }
        finally { TempPath.SafeCleanup(tempDir); }
    }

    [Fact]
    public void TrigramEntry_MultipleChunks_ProducesMergedSortedOutput()
    {
        string tempDir = TempPath.Test("external-sort-tg-multi");
        try
        {
            var rng = new Random(123);
            var input = GenerateRandomTrigramEntries(rng, 5_000);
            var expected = (TrigramEntry[])input.Clone();
            Array.Sort(expected);

            using var sorter = new ExternalSorter<TrigramEntry, TrigramEntryChunkSorter>(
                tempDir: tempDir,
                chunkSize: 700); // ~8 chunks
            foreach (var e in input) sorter.Add(in e);
            sorter.Complete();

            Assert.True(sorter.ChunkCount > 1);
            var actual = new List<TrigramEntry>(input.Length);
            while (sorter.TryDrainNext(out var e)) actual.Add(e);

            Assert.Equal(expected.Length, actual.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i].Hash, actual[i].Hash);
                Assert.Equal(expected[i].AtomId, actual[i].AtomId);
            }
        }
        finally { TempPath.SafeCleanup(tempDir); }
    }

    [Fact]
    public void Stability_EqualKeysPreserveAddOrder()
    {
        string tempDir = TempPath.Test("external-sort-stable");
        try
        {
            // Construct entries with several duplicate keys; tag the AddOrder
            // in Tertiary (lowest sort priority). After sorting, entries with
            // the same (Graph, Primary, Secondary) must come out in Tertiary
            // order — both within a chunk (radix is stable) and across chunks
            // (heap tie-breaks on lower reader index, which corresponds to
            // earlier-added chunks).
            var input = new List<ReferenceQuadIndex.ReferenceKey>();
            var rng = new Random(11);
            for (int i = 0; i < 1000; i++)
            {
                input.Add(new ReferenceQuadIndex.ReferenceKey
                {
                    Graph = rng.Next(0, 3),
                    Primary = rng.Next(0, 3),
                    Secondary = rng.Next(0, 3),
                    Tertiary = i, // Add-order tag
                });
            }
            var expected = SortedCopyReferenceKey(input.ToArray());

            using var sorter = new ExternalSorter<ReferenceQuadIndex.ReferenceKey, ReferenceKeyChunkSorter>(
                tempDir: tempDir,
                chunkSize: 100); // 10 chunks
            foreach (var k in input) sorter.Add(in k);
            sorter.Complete();

            var actual = DrainAll(sorter);
            AssertReferenceKeysEqual(expected, actual);
        }
        finally { TempPath.SafeCleanup(tempDir); }
    }

    // ---------------------- Helpers ----------------------

    private static ReferenceQuadIndex.ReferenceKey[] GenerateRandomReferenceKeys(Random rng, int count)
    {
        var arr = new ReferenceQuadIndex.ReferenceKey[count];
        for (int i = 0; i < count; i++)
        {
            arr[i] = new ReferenceQuadIndex.ReferenceKey
            {
                Graph = rng.NextInt64(0, 1_000_000),
                Primary = rng.NextInt64(0, 1_000_000),
                Secondary = rng.NextInt64(0, 1_000_000),
                Tertiary = rng.NextInt64(0, 1_000_000),
            };
        }
        return arr;
    }

    private static TrigramEntry[] GenerateRandomTrigramEntries(Random rng, int count)
    {
        var arr = new TrigramEntry[count];
        for (int i = 0; i < count; i++)
        {
            arr[i] = new TrigramEntry
            {
                Hash = (uint)rng.Next(),
                AtomId = rng.NextInt64(0, 100_000_000),
            };
        }
        return arr;
    }

    private static ReferenceQuadIndex.ReferenceKey[] SortedCopyReferenceKey(ReferenceQuadIndex.ReferenceKey[] source)
    {
        var copy = (ReferenceQuadIndex.ReferenceKey[])source.Clone();
        Array.Sort(copy, (a, b) => ReferenceQuadIndex.ReferenceKey.Compare(in a, in b));
        return copy;
    }

    private static List<ReferenceQuadIndex.ReferenceKey> DrainAll<TS>(ExternalSorter<ReferenceQuadIndex.ReferenceKey, TS> sorter)
        where TS : IChunkSorter<ReferenceQuadIndex.ReferenceKey>
    {
        var list = new List<ReferenceQuadIndex.ReferenceKey>();
        while (sorter.TryDrainNext(out var k)) list.Add(k);
        return list;
    }

    private static void AssertReferenceKeysEqual(IReadOnlyList<ReferenceQuadIndex.ReferenceKey> expected, IReadOnlyList<ReferenceQuadIndex.ReferenceKey> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            if (!expected[i].Equals(actual[i]))
            {
                Assert.Fail($"Mismatch at index {i}: expected ({expected[i].Graph},{expected[i].Primary},{expected[i].Secondary},{expected[i].Tertiary}), got ({actual[i].Graph},{actual[i].Primary},{actual[i].Secondary},{actual[i].Tertiary})");
            }
        }
    }
}
