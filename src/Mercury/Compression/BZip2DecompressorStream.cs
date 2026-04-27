using System;
using System.IO;

namespace SkyOmega.Mercury.Compression;

/// <summary>
/// Streaming bzip2 decompressor. Wraps an underlying compressed <see cref="Stream"/> and
/// exposes the decompressed bytes via standard Stream reads. Forward-only; <c>CanSeek</c>
/// is false. ADR-036 Decision 1.
/// </summary>
/// <remarks>
/// <para>
/// Stream format: 4-byte 'BZh<level>' header (level digit 1–9 selects max block size,
/// informational), then one or more blocks each starting with the pi block magic, ending
/// with the sqrt(pi) end-of-stream sentinel + 32-bit combined CRC. Multiple bzip2 streams
/// may be concatenated; this decompressor continues across boundaries until EOF.
/// </para>
/// <para>
/// Sliding compressed-input buffer (default 2 MB): a bzip2 block is at most ~900 KB
/// compressed, so a 2 MB window guarantees the next block is always in-buffer when
/// requested. Bytes consumed past the bit reader's position are slid out and the tail
/// is refilled from the underlying stream.
/// </para>
/// <para>
/// Per-block CRC is verified during <see cref="Read"/> against the value stored in the
/// block header. Stream-combined CRC (each block CRC mixed in by left-rotate-1 + XOR) is
/// verified at end-of-stream against the value in the trailer. CRC mismatches throw
/// <see cref="InvalidDataException"/> immediately — no opt-out.
/// </para>
/// </remarks>
public sealed class BZip2DecompressorStream : Stream
{
    private const int CompressedBufferSize = 2 * 1024 * 1024;  // 2 MB
    private const int MinBufferTailForRefill = 1 * 1024 * 1024; // refill when < 1 MB available

    private readonly Stream _compressed;
    private readonly bool _leaveOpen;

    private readonly byte[] _compressedBuffer = new byte[CompressedBufferSize];
    private int _compressedFill;       // bytes valid in _compressedBuffer
    private bool _underlyingEofSeen;   // _compressed.Read returned 0 at least once

    private readonly BZip2BlockReader _blockReader = new();

    /// <summary>State machine for the parser.</summary>
    private enum State
    {
        ExpectingStreamHeader,
        ExpectingBlock,
        Draining,
        ExpectingStreamTrailer,
        StreamComplete,
        EndOfStream,
    }
    private State _state = State.ExpectingStreamHeader;

    private uint _currentBlockCrc;     // CRC accumulator for the in-flight block
    private uint _streamCombinedCrc;   // running combined CRC across blocks
    private ulong _savedBitBuffer;     // BitReader accumulator preserved across Read calls
    private int _savedBitsInBuffer;    // BitReader bit-count preserved across Read calls
    private bool _disposed;

