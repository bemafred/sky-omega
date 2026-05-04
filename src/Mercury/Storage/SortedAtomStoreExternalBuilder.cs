using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkyOmega.Mercury.Abstractions;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// Disk-spilling external-merge-sort builder for <see cref="SortedAtomStore"/>. Suitable
/// for billion-atom vocabularies where the in-memory <see cref="SortedAtomStoreBuilder"/>
/// would exceed RAM. ADR-034 Phase 1B-4.
/// </summary>
/// <remarks>
/// <para>
/// Two-pass external sort over variable-length UTF-8 records:
/// </para>
/// <list type="number">
///   <item>
///     <b>Spill pass.</b> Read input, accumulate <c>(bytes, input-index)</c> records into a
///     memory-bounded buffer. When the buffer hits <c>chunkSizeBytes</c>, sort it in-memory
///     by UTF-8 byte order and spill to a chunk file as length-prefixed records.
///   </item>
///   <item>
///     <b>Merge pass.</b> Open all chunk files, perform a k-way merge via
///     <see cref="PriorityQueue{TElement, TPriority}"/>. Adjacent equal records (deduped
///     across chunks) collapse into one atom; the first occurrence assigns a dense atom ID.
///     Output streams to <c>{base}.atoms</c> + <c>{base}.offsets</c>.
///   </item>
/// </list>
/// <para>
/// Memory profile: <c>chunkSizeBytes</c> peak during the spill pass; in the merge pass,
/// one record buffer per open chunk plus the priority-queue overhead — for 1000 chunks
/// at 64 KB max record this is ~64 MB. Output is written sequentially with small buffers.
/// </para>
/// <para>
/// Temp files live under <paramref name="tempDir"/> and are deleted when the build
/// completes (success or failure). Default <paramref name="tempDir"/> is a randomly
/// named subdirectory of the system temp.
/// </para>
/// </remarks>
internal static class SortedAtomStoreExternalBuilder
{
    /// <summary>
    /// Default chunk size: 1 GB. At 21.3 B Wikidata scale (~4 TB total atom-occurrence
    /// volume) this produces ~4,000 chunks — comfortable under any default OS FD limit
    /// (macOS launchd default ~10,240, Linux default 1,024-65,535) without operators
    /// raising ulimit. Earlier 256 MB default produced ~17,000 chunks at 21.3 B and
    /// hit the macOS per-process FD ceiling during k-way merge.
    /// <para>
    /// Larger chunks also benefit the merge phase's I/O pattern: longer sequential
    /// runs per chunk before the priority queue cycles, more effective OS readahead,
    /// less filesystem-metadata churn. Trade-off: peak in-memory sort buffer rises
    /// from 256 MB to 1 GB, trivial on hosts that can hold the full vocabulary.
    /// </para>
    /// </summary>
    public const long DefaultChunkSizeBytes = 1024L * 1024 * 1024;

    /// <summary>Default chunk size for the disk-backed AssignedIds resolver: 16 M records × 16 B = 256 MB.</summary>
    public const int DefaultResolveSorterChunkSize = 16 * 1024 * 1024;

    /// <summary>
    /// ADR-034 Round 2 prefix compression: anchor every N atoms to bound reconstruction cost.
    /// Atoms whose 1-based ID modulo this interval equals 1 are stored full-text (prefix_len=0);
    /// all others are delta-encoded against their predecessor in sort order. 64 keeps anchor
    /// overhead small (~1.6% of atoms are full) while bounding worst-case reconstruction
    /// to 63 byte-copies per <c>GetAtomSpan</c> call on a compressed atom.
    /// </summary>
    public const int PrefixCompressionAnchorInterval = 64;

    /// <summary>
    /// Maximum number of chunk-file streams the merge keeps open at once. Bounds the
    /// merge's file-descriptor footprint regardless of chunk count. At 21.3B Wikidata
    /// scale the merge processes ~13K chunks; macOS default ulimit (256-1024) is exceeded
    /// without bounding. 64 leaves comfortable headroom for output streams + resolver
    /// while keeping cache hit rate high (consecutive priority-queue pops often hit the
    /// same chunk for similar-prefix atoms).
    /// </summary>
    /// <summary>
    /// Hard cap on simultaneously-open chunk-file streams during merge. Chosen below
    /// macOS <c>kern.maxfilesperproc</c> (245,760) with multi-× safety, well below
    /// typical ulimit -n (1M+). Covers the 21.3 B Wikidata case (~13K chunks) with
    /// 2.5× headroom. Above this cap LRU eviction kicks in; pool stats emitted at
    /// end of merge let us see whether eviction was meaningful.
    /// </summary>
    public const int MergeFileStreamPoolHardCap = 32 * 1024;

