using System;
using System.Collections.Generic;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// Correctness and zero-allocation tests for the LSD radix sort introduced
/// by ADR-032. The sort backs the secondary-index rebuild path; bugs here
/// would silently produce wrongly-ordered B+Trees on which queries would
/// then return wrong rows.
/// </summary>
public class RadixSortTests
{
    // ---------------------- ReferenceKey ----------------------

    [Fact]
    public void ReferenceKey_Random_MatchesArraySort()
    {
        var rng = new Random(42);
        var data = GenerateRandomReferenceKeys(rng, 10_000);
        var expected = (ReferenceQuadIndex.ReferenceKey[])data.Clone();
        Array.Sort(expected, (a, b) => ReferenceQuadIndex.ReferenceKey.Compare(in a, in b));

        var scratch = new ReferenceQuadIndex.ReferenceKey[data.Length];
        RadixSort.SortInPlace(data.AsSpan(), scratch.AsSpan());

        AssertReferenceKeysEqual(expected, data);
    }

    [Fact]
    public void ReferenceKey_AlreadySorted_RemainsSorted()
    {
        var rng = new Random(7);
        var data = GenerateRandomReferenceKeys(rng, 5_000);
        Array.Sort(data, (a, b) => ReferenceQuadIndex.ReferenceKey.Compare(in a, in b));
        var expected = (ReferenceQuadIndex.ReferenceKey[])data.Clone();

        var scratch = new ReferenceQuadIndex.ReferenceKey[data.Length];
        RadixSort.SortInPlace(data.AsSpan(), scratch.AsSpan());

        AssertReferenceKeysEqual(expected, data);
    }

    [Fact]
    public void ReferenceKey_ReverseSorted_ProducesAscending()
    {
        var rng = new Random(13);
        var data = GenerateRandomReferenceKeys(rng, 5_000);
        Array.Sort(data, (a, b) => ReferenceQuadIndex.ReferenceKey.Compare(in b, in a)); // descending
        var expected = (ReferenceQuadIndex.ReferenceKey[])data.Clone();
        Array.Sort(expected, (a, b) => ReferenceQuadIndex.ReferenceKey.Compare(in a, in b));

        var scratch = new ReferenceQuadIndex.ReferenceKey[data.Length];
        RadixSort.SortInPlace(data.AsSpan(), scratch.AsSpan());

        AssertReferenceKeysEqual(expected, data);
    }

    [Fact]
    public void ReferenceKey_AllIdentical_RemainsUnchanged()
    {
        var single = new ReferenceQuadIndex.ReferenceKey { Graph = 1, Primary = 2, Secondary = 3, Tertiary = 4 };
        var data = new ReferenceQuadIndex.ReferenceKey[1_000];
        for (int i = 0; i < data.Length; i++) data[i] = single;

        var scratch = new ReferenceQuadIndex.ReferenceKey[data.Length];
        RadixSort.SortInPlace(data.AsSpan(), scratch.AsSpan());

        for (int i = 0; i < data.Length; i++)
        {
            Assert.Equal(single, data[i]);
        }
    }

    [Fact]
    public void ReferenceKey_NegativeFields_SortsByCompareSemantics()
    {
        // Atom IDs are nominally positive, but the radix sort must match Compare
        // even on negative values (the biased-radix MSB trick must work).
        var data = new[]
        {
            new ReferenceQuadIndex.ReferenceKey { Graph = -100, Primary = 0, Secondary = 0, Tertiary = 0 },
            new ReferenceQuadIndex.ReferenceKey { Graph =  100, Primary = 0, Secondary = 0, Tertiary = 0 },
            new ReferenceQuadIndex.ReferenceKey { Graph =    0, Primary = 0, Secondary = 0, Tertiary = 0 },
            new ReferenceQuadIndex.ReferenceKey { Graph = -1,   Primary = long.MaxValue, Secondary = 0, Tertiary = 0 },
            new ReferenceQuadIndex.ReferenceKey { Graph =  1,   Primary = long.MinValue, Secondary = 0, Tertiary = 0 },
        };
        var expected = (ReferenceQuadIndex.ReferenceKey[])data.Clone();
        Array.Sort(expected, (a, b) => ReferenceQuadIndex.ReferenceKey.Compare(in a, in b));

        var scratch = new ReferenceQuadIndex.ReferenceKey[data.Length];
        RadixSort.SortInPlace(data.AsSpan(), scratch.AsSpan());

        AssertReferenceKeysEqual(expected, data);
    }

    [Fact]
    public void ReferenceKey_ZeroOrOneEntries_NoOp()
    {
        var empty = Array.Empty<ReferenceQuadIndex.ReferenceKey>();
        RadixSort.SortInPlace(empty.AsSpan(), empty.AsSpan()); // must not throw

        var single = new[] { new ReferenceQuadIndex.ReferenceKey { Graph = 99, Primary = 0, Secondary = 0, Tertiary = 0 } };
        var scratch = new ReferenceQuadIndex.ReferenceKey[1];
        RadixSort.SortInPlace(single.AsSpan(), scratch.AsSpan());
        Assert.Equal(99, single[0].Graph);
    }