    /// <summary>Wrap an existing stream. Caller retains ownership unless <paramref name="leaveOpen"/> is false.</summary>
    public BZip2DecompressorStream(Stream compressed, bool leaveOpen = false)
    {
        _compressed = compressed ?? throw new ArgumentNullException(nameof(compressed));
        if (!compressed.CanRead)
            throw new ArgumentException("compressed stream must be readable", nameof(compressed));
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException("bzip2 stream length is not known");
    public override long Position
    {
        get => throw new NotSupportedException("bzip2 stream is forward-only");
        set => throw new NotSupportedException("bzip2 stream is forward-only");
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if ((uint)offset > (uint)buffer.Length || (uint)count > (uint)(buffer.Length - offset))
            throw new ArgumentOutOfRangeException();
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> output)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BZip2DecompressorStream));
        if (output.IsEmpty) return 0;

        int totalWritten = 0;
        while (output.Length > 0 && _state != State.EndOfStream)
        {
            EnsureCompressedBufferReady();

            // Reconstruct the bit reader from saved accumulator state + the current buffer.
            // bzip2 codes are bit-packed; the accumulator can hold up to 63 leftover bits
            // between Stream.Read calls and must persist (cf. ADR-036 BitReader contract).
            var bitReader = new BZip2BitReader(
                _compressedBuffer.AsSpan(0, _compressedFill),
                _savedBitBuffer,
                _savedBitsInBuffer);

            switch (_state)
            {
                case State.ExpectingStreamHeader:
                    ReadStreamHeader(ref bitReader);
                    _state = State.ExpectingBlock;
                    AdvancePast(ref bitReader);
                    continue;

                case State.ExpectingBlock:
                    if (!_blockReader.TryReadBlock(ref bitReader))
                    {
                        _state = State.ExpectingStreamTrailer;
                        AdvancePast(ref bitReader);
                        continue;
                    }
                    _currentBlockCrc = BZip2Crc32.InitialValue;
                    _state = State.Draining;
                    AdvancePast(ref bitReader);
                    continue;

                case State.Draining:
                    int n = _blockReader.Drain(output, ref _currentBlockCrc);
                    if (n == 0)
                    {
                        // Block exhausted: verify CRC, mix into stream-combined CRC, advance.
                        uint blockCrcFinal = BZip2Crc32.Finalize(_currentBlockCrc);
                        if (blockCrcFinal != _blockReader.ExpectedBlockCrc)
                            throw new InvalidDataException(
                                $"Block CRC mismatch: expected 0x{_blockReader.ExpectedBlockCrc:X8}, got 0x{blockCrcFinal:X8}");
                        // Combined CRC: rotate left 1, XOR with block CRC.
                        _streamCombinedCrc = ((_streamCombinedCrc << 1) | (_streamCombinedCrc >> 31)) ^ blockCrcFinal;
                        _state = State.ExpectingBlock;
                        continue;
                    }
                    output = output.Slice(n);
                    totalWritten += n;
                    continue;

                case State.ExpectingStreamTrailer:
                    uint storedCombinedCrc = bitReader.ReadUInt32BigEndian();
                    if (storedCombinedCrc != _streamCombinedCrc)
                        throw new InvalidDataException(
                            $"Stream CRC mismatch: expected 0x{storedCombinedCrc:X8}, computed 0x{_streamCombinedCrc:X8}");
                    bitReader.AlignToByte();
                    AdvancePast(ref bitReader);
                    _streamCombinedCrc = 0;
                    // Multi-stream support: if the next 4 bytes are 'BZh<level>', another stream
                    // follows. Otherwise, end-of-stream.
                    if (NextBytesAreStreamHeader())
                    {
                        _state = State.ExpectingStreamHeader;
                    }
                    else
                    {
                        _state = State.EndOfStream;
                    }
                    continue;

                case State.StreamComplete:
                    _state = State.EndOfStream;
                    continue;
            }
        }
        return totalWritten;
    }

    private void ReadStreamHeader(ref BZip2BitReader reader)
    {
        if (reader.ReadBits(8) != (byte)'B' || reader.ReadBits(8) != (byte)'Z' || reader.ReadBits(8) != (byte)'h')
            throw new InvalidDataException("Invalid bzip2 stream header (expected 'BZh<level>')");
        uint level = reader.ReadBits(8);
        if (level < (byte)'1' || level > (byte)'9')
            throw new InvalidDataException($"Invalid bzip2 block-size level digit: 0x{level:X2}");
    }

    /// <summary>
    /// Slide consumed bytes out of the buffer and refill from the underlying stream so the
    /// bit reader has at least 1 MB of compressed input ahead. Reads zero bytes if the
    /// underlying stream is at EOF.
    /// </summary>
    private void EnsureCompressedBufferReady()
    {
        if (_compressedFill >= MinBufferTailForRefill || _underlyingEofSeen) return;

        // Read more if there's room. We've not yet consumed (the bit reader reads via the
        // span we hand it; we never slide here — sliding happens in AdvancePast after a
        // logical operation completes).
        while (_compressedFill < CompressedBufferSize && !_underlyingEofSeen)
        {
            int read = _compressed.Read(_compressedBuffer, _compressedFill, CompressedBufferSize - _compressedFill);
            if (read == 0)
            {
                _underlyingEofSeen = true;
                break;
            }
            _compressedFill += read;
        }
    }

    /// <summary>
    /// Slide unconsumed bytes (after the bit reader's <see cref="BZip2BitReader.ByteIndex"/>) to
    /// the start of the buffer, and snapshot the bit accumulator state for the next Read call.
    /// Without saving accumulator state, the next Stream.Read would lose any leftover bits
    /// past the byte boundary the parser stopped on.
    /// </summary>
    private void AdvancePast(ref BZip2BitReader reader)
    {
        _savedBitBuffer = reader.BitBuffer;
        _savedBitsInBuffer = reader.BufferedBits;
        int consumed = reader.ByteIndex;
        if (consumed == 0) return;
        int remaining = _compressedFill - consumed;
        if (remaining > 0)
            Buffer.BlockCopy(_compressedBuffer, consumed, _compressedBuffer, 0, remaining);
        _compressedFill = remaining;
    }

    private bool NextBytesAreStreamHeader()
    {
        // Need at least 4 bytes to inspect; refill if we have fewer.
        while (_compressedFill < 4 && !_underlyingEofSeen)
        {
            int read = _compressed.Read(_compressedBuffer, _compressedFill, CompressedBufferSize - _compressedFill);
            if (read == 0) { _underlyingEofSeen = true; break; }
            _compressedFill += read;
        }
        if (_compressedFill < 4) return false;
        return _compressedBuffer[0] == (byte)'B'
            && _compressedBuffer[1] == (byte)'Z'
            && _compressedBuffer[2] == (byte)'h'
            && _compressedBuffer[3] >= (byte)'1' && _compressedBuffer[3] <= (byte)'9';
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing && !_leaveOpen)
                _compressed.Dispose();
        }
        base.Dispose(disposing);
    }
}
