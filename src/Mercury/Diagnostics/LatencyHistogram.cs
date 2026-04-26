using System;
using System.Numerics;
using System.Threading;

namespace SkyOmega.Mercury.Diagnostics;

/// <summary>
/// HdrHistogram-mini, BCL-only. Bucketed reservoir with logarithmic bucket layout and
/// linear sub-bucket subdivision; computes p50/p95/p99/p999 with bounded relative error.
/// Atomic <see cref="Interlocked"/> increment for cross-thread emission. ADR-035 Decision 2.
/// </summary>
/// <remarks>
/// <para>
/// Layout: bucket 0 is linear over <c>[0, subBucketCount)</c>. Bucket k (k ≥ 1) covers
/// <c>[2^(k+subBucketBits-1), 2^(k+subBucketBits))</c> with <c>subBucketCount</c> equal-width
/// sub-buckets. Total slots = <c>bucketCount × subBucketCount</c>.
/// </para>
/// <para>
/// Default <c>subBucketBits = 7</c> gives 128 sub-buckets per bucket — 0.78% maximum bucket-aliasing
/// error, well below the documented 1% bound at p99. Memory cost: ~32 KB for the default
/// <c>highestTrackable = 1 hour in microseconds</c>.
/// </para>
/// <para>
/// Single-instance usage: create once per metric (per-thread is wasteful since increment is
/// already atomic). Reset via <see cref="Reset"/> for periodic re-emission windows; note
/// reset is not atomic with concurrent <see cref="Record"/> calls.
/// </para>
/// </remarks>
public sealed class LatencyHistogram
{
    private readonly long[] _counts;
    private readonly int _subBucketBits;
    private readonly int _subBucketCount;
    private readonly long _highestTrackable;
    private long _totalCount;

    public LatencyHistogram(long highestTrackable = 3_600_000_000L, int subBucketBits = 7)
    {
        if (highestTrackable <= 0)
            throw new ArgumentOutOfRangeException(nameof(highestTrackable));
        if (subBucketBits < 1 || subBucketBits > 14)
            throw new ArgumentOutOfRangeException(nameof(subBucketBits));

        _subBucketBits = subBucketBits;
        _subBucketCount = 1 << subBucketBits;
        _highestTrackable = highestTrackable;

        int highBit = highestTrackable < _subBucketCount
            ? _subBucketBits
            : 64 - BitOperations.LeadingZeroCount((ulong)highestTrackable);
        int bucketCount = Math.Max(1, highBit - _subBucketBits + 2);
        _counts = new long[bucketCount * _subBucketCount];
    }

    public long HighestTrackableValue => _highestTrackable;

    public long Count => Interlocked.Read(ref _totalCount);

    public void Record(long value)
    {
        if (value < 0) value = 0;
        if (value > _highestTrackable) value = _highestTrackable;
        int idx = IndexFor(value);
        Interlocked.Increment(ref _counts[idx]);
        Interlocked.Increment(ref _totalCount);
    }

    public long Percentile(double percentile)
    {
        if (percentile < 0 || percentile > 100)
            throw new ArgumentOutOfRangeException(nameof(percentile));

        long total = Interlocked.Read(ref _totalCount);
        if (total == 0) return 0;

        long target = Math.Max(1, (long)Math.Ceiling(total * percentile / 100.0));
        long acc = 0;
        for (int i = 0; i < _counts.Length; i++)
        {
            acc += Interlocked.Read(ref _counts[i]);
            if (acc >= target)
                return ValueAt(i);
        }
        return _highestTrackable;
    }

    public void Reset()
    {
        for (int i = 0; i < _counts.Length; i++)
            Interlocked.Exchange(ref _counts[i], 0);
        Interlocked.Exchange(ref _totalCount, 0);
    }

    private int IndexFor(long value)
    {
        if (value < _subBucketCount)
            return (int)value;

        int k = 63 - BitOperations.LeadingZeroCount((ulong)value);
        int shift = k - _subBucketBits;
        return (k - _subBucketBits) * _subBucketCount + (int)(value >> shift);
    }

    private long ValueAt(int idx)
    {
        if (idx < _subBucketCount)
            return idx;

        int relativeIdx = idx - _subBucketCount;
        int bucketOffset = relativeIdx / _subBucketCount;
        int subIdx = relativeIdx % _subBucketCount;
        long rangeBase = (long)_subBucketCount << bucketOffset;
        long subSize = 1L << bucketOffset;
        return rangeBase + subIdx * subSize + (subSize >> 1);
    }
}
