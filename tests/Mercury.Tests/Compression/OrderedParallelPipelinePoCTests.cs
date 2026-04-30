using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;

namespace SkyOmega.Mercury.Tests.Compression;

/// <summary>
/// PoC for the ordered-parallel-pipeline orchestration that
/// <see cref="SkyOmega.Mercury.Compression.ParallelBZip2DecompressorStream"/> uses.
/// Strips away all bzip2 specifics — work items are integers, the "compute" is squaring,
/// the result is a long. Tests the orchestration in isolation to determine whether the
/// multi-block bz2 crash is caused by the parallelism protocol itself or by something
/// bz2-specific (per-block state, buffer pool reuse, slice math, etc).
/// </summary>
public class OrderedParallelPipelinePoCTests
{
    [Theory]
    [InlineData(1, 100)]
    [InlineData(2, 100)]
    [InlineData(4, 100)]
    [InlineData(8, 1000)]
    [InlineData(14, 10000)]
    public async Task SquaresInOrder(int workers, int itemCount)
    {
        var pipeline = new OrderedPipeline<int, long>(workers, x => (long)x * x);

        var producer = Task.Run(() =>
        {
            for (int i = 0; i < itemCount; i++) pipeline.Enqueue(i);
            pipeline.CompleteEnqueue();
        });

        var results = new List<long>(itemCount);
        while (pipeline.TryDequeue(out var result))
            results.Add(result);

        await producer;

        Assert.Equal(itemCount, results.Count);
        for (int i = 0; i < itemCount; i++)
            Assert.Equal((long)i * i, results[i]);
    }

    [Fact]
    public async Task ManyWorkersFewItems_StillCorrect()
    {
        var pipeline = new OrderedPipeline<int, long>(workers: 14, x => (long)x * x);

        var producer = Task.Run(() =>
        {
            for (int i = 0; i < 3; i++) pipeline.Enqueue(i);
            pipeline.CompleteEnqueue();
        });

        var results = new List<long>();
        while (pipeline.TryDequeue(out var result)) results.Add(result);
        await producer;

        Assert.Equal(new long[] { 0, 1, 4 }, results);
    }

    [Fact]
    public async Task VariableWork_DemandsOrderedOutput()
    {
        // Workers with variable delays — completion order has nothing to do with input order.
        var pipeline = new OrderedPipeline<int, long>(workers: 8, x =>
        {
            var spins = (10000 - x * 100);
            Thread.SpinWait(Math.Max(0, spins));
            return (long)x * x;
        });

        const int itemCount = 50;
        var producer = Task.Run(() =>
        {
            for (int i = 0; i < itemCount; i++) pipeline.Enqueue(i);
            pipeline.CompleteEnqueue();
        });

        var results = new List<long>(itemCount);
        while (pipeline.TryDequeue(out var result)) results.Add(result);
        await producer;

        for (int i = 0; i < itemCount; i++)
            Assert.Equal((long)i * i, results[i]);
    }

    [Fact]
    public void WorkerThrowing_PropagatesAndDoesNotHang()
    {
        var pipeline = new OrderedPipeline<int, long>(workers: 4, x =>
        {
            if (x == 7) throw new InvalidOperationException($"intentional failure at item {x}");
            return (long)x * x;
        });

        _ = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++) pipeline.Enqueue(i);
            pipeline.CompleteEnqueue();
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            while (pipeline.TryDequeue(out _)) { }
        });
        Assert.Contains("intentional failure", ex.Message);
    }

    [Fact]
    public void EmptyInput_TerminatesCleanly()
    {
        var pipeline = new OrderedPipeline<int, long>(workers: 4, x => (long)x * x);

        _ = Task.Run(() => pipeline.CompleteEnqueue());

        Assert.False(pipeline.TryDequeue(out _));
    }
}

/// <summary>
/// Mirrors the orchestration used by ParallelBZip2DecompressorStream, with parametric
/// work types and a single function-pointer compute. Producer-side is Enqueue/CompleteEnqueue;
/// consumer-side is TryDequeue. Workers run in the background, results emerge in ordinal order.
/// </summary>
internal sealed class OrderedPipeline<TIn, TOut> : IDisposable
{
    private readonly Func<TIn, TOut> _compute;
    private readonly int _workerCount;

    private readonly Channel<(int Ordinal, TIn Value)> _input;
    private readonly object _outputLock = new();
    private readonly PriorityQueue<(int Ordinal, TOut Value), int> _pending = new();
    private readonly SemaphoreSlim _outputAvailable = new(0);

    private int _nextEnqueueOrdinal;
    private int _nextDequeueOrdinal;
    private int _workersAlive;
    private Exception? _firstError;

    private readonly Task[] _workerTasks;

    public OrderedPipeline(int workers, Func<TIn, TOut> compute)
    {
        if (workers < 1) throw new ArgumentOutOfRangeException(nameof(workers));
        _workerCount = workers;
        _compute = compute ?? throw new ArgumentNullException(nameof(compute));

        _input = Channel.CreateBounded<(int, TIn)>(new BoundedChannelOptions(workers * 2)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
        });

        _workersAlive = workers;
        _workerTasks = new Task[workers];
        for (int i = 0; i < workers; i++)
            _workerTasks[i] = Task.Run(WorkerLoop);
    }

    public void Enqueue(TIn value)
    {
        int ordinal = _nextEnqueueOrdinal++;
        // Synchronous wait — the producer is single-threaded by contract.
        _input.Writer.WriteAsync((ordinal, value)).AsTask().Wait();
    }

    public void CompleteEnqueue()
    {
        _input.Writer.Complete();
    }

    public bool TryDequeue(out TOut value)
    {
        while (true)
        {
            (int Ordinal, TOut Value)? next = null;
            lock (_outputLock)
            {
                if (_pending.TryPeek(out var top, out var pri) && pri == _nextDequeueOrdinal)
                {
                    next = _pending.Dequeue();
                }
                else if (_input.Reader.Completion.IsCompleted && _workersAlive == 0 && _pending.Count == 0)
                {
                    ThrowIfErrored();
                    value = default!;
                    return false;
                }
            }

            if (next is { } tuple)
            {
                _nextDequeueOrdinal++;
                value = tuple.Value;
                return true;
            }

            _outputAvailable.Wait();
            ThrowIfErrored();
        }
    }

    private async Task WorkerLoop()
    {
        try
        {
            while (await _input.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_input.Reader.TryRead(out var item))
                {
                    var result = _compute(item.Value);
                    lock (_outputLock)
                    {
                        _pending.Enqueue((item.Ordinal, result), item.Ordinal);
                    }
                    _outputAvailable.Release();
                }
            }
        }
        catch (Exception ex)
        {
            RecordError(ex);
            _outputAvailable.Release();
        }
        finally
        {
            if (Interlocked.Decrement(ref _workersAlive) == 0)
            {
                _outputAvailable.Release();
            }
        }
    }

    private void RecordError(Exception ex)
    {
        Interlocked.CompareExchange(ref _firstError, ex, null);
    }

    private void ThrowIfErrored()
    {
        if (_firstError is { } ex) throw ex;
    }

    public void Dispose()
    {
        try { _input.Writer.TryComplete(); } catch { }
        try { Task.WaitAll(_workerTasks, TimeSpan.FromSeconds(5)); } catch { }
    }
}
