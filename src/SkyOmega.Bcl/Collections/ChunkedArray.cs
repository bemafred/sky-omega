using System;

namespace SkyOmega.Bcl.Collections;

/// <summary>
/// Fixed-size array that supports more than <see cref="int.MaxValue"/> elements.
/// Sibling to <see cref="ChunkedList{T}"/>; differs in that the length is fixed at
/// construction and all chunks are allocated upfront. Used for translation tables
/// and other length-known-in-advance arrays past the int32 cap.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layout.</b> Same shape as <see cref="ChunkedList{T}"/>: top-level
/// <c>T[][]</c> of fixed-size chunks, indexed via shift-and-mask. Eager allocation
/// makes the access path branch-free vs the list's lazy variant.
/// </para>
/// <para>
/// <b>Memory.</b> Total = N elements + minor top-level overhead. At
/// <c>ChunkShift=20</c> (1 M elements per chunk), 4 B uint elements = 16 GB total
/// in 4 K chunks of 4 MB each.
/// </para>
/// </remarks>
public sealed class ChunkedArray<T>
{
    private readonly int _chunkShift;
    private readonly long _chunkSize;
    private readonly long _chunkMask;

    private readonly T[][] _chunks;
    public long Length { get; }

    public ChunkedArray(long length, int chunkShift = ChunkedList<T>.DefaultChunkShift)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (chunkShift < 8 || chunkShift > 30)
            throw new ArgumentOutOfRangeException(nameof(chunkShift));
        _chunkShift = chunkShift;
        _chunkSize = 1L << chunkShift;
        _chunkMask = _chunkSize - 1;
        Length = length;

        long chunkCount = (length + _chunkSize - 1) >> _chunkShift;
        if (chunkCount > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(length), $"chunk count {chunkCount} exceeds int32 (try larger chunkShift)");
        _chunks = new T[chunkCount][];
        for (int i = 0; i < _chunks.Length; i++)
        {
            // Last chunk may be partial.
            long thisChunkSize = (i == _chunks.Length - 1)
                ? length - (long)i * _chunkSize
                : _chunkSize;
            _chunks[i] = new T[thisChunkSize];
        }
    }

    public T this[long index]
    {
        get
        {
            if ((ulong)index >= (ulong)Length)
                throw new ArgumentOutOfRangeException(nameof(index), $"index={index} out of range [0, {Length})");
            long chunk = index >> _chunkShift;
            long offset = index & _chunkMask;
            return _chunks[chunk][offset];
        }
        set
        {
            if ((ulong)index >= (ulong)Length)
                throw new ArgumentOutOfRangeException(nameof(index), $"index={index} out of range [0, {Length})");
            long chunk = index >> _chunkShift;
            long offset = index & _chunkMask;
            _chunks[chunk][offset] = value;
        }
    }

    public long ChunkSize => _chunkSize;
    public int ChunkShift => _chunkShift;
    public int ChunkCount => _chunks.Length;
}