    /// <summary>
    /// Per-stream buffer size for merge ChunkReaders. 8 KB is small but adequate —
    /// the merge reads each chunk sequentially, OS-level readahead provides bulk
    /// throughput. Smaller buffer keeps total pool memory bounded: at the hard
    /// cap (32K open streams) total buffer memory is 32K × 8 KB = 256 MB.
    /// </summary>
    public const int MergeFileStreamBufferSize = 8 * 1024;

    /// <summary>
    /// Effective pool size for the next merge. Default is <paramref name="chunkCount"/>
    /// (every chunk held open — zero evictions, 100% hit rate) clamped to
    /// <see cref="MergeFileStreamPoolHardCap"/>. Override via <c>MERCURY_MERGE_POOL_SIZE</c>
    /// env var; the override is also clamped to the hard cap. The 1B trace
    /// (commit e8fa14f follow-up) showed pool=64 at 720 chunks gave 97.46% hit rate
    /// with ~15 min of miss overhead — small relative to wall-clock at 1B but
    /// extrapolates non-linearly worse at 21.3 B's 13K chunks. Holding every chunk
    /// open is cheap (8 KB buffer × chunk count) and eliminates merge-pool misses
    /// as a wall-clock factor.
    /// </summary>
    internal static int ResolveMergeFileStreamPoolSize(int chunkCount)
    {
        int requested;
        var v = Environment.GetEnvironmentVariable("MERCURY_MERGE_POOL_SIZE");
        if (!string.IsNullOrEmpty(v) && int.TryParse(v, out var size) && size >= 1)
            requested = size;
        else
            requested = chunkCount;

        // Clamp: floor 1 (BoundedFileStreamPool requires maxOpen >= 1, even when
        // chunkCount == 0); ceiling = hard cap.
        return Math.Max(1, Math.Min(requested, MergeFileStreamPoolHardCap));
    }

