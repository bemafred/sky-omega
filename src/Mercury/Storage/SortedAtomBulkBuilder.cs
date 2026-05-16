using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// Two-pass bulk builder for a Reference-profile <see cref="SortedAtomStore"/>. ADR-034
/// Phase 1B-5a / 5d / 5e. Accepts a stream of (G, S, P, O) UTF-8 byte spans, defers atom-ID
/// assignment until the full vocabulary is sorted, then yields per-triple resolved IDs
/// in input order so the caller can build <c>ReferenceKey</c> entries and feed them to
/// the GSPO external sorter.
/// </summary>
/// <remarks>
/// <para>
/// <b>The two-pass design.</b> The parser-side flow can't synthesize atom IDs synchronously
/// because <see cref="SortedAtomStore"/> assigns dense IDs in alphabetical order — the ID
/// for a string is unknowable until the entire vocabulary is sorted.
/// </para>
/// <para>
/// <b>Phase 1B-5e streaming-input.</b> Earlier phases buffered every atom occurrence in a
/// <c>List&lt;byte[]&gt;</c> before invoking the merge-sort. At 100 M Reference triples this is
/// ~32 GB heap (1 M × 4 atoms × ~80 bytes). At 1 B it would be ~320 GB — exceeding any
/// practical host. Phase 1B-5e replaces the buffer with an incremental chunk spill: every
/// <see cref="AddTriple"/> appends to a bounded in-memory buffer (default 256 MB) which
/// is sorted and spilled to a chunk file when full. <see cref="Finalize"/> flushes the
/// trailing partial chunk and runs the existing
/// <see cref="SortedAtomStoreExternalBuilder.MergeAndWrite"/> over the accumulated chunks.
/// Memory ceiling becomes the chunk-buffer size, independent of input scale.
/// </para>
/// <para>
/// <b>Phase 1B-5d disk-backed AssignedIds.</b> When <c>useDiskBackedAssigned: true</c>, the
/// per-input-index → atom-id mapping is streamed through an
/// <see cref="ExternalSorter{ResolveRecord, ResolveRecordChunkSorter}"/> instead of being
/// materialized as a <c>long[]</c>. Required for any input scale where the input occurrence
/// count × 8 bytes exceeds host RAM (~32 GB at 1 B occurrences, ~681 GB at full Wikidata).
/// </para>
/// <para>
/// <b>Lifecycle.</b> The builder owns its temp directory (which holds the chunk files and,
/// when disk-backed, the resolver's chunks). <see cref="Dispose"/> cleans both up. The
/// output files (<c>{base}.atoms</c>, <c>{base}.offsets</c>) are durable on
/// <see cref="Finalize"/> completion; the caller then constructs a
/// <see cref="SortedAtomStore"/> over them.
/// </para>
/// </remarks>
internal sealed class SortedAtomBulkBuilder : IDisposable
{
    /// <summary>
    /// Default in-memory chunk buffer threshold. Single source of truth — references
    /// <see cref="SortedAtomStoreExternalBuilder.DefaultChunkSizeBytes"/> so the
    /// production bulk-load path (this class) and the one-shot external builder
    /// share one chunk-size policy. A duplicate constant here previously masked a
    /// 1 GB tuning change to the canonical default at the production path.
    /// </summary>
    public const long DefaultChunkBufferBytes = SortedAtomStoreExternalBuilder.DefaultChunkSizeBytes;

    private readonly string _baseAtomPath;
    private readonly string _tempDir;
    private readonly bool _ownsTempDir;
    private readonly bool _useDiskBackedAssigned;
    private readonly long _chunkBufferBytes;
    private readonly int _resolveSorterChunkSize;

    // ADR-034 Phase 1B-5e streaming-input state. The buffer accumulates atom occurrences
    // until it crosses the chunk threshold, then sorts + spills to a chunk file. Memory
    // is bounded by chunkBufferBytes regardless of input scale.
    private List<(byte[] Bytes, long InputIdx)> _spillBuffer = new();
    private long _spillBufferBytes;
    private readonly List<string> _chunkFiles = new();
    private long _globalIdx;  // monotonic input-occurrence index across all atoms; widened from int to long for >500M-triple bulk loads (1B triples × 4 atoms = 4B occurrences exceeds int32)
    private long _tripleCount;

