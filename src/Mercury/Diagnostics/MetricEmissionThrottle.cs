using System;
using System.Diagnostics;
using System.Threading;

namespace SkyOmega.Mercury.Diagnostics;

/// <summary>
/// ADR-043 Part 2 — time-based emission throttle for high-bandwidth progress events.
///
/// Switches metric emission from "every N records" (workload-rate dependent) to
/// "at least <paramref name="minInterval"/> apart" (workload-rate independent). At a 5s
/// default + ~300 bytes/event, the metric channel is ~60 bytes/sec — three orders of
/// magnitude below any plausible SSD I/O queue contention with the workload, regardless
/// of workload throughput.
///
/// The records-based throttle in the calling code (e.g. <c>MergeProgressEmissionInterval =
/// 100M records</c>) becomes a *floor*: emit at least every N records OR every T seconds,
/// whichever fires first. At high throughput the time-based gate wins (bounded bytes/sec);
/// at low throughput the records-based gate wins (so a slow phase still emits eventually).
/// </summary>
/// <remarks>
/// <para>Thread-safe via <see cref="Interlocked.CompareExchange(ref long, long, long)"/> on the
/// last-emit timestamp. Multiple producer threads racing through <see cref="ShouldEmit"/>
/// resolve to exactly one true return per interval.</para>
///
/// <para>Uses <see cref="Stopwatch.GetTimestamp"/> rather than <see cref="DateTime.UtcNow"/> —
/// monotonic, no wall-clock skew, no NTP-adjustment surprises during long runs.</para>
/// </remarks>
internal sealed class MetricEmissionThrottle
{
    private readonly long _minIntervalTicks;
    private long _lastEmitTicks;

    public MetricEmissionThrottle(TimeSpan minInterval)
    {
        if (minInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minInterval), "Interval must be non-negative.");
        // Convert TimeSpan ticks (100ns units) to Stopwatch ticks (variable per-platform).
        _minIntervalTicks = (long)(minInterval.TotalSeconds * Stopwatch.Frequency);
        // Initialize to "minInterval ago" so the first ShouldEmit() always returns true.
        // Avoids long.MinValue underflow in the (now - last) subtraction below.
        _lastEmitTicks = Stopwatch.GetTimestamp() - _minIntervalTicks - 1;
    }

    /// <summary>
    /// Returns <c>true</c> at most once per <c>minInterval</c>; the caller emits its event when
    /// <c>true</c> is returned. Returns <c>false</c> if the previous emit was within the interval.
    /// Atomic across threads — exactly one true per interval, even under concurrent calls.
    /// </summary>
    public bool ShouldEmit()
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastEmitTicks);
        if (now - last < _minIntervalTicks)
            return false;
        // CompareExchange resolves the race: only the thread that successfully writes the new
        // timestamp returns true. Other concurrent threads see the new value and return false.
        return Interlocked.CompareExchange(ref _lastEmitTicks, now, last) == last;
    }

    /// <summary>
    /// Force the next <see cref="ShouldEmit"/> call to return true regardless of interval.
    /// Useful for end-of-phase emissions that must not be suppressed.
    /// </summary>
    public void Reset()
    {
        // Same "interval ago" initialization as the constructor — avoids the long.MinValue
        // underflow path the subtraction in ShouldEmit would otherwise take.
        Interlocked.Exchange(ref _lastEmitTicks, Stopwatch.GetTimestamp() - _minIntervalTicks - 1);
    }
}
