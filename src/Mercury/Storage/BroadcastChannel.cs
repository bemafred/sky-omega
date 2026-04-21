using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// Fan-out channel used by <c>QuadStore.RebuildSecondaryIndexes</c> to broadcast a single
/// GSPO scan to N secondary-index consumers (ADR-030 Phase 2). Each consumer owns one
/// index and is the sole writer to it — preserves the per-index single-writer contract
/// (ADR-020) while the shared primary-index scan feeds every secondary in parallel.
/// </summary>
/// <remarks>
/// <para>Implemented as N bounded <see cref="Channel{T}"/> instances, one per consumer.
/// <see cref="WriteAsync"/> writes the item to every channel in order; if any consumer's
/// bounded buffer fills, the producer awaits — natural back-pressure keeps the producer
/// within the capacity window of the slowest consumer.</para>
///
/// <para>Memory cost: <c>capacity × consumerCount × sizeof(T)</c>. At capacity=1024 and
/// 4 consumers for <c>TemporalQuad</c> (96 B), ~384 KB — negligible.</para>
///
/// <para>System.Threading.Channels is BCL only; no external dependencies.</para>
/// </remarks>
internal sealed class BroadcastChannel<T> where T : struct
{
    private readonly Channel<T>[] _channels;

    public BroadcastChannel(int consumerCount, int boundedCapacity = 1024)
    {
        if (consumerCount < 1)
            throw new ArgumentOutOfRangeException(nameof(consumerCount), "At least one consumer required.");
        if (boundedCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(boundedCapacity), "Capacity must be positive.");

        _channels = new Channel<T>[consumerCount];
        for (int i = 0; i < consumerCount; i++)
        {
            _channels[i] = Channel.CreateBounded<T>(new BoundedChannelOptions(boundedCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
        }
    }

    /// <summary>Number of consumers (== fan-out width).</summary>
    public int ConsumerCount => _channels.Length;

    /// <summary>Reader for the consumer at index <paramref name="consumerIndex"/>.</summary>
    public ChannelReader<T> Reader(int consumerIndex) => _channels[consumerIndex].Reader;

    /// <summary>
    /// Broadcast <paramref name="item"/> to every consumer. Awaits on each channel in
    /// sequence — if any consumer's buffer is full, the producer stalls on it. When all
    /// consumers accept the write, the call completes.
    /// </summary>
    public async ValueTask WriteAsync(T item, CancellationToken ct = default)
    {
        for (int i = 0; i < _channels.Length; i++)
            await _channels[i].Writer.WriteAsync(item, ct).ConfigureAwait(false);
    }

    /// <summary>Mark every consumer's channel complete. Readers drain remaining items then terminate.</summary>
    public void Complete()
    {
        for (int i = 0; i < _channels.Length; i++)
            _channels[i].Writer.TryComplete();
    }

    /// <summary>
    /// Propagate a failure to every consumer. Used when the producer (or another consumer)
    /// throws — completes each channel with the exception so readers terminate promptly
    /// rather than waiting for a normal end-of-stream.
    /// </summary>
    public void CompleteWithException(Exception ex)
    {
        for (int i = 0; i < _channels.Length; i++)
            _channels[i].Writer.TryComplete(ex);
    }
}
