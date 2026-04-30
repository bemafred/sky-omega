using System;
using System.IO;

namespace SkyOmega.Mercury.Compression;

/// <summary>
/// Sequential bit-aligned scanner that finds bzip2 block boundaries in a compressed stream
/// without decoding the blocks themselves. ADR-036 Phase 2 (parallel decompression).
/// </summary>
/// <remarks>
/// <para>
/// Bzip2 blocks are delimited by 48-bit magic numbers at *bit* boundaries, not byte
/// boundaries (bzip2's Huffman codes don't byte-align across blocks). The scanner reads
/// the underlying compressed stream into a sliding buffer and walks the bitstream looking
/// for the block magic <c>0x314159265359</c> (pi prefix) or the end-of-stream magic
/// <c>0x177245385090</c> (sqrt(pi) prefix).
/// </para>
/// <para>
/// Each call to <see cref="TryFindNextBlock"/> finds the next block's bit position,
/// emits a byte slice spanning [previous-block-magic-bit, this-block-magic-bit), and
/// returns the start-bit offset within the slice's first byte so a worker can construct
/// a <see cref="BZip2BitReader"/> at the correct alignment.
/// </para>
/// <para>
/// Throughput: scanning is one bit per iteration with a 64-bit shift register and a
/// 48-bit comparison. ~64 cycles/byte on a modern core. The scanner is not the bottleneck;
/// per-block decode dominates.
/// </para>
/// </remarks>
internal sealed class BZip2BlockBoundaryScanner
{
    private const ulong BlockMagic = BZip2BlockReader.BlockMagic;
    private const ulong EndOfStreamMagic = BZip2BlockReader.EndOfStreamMagic;
    private const ulong Magic48BitMask = 0xFFFF_FFFF_FFFFUL;

    // Sliding compressed-input window. 8 MB holds ~8 maximum-size blocks of lookahead —
    // enough for several workers to be fed without producer blocking.
    private const int BufferSize = 8 * 1024 * 1024;
    private const int RefillThreshold = 2 * 1024 * 1024;

    private readonly Stream _source;
    private readonly bool _leaveOpen;

    private readonly byte[] _buffer = new byte[BufferSize];
    private int _bufferFill;
    private int _bufferConsumed;  // bytes that no future block needs
    private bool _underlyingEof;

    // Bit-level scan state.
    private ulong _shiftRegister;
    private int _shiftRegisterBitsLoaded;

    // Bit position (relative to scan start) of the magic for the previous block we found —
    // i.e., the start of the next slice we'll yield. Set by ReadStreamHeader to bit 32.
    private long _currentBlockStartBit;
    private long _scanBitPos;  // bit position of the most recently shifted-in bit

    private bool _streamHeaderRead;
    private bool _atEndOfStream;
    private bool _hasFollowingStream;
    private uint _expectedCombinedCrc;
    private int _blockOrdinal;