    public static SortedAtomStoreBuilder.BuildResult BuildExternal(
        string baseFilePath,
        IEnumerable<byte[]> inputStrings,
        string? tempDir = null,
        long chunkSizeBytes = DefaultChunkSizeBytes,
        bool useDiskBackedAssigned = false,
        int resolveSorterChunkSize = DefaultResolveSorterChunkSize)
    {
        if (baseFilePath is null) throw new ArgumentNullException(nameof(baseFilePath));
        if (inputStrings is null) throw new ArgumentNullException(nameof(inputStrings));
        if (chunkSizeBytes < 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(chunkSizeBytes), "chunkSizeBytes must be at least 1 MB");
        if (useDiskBackedAssigned && resolveSorterChunkSize < 1024)
            throw new ArgumentOutOfRangeException(nameof(resolveSorterChunkSize), "resolveSorterChunkSize must be at least 1024 records");

        var resolvedTempDir = tempDir ?? Path.Combine(Path.GetTempPath(), "sorted-atom-build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(resolvedTempDir);

        // The resolveSorter outlives this method when disk-backed: ownership transfers to
        // DiskBackedAssignedIds in the BuildResult. On disk-backed paths we must NOT delete
        // the resolver's tempDir here; ExternalSorter manages its own subdirectory.
        ExternalSorter<ResolveRecord, ResolveRecordChunkSorter>? resolveSorter = null;
        bool resolverOwnedByCaller = false;

        try
        {
            if (useDiskBackedAssigned)
            {
                var resolverTempDir = Path.Combine(resolvedTempDir, "assigned-ids-resolver");
                resolveSorter = new ExternalSorter<ResolveRecord, ResolveRecordChunkSorter>(
                    resolverTempDir, resolveSorterChunkSize);
            }

            // Pass 1: collect records into memory-bounded chunks; sort and spill each chunk.
            // For disk-backed builds, empty-graph sentinels are emitted directly to the
            // resolveSorter as ResolveRecord(globalIdx, 0) so the resolver has dense coverage
            // across every InputIdx slot.
            var chunkFiles = new List<string>();
            var inputCount = SpillChunks(inputStrings, resolvedTempDir, chunkSizeBytes, chunkFiles, resolveSorter);

            // Pass 2: k-way merge, dedupe, write to .atoms + .offsets.
            // For disk-backed builds, also emits ResolveRecord(InputIdx, atomId) per merged
            // occurrence to the resolveSorter and finalizes it before returning.
            var result = MergeAndWrite(baseFilePath, chunkFiles, inputCount, resolveSorter);
            resolverOwnedByCaller = useDiskBackedAssigned;
            return result;
        }
        catch
        {
            // Failure path: reclaim the resolveSorter so its tempDir gets cleaned up.
            resolveSorter?.Dispose();
            throw;
        }
        finally
        {
            // Clean the bulk tempDir, but only if the resolveSorter doesn't live there.
            // When useDiskBackedAssigned is true, the resolver's subdirectory is inside
            // resolvedTempDir, and the resolver is alive past this method — leave both.
            if (!resolverOwnedByCaller && Directory.Exists(resolvedTempDir))
            {
                try { Directory.Delete(resolvedTempDir, recursive: true); }
                catch { /* best effort cleanup */ }
            }
        }
    }

    /// <summary>Convenience overload taking strings; encodes to UTF-8 once on input.</summary>
    public static SortedAtomStoreBuilder.BuildResult BuildExternal(
        string baseFilePath,
        IEnumerable<string> inputStrings,
        string? tempDir = null,
        long chunkSizeBytes = DefaultChunkSizeBytes,
        bool useDiskBackedAssigned = false,
        int resolveSorterChunkSize = DefaultResolveSorterChunkSize)
    {
        IEnumerable<byte[]> AsBytes()
        {
            foreach (var s in inputStrings)
                yield return s is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(s);
        }
        return BuildExternal(baseFilePath, AsBytes(), tempDir, chunkSizeBytes,
            useDiskBackedAssigned, resolveSorterChunkSize);
    }

    private static long SpillChunks(
        IEnumerable<byte[]> input,
        string tempDir,
        long chunkSizeBytes,
        List<string> chunkFiles,
        ExternalSorter<ResolveRecord, ResolveRecordChunkSorter>? resolveSorter)
    {
        var buffer = new List<(byte[] Bytes, long InputIdx)>();
        long bufferBytes = 0;
        long globalIdx = 0;  // widened from int — 1B triples × 4 atoms = 4B occurrences exceeds int32

        foreach (var s in input)
        {
            if (s is null || s.Length == 0)
            {
                // Empty-slot sentinel. In-memory path leaves assigned[globalIdx] = 0
                // (array default). Disk-backed path needs an explicit record so the
                // resolver has dense coverage over every InputIdx.
                if (resolveSorter is not null)
                    resolveSorter.Add(new ResolveRecord(globalIdx, 0));

                globalIdx++;
                continue;
            }

            buffer.Add((s, globalIdx));
            bufferBytes += s.Length + 16;  // approx record overhead
            globalIdx++;

            if (bufferBytes >= chunkSizeBytes)
            {
                chunkFiles.Add(SpillOneChunk(tempDir, buffer, chunkFiles.Count).Path);
                buffer.Clear();
                bufferBytes = 0;
            }
        }
        if (buffer.Count > 0)
            chunkFiles.Add(SpillOneChunk(tempDir, buffer, chunkFiles.Count).Path);

        return globalIdx;
    }

    /// <summary>
    /// Result of spilling one chunk — path plus timing instrumentation. Sort vs write
    /// split lets observers measure the parser-blocking spill cost (sibling limit:
    /// <c>spill-blocks-parser.md</c>) directly rather than estimating it.
    /// </summary>
    internal readonly record struct SpillResult(
        string Path,
        long BytesWritten,
        TimeSpan SortDuration,
        TimeSpan WriteDuration);

    /// <summary>
    /// Spill one in-memory buffer of <c>(bytes, inputIdx)</c> records as a sorted chunk
    /// file under <paramref name="tempDir"/>. Returns the path + timings. Made
    /// <c>internal</c> so <see cref="SortedAtomBulkBuilder"/> can spill incrementally
    /// during <c>AddTriple</c> (ADR-034 Phase 1B-5e streaming-input fix).
    /// </summary>
    internal static SpillResult SpillOneChunk(string tempDir, List<(byte[] Bytes, long InputIdx)> buffer, int chunkIndex)
    {
        // Sort by UTF-8 byte order; ties broken by input index for stable output.
        // Uses Comparison<T> delegate; per-comparison cost is dominated by
        // SequenceCompareTo body (~9-10 ns of ~12.5 ns total), so delegate-
        // dispatch overhead (~2-3 ns) is a small fraction. A struct IComparer<T>
        // experiment via Span<T>.Sort was measured 4-6% SLOWER on this workload —
        // Span<T>.Sort's codegen differs from List<T>.Sort and overshadows the
        // dispatch saving. Keeping Comparison<T>.
        var sortStart = System.Diagnostics.Stopwatch.GetTimestamp();
        buffer.Sort((a, b) =>
        {
            int cmp = a.Bytes.AsSpan().SequenceCompareTo(b.Bytes);
            if (cmp != 0) return cmp;
            return a.InputIdx.CompareTo(b.InputIdx);
        });
        var sortDuration = System.Diagnostics.Stopwatch.GetElapsedTime(sortStart);

        var path = Path.Combine(tempDir, $"chunk-{chunkIndex:D6}.bin");
        long bytesWritten = 0;
        var writeStart = System.Diagnostics.Stopwatch.GetTimestamp();
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
        {
            // Record format: int32 length + int64 input-idx + raw bytes. (12-byte header.)
            // InputIdx widened from int32 to int64 to support >500M-triple bulk loads
            // (1B triples × 4 atoms = 4B occurrences exceeds int32 max).
            Span<byte> header = stackalloc byte[12];
            foreach (var (bytes, inputIdx) in buffer)
            {
                BinaryPrimitives.WriteInt32LittleEndian(header.Slice(0, 4), bytes.Length);
                BinaryPrimitives.WriteInt64LittleEndian(header.Slice(4, 8), inputIdx);
                fs.Write(header);
                fs.Write(bytes);
                bytesWritten += 12 + bytes.Length;
            }
        }
        var writeDuration = System.Diagnostics.Stopwatch.GetElapsedTime(writeStart);
        return new SpillResult(path, bytesWritten, sortDuration, writeDuration);
    }

    /// <summary>
    /// Pass-2 merge over already-spilled chunk files. K-way merge with dedup; assigns
    /// dense atom IDs in alphabetical sort order; writes <c>{base}.atoms</c> + <c>{base}.offsets</c>.
    /// Made <c>internal</c> so <see cref="SortedAtomBulkBuilder"/> can call it directly with
    /// chunks accumulated during streaming <c>AddTriple</c> (ADR-034 Phase 1B-5e).
    /// </summary>
    /// <summary>
    /// Periodic merge-progress emission interval. Every N records processed, the merge
    /// emits a <see cref="MergeProgressEvent"/> via the listener (if attached). 100 M
    /// records is small enough to give meaningful granularity at 21.3 B-scale runs and
    /// large enough to keep the per-record dispatch overhead negligible.
    /// </summary>
    public const long MergeProgressEmissionInterval = 100_000_000L;

    internal static SortedAtomStoreBuilder.BuildResult MergeAndWrite(
        string baseFilePath, List<string> chunkFiles, long inputCount,
        ExternalSorter<ResolveRecord, ResolveRecordChunkSorter>? resolveSorter,
        Abstractions.IObservabilityListener? listener = null)
    {
        // Disk-backed: resolveSorter receives one record per non-empty input occurrence here,
        // plus the empty-slot sentinels emitted by SpillChunks. Caller wraps into DiskBackedAssignedIds.
        // In-memory: assigned[] is materialized as today (bounded by int32 array max).
        bool useDiskBacked = resolveSorter is not null;
        if (!useDiskBacked && inputCount > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(inputCount),
                $"inputCount {inputCount} exceeds int32 max for in-memory AssignedIds; pass useDiskBackedAssigned: true.");
        var assigned = useDiskBacked ? Array.Empty<long>() : new long[(int)inputCount];
        long atomCount = 0;
        long fileBytes = 0;       // running file-position accumulator for offsets file (includes prefix_len headers)
        long contentBytes = 0;    // atom-content bytes only (no headers); reported as BuildResult.DataBytes
        long recordsProcessed = 0;       // every record dequeued from the priority queue
        long resolverRecordsSpilled = 0; // every record handed to the resolveSorter
        long mergeStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        var dataPath = baseFilePath + ".atoms";
        var offsetsPath = baseFilePath + ".offsets";

        // Open one ChunkReader per chunk file. The priority queue holds the front
        // record from each chunk; pop, emit/dedupe, advance the popped chunk's reader.
        // ChunkReaders share a bounded LRU pool of FileStream handles. Default pool
        // size is the chunk count (zero evictions); the hard cap engages eviction
        // only above MergeFileStreamPoolHardCap (32K). Per-stream buffer is small
        // (8 KB) — the merge reads sequentially, OS readahead is the workhorse.
        var poolSize = ResolveMergeFileStreamPoolSize(chunkFiles.Count);
        using var streamPool = new BoundedFileStreamPool(poolSize, MergeFileStreamBufferSize);
        var readers = new List<ChunkReader>(chunkFiles.Count);
        try
        {
            foreach (var path in chunkFiles)
            {
                var reader = new ChunkReader(path, readers.Count, streamPool);
                if (reader.MoveNext())
                    readers.Add(reader);
                else
                    streamPool.Drop(path);
            }

            // Priority queue keyed by (current bytes, then chunk index for stable ordering).
            var pq = new PriorityQueue<int, ChunkPriorityKey>(readers.Count, ChunkPriorityKeyComparer.Instance);
            for (int i = 0; i < readers.Count; i++)
                pq.Enqueue(i, new ChunkPriorityKey(readers[i].Current.Bytes, i));

            using var dataFs = new FileStream(dataPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan);
            using var offsetsFs = new FileStream(offsetsPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan);
            Span<byte> offsetBuf = stackalloc byte[8];

            // offsets[0] = 0 (first atom starts at byte 0 of .atoms).
            BinaryPrimitives.WriteInt64LittleEndian(offsetBuf, 0);
            offsetsFs.Write(offsetBuf);

            byte[]? prevBytes = null;

            while (pq.Count > 0)
            {
                int readerIdx = pq.Dequeue();
                var rec = readers[readerIdx].Current;

                bool isNew = prevBytes is null || !rec.Bytes.AsSpan().SequenceEqual(prevBytes);
                if (isNew)
                {
                    atomCount++;
                    // ADR-034 Round 2 prefix compression: anchor every Nth atom (full bytes,
                    // prefix_len=0); all others are delta-encoded against their predecessor
                    // in sort order. Anchor on the FIRST atom (atomCount==1) and every
                    // PrefixCompressionAnchorInterval thereafter.
                    bool isAnchor = ((atomCount - 1) % PrefixCompressionAnchorInterval) == 0;
                    int prefixLen = 0;
                    if (!isAnchor && prevBytes is not null)
                    {
                        prefixLen = ComputeLongestCommonPrefix(prevBytes, rec.Bytes, maxLen: 255);
                    }
                    int suffixLen = rec.Bytes.Length - prefixLen;
                    dataFs.WriteByte((byte)prefixLen);
                    if (suffixLen > 0)
                        dataFs.Write(rec.Bytes, prefixLen, suffixLen);
                    fileBytes += 1 + suffixLen;
                    contentBytes += rec.Bytes.Length;
                    BinaryPrimitives.WriteInt64LittleEndian(offsetBuf, fileBytes);
                    offsetsFs.Write(offsetBuf);
                    prevBytes = rec.Bytes;
                }

                if (useDiskBacked)
                {
                    resolveSorter!.Add(new ResolveRecord(rec.InputIdx, atomCount));
                    resolverRecordsSpilled++;
                }
                else
                    assigned[checked((int)rec.InputIdx)] = atomCount;

                if (readers[readerIdx].MoveNext())
                    pq.Enqueue(readerIdx, new ChunkPriorityKey(readers[readerIdx].Current.Bytes, readerIdx));

                recordsProcessed++;
                if (listener is not null && recordsProcessed % MergeProgressEmissionInterval == 0)
                {
                    listener.OnMergeProgress(new MergeProgressEvent(
                        Timestamp: DateTimeOffset.UtcNow,
                        RecordsProcessed: recordsProcessed,
                        AtomsEmitted: atomCount,
                        ResolverRecordsSpilled: resolverRecordsSpilled,
                        CurrentPoolOpenCount: streamPool.OpenCount,
                        CurrentPoolHits: streamPool.Hits,
                        CurrentPoolMisses: streamPool.Misses,
                        DataBytesWritten: fileBytes));
                }
            }

            dataFs.Flush(flushToDisk: true);
            offsetsFs.Flush(flushToDisk: true);
        }
        finally
        {
            foreach (var r in readers) r.Dispose();
        }

        // Emit final merge-completed event with full pool stats. Replaces the
        // earlier ad-hoc Console.WriteLine [merge-pool] line with structured
        // emission to the JSONL listener — single source of truth for run data.
        long totalGets = streamPool.Hits + streamPool.Misses;
        var mergeDuration = System.Diagnostics.Stopwatch.GetElapsedTime(mergeStartTicks);
        listener?.OnMergeCompleted(new MergeCompletedEvent(
            Timestamp: DateTimeOffset.UtcNow,
            ChunkCount: chunkFiles.Count,
            PoolMaxOpen: streamPool.MaxOpen,
            PoolPeakOpen: streamPool.PeakOpenCount,
            PoolHits: streamPool.Hits,
            PoolMisses: streamPool.Misses,
            TotalGets: totalGets,
            AtomsEmitted: atomCount,
            DataBytes: contentBytes,
            Duration: mergeDuration));

        if (useDiskBacked)
        {
            // Finalize the resolver's spill so it's drainable. Caller wraps as DiskBackedAssignedIds.
            resolveSorter!.Complete();
            return new SortedAtomStoreBuilder.BuildResult(atomCount, contentBytes, assigned)
            {
                AssignedIdsResolver = new DiskBackedAssignedIds(resolveSorter, inputCount),
            };
        }

        return new SortedAtomStoreBuilder.BuildResult(atomCount, contentBytes, assigned);
    }

    /// <summary>
    /// Streaming reader over a single sorted chunk file. Pull-based via MoveNext / Current.
    /// Holds <c>(path, offset)</c> state and acquires its <see cref="FileStream"/> from a
    /// shared <see cref="BoundedFileStreamPool"/> on each read — does NOT own the stream.
    /// This bounds total open-FD count for the merge regardless of chunk count.
    /// </summary>
    private sealed class ChunkReader : IDisposable
    {
        private readonly string _path;
        private readonly long _fileLength;  // cached via FileInfo at construction; chunk files are read-only
        private readonly int _chunkIndex;
        private readonly BoundedFileStreamPool _pool;
        private long _offset;  // saved file position; restored on cache miss
        public (byte[] Bytes, long InputIdx) Current { get; private set; }
        private bool _disposed;

        public ChunkReader(string path, int chunkIndex, BoundedFileStreamPool pool)
        {
            _path = path;
            _chunkIndex = chunkIndex;
            _pool = pool;
            // FileInfo.Length is one fstat at construction; avoids holding an open FD just
            // to read length. The 1B FlushToDisk trace (commit 8ca5388) showed FileStream
            // get_Length() at 12.36% of CPU when called per-MoveNext — caching once is
            // load-bearing for performance even with the pool.
            _fileLength = new FileInfo(path).Length;
            _offset = 0;
            Current = default;
        }

        public bool MoveNext()
        {
            if (_disposed || _offset >= _fileLength) return false;

            var fs = _pool.Get(_path);
            if (fs.Position != _offset) fs.Position = _offset;

            // Record format: int32 length + int64 input-idx + raw bytes (12-byte header).
            Span<byte> header = stackalloc byte[12];
            int read = fs.Read(header);
            if (read < 12) return false;

            int length = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(0, 4));
            long inputIdx = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(4, 8));
            var bytes = new byte[length];
            int got = 0;
            while (got < length)
            {
                int n = fs.Read(bytes, got, length - got);
                if (n == 0) throw new InvalidDataException($"Unexpected EOF in chunk {_chunkIndex}");
                got += n;
            }
            _offset = fs.Position;
            Current = (bytes, inputIdx);
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Stream is owned by the pool; do not dispose here. The pool's Dispose
            // (or LRU eviction) closes it. Drop our path from the pool so FDs free
            // promptly when this reader is exhausted.
            _pool.Drop(_path);
        }
    }

    /// <summary>
    /// Compute the longest common prefix length between two byte arrays, capped at
    /// <paramref name="maxLen"/>. Used by ADR-034 Round 2 prefix compression to
    /// delta-encode each atom against its predecessor in sort order.
    /// </summary>
    internal static int ComputeLongestCommonPrefix(byte[] a, byte[] b, int maxLen)
    {
        int upper = Math.Min(Math.Min(a.Length, b.Length), maxLen);
        int i = 0;
        while (i < upper && a[i] == b[i]) i++;
        return i;
    }

    private readonly record struct ChunkPriorityKey(byte[] Bytes, int ChunkIndex);

    private sealed class ChunkPriorityKeyComparer : IComparer<ChunkPriorityKey>
    {
        public static readonly ChunkPriorityKeyComparer Instance = new();
        public int Compare(ChunkPriorityKey x, ChunkPriorityKey y)
        {
            int cmp = x.Bytes.AsSpan().SequenceCompareTo(y.Bytes);
            if (cmp != 0) return cmp;
            return x.ChunkIndex.CompareTo(y.ChunkIndex);
        }
    }
}
