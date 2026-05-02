using System;
using System.Collections.Generic;
using System.IO;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// Bounded LRU cache of read-only <see cref="FileStream"/> handles. Used by the k-way
/// merge in <see cref="SortedAtomStoreExternalBuilder.MergeAndWrite"/> to bound the
/// number of simultaneously-open file descriptors during merges over many thousands
/// of chunk files (10K+ chunks at full Wikidata scale exceed macOS default ulimit).
/// </summary>
/// <remarks>
/// <para>
/// <b>Design.</b> Caller holds <c>(path, offset)</c> state per logical chunk reader.
/// On each read, calls <see cref="Get"/> to acquire a <see cref="FileStream"/> for the
/// path. Cache hit: returns the existing stream (position must be valid for caller's
/// offset). Cache miss: opens fresh stream; evicts LRU entry if pool is full. Caller
/// is responsible for seeking to its saved offset on cache miss (compare returned
/// stream's <see cref="FileStream.Position"/> against expected).
/// </para>
/// <para>
/// <b>Single-threaded contract.</b> The k-way merge in MergeAndWrite is single-threaded
/// (one priority-queue dequeue → one MoveNext → one Get call → one read sequence at
/// a time). Pool is NOT thread-safe; callers must serialize Get calls. This matches
/// the merge's actual execution model.
/// </para>
/// <para>
/// <b>I/O cost.</b> Cache hit: zero overhead vs holding the stream open directly.
/// Cache miss: one open() syscall + one seek() if position must be restored. macOS
/// open() ≈ 10μs, seek() ≈ 1μs. Total miss cost ≈ 12μs per miss. With pool size 64
/// and 13K chunks, the merge's locality (consecutive priority-queue pops often hit
/// the same chunk for similar-prefix atoms) keeps miss rate well below 100%.
/// </para>
/// </remarks>
internal sealed class BoundedFileStreamPool : IDisposable
{
    private readonly int _maxOpen;
    private readonly int _bufferSize;
    private readonly LinkedList<PoolEntry> _lru = new();
    private readonly Dictionary<string, LinkedListNode<PoolEntry>> _byPath = new();
    private long _hits;
    private long _misses;
    private int _peakOpenCount;
    private bool _disposed;

    public BoundedFileStreamPool(int maxOpen, int bufferSize = 64 * 1024)
    {
        if (maxOpen < 1) throw new ArgumentOutOfRangeException(nameof(maxOpen), "maxOpen must be >= 1");
        _maxOpen = maxOpen;
        _bufferSize = bufferSize;
    }

    /// <summary>Number of cache hits (cumulative). Diagnostic.</summary>
    public long Hits => _hits;

    /// <summary>Number of cache misses (cumulative — opens or re-opens). Diagnostic.</summary>
    public long Misses => _misses;

    /// <summary>Current number of open streams in the pool (≤ MaxOpen).</summary>
    public int OpenCount => _lru.Count;

    /// <summary>Maximum <see cref="OpenCount"/> observed during the pool's lifetime. Diagnostic.</summary>
    public int PeakOpenCount => _peakOpenCount;

    /// <summary>Configured maximum simultaneously-open streams.</summary>
    public int MaxOpen => _maxOpen;

    /// <summary>
    /// Acquire a read-only FileStream for the given path. Returns a stream owned by the
    /// pool — caller must NOT dispose it. The returned stream may have any position;
    /// the caller is responsible for seeking to its expected read offset before reading.
    /// </summary>
    public FileStream Get(string path)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BoundedFileStreamPool));

        if (_byPath.TryGetValue(path, out var node))
        {
            // Cache hit — bump to front of LRU.
            _lru.Remove(node);
            _lru.AddFirst(node);
            _hits++;
            return node.Value.Stream;
        }

        // Cache miss — evict LRU if at capacity, then open fresh.
        if (_lru.Count >= _maxOpen)
        {
            var lru = _lru.Last!;
            _lru.RemoveLast();
            _byPath.Remove(lru.Value.Path);
            lru.Value.Stream.Dispose();
        }

        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            _bufferSize, FileOptions.SequentialScan);
        var entry = new PoolEntry(path, fs);
        var newNode = new LinkedListNode<PoolEntry>(entry);
        _lru.AddFirst(newNode);
        _byPath[path] = newNode;
        if (_lru.Count > _peakOpenCount) _peakOpenCount = _lru.Count;
        _misses++;
        return fs;
    }

    /// <summary>
    /// Explicitly drop the stream for a path (e.g., when the caller is done with the
    /// chunk and wants to free the FD). Idempotent.
    /// </summary>
    public void Drop(string path)
    {
        if (_disposed) return;
        if (_byPath.TryGetValue(path, out var node))
        {
            _lru.Remove(node);
            _byPath.Remove(path);
            node.Value.Stream.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var node in _lru)
        {
            try { node.Stream.Dispose(); } catch { }
        }
        _lru.Clear();
        _byPath.Clear();
    }

    private sealed record PoolEntry(string Path, FileStream Stream);
}