    // ADR-037 pipelined-spill state. The handoff queue is bounded at 1 — exactly enough
    // for one snapshot-in-flight to overlap with the next buffer fill. Worker exclusively
    // owns _workerChunkFiles; parser merges into _chunkFiles after Wait() at Finalize.
    // _workerException captures any sort/write/listener fault on the worker thread; the
    // parser checks it on every AddOneAtomOccurrence so faults surface synchronously
    // rather than being silently lost.
    private const int SpillQueueBoundedCapacity = 1;
    private BlockingCollection<SpillJob>? _spillQueue;
    private Task? _workerTask;
    private readonly List<string> _workerChunkFiles = new();
    private volatile Exception? _workerException;
    // Worker signals fault by cancelling this CTS. Parser's BlockingCollection.Add
    // is called with the token so it wakes up promptly rather than hanging on a
    // never-to-drain queue. CompleteAdding from the worker is unsafe (racy with
    // a concurrent Add) — cancellation is the documented BCL pattern for this case.
    private CancellationTokenSource? _faultCts;
    private TimeSpan _parserBlockedDuration;
    private readonly Stopwatch _parserWallClock = new();

    // Disk-backed AssignedIds resolver (Phase 1B-5d). Allocated lazily in EnsureSpillerInitialized
    // when useDiskBackedAssigned is true. Receives empty-slot sentinels directly from
    // AddTriple; non-empty atoms get their resolution records emitted by MergeAndWrite.
    private ExternalSorter<ResolveRecord, ResolveRecordChunkSorter>? _resolveSorter;
    private string _occurrenceTempDir = string.Empty;  // resolved on first AddTriple
    private bool _spillerInitialized;

    private SortedAtomStoreBuilder.BuildResult? _buildResult;
    private bool _finalized;
    private bool _disposed;

    /// <summary>Observability listener for spill events and merge progress. Null = no emission.</summary>
    private readonly Abstractions.IObservabilityListener? _listener;

    public SortedAtomBulkBuilder(
        string baseAtomPath,
        string? tempDir = null,
        bool useDiskBackedAssigned = false,
        long chunkBufferBytes = DefaultChunkBufferBytes,
        int resolveSorterChunkSize = SortedAtomStoreExternalBuilder.DefaultResolveSorterChunkSize,
        Abstractions.IObservabilityListener? listener = null)
    {
        if (baseAtomPath is null) throw new ArgumentNullException(nameof(baseAtomPath));
        if (chunkBufferBytes < 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(chunkBufferBytes), "chunkBufferBytes must be at least 1 MB");
        _baseAtomPath = baseAtomPath;
        _useDiskBackedAssigned = useDiskBackedAssigned;
        _chunkBufferBytes = chunkBufferBytes;
        _resolveSorterChunkSize = resolveSorterChunkSize;
        _listener = listener;
        if (tempDir is null)
        {
            // ADR-041: default tempDir includes "bulk-tmp" as a path segment so
            // the MergeAndWrite cleanup assertion (chunkFiles must live under
            // bulk-tmp/) passes without callers having to wire the convention.
            _tempDir = Path.Combine(Path.GetTempPath(), "bulk-tmp", "sorted-atom-bulk-" + Guid.NewGuid().ToString("N"));
            _ownsTempDir = true;
        }
        else
        {
            _tempDir = tempDir;
            _ownsTempDir = false;
        }
        Directory.CreateDirectory(_tempDir);
    }

    public long TripleCount => _tripleCount;

    /// <summary>
    /// Add a triple to the build queue. Bytes are streamed into the chunk-spill buffer;
    /// when the buffer crosses the threshold, it is sorted in memory and spilled to disk.
    /// Empty <paramref name="graph"/> is recorded as the "no atom" sentinel (atom ID 0)
    /// in the resolver — no chunk record emitted, but the input-index slot is still
    /// covered for dense resolver coverage.
    /// </summary>
    public void AddTriple(
        ReadOnlySpan<byte> graph,
        ReadOnlySpan<byte> subject,
        ReadOnlySpan<byte> predicate,
        ReadOnlySpan<byte> @object)
    {
        ThrowIfDisposed();
        ThrowIfFinalized();
        EnsureSpillerInitialized();

        AddOneAtomOccurrence(graph);
        AddOneAtomOccurrence(subject);
        AddOneAtomOccurrence(predicate);
        AddOneAtomOccurrence(@object);
        _tripleCount++;
    }

