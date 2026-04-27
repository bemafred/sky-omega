using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SkyOmega.Mercury.Compression;

/// <summary>
/// Forward-only MSB-first bit reader over a byte buffer. ADR-036 Decision 2.
/// </summary>
/// <remarks>
/// <para>
/// bzip2 packs codes most-significant-bit first within each byte. The reader maintains
/// a 64-bit accumulator with the next bits to consume aligned to bit 63 (MSB), refilling
/// from the underlying buffer when the accumulator drops below 32 bits.
/// </para>
/// <para>
/// Implemented as <c>ref struct</c> so the reader lives entirely on the stack — no heap
/// allocation, no boxing, callable in a tight loop without GC pressure. Holds the input
/// as a <see cref="ReadOnlySpan{T}"/>; the caller is responsible for ensuring the
/// underlying buffer outlives the reader.
/// </para>
/// </remarks>
internal ref struct BZip2BitReader
{
    private ReadOnlySpan<byte> _source;
    private int _byteIndex;
    private ulong _bitBuffer;
    private int _bitsInBuffer;

    public BZip2BitReader(ReadOnlySpan<byte> source)
    {
        _source = source;
        _byteIndex = 0;
        _bitBuffer = 0;
        _bitsInBuffer = 0;
    }

    /// <summary>
    /// Reconstruct from saved accumulator state plus a fresh source buffer. Used by the
    /// streaming decompressor to persist bit-level position across <see cref="Stream.Read"/>
    /// calls: bzip2 codes don't align to byte boundaries, so the accumulator can hold up
    /// to 63 leftover bits between calls and must survive buffer slides.
    /// </summary>
    public BZip2BitReader(ReadOnlySpan<byte> source, ulong bitBuffer, int bitsInBuffer)
    {
        _source = source;
        _byteIndex = 0;
        _bitBuffer = bitBuffer;
        _bitsInBuffer = bitsInBuffer;
    }

    /// <summary>
    /// Replace the underlying source buffer while preserving the bit accumulator. Used by
    /// the streaming decompressor to slide its compressed-input window: bytes consumed past
    /// <see cref="ByteIndex"/> are dropped, the buffer is refilled from the underlying stream,
    /// and Refill is called with the new buffer slice. The accumulator (which may hold up
    /// to 64 bits read from the previous source) is intact.
    /// </summary>
    public void Refill(ReadOnlySpan<byte> newSource)
    {
        _source = newSource;
        _byteIndex = 0;
    }

    /// <summary>Number of bytes consumed from the current source so far.</summary>
    public int ByteIndex => _byteIndex;

    /// <summary>Current bit accumulator state — for save/restore across stream reads.</summary>
    public ulong BitBuffer => _bitBuffer;

    /// <summary>True when no more bytes are available and the accumulator is empty.</summary>
    public bool IsExhausted => _byteIndex >= _source.Length && _bitsInBuffer == 0;

    /// <summary>Bits available in the accumulator without refill.</summary>
    public int BufferedBits => _bitsInBuffer;

    /// <summary>Bytes still untouched in the underlying source buffer.</summary>
    public int RemainingSourceBytes => _source.Length - _byteIndex;

    /// <summary>
    /// Read <paramref name="count"/> bits (1–32). Result is right-aligned in the returned
    /// uint. Throws <see cref="EndOfStreamException"/> if the source is exhausted before
    /// <paramref name="count"/> bits are available.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int count)
    {
        if ((uint)(count - 1) >= 32)
            throw new ArgumentOutOfRangeException(nameof(count), "count must be in [1, 32]");
        EnsureBits(count);
        uint result = (uint)(_bitBuffer >> (64 - count));
        _bitBuffer <<= count;
        _bitsInBuffer -= count;
        return result;
    }

    /// <summary>Read one bit; returns 0 or 1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBit()
    {
        if (_bitsInBuffer == 0) Refill();
        if (_bitsInBuffer == 0)
            throw new EndOfStreamException();
        uint result = (uint)(_bitBuffer >> 63);
        _bitBuffer <<= 1;
        _bitsInBuffer--;
        return result;
    }

    /// <summary>
    /// Peek <paramref name="count"/> bits (1–32) without consuming them. Useful for
    /// table-based Huffman decode, where the next N bits are looked up before the
    /// actual code length is known.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint PeekBits(int count)
    {
        if ((uint)(count - 1) >= 32)
            throw new ArgumentOutOfRangeException(nameof(count));
        EnsureBits(count);
        return (uint)(_bitBuffer >> (64 - count));
    }

    /// <summary>Consume <paramref name="count"/> bits previously inspected via <see cref="PeekBits"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ConsumeBits(int count)
    {
        // No EnsureBits here — caller already peeked, so the bits are in the buffer.
        _bitBuffer <<= count;
        _bitsInBuffer -= count;
    }

    /// <summary>Read a 32-bit big-endian unsigned integer (4 byte-aligned reads).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadUInt32BigEndian() => ReadBits(32);

    /// <summary>Read a 48-bit big-endian unsigned integer as a ulong (high 16 bits zero).</summary>
    public ulong ReadUInt48BigEndian()
    {
        ulong hi = ReadBits(24);
        ulong lo = ReadBits(24);
        return (hi << 24) | lo;
    }

    /// <summary>Discard buffered fractional bits until the next byte boundary.</summary>
    public void AlignToByte()
    {
        int drop = _bitsInBuffer & 7;
        _bitBuffer <<= drop;
        _bitsInBuffer -= drop;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureBits(int count)
    {
        if (_bitsInBuffer < count) Refill();
        if (_bitsInBuffer < count)
            throw new EndOfStreamException();
    }

    /// <summary>
    /// Refill the accumulator from the source buffer. Pulls 64-bit big-endian when
    /// 8 source bytes are aligned-available; otherwise byte-at-a-time. Both paths
    /// place the next bits at the high end of the accumulator.
    /// </summary>
    private void Refill()
    {
        // Fast path: at least 8 source bytes available AND the accumulator empty.
        // Read a 64-bit big-endian load and place it directly. Common during bulk
        // decode of long Huffman streams.
        if (_bitsInBuffer == 0 && _byteIndex + 8 <= _source.Length)
        {
            _bitBuffer = BinaryPrimitives.ReadUInt64BigEndian(_source.Slice(_byteIndex, 8));
            _bitsInBuffer = 64;
            _byteIndex += 8;
            return;
        }

        // Slow path: pull bytes one at a time into the high-bits-empty region of the
        // accumulator. Stops when accumulator is full (would lose data on next byte)
        // or source is exhausted.
        while (_bitsInBuffer <= 56 && _byteIndex < _source.Length)
        {
            _bitBuffer |= ((ulong)_source[_byteIndex]) << (56 - _bitsInBuffer);
            _bitsInBuffer += 8;
            _byteIndex++;
        }
    }
}
