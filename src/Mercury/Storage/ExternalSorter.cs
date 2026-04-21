using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// Static-abstract dispatch hook so <see cref="ExternalSorter{T,TSorter}"/> can
/// invoke the correct radix-sort overload at JIT-specialized call sites with
/// no per-chunk delegate dispatch (ADR-032 Phase 2).
/// </summary>
internal interface IChunkSorter<T> where T : unmanaged
{
    static abstract void Sort(Span<T> data, Span<T> scratch);
}

internal sealed class ReferenceKeyChunkSorter : IChunkSorter<ReferenceQuadIndex.ReferenceKey>
{
    public static void Sort(Span<ReferenceQuadIndex.ReferenceKey> data, Span<ReferenceQuadIndex.ReferenceKey> scratch)
        => RadixSort.SortInPlace(data, scratch);
}

internal sealed class TrigramEntryChunkSorter : IChunkSorter<TrigramEntry>
{
    public static void Sort(Span<TrigramEntry> data, Span<TrigramEntry> scratch)
        => RadixSort.SortInPlace(data, scratch);
}

/// <summary>
/// External merge-sort over chunks too large for a single in-memory sort
/// (ADR-032 Phase 2). The producer streams entries via <see cref="Add"/>; once
/// the in-memory buffer fills, the chunk is sorted in place via the
/// <typeparamref name="TSorter"/>'s radix sort and spilled to a temp file. After
/// <see cref="Complete"/>, the consumer pulls sorted entries via
/// <see cref="TryDrainNext"/>, which performs a k-way merge across the spilled
/// chunks using a binary min-heap.
/// </summary>
/// <remarks>
/// <para><strong>Caller-owned buffers (ADR-032 Section 7).</strong> The
/// in-memory chunk buffer (<typeparamref name="T"/>[<paramref name="chunkSize"/>])
/// and its scratch counterpart are allocated once in the constructor and reused
/// across every chunk. Both live on the LOH for the sorter's lifetime; on
/// <see cref="Dispose"/> they become collectable.</para>
///
/// <para><strong>Temp file lifecycle.</strong> Chunk files live under
/// <c>{tempDir}/chunk-NNNNNN.bin</c> as raw blittable arrays of T (no header).
/// On <see cref="Dispose"/> the entire <paramref name="tempDir"/> is removed
/// recursively. If a prior session crashed leaving an orphan tempDir, the
/// constructor wipes it before starting (idempotent).</para>
///
/// <para><strong>Sort stability.</strong> The chunk-level sort (radix LSD) is
/// stable. The k-way merge is stable across chunks because chunk-N's entries
/// were Added strictly before chunk-(N+1)'s, so when two chunks have equal-
/// keyed entries at the heap top the lower-index reader breaks the tie.</para>
///
/// <para><strong>Memory budget at scale.</strong> 16M-entry chunks (the ADR
/// baseline) are 512 MB for ReferenceKey (32 B) or 192 MB for TrigramEntry
/// (12 B). The merge phase opens <c>chunkCount</c> readers, each with a
/// <paramref name="readBufferEntries"/> read-ahead buffer (default 8K entries =
/// 256 KB / 96 KB). For 100M ReferenceKey input → 7 chunks → ~1.8 MB of
/// merge-time read buffers. At 21.3 B → ~1330 chunks → ~340 MB; if that becomes
/// problematic, hierarchical merge (per ADR Section 2) is the fix.</para>
/// </remarks>
internal sealed unsafe class ExternalSorter<T, TSorter> : IDisposable
    where T : unmanaged, IComparable<T>
    where TSorter : IChunkSorter<T>
{
    private readonly string _tempDir;
    private readonly int _chunkSize;
    private readonly int _readBufferEntries;
    private readonly T[] _buffer;
    private readonly T[] _scratch;
    private int _bufferCount;
    private int _chunkIndex;
    private readonly List<string> _chunkPaths = new();
    private bool _completedAdd;
    private bool _disposed;

    // Merge-phase state, initialized lazily on first TryDrainNext call.
    private ChunkReader[]? _readers;
    private HeapEntry[]? _heap;
    private int _heapSize;

    public ExternalSorter(string tempDir, int chunkSize, int readBufferEntries = 8192)
    {
        if (string.IsNullOrEmpty(tempDir)) throw new ArgumentException("tempDir is required", nameof(tempDir));
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize), "chunkSize must be positive");
        if (readBufferEntries <= 0) throw new ArgumentOutOfRangeException(nameof(readBufferEntries));

        _tempDir = tempDir;
        _chunkSize = chunkSize;
        _readBufferEntries = readBufferEntries;
        _buffer = new T[chunkSize];
        _scratch = new T[chunkSize];

        // Idempotent: wipe any orphan tempDir from a prior crashed session.
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        Directory.CreateDirectory(_tempDir);
    }

    public int ChunkCount => _chunkPaths.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(in T entry)
    {
        if (_completedAdd) throw new InvalidOperationException("Add called after Complete");
        _buffer[_bufferCount++] = entry;
        if (_bufferCount == _chunkSize) FlushChunk();
    }

    public void Complete()
    {
        if (_completedAdd) return;
        if (_bufferCount > 0) FlushChunk();
        _completedAdd = true;
    }

    private void FlushChunk()
    {
        TSorter.Sort(_buffer.AsSpan(0, _bufferCount), _scratch.AsSpan(0, _bufferCount));
        var path = Path.Combine(_tempDir, $"chunk-{_chunkIndex++:D6}.bin");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024))
        {
            fs.Write(MemoryMarshal.AsBytes(_buffer.AsSpan(0, _bufferCount)));
        }
        _chunkPaths.Add(path);
        _bufferCount = 0;
    }

    /// <summary>
    /// Pull the next entry in sorted order. Returns false when all chunks are
    /// exhausted. Implicitly calls <see cref="Complete"/> on first invocation
    /// if the producer hasn't already.
    /// </summary>
    public bool TryDrainNext(out T entry)
    {
        if (!_completedAdd) Complete();

        if (_readers is null) InitMerge();

        if (_heapSize == 0)
        {
            entry = default;
            return false;
        }

        // Pop min, emit, advance the source reader, re-push if more available.
        var top = _heap![0];
        entry = top.Key;

        if (_readers![top.ReaderIdx].TryReadNext(out var next))
        {
            _heap[0] = new HeapEntry { Key = next, ReaderIdx = top.ReaderIdx };
            SiftDown(0);
        }
        else
        {
            _heap[0] = _heap[--_heapSize];
            if (_heapSize > 0) SiftDown(0);
        }
        return true;
    }

    private void InitMerge()
    {
        _readers = new ChunkReader[_chunkPaths.Count];
        _heap = new HeapEntry[Math.Max(1, _chunkPaths.Count)];

        for (int i = 0; i < _chunkPaths.Count; i++)
        {
            _readers[i] = new ChunkReader(_chunkPaths[i], _readBufferEntries);
            if (_readers[i].TryReadNext(out var first))
            {
                _heap[_heapSize] = new HeapEntry { Key = first, ReaderIdx = i };
                SiftUp(_heapSize);
                _heapSize++;
            }
        }
    }

    private void SiftUp(int idx)
    {
        var heap = _heap!;
        while (idx > 0)
        {
            int parent = (idx - 1) / 2;
            // Strict less-than: equal keys keep the lower-index reader on top
            // (preserves merge stability across chunks).
            if (heap[parent].Key.CompareTo(heap[idx].Key) < 0) break;
            if (heap[parent].Key.CompareTo(heap[idx].Key) == 0 && heap[parent].ReaderIdx <= heap[idx].ReaderIdx) break;
            (heap[parent], heap[idx]) = (heap[idx], heap[parent]);
            idx = parent;
        }
    }

    private void SiftDown(int idx)
    {
        var heap = _heap!;
        int n = _heapSize;
        while (true)
        {
            int left = 2 * idx + 1;
            int right = 2 * idx + 2;
            int min = idx;
            if (left < n && IsHeapLess(in heap[left], in heap[min])) min = left;
            if (right < n && IsHeapLess(in heap[right], in heap[min])) min = right;
            if (min == idx) break;
            (heap[min], heap[idx]) = (heap[idx], heap[min]);
            idx = min;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsHeapLess(in HeapEntry a, in HeapEntry b)
    {
        int cmp = a.Key.CompareTo(b.Key);
        if (cmp != 0) return cmp < 0;
        return a.ReaderIdx < b.ReaderIdx;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_readers != null)
        {
            foreach (var r in _readers) r?.Dispose();
            _readers = null;
        }

        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup. A leaked tempDir is recoverable on the next
            // ExternalSorter ctor against the same path (it wipes idempotently).
        }
    }

    private struct HeapEntry
    {
        public T Key;
        public int ReaderIdx;
    }

    private sealed class ChunkReader : IDisposable
    {
        private readonly FileStream _stream;
        private readonly T[] _buffer;
        private int _bufferPos;
        private int _bufferCount;

        public ChunkReader(string path, int bufferEntries)
        {
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, bufferSize: 64 * 1024, FileOptions.SequentialScan);
            _buffer = new T[bufferEntries];
        }

        public bool TryReadNext(out T entry)
        {
            if (_bufferPos >= _bufferCount && !RefillBuffer())
            {
                entry = default;
                return false;
            }
            entry = _buffer[_bufferPos++];
            return true;
        }

        private bool RefillBuffer()
        {
            var byteSpan = MemoryMarshal.AsBytes(_buffer.AsSpan());
            int totalBytes = 0;
            while (totalBytes < byteSpan.Length)
            {
                int n = _stream.Read(byteSpan.Slice(totalBytes));
                if (n == 0) break;
                totalBytes += n;
            }
            int entrySize = Unsafe.SizeOf<T>();
            _bufferCount = totalBytes / entrySize;
            _bufferPos = 0;
            return _bufferCount > 0;
        }

        public void Dispose() => _stream.Dispose();
    }
}
