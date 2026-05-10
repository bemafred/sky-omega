using System;
using System.IO;
using System.Threading;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// ADR-038 Part 2: per-chunk user-space readahead buffer with double-buffered
/// producer-consumer synchronization. Owned by one <c>ChunkReader</c>; refilled
/// by a background worker via <see cref="ChunkReadAheadDispatcher"/>.
/// </summary>
/// <remarks>
/// <para>
/// Architectural intent: transform the merge phase's read pattern from "interleaved
/// random switches across N chunk file streams" (priority-queue-driven) into "N truly
/// sequential streams, each constantly refilled by a background worker." The kernel
/// page-cache eviction policy is LRU on access; with random-switch pattern, frontier
/// pages of recently-not-visited chunks are evicted under memory pressure. With async
/// refill, each chunk's frontier stays in our user-space anonymous memory under our
/// control — kernel's eviction policy is irrelevant for these pages.
/// </para>
/// <para>
/// <b>Double-buffering:</b> two byte arrays <c>_front</c> (consumer) and <c>_back</c>
/// (producer). Consumer drains <c>_front</c> via <see cref="Read"/>; when exhausted,
/// blocks on <see cref="_backFilled"/> until the worker swaps in <c>_back</c>. Producer
/// (worker thread) waits on <see cref="_backEmpty"/> to claim the back buffer, fills
/// it, and signals.
/// </para>
/// <para>
/// <b>Concurrency contract:</b> exactly TWO threads access this object —
/// the consumer (the merge loop calling <see cref="Read"/> + <see cref="Swap"/>) and
/// one producer (a refill worker calling <see cref="FillBack"/>). The semaphores
/// enforce strict alternation: producer fills empty back; consumer swaps when front
/// drains; producer fills again. No locks needed beyond the semaphores.
/// </para>
/// <para>
/// <b>Records-spanning-buffers:</b> records are at most a few KB (atom URI ~60 B + a
/// few bytes of varint header). Buffer size is 4 MB by default — &gt;1000× larger than
/// any single record. <see cref="Read"/> handles the rare case where a record straddles
/// a buffer boundary by transparently swapping mid-read.
/// </para>
/// </remarks>
internal sealed class ChunkReadAheadBuffer : IDisposable
{
    /// <summary>
    /// Default buffer size per chunk side (4 MB). The buffer is double-buffered, so the
    /// real per-chunk-reader memory footprint is <b>2 ×</b> this value (front + back) =
    /// 8 MB by default. At 21.3 B Wikidata atoms / ~3,923 chunks, peak user-space
    /// anonymous memory across all simultaneously-warm chunk readers is ≈ 31 GiB.
    /// Substrate hosts are sized for this (128 GB target host); see
    /// <c>docs/limits/readahead-buffer-memory-budget.md</c> for the full characterization.
    /// </summary>
    public const int DefaultBufferSize = 4 * 1024 * 1024;

    private byte[] _front;
    private byte[] _back;
    private int _frontPos;
    private int _frontLen;
    private int _backLen;
    private long _filePosition;
    private readonly long _fileLength;
    private bool _eof;
    private volatile Exception? _producerException;

    // _backFilled.Release(): producer signals back has data ready to swap.
    // _backFilled.Wait(): consumer waits for back to be filled.
    private readonly SemaphoreSlim _backFilled;

    // _backEmpty.Release(): consumer signals back is now empty (ready to refill).
    // _backEmpty.Wait(): producer waits for back to be empty before filling.
    private readonly SemaphoreSlim _backEmpty;

    // Invoked by the consumer after a swap when the back buffer becomes empty —
    // signals that a refill request should be enqueued for the producer pool.
    // Called on consumer thread; backpressure on the dispatcher's queue is
    // intentional (caps simultaneous in-flight refills).
    private readonly Action? _onBackEmpty;

    private bool _disposed;

    public ChunkReadAheadBuffer(long fileLength, int bufferSize = DefaultBufferSize, Action? onBackEmpty = null)
    {
        if (bufferSize < 1024) throw new ArgumentOutOfRangeException(nameof(bufferSize), "bufferSize must be at least 1 KB");
        _front = new byte[bufferSize];
        _back = new byte[bufferSize];
        _fileLength = fileLength;
        _onBackEmpty = onBackEmpty;
        // Initial state: front is empty (consumer must wait for first fill);
        // back is empty (producer is allowed to fill immediately).
        _backFilled = new SemaphoreSlim(0, 1);
        _backEmpty = new SemaphoreSlim(1, 1);
    }

    /// <summary>
    /// Read up to <paramref name="dest"/>.Length bytes from the buffer into the destination
    /// span. Returns actual bytes read (may be less than requested at EOF).
    /// Blocks if the front buffer is exhausted and waits for the worker to fill back,
    /// then swaps. Re-throws any producer exception that occurred during fill.
    /// </summary>
    public int Read(Span<byte> dest)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ChunkReadAheadBuffer));
        int written = 0;
        while (written < dest.Length)
        {
            // Drain front buffer first
            int avail = _frontLen - _frontPos;
            if (avail > 0)
            {
                int take = Math.Min(avail, dest.Length - written);
                _front.AsSpan(_frontPos, take).CopyTo(dest.Slice(written));
                _frontPos += take;
                written += take;
                if (written == dest.Length) return written;
            }
            // Front exhausted. If we already saw EOF and there's no more back to swap,
            // return what we have.
            if (_eof && _backLen == 0) return written;

            // Wait for back to be filled, swap, then signal back is empty for next refill.
            _backFilled.Wait();
            if (_producerException is not null)
                throw new InvalidOperationException("Readahead refill failed.", _producerException);
            // Swap front and back
            (_front, _back) = (_back, _front);
            _frontLen = _backLen;
            _frontPos = 0;
            _backLen = 0;
            _backEmpty.Release();
            // Trigger next refill if not at EOF.
            if (!_eof) _onBackEmpty?.Invoke();
        }
        return written;
    }

    /// <summary>
    /// Producer: fill the back buffer from the file stream. Called by a refill worker
    /// thread holding a FileStream pulled from the BoundedFileStreamPool. Returns
    /// when the back buffer is filled (or EOF), signals consumer via <c>_backFilled</c>.
    /// On exception, records it; the consumer's next Read raises it.
    /// </summary>
    public void FillBack(FileStream fs)
    {
        if (_disposed) return;
        try
        {
            _backEmpty.Wait();
            if (_eof || _filePosition >= _fileLength)
            {
                _eof = true;
                _backLen = 0;
                _backFilled.Release();
                return;
            }
            if (fs.Position != _filePosition) fs.Position = _filePosition;
            int n = fs.Read(_back, 0, _back.Length);
            _backLen = n;
            _filePosition += n;
            if (n == 0 || _filePosition >= _fileLength) _eof = true;
            _backFilled.Release();
        }
        catch (Exception ex)
        {
            _producerException = ex;
            _eof = true;
            _backLen = 0;
            try { _backFilled.Release(); } catch { /* already at max — consumer will see EOF + exception */ }
        }
    }

    /// <summary>True once the producer has read past EOF AND the back buffer is consumed.</summary>
    public bool IsExhausted => _eof && _backLen == 0 && _frontPos >= _frontLen;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Release any waiting threads so they exit cleanly.
        try { _backEmpty.Release(); } catch { }
        try { _backFilled.Release(); } catch { }
        _backEmpty.Dispose();
        _backFilled.Dispose();
    }
}