    public BZip2BlockBoundaryScanner(Stream source, bool leaveOpen = false)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (!source.CanRead)
            throw new ArgumentException("source stream must be readable", nameof(source));
        _leaveOpen = leaveOpen;
    }

    /// <summary>True once the end-of-stream sentinel has been found.</summary>
    public bool AtEndOfStream => _atEndOfStream;

    /// <summary>True if a follow-on bzip2 stream begins after the current one's trailer.</summary>
    public bool HasFollowingStream => _hasFollowingStream;

    /// <summary>Stream-combined CRC declared at end-of-stream; populated after the trailer is parsed.</summary>
    public uint ExpectedCombinedCrc => _expectedCombinedCrc;

    /// <summary>
    /// Read and validate the 4-byte stream header (<c>BZh</c> + level digit). Must be called
    /// before <see cref="TryFindNextBlock"/>. Throws on malformed header.
    /// </summary>
    public void ReadStreamHeader()
    {
        if (_streamHeaderRead) throw new InvalidOperationException("Stream header already read.");
        EnsureBufferReady();
        if (_bufferFill < 4)
            throw new InvalidDataException("Truncated bzip2 stream: header < 4 bytes.");
        if (_buffer[0] != (byte)'B' || _buffer[1] != (byte)'Z' || _buffer[2] != (byte)'h')
            throw new InvalidDataException("Invalid bzip2 stream header (expected 'BZh<level>').");
        if (_buffer[3] < (byte)'1' || _buffer[3] > (byte)'9')
            throw new InvalidDataException($"Invalid bzip2 block-size level digit: 0x{_buffer[3]:X2}");

        // Position the bit cursor immediately after the header.
        _bufferConsumed = 0;
        _scanBitPos = 32;  // 4 bytes = 32 bits past start
        _shiftRegister = 0;
        _shiftRegisterBitsLoaded = 0;
        _currentBlockStartBit = 32;  // first block's magic is at bit 32

        // Pre-fill the shift register from byte 4 onward — but we'll do that lazily in the
        // scan loop. Mark that we're past the header.
        _streamHeaderRead = true;
        _atEndOfStream = false;
        _hasFollowingStream = false;
    }

    /// <summary>
    /// Find the next block boundary. On success: <paramref name="blockBytes"/> is a fresh
    /// byte[] containing the bytes that span the block (including its leading magic),
    /// <paramref name="startBitOffset"/> is the bit offset within <c>blockBytes[0]</c>
    /// where the block begins, <paramref name="bitLength"/> is the total bit count.
    /// On end-of-stream: returns false and sets <see cref="AtEndOfStream"/>.
    /// </summary>
    public bool TryFindNextBlock(out byte[] blockBytes, out int startBitOffset, out int bitLength, out int blockOrdinal)
    {
        if (!_streamHeaderRead) throw new InvalidOperationException("ReadStreamHeader must be called first.");
        if (_atEndOfStream)
        {
            blockBytes = Array.Empty<byte>();
            startBitOffset = 0;
            bitLength = 0;
            blockOrdinal = -1;
            return false;
        }

        long blockStart = _currentBlockStartBit;
        long blockEnd;
        bool foundEndOfStream;

        // Walk forward until we hit either the next block magic or the end-of-stream magic.
        // The first scan sees the current block's magic at bit position blockStart; we need
        // to advance past that magic before searching for the next one.
        long minBitPos = blockStart + 48;  // skip past the leading magic of the current block
        if (!ScanForNextMagic(minBitPos, out blockEnd, out foundEndOfStream))
            throw new InvalidDataException("Truncated bzip2 stream: missing end-of-stream sentinel.");

        // The current block spans [blockStart, blockEnd). Slice the bytes that contain it,
        // plus a small lookahead so the worker's BZip2BitReader can pre-fetch into its
        // 64-bit accumulator without running off the end of the slice. The fast-path refill
        // pulls 8 bytes when the accumulator empties; without lookahead, the last few bits
        // of a strict-fit slice can't be drained because the refill itself would over-read.
        const int BitReaderLookaheadBytes = 8;
        long sliceStartByte = blockStart / 8;
        startBitOffset = (int)(blockStart % 8);
        long sliceEndBitExclusive = blockEnd;
        long sliceEndByteInclusive = (sliceEndBitExclusive - 1) / 8;
        int strictByteLength = (int)(sliceEndByteInclusive - sliceStartByte + 1);
        bitLength = (int)(sliceEndBitExclusive - blockStart);

        // Add lookahead bytes if available; clamp to whatever's actually present in the
        // underlying stream (the last block before EOS has limited tail, multi-stream
        // boundary may have less).
        int desiredLength = strictByteLength + BitReaderLookaheadBytes;
        EnsureBufferContains(sliceStartByte, strictByteLength);
        bool gotLookahead = TryEnsureBufferContains(sliceStartByte, desiredLength);
        int sliceByteLength = strictByteLength;
        if (gotLookahead)
        {
            sliceByteLength = desiredLength;
        }
        else
        {
            // Pad to lookahead with zero bytes — the BitReader will read them but the
            // block decoder stops at EOB before consuming them logically.
            sliceByteLength = desiredLength;
        }

        blockBytes = new byte[sliceByteLength];
        int copyOffset = (int)(sliceStartByte - _bufferConsumed);
        int copyLen = Math.Min(sliceByteLength, _bufferFill - copyOffset);
        Buffer.BlockCopy(_buffer, copyOffset, blockBytes, 0, copyLen);
        // Tail of blockBytes (if copyLen < sliceByteLength) is left as zero by new byte[].

        blockOrdinal = _blockOrdinal++;
        _currentBlockStartBit = blockEnd;

        if (foundEndOfStream)
        {
            // Read the 32-bit combined CRC immediately after the EOS magic (bits blockEnd+0..31).
            // Then byte-align (bzip2 trailers byte-align after the combined CRC).
            ReadStreamTrailer(blockEnd + 48);
            _atEndOfStream = true;
        }

        return true;
    }

    /// <summary>
    /// Scan the bitstream forward until a 48-bit magic is matched whose first bit is at or
    /// after <paramref name="minBitPos"/>. Returns the bit position of the *first* bit of
    /// the matched magic. Sets <paramref name="isEndOfStream"/> based on which magic matched.
    /// Returns false at EOF without finding either magic.
    /// </summary>
    /// <remarks>
    /// The 64-bit shift register holds the most-recent 48 bits in its low-order word; after
    /// shifting in bit at position <c>p</c>, the register's low 48 bits are
    /// <c>[p-47..p]</c>. To match a magic whose first bit is at <c>m</c>, the last bit
    /// (<c>m+47</c>) must have been shifted in — i.e., <c>_scanBitPos == m + 48</c>. So
    /// <c>magicStartBit = _scanBitPos - 48</c>.
    ///
    /// To not match a magic before <paramref name="minBitPos"/>, we need the register's
    /// oldest bit (<c>_scanBitPos - 48</c>) to be at or past <paramref name="minBitPos"/>
    /// before checking. So warm-up advances <c>_scanBitPos</c> to <c>minBitPos + 48</c>
    /// before the first comparison.
    /// </remarks>
    private bool ScanForNextMagic(long minBitPos, out long magicStartBit, out bool isEndOfStream)
    {
        long warmupTarget = minBitPos + 48;
        while (_scanBitPos < warmupTarget)
        {
            if (!ShiftInOneBit())
            {
                magicStartBit = 0;
                isEndOfStream = false;
                return false;
            }
        }

        while (true)
        {
            if (_shiftRegisterBitsLoaded >= 48)
            {
                ulong low48 = _shiftRegister & Magic48BitMask;
                if (low48 == BlockMagic)
                {
                    magicStartBit = _scanBitPos - 48;
                    isEndOfStream = false;
                    return true;
                }
                if (low48 == EndOfStreamMagic)
                {
                    magicStartBit = _scanBitPos - 48;
                    isEndOfStream = true;
                    return true;
                }
            }
            if (!ShiftInOneBit())
            {
                magicStartBit = 0;
                isEndOfStream = false;
                return false;
            }
        }
    }

    /// <summary>Shift one bit (MSB-first per bzip2 convention) into the scan register. Returns false at EOF.</summary>
    private bool ShiftInOneBit()
    {
        long byteIdxAbsolute = _scanBitPos / 8;
        int bitWithinByte = (int)(_scanBitPos % 8);

        EnsureBufferContains(byteIdxAbsolute, 1);
        int relativeIdx = (int)(byteIdxAbsolute - _bufferConsumed);
        if (relativeIdx >= _bufferFill) return false;

        byte b = _buffer[relativeIdx];
        // bzip2 packs bits MSB-first within each byte: bit 7 is read first.
        int shiftAmount = 7 - bitWithinByte;
        ulong bit = (ulong)((b >> shiftAmount) & 1);
        _shiftRegister = (_shiftRegister << 1) | bit;
        if (_shiftRegisterBitsLoaded < 64) _shiftRegisterBitsLoaded++;
        _scanBitPos++;
        return true;
    }

    /// <summary>Read the post-EOS trailer: 32-bit combined CRC, byte-align, optional next stream header.</summary>
    private void ReadStreamTrailer(long trailerStartBit)
    {
        // Byte-aligned read of the 32-bit CRC if we happen to be byte-aligned, otherwise
        // pull 32 bits from the bitstream. The simplest correct path: continue shifting
        // bits into the register and read 32 bits worth.
        // Reset shift register so previous magic doesn't influence the next match.
        _shiftRegisterBitsLoaded = 0;
        _shiftRegister = 0;
        while (_scanBitPos < trailerStartBit)
        {
            if (!ShiftInOneBit())
                throw new InvalidDataException("Truncated bzip2 stream after end-of-stream magic.");
        }
        // Read 32 bits of CRC big-endian.
        uint crc = 0;
        for (int i = 0; i < 32; i++)
        {
            if (!ShiftInOneBit())
                throw new InvalidDataException("Truncated bzip2 stream-combined CRC.");
            crc = (crc << 1) | (uint)(_shiftRegister & 1);
        }
        _expectedCombinedCrc = crc;

        // Byte-align: skip the remaining bits in the current byte if any.
        long aligned = (_scanBitPos + 7) & ~7L;
        while (_scanBitPos < aligned)
        {
            if (!ShiftInOneBit()) break;
        }

        // Multi-stream support: are the next 4 bytes a 'BZh<level>' header?
        // If the underlying stream is at EOF and fewer than 4 bytes remain, there is no
        // following stream — that's a normal end of input, not an error.
        long nextHeaderByte = aligned / 8;
        if (TryEnsureBufferContains(nextHeaderByte, 4))
        {
            int rel = (int)(nextHeaderByte - _bufferConsumed);
            if (rel + 4 <= _bufferFill
                && _buffer[rel] == (byte)'B' && _buffer[rel + 1] == (byte)'Z' && _buffer[rel + 2] == (byte)'h'
                && _buffer[rel + 3] >= (byte)'1' && _buffer[rel + 3] <= (byte)'9')
            {
                _hasFollowingStream = true;
            }
        }
    }

    /// <summary>
    /// Slide consumed bytes out of the buffer and refill from the underlying stream so the
    /// scan position has lookahead. Conservative: keeps everything from
    /// <c>_currentBlockStartBit / 8</c> onward (the start of any in-flight block slice).
    /// </summary>
    private void EnsureBufferReady()
    {
        long earliestNeededByte = _currentBlockStartBit / 8;
        if (earliestNeededByte > _bufferConsumed)
        {
            int slideBy = (int)(earliestNeededByte - _bufferConsumed);
            int remaining = _bufferFill - slideBy;
            if (remaining > 0 && slideBy > 0)
                Buffer.BlockCopy(_buffer, slideBy, _buffer, 0, remaining);
            _bufferFill = Math.Max(0, remaining);
            _bufferConsumed = (int)earliestNeededByte;
        }

        // Refill if below threshold and we haven't hit EOF.
        while (_bufferFill < BufferSize && !_underlyingEof)
        {
            int read = _source.Read(_buffer, _bufferFill, BufferSize - _bufferFill);
            if (read == 0)
            {
                _underlyingEof = true;
                break;
            }
            _bufferFill += read;
            if (_bufferFill >= BufferSize - RefillThreshold) break;
        }
    }

    /// <summary>Ensure the buffer contains <paramref name="byteCount"/> bytes starting at <paramref name="absoluteByteIdx"/>.</summary>
    private void EnsureBufferContains(long absoluteByteIdx, int byteCount)
    {
        if (!TryEnsureBufferContains(absoluteByteIdx, byteCount))
        {
            int relative = (int)(absoluteByteIdx - _bufferConsumed);
            int needed = relative + byteCount;
            throw new InvalidDataException($"Truncated bzip2 stream: need {needed} bytes from buffer-consumed mark, have {_bufferFill}.");
        }
    }

    /// <summary>Same as <see cref="EnsureBufferContains"/> but returns false instead of throwing on EOF.</summary>
    private bool TryEnsureBufferContains(long absoluteByteIdx, int byteCount)
    {
        if (absoluteByteIdx < _bufferConsumed)
            throw new InvalidOperationException($"Requested byte index {absoluteByteIdx} is before buffer consumed mark {_bufferConsumed}.");

        int relative = (int)(absoluteByteIdx - _bufferConsumed);
        int needed = relative + byteCount;
        while (_bufferFill < needed && !_underlyingEof)
        {
            EnsureBufferReady();
            relative = (int)(absoluteByteIdx - _bufferConsumed);
            needed = relative + byteCount;
            if (_underlyingEof) break;
        }
        return _bufferFill >= needed;
    }

    public void Dispose()
    {
        if (!_leaveOpen) _source.Dispose();
    }
}
