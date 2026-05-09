using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// ADR-038 Part 2: shared bounded worker pool servicing readahead refill requests
/// across all <see cref="ChunkReadAheadBuffer"/> instances during a merge.
/// </summary>
/// <remarks>
/// <para>
/// One dispatcher per <c>MergeAndWrite</c> invocation. Workers pull
/// <see cref="RefillRequest"/> entries from a shared FIFO queue, get the chunk's
/// FileStream from <see cref="BoundedFileStreamPool"/>, and call
/// <see cref="ChunkReadAheadBuffer.FillBack"/>. Sized at <c>min(8, ProcessorCount/2)</c>
/// by default — enough parallelism to keep all 3,923 chunk frontiers prefilled at
/// 21.3 B Wikidata scale without saturating CPU or SSD bandwidth.
/// </para>
/// <para>
/// <b>Lifecycle:</b> created at start of <c>MergeAndWrite</c>; <see cref="Dispose"/>
/// signals <c>CompleteAdding</c> + waits for all workers to drain. After dispose,
/// no further refill requests are honored.
/// </para>
/// <para>
/// <b>Concurrency contract:</b> the request channel is a <see cref="BlockingCollection{T}"/>
/// (FIFO). Producers (consumers of readahead — i.e. the merge loop's <c>Swap</c>
/// path on each <see cref="ChunkReadAheadBuffer"/>) enqueue refill requests. Workers
/// dequeue and process. Each <see cref="ChunkReadAheadBuffer"/> is filled by exactly one
/// worker at a time (enforced by the buffer's own semaphore — <c>_backEmpty</c>).
/// </para>
/// </remarks>
internal sealed class ChunkReadAheadDispatcher : IDisposable
{
    /// <summary>
    /// Default worker count: <c>min(8, ProcessorCount/2)</c>. Picks N high enough to
    /// keep frontier-buffer fills concurrent across many chunks without contending for
    /// the SSD bandwidth that the merge's own sequential output also needs.
    /// </summary>
    public static int DefaultWorkerCount => Math.Max(1, Math.Min(8, Environment.ProcessorCount / 2));

    private readonly BoundedFileStreamPool _pool;
    private readonly BlockingCollection<RefillRequest> _queue;
    private readonly Task[] _workers;
    private bool _disposed;

    public ChunkReadAheadDispatcher(BoundedFileStreamPool pool, int? workerCount = null)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        int n = workerCount ?? DefaultWorkerCount;
        if (n < 1) throw new ArgumentOutOfRangeException(nameof(workerCount), "workerCount must be >= 1");
        // Bounded capacity matches worker count × 2 — enough to absorb a burst of
        // simultaneous refill requests without unbounded memory growth.
        _queue = new BlockingCollection<RefillRequest>(boundedCapacity: n * 2);
        _workers = new Task[n];
        for (int i = 0; i < n; i++)
        {
            _workers[i] = Task.Factory.StartNew(
                RunWorker,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Enqueue a refill request. Producer (typically the consumer's <c>Swap</c> path on
    /// a <see cref="ChunkReadAheadBuffer"/>) calls this when the back buffer is empty.
    /// Blocks if the queue is at <c>boundedCapacity</c> — gives natural backpressure.
    /// </summary>
    public void RequestRefill(string path, ChunkReadAheadBuffer buffer)
    {
        if (_disposed) return;
        try { _queue.Add(new RefillRequest(path, buffer)); }
        catch (InvalidOperationException) { /* CompleteAdding called — refill is being shut down */ }
    }

    private void RunWorker()
    {
        try
        {
            foreach (var req in _queue.GetConsumingEnumerable())
            {
                var fs = _pool.Get(req.Path);
                req.Buffer.FillBack(fs);
            }
        }
        catch (Exception)
        {
            // Worker died unexpectedly. The bigger merge fails because at least one
            // ChunkReader will not get its buffer refilled and Read will throw on
            // _producerException — which surfaces upstream.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _queue.CompleteAdding(); } catch { }
        try { Task.WaitAll(_workers, TimeSpan.FromSeconds(30)); } catch { }
        _queue.Dispose();
    }

    /// <summary>One refill request: a chunk path + the buffer to fill.</summary>
    private readonly record struct RefillRequest(string Path, ChunkReadAheadBuffer Buffer);
}
