using System;
using System.Diagnostics;
using System.IO;
using SkyOmega.Mercury.Abstractions;

namespace SkyOmega.Mercury.Diagnostics;

/// <summary>
/// Process-level state producers for the periodic <see cref="JsonlMetricsListener"/> timer
/// (ADR-035 Phase 7a.2 — Category G). Each factory returns a closure that captures its own
/// previous-tick state and emits one record per tick when relevant.
/// </summary>
/// <remarks>
/// <para>
/// All producers are sampling-based: the timer fires every N seconds and the producer
/// reads current process state. Multi-GC bursts between ticks are coalesced — only the
/// most recent <see cref="GCMemoryInfo"/> is observed. If precise per-event GC tracking
/// becomes load-bearing, switch to <c>GC.RegisterForFullGCNotification</c>; the polling
/// approach is sufficient for the operational visibility this category targets.
/// </para>
/// <para>
/// Producer invocation is on the timer thread. Heavy work (file I/O for disk-free,
/// process snapshot for RSS) runs there; producers should remain bounded so a slow
/// producer doesn't delay subsequent ticks.
/// </para>
/// </remarks>
public static class ProcessStateProducers
{
    /// <summary>
    /// GC sampler: emits <see cref="GcEvent"/> when <see cref="GCMemoryInfo.Index"/>
    /// advances since the previous tick. Reports the latest GC's generation, pause duration
    /// (first reported pause), and post-collection heap size.
    /// </summary>
    public static JsonlMetricsListener.StateProducer Gc()
    {
        long lastIndex = -1;
        return listener =>
        {
            var info = GC.GetGCMemoryInfo();
            if (info.Index <= lastIndex) return;

            var pauses = info.PauseDurations;
            var pauseDuration = pauses.Length > 0 ? pauses[0] : TimeSpan.Zero;
            listener.OnGcEvent(new GcEvent(
                Timestamp: DateTimeOffset.UtcNow,
                Generation: info.Generation,
                PauseDuration: pauseDuration,
                HeapSizeAfterBytes: info.HeapSizeBytes));
            lastIndex = info.Index;
        };
    }

    /// <summary>
    /// LOH-and-total-allocation sampler: emits <see cref="LohDeltaEvent"/> with the delta
    /// since the previous tick. Uses <see cref="GC.GetTotalAllocatedBytes(bool)"/> with
    /// <c>precise=true</c> for reliable cross-thread totals.
    /// </summary>
    public static JsonlMetricsListener.StateProducer Loh()
    {
        long lastTotal = GC.GetTotalAllocatedBytes(precise: true);
        return listener =>
        {
            long current = GC.GetTotalAllocatedBytes(precise: true);
            long delta = current - lastTotal;
            lastTotal = current;
            listener.OnLohDelta(new LohDeltaEvent(
                Timestamp: DateTimeOffset.UtcNow,
                DeltaBytes: delta,
                TotalAllocatedBytes: current));
        };
    }

    /// <summary>
    /// Resident-set sampler: emits <see cref="RssState"/> with current working set and
    /// private memory size from <see cref="Process.GetCurrentProcess"/>.
    /// </summary>
    public static JsonlMetricsListener.StateProducer Rss()
    {
        return listener =>
        {
            using var proc = Process.GetCurrentProcess();
            listener.OnRssState(new RssState(
                Timestamp: DateTimeOffset.UtcNow,
                WorkingSetBytes: proc.WorkingSet64,
                PrivateMemoryBytes: proc.PrivateMemorySize64));
        };
    }

    /// <summary>
    /// Disk-free sampler for the supplied path's drive. Best-effort: missing/inaccessible
    /// drives are silently skipped (no record emitted that tick).
    /// </summary>
    public static JsonlMetricsListener.StateProducer DiskFree(string path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        return listener =>
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root)) return;
                var drive = new DriveInfo(root);
                if (!drive.IsReady) return;
                listener.OnDiskFreeState(new DiskFreeState(
                    Timestamp: DateTimeOffset.UtcNow,
                    Path: path,
                    FreeBytes: drive.AvailableFreeSpace,
                    TotalBytes: drive.TotalSize));
            }
            catch
            {
                // Disk-free is best-effort — never fail an observability tick over it.
            }
        };
    }

    /// <summary>
    /// Register all Category G producers on the listener: Gc, Loh, Rss, and (when
    /// <paramref name="diskPath"/> is non-null) DiskFree(diskPath).
    /// </summary>
    public static void RegisterAll(JsonlMetricsListener listener, string? diskPath = null)
    {
        if (listener is null) throw new ArgumentNullException(nameof(listener));
        listener.RegisterStateProducer(Gc());
        listener.RegisterStateProducer(Loh());
        listener.RegisterStateProducer(Rss());
        if (diskPath is not null)
            listener.RegisterStateProducer(DiskFree(diskPath));
    }
}