    /// <summary>UTF-8 convenience overload for char-based callers.</summary>
    public void AddTriple(
        ReadOnlySpan<char> graph,
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> @object)
    {
        // Encode each span once. Stack-buffer for typical sizes; heap-fallback for outliers.
        Span<byte> stack = stackalloc byte[2048];
        AddTriple(EncodeUtf8(graph, stack), EncodeUtf8(subject, stack), EncodeUtf8(predicate, stack), EncodeUtf8(@object, stack));
    }

    private void AddOneAtomOccurrence(ReadOnlySpan<byte> bytes)
    {
        // ADR-037 Decision 4: surface worker faults synchronously on the parser thread.
        // Check on every occurrence — a fault between AddTriple calls must propagate
        // to the next caller, not be silently lost until Finalize.
        ThrowIfWorkerFaulted();

        if (bytes.IsEmpty)
        {
            // Empty-slot sentinel. Disk-backed: emit (globalIdx, 0) to the resolver so it
            // has dense coverage. In-memory: nothing to do — the caller's assigned[] is
            // initialized to zero, so the sentinel reads back as 0 naturally.
            _resolveSorter?.Add(new ResolveRecord(_globalIdx, 0));
            _globalIdx++;
            return;
        }

        // Heap-allocate one byte[] per occurrence. The byte[] becomes a chunk record on
        // spill. Per-atom alloc cost is unchanged from Phase 1B-5a; what's saved is the
        // List<byte[]> retention beyond the chunk threshold.
        var copy = bytes.ToArray();
        _spillBuffer.Add((copy, _globalIdx));
        _spillBufferBytes += copy.Length + 16;  // approx record overhead
        _globalIdx++;

        if (_spillBufferBytes >= _chunkBufferBytes)
            FlushSpillBuffer();
    }

    /// <summary>
    /// ADR-037 Decision 2 + 4: snapshot-and-swap buffer ownership transfer + parser-blocked
    /// timing. The parser captures the current buffer, replaces <c>_spillBuffer</c> with a
    /// fresh List, and hands the snapshot to the worker via <c>BlockingCollection.Add</c>.
    /// After <c>Add</c>, the parser never touches the snapshot — the worker has exclusive
    /// ownership. <c>Add</c> blocks if the queue is at <see cref="SpillQueueBoundedCapacity"/>;
    /// the wall-time spent blocked is the load-bearing measurement.
    /// </summary>
    private void FlushSpillBuffer()
    {
        if (_spillBuffer.Count == 0) return;

        // Snapshot + swap. Parser never touches `snapshot` after this line; worker has
        // exclusive ownership starting at the Add below.
        var snapshot = _spillBuffer;
        _spillBuffer = new List<(byte[] Bytes, long InputIdx)>();
        _spillBufferBytes = 0;

        int chunkIndex = _nextChunkIndex++;
        int queueDepth = _spillQueue!.Count;
        var blockStart = Stopwatch.GetTimestamp();
        try
        {
            _spillQueue.Add(new SpillJob(snapshot, chunkIndex, queueDepth), _faultCts!.Token);
        }
        catch (OperationCanceledException)
        {
            // Worker faulted and cancelled the token to wake us up; surface the
            // original exception via ThrowIfWorkerFaulted (which has the captured
            // _workerException as its source of truth).
            ThrowIfWorkerFaulted();
            throw;  // unreachable if _workerException is set; safety net
        }
        _parserBlockedDuration += Stopwatch.GetElapsedTime(blockStart);
    }

    private int _nextChunkIndex;

