using System.Diagnostics;
using System.Text;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.Diagnostics;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Pruning;

/// <summary>
/// Orchestrates quad transfer between QuadStore instances with filtering.
/// Thread-safe: acquires appropriate locks on source and target.
/// </summary>
public sealed class PruningTransfer
{
    private readonly QuadStore _source;
    private readonly QuadStore _target;
    private readonly TransferOptions _options;

    /// <summary>
    /// Creates a new transfer from source to target with the specified options.
    /// </summary>
    /// <param name="source">Source store to read from.</param>
    /// <param name="target">Target store to write to.</param>
    /// <param name="options">Transfer options (null for defaults).</param>
    public PruningTransfer(QuadStore source, QuadStore target, TransferOptions? options = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _options = options ?? TransferOptions.Default;
    }

    /// <summary>
    /// Execute the transfer with optional progress callback.
    /// </summary>
    /// <param name="progress">Optional callback for progress reporting.</param>
    /// <returns>Transfer result with statistics and optional verification.</returns>
    public TransferResult Execute(TransferProgressCallback? progress = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var stats = new TransferStats();
        Fnv1aHasher? hasher = _options.ComputeChecksum ? new Fnv1aHasher() : null;
        StreamWriter? auditWriter = null;

        _options.Logger.Info("Starting pruning transfer".AsSpan());
        if (_options.DryRun)
            _options.Logger.Info("Dry-run mode - no writes will be performed".AsSpan());

        try
        {
            // Open audit log if configured
            if (!string.IsNullOrEmpty(_options.AuditLogPath) && !_options.DryRun)
            {
                auditWriter = new StreamWriter(_options.AuditLogPath, append: false, Encoding.UTF8);
            }

            // Get source statistics for logging
            var (sourceQuads, sourceAtoms, sourceBytes) = _source.GetStatistics();
            _options.Logger.Info($"Source store: {sourceQuads} quads, {sourceAtoms} atoms".AsSpan());

            // Track batch state for cleanup
            bool batchStarted = false;
            bool batchCompleted = false;

            // Acquire read lock on source for duration of transfer
            _source.AcquireReadLock();
            try
            {
                batchStarted = !_options.DryRun;

                // Process based on history mode
                if (_options.HistoryMode == HistoryMode.FlattenToCurrent)
                {
                    TransferCurrentOnly(ref stats, progress, hasher, auditWriter);
                }
                else
                {
                    var includeDeleted = _options.HistoryMode == HistoryMode.PreserveAll;
                    TransferWithHistory(ref stats, progress, hasher, auditWriter, includeDeleted);
                }

                batchCompleted = true;
            }
            finally
            {
                // Rollback any uncommitted batch on exception
                if (batchStarted && !batchCompleted)
                {
                    try { _target.RollbackBatch(); } catch { /* ignore cleanup errors */ }
                }

                _source.ReleaseReadLock();
            }

            // Checkpoint target to flush all writes
            if (!_options.DryRun)
            {
                _target.Checkpoint();
            }

            // Get final statistics
            var (targetQuads, targetAtoms, targetBytes) = _options.DryRun
                ? (0L, 0L, 0L)
                : _target.GetStatistics();

            _options.Logger.Info($"Transfer complete: {stats.Written} quads written".AsSpan());

            // Compute checksum from accumulated hash
            ulong? checksum = null;
            if (hasher != null)
            {
                checksum = hasher.GetHash();
            }

            // Perform verification if requested
            TransferVerification? verification = null;
            if (_options.VerifyAfterTransfer && !_options.DryRun)
            {
                verification = Verify(stats, checksum);
            }

            return new TransferResult
            {
                Success = true,
                TotalScanned = stats.Scanned,
                TotalMatched = stats.Matched,
                TotalWritten = stats.Written,
                TargetAtomCount = targetAtoms,
                Duration = stopwatch.Elapsed,
                BytesSaved = sourceBytes - targetBytes,
                ContentChecksum = checksum,
                Verification = verification
            };
        }
        catch (OperationCanceledException)
        {
            _options.Logger.Warning("Transfer cancelled".AsSpan());
            return new TransferResult
            {
                Success = false,
                TotalScanned = stats.Scanned,
                TotalMatched = stats.Matched,
                TotalWritten = stats.Written,
                Duration = stopwatch.Elapsed,
                ErrorMessage = "Transfer was cancelled"
            };
        }
        catch (Exception ex)
        {
            _options.Logger.Error($"Transfer failed: {ex.Message}".AsSpan());
            return new TransferResult
            {
                Success = false,
                TotalScanned = stats.Scanned,
                TotalMatched = stats.Matched,
                TotalWritten = stats.Written,
                Duration = stopwatch.Elapsed,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            auditWriter?.Dispose();
        }
    }

    private void TransferCurrentOnly(
        ref TransferStats stats,
        TransferProgressCallback? progress,
        Fnv1aHasher? hasher,
        StreamWriter? auditWriter)
    {
        var stopwatch = Stopwatch.StartNew();
        var filter = _options.Filter;
        var batchSize = _options.BatchSize;
        var progressInterval = _options.ProgressInterval;
        var ct = _options.CancellationToken;
        var isDryRun = _options.DryRun;

        if (!isDryRun)
            _target.BeginBatch();

        int batchCount = 0;

        // 1. Process default graph
        var defaultEnumerator = _source.QueryCurrent(
            ReadOnlySpan<char>.Empty,
            ReadOnlySpan<char>.Empty,
            ReadOnlySpan<char>.Empty);

        try
        {
            while (defaultEnumerator.MoveNext())
            {
                ct.ThrowIfCancellationRequested();
                var quad = defaultEnumerator.Current;
                ProcessCurrentQuad(quad, ref stats, ref batchCount, filter, hasher,
                    auditWriter, batchSize, progressInterval, progress, isDryRun, stopwatch);
            }
        }
        finally
        {
            defaultEnumerator.Dispose();
        }

        // 2. Process all named graphs
        var graphNames = new List<string>();
        var graphEnum = _source.GetNamedGraphs();
        while (graphEnum.MoveNext())
        {
            graphNames.Add(graphEnum.Current.ToString());
        }

        foreach (var graphIri in graphNames)
        {
            ct.ThrowIfCancellationRequested();

            var graphEnumerator = _source.QueryCurrent(
                ReadOnlySpan<char>.Empty,
                ReadOnlySpan<char>.Empty,
                ReadOnlySpan<char>.Empty,
                graphIri.AsSpan());

            try
            {
                while (graphEnumerator.MoveNext())
                {
                    ct.ThrowIfCancellationRequested();
                    var quad = graphEnumerator.Current;
                    ProcessCurrentQuad(quad, ref stats, ref batchCount, filter, hasher,
                        auditWriter, batchSize, progressInterval, progress, isDryRun, stopwatch);
                }
            }
            finally
            {
                graphEnumerator.Dispose();
            }
        }

        // Commit final partial batch
        if (!isDryRun)
        {
            if (batchCount > 0)
            {
                _target.CommitBatch();
            }
            else
            {
                _target.RollbackBatch();
            }
        }
    }

    private void ProcessCurrentQuad(
        in ResolvedTemporalQuad quad,
        ref TransferStats stats,
        ref int batchCount,
        IPruningFilter filter,
        Fnv1aHasher? hasher,
        StreamWriter? auditWriter,
        int batchSize,
        int progressInterval,
        TransferProgressCallback? progress,
        bool isDryRun,
        Stopwatch stopwatch)
    {
        stats.Scanned++;

        if (!filter.ShouldInclude(
            quad.Graph, quad.Subject, quad.Predicate, quad.Object,
            quad.ValidFrom, quad.ValidTo))
        {
            WriteToAuditLog(auditWriter, quad);
            return;
        }

        stats.Matched++;

        if (hasher != null)
        {
            HashQuad(hasher, quad.Graph, quad.Subject, quad.Predicate, quad.Object);
        }

        if (!isDryRun)
        {
            _target.AddCurrentBatched(
                quad.Subject, quad.Predicate, quad.Object, quad.Graph);

            stats.Written++;
            batchCount++;

            if (batchCount >= batchSize)
            {
                _target.CommitBatch();
                _target.BeginBatch();
                batchCount = 0;
            }
        }
        else
        {
            stats.Written++;
        }

        if (progressInterval > 0 && stats.Scanned % progressInterval == 0)
        {
            progress?.Invoke(new TransferProgress
            {
                QuadsScanned = stats.Scanned,
                QuadsMatched = stats.Matched,
                QuadsWritten = stats.Written,
                Elapsed = stopwatch.Elapsed
            });
        }
    }

    private void TransferWithHistory(
        ref TransferStats stats,
        TransferProgressCallback? progress,
        Fnv1aHasher? hasher,
        StreamWriter? auditWriter,
        bool includeDeleted)
    {
        var stopwatch = Stopwatch.StartNew();
        var filter = _options.Filter;
        var batchSize = _options.BatchSize;
        var progressInterval = _options.ProgressInterval;
        var ct = _options.CancellationToken;
        var isDryRun = _options.DryRun;

        if (!isDryRun)
            _target.BeginBatch();

        int batchCount = 0;

        // 1. Process default graph
        var defaultEnumerator = _source.QueryEvolution(
            ReadOnlySpan<char>.Empty,
            ReadOnlySpan<char>.Empty,
            ReadOnlySpan<char>.Empty);

        try
        {
            while (defaultEnumerator.MoveNext())
            {
                ct.ThrowIfCancellationRequested();
                var quad = defaultEnumerator.Current;
                ProcessHistoryQuad(quad, ref stats, ref batchCount, filter, hasher,
                    auditWriter, batchSize, progressInterval, progress, isDryRun, includeDeleted, stopwatch);
            }
        }
        finally
        {
            defaultEnumerator.Dispose();
        }

        // 2. Process all named graphs
        var graphNames = new List<string>();
        var graphEnum = _source.GetNamedGraphs();
        while (graphEnum.MoveNext())
        {
            graphNames.Add(graphEnum.Current.ToString());
        }

        foreach (var graphIri in graphNames)
        {
            ct.ThrowIfCancellationRequested();

            var graphEnumerator = _source.QueryEvolution(
                ReadOnlySpan<char>.Empty,
                ReadOnlySpan<char>.Empty,
                ReadOnlySpan<char>.Empty,
                graphIri.AsSpan());

            try
            {
                while (graphEnumerator.MoveNext())
                {
                    ct.ThrowIfCancellationRequested();
                    var quad = graphEnumerator.Current;
                    ProcessHistoryQuad(quad, ref stats, ref batchCount, filter, hasher,
                        auditWriter, batchSize, progressInterval, progress, isDryRun, includeDeleted, stopwatch);
                }
            }
            finally
            {
                graphEnumerator.Dispose();
            }
        }

        // Commit final partial batch
        if (!isDryRun)
        {
            if (batchCount > 0)
            {
                _target.CommitBatch();
            }
            else
            {
                _target.RollbackBatch();
            }
        }
    }

    private void ProcessHistoryQuad(
        in ResolvedTemporalQuad quad,
        ref TransferStats stats,
        ref int batchCount,
        IPruningFilter filter,
        Fnv1aHasher? hasher,
        StreamWriter? auditWriter,
        int batchSize,
        int progressInterval,
        TransferProgressCallback? progress,
        bool isDryRun,
        bool includeDeleted,
        Stopwatch stopwatch)
    {
        stats.Scanned++;

        // Skip deleted unless preserving all
        if (quad.IsDeleted && !includeDeleted)
            return;

        if (!filter.ShouldInclude(
            quad.Graph, quad.Subject, quad.Predicate, quad.Object,
            quad.ValidFrom, quad.ValidTo))
        {
            WriteToAuditLog(auditWriter, quad);
            return;
        }

        stats.Matched++;

        if (hasher != null)
        {
            HashQuad(hasher, quad.Graph, quad.Subject, quad.Predicate, quad.Object);
        }

        if (!isDryRun)
        {
            _target.AddBatched(
                quad.Subject, quad.Predicate, quad.Object,
                quad.ValidFrom, quad.ValidTo, quad.Graph);

            stats.Written++;
            batchCount++;

            if (batchCount >= batchSize)
            {
                _target.CommitBatch();
                _target.BeginBatch();
                batchCount = 0;
            }
        }
        else
        {
            stats.Written++;
        }

        if (progressInterval > 0 && stats.Scanned % progressInterval == 0)
        {
            progress?.Invoke(new TransferProgress
            {
                QuadsScanned = stats.Scanned,
                QuadsMatched = stats.Matched,
                QuadsWritten = stats.Written,
                Elapsed = stopwatch.Elapsed
            });
        }
    }

    private TransferVerification Verify(TransferStats stats, ulong? sourceChecksum)
    {
        _options.Logger.Info("Verifying transfer...".AsSpan());

        // Re-enumerate target and count
        _target.AcquireReadLock();
        try
        {
            long targetCount = 0;
            Fnv1aHasher? targetHasher = _options.ComputeChecksum ? new Fnv1aHasher() : null;

            var enumerator = _target.QueryCurrent(
                ReadOnlySpan<char>.Empty,
                ReadOnlySpan<char>.Empty,
                ReadOnlySpan<char>.Empty);

            try
            {
                while (enumerator.MoveNext())
                {
                    targetCount++;

                    if (targetHasher != null)
                    {
                        var quad = enumerator.Current;
                        HashQuad(targetHasher, quad.Graph, quad.Subject, quad.Predicate, quad.Object);
                    }
                }
            }
            finally
            {
                enumerator.Dispose();
            }

            ulong? targetChecksum = targetHasher?.GetHash();

            // Verify counts match
            if (targetCount != stats.Written)
            {
                return TransferVerification.Failure(
                    stats.Written,
                    targetCount,
                    $"Count mismatch: expected {stats.Written}, found {targetCount}",
                    sourceChecksum,
                    targetChecksum);
            }

            // Verify checksums if enabled
            if (sourceChecksum.HasValue && targetChecksum.HasValue)
            {
                if (sourceChecksum.Value != targetChecksum.Value)
                {
                    return TransferVerification.Failure(
                        stats.Written,
                        targetCount,
                        $"Checksum mismatch: source={sourceChecksum.Value:X16}, target={targetChecksum.Value:X16}",
                        sourceChecksum,
                        targetChecksum);
                }
            }

            _options.Logger.Info($"Verification passed: {targetCount} quads verified".AsSpan());
            return TransferVerification.Success(stats.Written, targetCount, sourceChecksum, targetChecksum);
        }
        finally
        {
            _target.ReleaseReadLock();
        }
    }

    private static void HashQuad(Fnv1aHasher hasher, ReadOnlySpan<char> graph, ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj)
    {
        // Hash each component as UTF-8 bytes
        Span<byte> buffer = stackalloc byte[1024];

        HashSpan(hasher, graph, buffer);
        HashSpan(hasher, subject, buffer);
        HashSpan(hasher, predicate, buffer);
        HashSpan(hasher, obj, buffer);
    }

    private static void HashSpan(Fnv1aHasher hasher, ReadOnlySpan<char> span, Span<byte> buffer)
    {
        if (span.IsEmpty)
        {
            return;
        }

        var byteCount = Encoding.UTF8.GetByteCount(span);
        if (byteCount <= buffer.Length)
        {
            var written = Encoding.UTF8.GetBytes(span, buffer);
            hasher.Append(buffer.Slice(0, written));
        }
        else
        {
            // Rare case: very long string, allocate
            var bytes = Encoding.UTF8.GetBytes(span.ToString());
            hasher.Append(bytes);
        }
    }

    private static void WriteToAuditLog(StreamWriter? writer, in ResolvedTemporalQuad quad)
    {
        if (writer == null) return;

        // Write in N-Quads format: <s> <p> <o> <g> .
        writer.Write('<');
        writer.Write(quad.Subject);
        writer.Write("> <");
        writer.Write(quad.Predicate);
        writer.Write("> ");

        // Object could be literal or IRI
        if (quad.Object.Length > 0 && quad.Object[0] == '"')
        {
            writer.Write(quad.Object);
        }
        else
        {
            writer.Write('<');
            writer.Write(quad.Object);
            writer.Write('>');
        }

        if (!quad.Graph.IsEmpty)
        {
            writer.Write(" <");
            writer.Write(quad.Graph);
            writer.Write('>');
        }

        writer.WriteLine(" .");
    }

    private struct TransferStats
    {
        public long Scanned;
        public long Matched;
        public long Written;
    }
}

/// <summary>
/// FNV-1a 64-bit hash for content verification.
/// Fast, simple, BCL-only implementation.
/// </summary>
internal sealed class Fnv1aHasher
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private ulong _hash = FnvOffsetBasis;

    /// <summary>
    /// Append data to the hash.
    /// </summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            _hash ^= b;
            _hash *= FnvPrime;
        }
    }

    /// <summary>
    /// Get the current hash value.
    /// </summary>
    public ulong GetHash() => _hash;

    /// <summary>
    /// Reset to initial state.
    /// </summary>
    public void Reset() => _hash = FnvOffsetBasis;
}