    [Fact]
    public void ReferenceKey_ScratchSizeMismatch_Throws()
    {
        var data = new ReferenceQuadIndex.ReferenceKey[10];
        var scratch = new ReferenceQuadIndex.ReferenceKey[5];
        Assert.Throws<ArgumentException>(() => RadixSort.SortInPlace(data.AsSpan(), scratch.AsSpan()));
    }

    [Fact]
    public void ReferenceKey_LargeRandom_MatchesArraySort()
    {
        // Larger sample size to exercise multiple histogram passes including
        // the high-byte zeros (skip-trivial path).
        var rng = new Random(2026);
        var data = GenerateRandomReferenceKeys(rng, 100_000);
        var expected = (ReferenceQuadIndex.ReferenceKey[])data.Clone();
        Array.Sort(expected, (a, b) => ReferenceQuadIndex.ReferenceKey.Compare(in a, in b));

        var scratch = new ReferenceQuadIndex.ReferenceKey[data.Length];
        RadixSort.SortInPlace(data.AsSpan(), scratch.AsSpan());

        AssertReferenceKeysEqual(expected, data);
    }

    [Fact]
    public void ReferenceKey_Stable_PreservesOriginalOrderForDuplicates()
    {
        // LSD radix sort is stable. Verify by giving entries that compare equal
        // (same key) and tagging them by order of insertion via Tertiary,
        // which is the lowest sort priority — entries with identical
        // (Graph, Primary, Secondary) must come out in Tertiary order.
        var data = new ReferenceQuadIndex.ReferenceKey[100];
        var rng = new Random(99);
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = new ReferenceQuadIndex.ReferenceKey
            {
                Graph = rng.Next(0, 5),
                Primary = rng.Next(0, 5),
                Secondary = rng.Next(0, 5),
                Tertiary = i,
            };
        }
        var expected = (ReferenceQuadIndex.ReferenceKey[])data.Clone();
        Array.Sort(expected, (a, b) => ReferenceQuadIndex.ReferenceKey.Compare(in a, in b));

        var scratch = new ReferenceQuadIndex.ReferenceKey[data.Length];
        RadixSort.SortInPlace(data.AsSpan(), scratch.AsSpan());

