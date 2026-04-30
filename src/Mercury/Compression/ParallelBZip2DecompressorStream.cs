using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SkyOmega.Mercury.Compression;

/// <summary>
/// Block-parallel bzip2 decompressor. Wraps a compressed <see cref="Stream"/> and decodes
/// independent bzip2 blocks across a worker pool, exposing the in-order decompressed
/// bytes via standard Stream reads. Forward-only; <c>CanSeek</c> is false. ADR-036 Phase 2.
/// </summary>
/// <remarks>
/// <para>
/// Bzip2's block structure makes parallel decoding natural: each ~900 KB compressed block
/// has independent BWT/MTF/Huffman state and depends only on its own header. A single
/// producer thread runs <see cref="BZip2BlockBoundaryScanner"/> to find block bit-ranges
/// without decoding, dispatches work items to a bounded channel, and N worker threads pull
/// items, decode via the existing <see cref="BZip2BlockReader"/> primitive, and push results
/// back to a priority queue keyed by block ordinal. Stream consumers see the bytes in
/// original order.
/// </para>
/// <para>
/// Worker count defaults to <c>Math.Max(1, (Environment.ProcessorCount * 4) / 5)</c> —
/// ~80% of available cores, leaving the rest for the consumer-side parser, Mercury
/// internals, and OS. On the M5 Max (18 cores) this is 14 workers.
/// </para>
/// <para>
/// Per-block CRC is verified inside each worker. Stream-combined CRC (each block CRC
/// rotate-left-1 + XOR) is accumulated by the consumer thread in emission order and
/// verified against the trailer at end-of-stream.
/// </para>
/// <para>
/// <strong>Single-stream input only.</strong> Concatenated bzip2 streams (which the
/// single-threaded <see cref="BZip2DecompressorStream"/> handles transparently) are
/// rejected at end-of-stream. Wikidata's <c>latest-all.ttl.bz2</c> is a single stream;
/// this is sufficient for the production path.
/// </para>
/// </remarks>
public sealed class ParallelBZip2DecompressorStream : Stream
{
    private readonly Stream _compressed;
    private readonly bool _leaveOpen;
    private readonly int _workerCount;

    private readonly BZip2BlockBoundaryScanner _scanner;

    // Producer → workers. Bounded so the producer blocks if workers can't keep up.
    private readonly Channel<WorkItem> _inputChannel;

    // Workers → consumer. Lock-protected priority queue keyed by ordinal so blocks are
    // emitted in the original order.
    private readonly object _outputLock = new();
    private readonly PriorityQueue<BlockResult, int> _pending = new();
    private readonly SemaphoreSlim _outputAvailable = new(0);

    private int _nextOrdinalToEmit;
    private uint _streamCombinedCrc;
    private bool _producerDone;
    private int _workersAlive;
    private Exception? _firstError;

    /// <summary>
    /// Diagnostic: when not null, each worker appends (worker-id, ordinal, decode-elapsed-ticks)
    /// for every block it processes. Used to test memory-bandwidth-contention hypothesis vs
    /// orchestration-overhead hypothesis: if per-block decode time scales with worker count
    /// (N=1: 30 ms, N=14: 150 ms), workers are competing for shared resource (memory bandwidth).
    /// If per-block decode time stays constant but wall-clock doesn't drop, orchestration is
    /// the bottleneck. Guarded by a static toggle to keep the production path zero-overhead.
    /// </summary>
    internal static ConcurrentBag<(int WorkerId, int Ordinal, long DecodeTicks)>? DiagPerBlockDecode;
    internal static int _diagNextWorkerId;

    private Task? _producerTask;
    private Task[]? _workerTasks;

    private BlockResult? _currentBlock;
    private int _currentBlockOffset;
    private bool _started;
    private bool _disposed;
    private bool _endOfStreamSeen;