    /// <summary>
    /// Worker thread loop. Drains <see cref="_spillQueue"/>, runs sort + write per snapshot,
    /// emits <see cref="Abstractions.SpillEvent"/>, and accumulates output paths in
    /// <see cref="_workerChunkFiles"/>. On any fault, captures the exception in
    /// <see cref="_workerException"/> and signals <c>CompleteAdding</c> so subsequent
    /// parser <c>Add</c> calls throw <see cref="InvalidOperationException"/>; the parser's
    /// <see cref="ThrowIfWorkerFaulted"/> rethrows the original.
    /// </summary>
    private void RunSpillWorker()
    {
        try
        {
            foreach (var job in _spillQueue!.GetConsumingEnumerable())
            {
                int recordCount = job.Buffer.Count;
                var result = SortedAtomStoreExternalBuilder.SpillOneChunk(
                    _occurrenceTempDir, job.Buffer, job.ChunkIndex);
                _workerChunkFiles.Add(result.Path);

                if (_listener is not null)
                {
                    _listener.OnSpill(new Abstractions.SpillEvent(
                        Timestamp: DateTimeOffset.UtcNow,
                        ChunkIndex: job.ChunkIndex,
                        RecordCount: recordCount,
                        BytesWritten: result.BytesWritten,
                        SortDuration: result.SortDuration,
                        WriteDuration: result.WriteDuration,
                        QueueDepthAtHandoff: job.QueueDepthAtHandoff));
                }
            }
        }
        catch (Exception ex)
        {
            _workerException = ex;
            // Wake any parser thread blocked in Add. We must NOT call CompleteAdding
            // from the worker — that races with concurrent Add and throws
            // InvalidOperationException ("CompleteAdding may not be used concurrently
            // with additions to the collection"). Cancellation is the BCL-supported
            // pattern: parser's Add(item, token) throws OperationCanceledException,
            // gets caught, ThrowIfWorkerFaulted then rethrows the original.
            try { _faultCts!.Cancel(); } catch { /* already disposed */ }
        }
    }

    private void ThrowIfWorkerFaulted()
    {
        var ex = _workerException;
        if (ex is not null)
            throw new InvalidOperationException(
                "Spill worker faulted; bulk-load aborted. See InnerException.", ex);
    }