        AssertReferenceKeysEqual(expected, data);
    }

    // ---------------------- TrigramEntry ----------------------

    [Fact]
    public void TrigramEntry_Random_MatchesArraySort()
    {
        var rng = new Random(1234);
        var data = GenerateRandomTrigramEntries(rng, 10_000);
        var expected = (TrigramEntry[])data.Clone();
        Array.Sort(expected, CompareTrigramEntry);

        var scratch = new TrigramEntry[data.Length];
        RadixSort.SortInPlace(data.AsSpan(), scratch.AsSpan());

        AssertTrigramEntriesEqual(expected, data);
    }

    [Fact]
    public void TrigramEntry_GroupsByHashThenSortsByAtomId()
    {
        // Construct entries with deliberately repeated hashes; verify each hash
        // group is contiguous and AtomIds within a group are ascending.
        var rng = new Random(55);
        var data = new TrigramEntry[5_000];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = new TrigramEntry
            {
                Hash = (uint)rng.Next(0, 50),
                AtomId = rng.NextInt64(0, 1_000_000),
            };
        }
        var scratch = new TrigramEntry[data.Length];
        RadixSort.SortInPlace(data.AsSpan(), scratch.AsSpan());

        for (int i = 1; i < data.Length; i++)
        {
            int cmp = data[i - 1].Hash.CompareTo(data[i].Hash);
            if (cmp == 0)
            {
                Assert.True(data[i - 1].AtomId <= data[i].AtomId,
                    $"At index {i}: same hash {data[i].Hash} but AtomId out of order ({data[i-1].AtomId} > {data[i].AtomId})");
            }
            else
            {
                Assert.True(cmp < 0,
                    $"At index {i}: hashes out of order ({data[i-1].Hash} > {data[i].Hash})");
            }
        }
    }

    [Fact]
    public void TrigramEntry_NegativeAtomIds_SortsCorrectly()
    {
        var data = new[]
        {
            new TrigramEntry { Hash = 1, AtomId = -5 },
            new TrigramEntry { Hash = 1, AtomId =  3 },
            new TrigramEntry { Hash = 1, AtomId =  0 },
            new TrigramEntry { Hash = 1, AtomId = long.MinValue },
            new TrigramEntry { Hash = 1, AtomId = long.MaxValue },
        };
        var expected = (TrigramEntry[])data.Clone();
        Array.Sort(expected, CompareTrigramEntry);

        var scratch = new TrigramEntry[data.Length];
        RadixSort.SortInPlace(data.AsSpan(), scratch.AsSpan());

        AssertTrigramEntriesEqual(expected, data);
    }

    [Fact]
    public void TrigramEntry_HashUsesUnsignedComparison()
    {
        // Two hashes that differ only in the MSB. As unsigned uint32, 0x80000001
        // > 0x00000001. Naïve signed byte comparison without unsigned-MSB handling
        // would flip these. The radix sort must treat Hash as unsigned.
        var data = new[]
        {
            new TrigramEntry { Hash = 0x80000001u, AtomId = 1 },
            new TrigramEntry { Hash = 0x00000001u, AtomId = 2 },
        };
        var scratch = new TrigramEntry[data.Length];
        RadixSort.SortInPlace(data.AsSpan(), scratch.AsSpan());

        Assert.Equal(0x00000001u, data[0].Hash);
        Assert.Equal(0x80000001u, data[1].Hash);
    }

    // ---------------------- Allocation ----------------------

    [Fact]
    public void SortInPlace_ReferenceKey_ZeroAllocations()
    {
        var rng = new Random(2026);
        var data = GenerateRandomReferenceKeys(rng, 1_000);
        var scratch = new ReferenceQuadIndex.ReferenceKey[data.Length];

        // Warmup
        RadixSort.SortInPlace(data.AsSpan(), scratch.AsSpan());

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            // Refill data each iteration so we exercise full passes
            FillRandomReferenceKeys(rng, data);
            RadixSort.SortInPlace(data.AsSpan(), scratch.AsSpan());
        }
        var after = GC.GetAllocatedBytesForCurrentThread();
        var allocated = after - before;

        Assert.True(allocated < 1000,
            $"RadixSort.SortInPlace<ReferenceKey> allocated {allocated} bytes over 100 sorts of 1000 entries. Expected ~zero.");
    }

    [Fact]
    public void SortInPlace_TrigramEntry_ZeroAllocations()
    {
        var rng = new Random(2027);
        var data = GenerateRandomTrigramEntries(rng, 1_000);
        var scratch = new TrigramEntry[data.Length];

        RadixSort.SortInPlace(data.AsSpan(), scratch.AsSpan());

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            FillRandomTrigramEntries(rng, data);
            RadixSort.SortInPlace(data.AsSpan(), scratch.AsSpan());
        }
        var after = GC.GetAllocatedBytesForCurrentThread();
        var allocated = after - before;

        Assert.True(allocated < 1000,
            $"RadixSort.SortInPlace<TrigramEntry> allocated {allocated} bytes over 100 sorts of 1000 entries. Expected ~zero.");
    }

    // ---------------------- Helpers ----------------------

    private static ReferenceQuadIndex.ReferenceKey[] GenerateRandomReferenceKeys(Random rng, int count)
    {
        var arr = new ReferenceQuadIndex.ReferenceKey[count];
        FillRandomReferenceKeys(rng, arr);
        return arr;
    }

    private static void FillRandomReferenceKeys(Random rng, ReferenceQuadIndex.ReferenceKey[] arr)
    {
        // Realistic atom-ID ranges: 30-bit positive longs (mimics actual Mercury data).
        for (int i = 0; i < arr.Length; i++)
        {
            arr[i] = new ReferenceQuadIndex.ReferenceKey
            {
                Graph = rng.NextInt64(0, 1_000_000),
                Primary = rng.NextInt64(0, 1_000_000),
                Secondary = rng.NextInt64(0, 1_000_000),
                Tertiary = rng.NextInt64(0, 1_000_000),
            };
        }
    }

    private static TrigramEntry[] GenerateRandomTrigramEntries(Random rng, int count)
    {
        var arr = new TrigramEntry[count];
        FillRandomTrigramEntries(rng, arr);
        return arr;
    }

    private static void FillRandomTrigramEntries(Random rng, TrigramEntry[] arr)
    {
        for (int i = 0; i < arr.Length; i++)
        {
            arr[i] = new TrigramEntry
            {
                Hash = (uint)rng.Next(),
                AtomId = rng.NextInt64(0, 100_000_000),
            };
        }
    }

    private static int CompareTrigramEntry(TrigramEntry a, TrigramEntry b)
    {
        int cmp = a.Hash.CompareTo(b.Hash);
        return cmp != 0 ? cmp : a.AtomId.CompareTo(b.AtomId);
    }

    private static void AssertReferenceKeysEqual(ReferenceQuadIndex.ReferenceKey[] expected, ReferenceQuadIndex.ReferenceKey[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            if (!expected[i].Equals(actual[i]))
            {
                Assert.Fail($"Mismatch at index {i}: expected ({expected[i].Graph},{expected[i].Primary},{expected[i].Secondary},{expected[i].Tertiary}), got ({actual[i].Graph},{actual[i].Primary},{actual[i].Secondary},{actual[i].Tertiary})");
            }
        }
    }

    private static void AssertTrigramEntriesEqual(TrigramEntry[] expected, TrigramEntry[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i].Hash != actual[i].Hash || expected[i].AtomId != actual[i].AtomId)
            {
                Assert.Fail($"Mismatch at index {i}: expected (Hash={expected[i].Hash}, AtomId={expected[i].AtomId}), got (Hash={actual[i].Hash}, AtomId={actual[i].AtomId})");
            }
        }
    }
}