    public ParallelBZip2DecompressorStream(Stream compressed, int workerCount = -1, bool leaveOpen = false)
    {
        _compressed = compressed ?? throw new ArgumentNullException(nameof(compressed));
        if (!compressed.CanRead)
            throw new ArgumentException("compressed stream must be readable", nameof(compressed));
        _leaveOpen = leaveOpen;

        _workerCount = workerCount > 0
            ? workerCount
            : Math.Max(1, (Environment.ProcessorCount * 4) / 5);

        _scanner = new BZip2BlockBoundaryScanner(_compressed, leaveOpen: true);

        // Bounded input channel — 2× worker count gives reasonable throughput without
        // unbounded memory growth if a worker stalls.
        _inputChannel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(_workerCount * 2)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
        });
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
        if (_disposed) throw new ObjectDisposedException(nameof(ParallelBZip2DecompressorStream));
        if (output.IsEmpty) return 0;

        EnsureStarted();

        int totalWritten = 0;
        while (output.Length > 0)
        {
            if (_currentBlock is null)
            {
                if (!TryAdvanceToNextBlock())
                    break;  // no more blocks; reached end of stream
            }

            int available = _currentBlock!.Length - _currentBlockOffset;
            int toCopy = Math.Min(available, output.Length);
            _currentBlock.Bytes.AsSpan(_currentBlockOffset, toCopy).CopyTo(output);
            output = output.Slice(toCopy);
            totalWritten += toCopy;
            _currentBlockOffset += toCopy;

            if (_currentBlockOffset >= _currentBlock.Length)
            {
                // Done with this block: return its buffer to the pool.
                ArrayPool<byte>.Shared.Return(_currentBlock.Bytes);
                _currentBlock = null;
                _currentBlockOffset = 0;
            }
        }

        if (totalWritten == 0 && !_endOfStreamSeen)
        {
            // No bytes written and we haven't seen end-of-stream — must be a truncation.
            // Surface any latent error.
            ThrowIfErrored();
        }

        return totalWritten;
    }

    private void EnsureStarted()
    {
        if (_started) return;
        _started = true;

        _scanner.ReadStreamHeader();

        _producerTask = Task.Run(ProducerLoop);

        _workersAlive = _workerCount;
        _workerTasks = new Task[_workerCount];
        for (int i = 0; i < _workerCount; i++)
        {
            _workerTasks[i] = Task.Run(WorkerLoop);
        }
    }

    private async Task ProducerLoop()
    {
        try
        {
            while (_scanner.TryFindNextBlock(out var bytes, out var startBitOffset, out var bitLength, out var ordinal))
            {
                await _inputChannel.Writer.WriteAsync(new WorkItem(ordinal, bytes, startBitOffset, bitLength)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            RecordError(ex);
        }
        finally
        {
            _inputChannel.Writer.Complete();
            lock (_outputLock)
            {
                _producerDone = true;
            }
            // Wake the consumer in case it's waiting and there will be no more blocks.
            _outputAvailable.Release();
        }
    }

    private async Task WorkerLoop()
    {
        var diag = DiagPerBlockDecode;
        int workerId = diag is null ? 0 : Interlocked.Increment(ref _diagNextWorkerId);
        try
        {
            // Each worker has its own decoder + scratch — preallocated, reused per block.
            var blockReader = new BZip2BlockReader();

            while (await _inputChannel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_inputChannel.Reader.TryRead(out var item))
                {
                    long startTicks = diag is null ? 0 : Stopwatch.GetTimestamp();
                    var result = DecodeBlock(blockReader, item);
                    if (diag is not null)
                        diag.Add((workerId, item.Ordinal, Stopwatch.GetTimestamp() - startTicks));
                    lock (_outputLock)
                    {
                        _pending.Enqueue(result, result.Ordinal);
                    }
                    _outputAvailable.Release();
                }
            }
        }
        catch (Exception ex)
        {
            RecordError(ex);
            // Wake the consumer so it can surface the error rather than hang.
            _outputAvailable.Release();
        }
        finally
        {
            if (Interlocked.Decrement(ref _workersAlive) == 0)
            {
                // Last worker out — make sure consumer wakes if it's waiting on results.
                _outputAvailable.Release();
            }
        }
    }

    /// <summary>Decode one block. Returns the BlockResult with decompressed bytes and per-block CRC.</summary>
    private static BlockResult DecodeBlock(BZip2BlockReader blockReader, WorkItem item)
    {
        var bitReader = new BZip2BitReader(item.Bytes.AsSpan());
        if (item.StartBitOffset > 0)
            _ = bitReader.ReadBits(item.StartBitOffset);  // skip prefix bits

        if (!blockReader.TryReadBlock(ref bitReader))
            throw new InvalidDataException("Worker block decode unexpectedly returned end-of-stream sentinel.");

        // Drain into a freshly rented buffer. Block decompresses to at most ~9 × blockSize
        // bytes — for default level 9 that's ~9 × 100 KB × 9 = 8.1 MB upper bound. Use the
        // standard pool with a generous size.
        const int InitialDecodeBuffer = 9 * 1024 * 1024;
        byte[] outputBuffer = ArrayPool<byte>.Shared.Rent(InitialDecodeBuffer);
        int written = 0;
        uint crc = BZip2Crc32.InitialValue;

        while (true)
        {
            int n = blockReader.Drain(outputBuffer.AsSpan(written), ref crc);
            if (n == 0) break;
            written += n;
            if (written >= outputBuffer.Length - 1024)
            {
                // Grow if we're close to filling. (Rare — bzip2 max block ≤ ~9 MB at level 9.)
                var bigger = ArrayPool<byte>.Shared.Rent(outputBuffer.Length * 2);
                outputBuffer.AsSpan(0, written).CopyTo(bigger);
                ArrayPool<byte>.Shared.Return(outputBuffer);
                outputBuffer = bigger;
            }
        }

        uint blockCrcFinal = BZip2Crc32.Finalize(crc);
        if (blockCrcFinal != blockReader.ExpectedBlockCrc)
        {
            ArrayPool<byte>.Shared.Return(outputBuffer);
            throw new InvalidDataException(
                $"Block {item.Ordinal} CRC mismatch: expected 0x{blockReader.ExpectedBlockCrc:X8}, got 0x{blockCrcFinal:X8}");
        }

        return new BlockResult(item.Ordinal, outputBuffer, written, blockCrcFinal);
    }

    /// <summary>
    /// Wait for the block at <c>_nextOrdinalToEmit</c> to be ready, mix its CRC into the
    /// stream-combined CRC, and set it as the current block. Returns false at end-of-stream.
    /// </summary>
    private bool TryAdvanceToNextBlock()
    {
        while (true)
        {
            BlockResult? next = null;
            lock (_outputLock)
            {
                if (_pending.TryPeek(out var top, out var ordinal) && ordinal == _nextOrdinalToEmit)
                {
                    next = _pending.Dequeue();
                }
                else if (_producerDone && _workersAlive == 0 && _pending.Count == 0)
                {
                    // No more results coming. End-of-stream verification time.
                    if (!_endOfStreamSeen)
                    {
                        _endOfStreamSeen = true;
                        ThrowIfErrored();
                        // Compare stream-combined CRC against scanner's expected value.
                        // The scanner populates ExpectedCombinedCrc on the EOS magic encounter
                        // (which happened during producer's TryFindNextBlock).
                        if (_scanner.ExpectedCombinedCrc != _streamCombinedCrc)
                        {
                            throw new InvalidDataException(
                                $"Stream-combined CRC mismatch: expected 0x{_scanner.ExpectedCombinedCrc:X8}, computed 0x{_streamCombinedCrc:X8}");
                        }
                    }
                    return false;
                }
            }

            if (next is not null)
            {
                _streamCombinedCrc = ((_streamCombinedCrc << 1) | (_streamCombinedCrc >> 31)) ^ next.BlockCrc;
                _nextOrdinalToEmit++;
                _currentBlock = next;
                _currentBlockOffset = 0;
                return true;
            }

            // Wait for more items, but also surface any error.
            _outputAvailable.Wait();
            ThrowIfErrored();
        }
    }

    private void RecordError(Exception ex)
    {
        Interlocked.CompareExchange(ref _firstError, ex, null);
    }

    private void ThrowIfErrored()
    {
        if (_firstError is not null)
            throw new InvalidDataException("Parallel bzip2 decode failed.", _firstError);
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            // Drain any pending result buffers back to the pool.
            try
            {
                if (_currentBlock is not null)
                    ArrayPool<byte>.Shared.Return(_currentBlock.Bytes);
                lock (_outputLock)
                {
                    while (_pending.Count > 0)
                    {
                        var r = _pending.Dequeue();
                        ArrayPool<byte>.Shared.Return(r.Bytes);
                    }
                }
            }
            catch { /* best-effort cleanup */ }

            // Wake everyone so they can exit cleanly.
            try { _inputChannel.Writer.TryComplete(); } catch { }
            try { _outputAvailable.Release(); } catch { }

            // Wait briefly for workers to exit so we don't leave threads decoding into
            // disposed pool buffers. Bounded by the time it takes to finish the in-flight block.
            try
            {
                if (_producerTask is not null) _producerTask.Wait(TimeSpan.FromSeconds(5));
                if (_workerTasks is not null) Task.WaitAll(_workerTasks, TimeSpan.FromSeconds(5));
            }
            catch { /* workers already errored or finished */ }

            if (disposing && !_leaveOpen)
                _compressed.Dispose();
        }
        base.Dispose(disposing);
    }

    private readonly record struct WorkItem(int Ordinal, byte[] Bytes, int StartBitOffset, int BitLength);

    private sealed class BlockResult
    {
        public int Ordinal { get; }
        public byte[] Bytes { get; }
        public int Length { get; }
        public uint BlockCrc { get; }

        public BlockResult(int ordinal, byte[] bytes, int length, uint blockCrc)
        {
            Ordinal = ordinal;
            Bytes = bytes;
            Length = length;
            BlockCrc = blockCrc;
        }
    }
}
