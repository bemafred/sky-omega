using System;

namespace SkyOmega.Bcl.Collections;

/// <summary>
/// Append-and-index list that supports more than <see cref="int.MaxValue"/> elements.
/// BCL <c>List&lt;T&gt;</c> caps at int32 indexing (~2.15 B); workloads like BBHash
/// MPHF construction over 4 B Wikidata atoms exceed that. <see cref="ChunkedList{T}"/>
/// stores elements in an array of fixed-size chunks, indexed via shift-and-mask, with
/// long-typed <see cref="Count"/> and indexer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layout.</b> A top-level <c>T[][]</c> holds references to per-chunk <c>T[]</c>
/// arrays of size <c>1 &lt;&lt; ChunkShift</c>. Indexing decomposes into
/// <c>(chunk = i &gt;&gt; ChunkShift, offset = i &amp; ChunkMask)</c>. A per-element
/// access is one shift + one mask + two pointer dereferences — &lt; 2 ns on modern CPUs.
/// </para>
/// <para>
/// <b>Growth.</b> Chunks are allocated lazily as <see cref="Add"/> crosses chunk
/// boundaries. The top-level <c>T[][]</c> doubles when full (very rarely — at the
/// default <c>ChunkShift=20</c>, capacity for 4 B elements is 4 K chunks; the top-level
/// resize copies pointer references only, never element data).
/// </para>
/// <para>
/// <b>Memory.</b> No allocation overshoot beyond one chunk-worth at the tail.
/// Vs <c>List&lt;T&gt;</c>'s doubling strategy which transiently doubles backing-array
/// allocation during resize, this avoids the 1.5×–2× transient peak at large sizes.
/// </para>
/// <para>
/// <b>Concurrency.</b> Not thread-safe. Single-threaded producer-consumer expected.
/// </para>
/// </remarks>
public sealed class ChunkedList<T>
{
    /// <summary>Default chunk shift: 1 &lt;&lt; 20 = ~1 M elements per chunk.</summary>
    public const int DefaultChunkShift = 20;

    private readonly int _chunkShift;
    private readonly long _chunkSize;
    private readonly long _chunkMask;

    private T[]?[] _chunks;
    private long _count;

    public ChunkedList() : this(DefaultChunkShift) { }

    public ChunkedList(int chunkShift)
    {
        if (chunkShift < 8 || chunkShift > 30)
            throw new ArgumentOutOfRangeException(nameof(chunkShift), "chunkShift must be in [8, 30]");
        _chunkShift = chunkShift;
        _chunkSize = 1L << chunkShift;
        _chunkMask = _chunkSize - 1;
        _chunks = new T[]?[16];  // initial top-level capacity (16 chunks = up to ~16 M elements at default shift)
    }

    public long Count => _count;

    public T this[long index]
    {
        get
        {
            if ((ulong)index >= (ulong)_count)
                throw new ArgumentOutOfRangeException(nameof(index), $"index={index} out of range [0, {_count})");
            long chunk = index >> _chunkShift;
            long offset = index & _chunkMask;
            return _chunks[chunk]![offset];
        }
        set
        {
            if ((ulong)index >= (ulong)_count)
                throw new ArgumentOutOfRangeException(nameof(index), $"index={index} out of range [0, {_count})");
            long chunk = index >> _chunkShift;
            long offset = index & _chunkMask;
            _chunks[chunk]![offset] = value;
        }
    }

    public void Add(T value)
    {
        long chunkIdx = _count >> _chunkShift;
        long offset = _count & _chunkMask;
        if (chunkIdx >= _chunks.Length)
        {
            // Top-level grows by doubling. References only — no element copy.
            int newSize = _chunks.Length * 2;
            if ((long)newSize <= chunkIdx) newSize = (int)(chunkIdx + 1);
            var newChunks = new T[]?[newSize];
            Array.Copy(_chunks, newChunks, _chunks.Length);
            _chunks = newChunks;
        }
        if (_chunks[chunkIdx] is null)
        {
            // Fresh chunk allocation — single int.MaxValue-bounded array (we cap at 1<<30 << int.MaxValue).
            _chunks[chunkIdx] = new T[_chunkSize];
        }
        _chunks[chunkIdx]![offset] = value;
        _count++;
    }

    /// <summary>
    /// Reset to empty. Releases chunk references for GC; does not zero existing chunks
    /// (the indexer's bounds check on <see cref="Count"/> protects against stale reads).
    /// </summary>
    public void Clear()
    {
        for (int i = 0; i < _chunks.Length; i++) _chunks[i] = null;
        _count = 0;
    }

    /// <summary>
    /// Total number of allocated chunks (for diagnostic / size accounting).
    /// </summary>
    public int AllocatedChunkCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _chunks.Length; i++) if (_chunks[i] is not null) n++;
            return n;
        }
    }

    /// <summary>Chunk size in elements (<c>1 &lt;&lt; ChunkShift</c>).</summary>
    public long ChunkSize => _chunkSize;

    /// <summary>Chunk shift used for index decomposition.</summary>
    public int ChunkShift => _chunkShift;
}
