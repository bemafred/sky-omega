using System;
using BenchmarkDotNet.Attributes;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Benchmarks;

/// <summary>
/// Microbenchmark comparing the LSD radix sort (ADR-032) against the BCL
/// <c>Array.Sort</c> with the existing <see cref="ReferenceQuadIndex.ReferenceKey.Compare"/>
/// comparator. The radix sort is the planned replacement for the rebuild path's
/// in-memory chunk sort; comparator-free byte-bucketing should beat IntroSort
/// on 32-byte composite keys.
/// </summary>
/// <remarks>
/// Each iteration sorts a fresh shuffled copy of the same dataset. The setup
/// allocates the scratch buffer once per parameter combination so the per-sort
/// numbers reflect the algorithm's cost, not the allocator's.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class RadixSortBenchmarks
{
    [Params(100_000, 1_000_000, 10_000_000)]
    public int N { get; set; }

    private ReferenceQuadIndex.ReferenceKey[] _source = null!;
    private ReferenceQuadIndex.ReferenceKey[] _data = null!;
    private ReferenceQuadIndex.ReferenceKey[] _scratch = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(2026);
        _source = new ReferenceQuadIndex.ReferenceKey[N];
        for (int i = 0; i < N; i++)
        {
            _source[i] = new ReferenceQuadIndex.ReferenceKey
            {
                Graph = rng.NextInt64(0, 1_000_000),
                Primary = rng.NextInt64(0, 1_000_000),
                Secondary = rng.NextInt64(0, 1_000_000),
                Tertiary = rng.NextInt64(0, 1_000_000),
            };
        }
        _data = new ReferenceQuadIndex.ReferenceKey[N];
        _scratch = new ReferenceQuadIndex.ReferenceKey[N];
    }

    [IterationSetup]
    public void IterationSetup()
    {
        Array.Copy(_source, _data, N);
    }

    [Benchmark(Baseline = true)]
    public void ArraySort_Comparator()
    {
        Array.Sort(_data, (a, b) => ReferenceQuadIndex.ReferenceKey.Compare(in a, in b));
    }

    [Benchmark]
    public void RadixSort_Lsd()
    {
        RadixSort.SortInPlace(_data.AsSpan(), _scratch.AsSpan());
    }
}