    private void EnsureSpillerInitialized()
    {
        if (_spillerInitialized) return;
        _spillerInitialized = true;

        _occurrenceTempDir = Path.Combine(_tempDir, "occurrences");
        Directory.CreateDirectory(_occurrenceTempDir);

        if (_useDiskBackedAssigned)
        {
            var resolverTempDir = Path.Combine(_tempDir, "assigned-ids-resolver");
            _resolveSorter = new ExternalSorter<ResolveRecord, ResolveRecordChunkSorter>(
                resolverTempDir, _resolveSorterChunkSize);
        }

        // ADR-037: spill queue + worker thread come up here so they exist before any
        // FlushSpillBuffer call. Bound = 1 → 2 buffers alive at peak (one in parser's
        // hand, one in queue / being processed by worker).
        _spillQueue = new BlockingCollection<SpillJob>(boundedCapacity: SpillQueueBoundedCapacity);
        _faultCts = new CancellationTokenSource();
        _workerTask = Task.Factory.StartNew(
            RunSpillWorker,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        _parserWallClock.Start();
    }

    /// <summary>
    /// ADR-037: a single buffer-handoff record passed from parser to worker. The
    /// <paramref name="Buffer"/> is the parser's pre-fill snapshot, owned by the
    /// worker after enqueue. <paramref name="QueueDepthAtHandoff"/> is captured by
    /// the parser immediately before <c>Add</c> for the per-spill metric.
    /// </summary>
    private readonly record struct SpillJob(
        List<(byte[] Bytes, long InputIdx)> Buffer,
        int ChunkIndex,
        int QueueDepthAtHandoff);

    private static byte[] EncodeUtf8(ReadOnlySpan<char> chars, Span<byte> _)
    {
        if (chars.IsEmpty) return Array.Empty<byte>();
        int n = Encoding.UTF8.GetByteCount(chars);
        var buf = new byte[n];
        Encoding.UTF8.GetBytes(chars, buf);
        return buf;
    }

    /// <summary>
    /// Flush the trailing chunk buffer, run the merge over accumulated chunks, write the
    /// SortedAtomStore files. Subsequent calls to <see cref="EnumerateResolved"/> stream
    /// the per-triple resolved atom IDs in input order.
    /// </summary>
    public SortedAtomStoreBuilder.BuildResult Finalize()
    {
        ThrowIfDisposed();
        if (_finalized) return _buildResult!;

        EnsureSpillerInitialized();

        // ADR-037: surface a worker fault before pushing the trailing buffer through
        // the same handoff path. Without this check, FlushSpillBuffer's Add could hang
        // on a queue whose worker has faulted and not yet completed.
        ThrowIfWorkerFaulted();
        FlushSpillBuffer();

        // ADR-037 Decision 3: signal end-of-input, wait for worker drain, surface any
        // fault that happened during the final spills, then merge worker's chunk paths
        // into the parser-owned _chunkFiles list (FIFO order preserved by the queue).
        _spillQueue!.CompleteAdding();
        try { _workerTask!.Wait(); }
        catch (AggregateException) { /* RunSpillWorker swallows; _workerException is the source of truth */ }
        ThrowIfWorkerFaulted();
        _chunkFiles.AddRange(_workerChunkFiles);

        _parserWallClock.Stop();

        if (_listener is not null)
        {
            _listener.OnBulkBuilderCompleted(new Abstractions.BulkBuilderCompletedEvent(
                Timestamp: DateTimeOffset.UtcNow,
                TripleCount: _tripleCount,
                AtomOccurrenceCount: _globalIdx,
                SpillCount: _chunkFiles.Count,
                ParserBlockedOnSpill: _parserBlockedDuration,
                TotalParserWallClock: _parserWallClock.Elapsed));
        }

        // MergeAndWrite consumes the chunks, dedupes, assigns dense atom IDs, writes
        // .atoms + .offsets, and (when resolveSorter is non-null) emits resolution
        // records for each non-empty atom occurrence. The empty-slot sentinels were
        // already emitted by AddOneAtomOccurrence.
        _buildResult = SortedAtomStoreExternalBuilder.MergeAndWrite(
            _baseAtomPath, _chunkFiles, _globalIdx, _resolveSorter, _listener);

        // Wrap in DiskBackedAssignedIds if disk-backed. MergeAndWrite already calls
        // Complete() and wraps in BuildResult.AssignedIdsResolver when resolveSorter
        // is non-null. We don't double-wrap here; just return the result.
        _finalized = true;
        return _buildResult;
    }

    /// <summary>
    /// Stream resolved per-triple atom IDs in original input order. <see cref="Finalize"/>
    /// must have been called. Each yielded tuple corresponds to one <see cref="AddTriple"/>
    /// call: <c>(GraphId, SubjectId, PredicateId, ObjectId)</c>. Empty atom occurrences
    /// (e.g. the default graph) yield 0 — the sentinel.
    /// </summary>
    public IEnumerable<(long GraphId, long SubjectId, long PredicateId, long ObjectId)> EnumerateResolved()
    {
        ThrowIfDisposed();
        if (!_finalized)
            throw new InvalidOperationException("EnumerateResolved called before Finalize.");

        if (_buildResult!.AssignedIdsResolver is { } resolver)
        {
            using var reader = resolver.GetReader();
            for (long t = 0; t < _tripleCount; t++)
            {
                if (!reader.TryReadNext(out var g) ||
                    !reader.TryReadNext(out var s) ||
                    !reader.TryReadNext(out var p) ||
                    !reader.TryReadNext(out var o))
                {
                    throw new InvalidOperationException(
                        $"AssignedIdsResolver drained early at triple {t}/{_tripleCount}.");
                }
                yield return (g, s, p, o);
            }
        }
        else
        {
            var ids = _buildResult!.AssignedIds;
            for (long t = 0; t < _tripleCount; t++)
            {
                int baseIdx = checked((int)(t * 4));
                yield return (ids[baseIdx], ids[baseIdx + 1], ids[baseIdx + 2], ids[baseIdx + 3]);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // ADR-037 Decision 5: shut the worker down cleanly when Dispose runs without
        // a preceding Finalize (cancellation or error path). CompleteAdding lets the
        // worker drain any in-flight job and exit; the 30 s wait is a safety bound for
        // the largest plausible chunk write — well above measured spill durations.
        if (!_finalized && _spillQueue is not null)
        {
            // CompleteAdding here is safe — Dispose runs on the parser/owner thread,
            // never concurrently with Add (the caller has stopped using the builder).
            try { _spillQueue.CompleteAdding(); } catch { /* already completed */ }
            try { _workerTask?.Wait(TimeSpan.FromSeconds(30)); }
            catch { /* worker fault or timeout — nothing actionable in Dispose */ }
        }
        _spillQueue?.Dispose();
        _faultCts?.Dispose();

        // Disk-backed AssignedIds owns an ExternalSorter with its own tempDir under _tempDir;
        // dispose it first so its tempDir is cleaned before _tempDir's recursive delete.
        _buildResult?.AssignedIdsResolver?.Dispose();

        if (_ownsTempDir && Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SortedAtomBulkBuilder));
    }

    private void ThrowIfFinalized()
    {
        if (_finalized) throw new InvalidOperationException("Builder is finalized; AddTriple is no longer accepted.");
    }
}
