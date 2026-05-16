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
    /// Hard cap on simultaneously-open chunk-file streams during merge. Sized below
    /// the **effective per-process FD soft limit** that macOS launchd applies to
    /// child processes — empirically ~10,240 (cycle 1, 4, and cycle 8 trigram drain
    /// all crashed at chunk-010131 with EMFILE). The 32K theoretical cap based on
    /// <c>kern.maxfilesperproc</c> (245,760) is fictional in practice — launchd's
    /// soft limit is what actually applies, regardless of <c>ulimit -n</c> in the
    /// invoking shell.
    /// <para>
    /// 8 K leaves comfortable headroom: ~200 stdlib FDs + 2 output streams + a few
    /// resolver/atom store FDs + 8 K pool ≈ 8.3 K total, well under the 10K limit.
    /// At chunk counts > 8 K, LRU eviction kicks in (cost: misses) but the merge
    /// completes rather than aborting. At chunk counts ≤ 8 K (e.g., the cycle 8
    /// atom-merge's 3,923 chunks) the pool holds every chunk simultaneously with
    /// zero evictions.
    /// </para>
    /// </summary>
    public const int MergeFileStreamPoolHardCap = 8 * 1024;

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

        // ADR-041: default tempDir includes "bulk-tmp" as a path segment so the
        // MergeAndWrite cleanup assertion (chunkFiles must live under bulk-tmp/)
        // passes by construction without callers having to know about it.
        var resolvedTempDir = tempDir ?? Path.Combine(Path.GetTempPath(), "bulk-tmp", "sorted-atom-build-" + Guid.NewGuid().ToString("N"));
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

        // ADR-038 Part 1: prefix-compressed chunk records.
        // Per record: [byte prefix_len][varint InputIdx][varint suffix_length][suffix bytes].
        // Anchor every ChunkAnchorInterval records — anchors have prefix_len=0 and
        // carry full bytes (suffix_length == bytes.Length). Non-anchor records carry
        // only the bytes that differ from the previous record's full bytes.
        //
        // Sidecar (chunk-NNNNNN.idx): per-anchor file offset. Enables seek-to-anchor
        // recovery on BoundedFileStreamPool eviction-and-re-acquire (the offset of
        // record k*ChunkAnchorInterval lives at sidecar entry k). Without the sidecar,
        // re-acquire would O(N) scan from chunk start to reconstruct prevBytes context.
        var path = Path.Combine(tempDir, $"chunk-{chunkIndex:D6}.bin");
        var sidecarPath = Path.Combine(tempDir, $"chunk-{chunkIndex:D6}.idx");
        long bytesWritten = 0;
        var anchorOffsets = new List<long>(buffer.Count / ChunkAnchorInterval + 1);
        var writeStart = System.Diagnostics.Stopwatch.GetTimestamp();
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
        {
            byte[]? prevBytes = null;
            Span<byte> varintBuf = stackalloc byte[10];
            for (int i = 0; i < buffer.Count; i++)
            {
                var (bytes, inputIdx) = buffer[i];
                bool isAnchor = (i % ChunkAnchorInterval) == 0;
                int prefixLen = 0;
                if (!isAnchor && prevBytes is not null)
                {
                    // ComputeLongestCommonPrefix bounded at 255 — fits in single byte.
                    prefixLen = ComputeLongestCommonPrefix(prevBytes, bytes, maxLen: 255);
                }
                int suffixLen = bytes.Length - prefixLen;

                if (isAnchor) anchorOffsets.Add(fs.Position);

                // [byte prefix_len]
                fs.WriteByte((byte)prefixLen);
                bytesWritten++;
                // [varint InputIdx]  (zigzag not needed — InputIdx is non-negative, monotonic)
                int n = WriteVarUInt64(varintBuf, (ulong)inputIdx);
                fs.Write(varintBuf.Slice(0, n));
                bytesWritten += n;
                // [varint suffix_length]
                n = WriteVarUInt64(varintBuf, (ulong)suffixLen);
                fs.Write(varintBuf.Slice(0, n));
                bytesWritten += n;
                // [suffix bytes]
                if (suffixLen > 0)
                    fs.Write(bytes, prefixLen, suffixLen);
                bytesWritten += suffixLen;

                prevBytes = bytes;
            }
        }
        // Sidecar: [magic(4)][version(4)][anchor_count(4)][anchor_count × 8 bytes offsets]
        using (var sfs = new FileStream(sidecarPath, FileMode.Create, FileAccess.Write, FileShare.None, 4 * 1024, FileOptions.SequentialScan))
        {
            Span<byte> hdr = stackalloc byte[12];
            BinaryPrimitives.WriteUInt32BigEndian(hdr.Slice(0, 4), ChunkSidecarMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(4, 4), ChunkSidecarVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(8, 4), (uint)anchorOffsets.Count);
            sfs.Write(hdr);
            Span<byte> off = stackalloc byte[8];
            foreach (var pos in anchorOffsets)
            {
                BinaryPrimitives.WriteInt64LittleEndian(off, pos);
                sfs.Write(off);
            }
        }
        var writeDuration = System.Diagnostics.Stopwatch.GetElapsedTime(writeStart);
        return new SpillResult(path, bytesWritten, sortDuration, writeDuration);
    }

    /// <summary>
    /// Write a 64-bit unsigned integer as LEB128 varint into the destination span.
    /// Returns the number of bytes written (1..10). Caller must provide ≥ 10 bytes.
    /// Standard LEB128: each byte holds 7 data bits + 1 continuation bit (high bit).
    /// </summary>
    internal static int WriteVarUInt64(Span<byte> dest, ulong value)
    {
        int written = 0;
        while (value >= 0x80)
        {
            dest[written++] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }
        dest[written++] = (byte)value;
        return written;
    }

    /// <summary>
    /// Read a 64-bit unsigned integer LEB128 varint. Returns the value;
    /// <paramref name="bytesConsumed"/> reports how many bytes were read.
    /// Throws <see cref="InvalidDataException"/> on a malformed varint (more than 10 bytes).
    /// </summary>
    internal static ulong ReadVarUInt64(ReadOnlySpan<byte> source, out int bytesConsumed)
    {
        ulong value = 0;
        int shift = 0;
        int i = 0;
        while (i < source.Length && i < 10)
        {
            byte b = source[i++];
            value |= ((ulong)(b & 0x7F)) << shift;
            if ((b & 0x80) == 0)
            {
                bytesConsumed = i;
                return value;
            }
            shift += 7;
        }
        throw new InvalidDataException("Malformed LEB128 varint (truncated or > 10 bytes)");
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

    /// <summary>
    /// ADR-038 Part 1: anchor every N records in the chunk file. Anchors carry full
    /// bytes (prefix_len = 0); non-anchor records carry only the differing suffix
    /// vs the previous record's reconstructed bytes. N = 64 chosen to bound
    /// reconstruction-on-eviction cost (worst case 63 records) while keeping anchor
    /// overhead small (~1.6% of records are full).
    /// </summary>
    internal const int ChunkAnchorInterval = 64;

    /// <summary>Sidecar file format magic: "IDX\0" big-endian.</summary>
    private const uint ChunkSidecarMagic = 0x49445800u;

    /// <summary>Sidecar file format version. Bump on incompatible change.</summary>
    private const uint ChunkSidecarVersion = 1;

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
        // ADR-038 Part 2: shared per-merge readahead dispatcher. Workers fill per-chunk
        // buffers asynchronously, transforming the kernel-visible access pattern from
        // "interleaved random switches across N streams" into "N truly sequential streams."
        // Workers open short-lived per-refill FileStreams (NOT shared with streamPool —
        // the pool's documented single-threaded contract would otherwise be violated).
        // Disabled when MERCURY_MERGE_READAHEAD env var is "0" — preserves the
        // direct-fs-read fallback path and lets the gradient A/B against the no-readahead
        // baseline cleanly.
        bool readAheadEnabled = Environment.GetEnvironmentVariable("MERCURY_MERGE_READAHEAD") != "0";
        using var readAheadDispatcher = readAheadEnabled
            ? new ChunkReadAheadDispatcher(MergeFileStreamBufferSize)
            : null;
        var readers = new List<ChunkReader>(chunkFiles.Count);
        int chunksDeleted = 0;
        long chunkBytesReclaimed = 0;
        // ADR-041: track cleanup outcome across the merge-phase and MPHF-phase try-blocks.
        // success flips true at the function's last reachable statement; the outer finally
        // emits BulkTmpCleanupEvent with Trigger derived from this flag, distinguishing
        // success-path from exception-path cleanup for per-cycle attribution.
        bool mergeOk = false;
        bool success = false;
        bool preserveBulkTmp = Environment.GetEnvironmentVariable("MERCURY_PRESERVE_BULK_TMP_ON_EXCEPTION") == "1";
        bool anyDeleteFailures = false;
        string? firstFailureMessage = null;
        TimeSpan cleanupDuration = TimeSpan.Zero;

        try
        {
            try
            {
                foreach (var path in chunkFiles)
                {
                    var reader = new ChunkReader(path, readers.Count, streamPool, readAheadDispatcher);
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
                mergeOk = true;
            }
            finally
            {
                foreach (var r in readers) r.Dispose();

                // ADR-041 Part 1+2: chunkFile cleanup with bulk-tmp segment assertion and
                // diagnostic-preserve env var. Phase-transition cleanup: chunk files are no
                // longer referenced by any active code path once readers are Disposed (each
                // reader's Dispose calls _pool.Drop(_path) which closes the underlying FD).
                // Deleting them here — rather than waiting for the caller's end-of-run
                // tempDir wipe — releases disk pressure immediately. At 21.3B Wikidata
                // cycle 8, ~3.6 TB of occurrence chunks were held until run end, putting
                // the GSPO drain phase within ~600 GB of MinimumFreeDiskSpace abort on a
                // 7.3 TB host. Limits: docs/limits/intermediate-cleanup-deferred-to-run-end.md.
                //
                // ADR-041 preserve flag: on merge-phase exception, MERCURY_PRESERVE_BULK_TMP_ON_EXCEPTION=1
                // keeps chunkFiles in place for forensic inspection. Success-path cleanup
                // is unconditional. The flag does NOT preserve through MPHF-phase exception
                // (the chunks are already gone by then; the resolveSorter residue is upstream
                // and handled by SortedAtomBulkBuilder.Dispose in FinalizeSortedAtomBulkIfPresent).
                long cleanupStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                bool shouldCleanup = mergeOk || !preserveBulkTmp;
                if (shouldCleanup)
                {
                    foreach (var path in chunkFiles)
                    {
                        // ADR-041 Part 2: refuse to delete any path that does not live under
                        // a "bulk-tmp" directory segment. Fail-fast defends against a future
                        // refactor that accidentally populates chunkFiles with output paths
                        // (atoms.atoms, atoms.offsets, atoms.mphf, atoms.idx) — those live
                        // at baseFilePath, not under bulk-tmp/.
                        if (!IsUnderBulkTmp(path))
                            throw new InvalidOperationException(
                                $"MergeAndWrite cleanup refusing to delete non-bulk-tmp path: {path}");

                        try
                        {
                            chunkBytesReclaimed += new FileInfo(path).Length;
                            File.Delete(path);
                            chunksDeleted++;
                        }
                        catch (Exception ex)
                        {
                            anyDeleteFailures = true;
                            firstFailureMessage ??= ex.Message;
                        }
                        // ADR-038 Part 1: also delete the per-chunk sidecar (chunk-NNNNNN.idx).
                        // Sidecar carries anchor offsets for re-acquire-after-eviction; once the
                        // chunk is consumed, the sidecar is no longer referenced.
                        var sidecarPath = Path.ChangeExtension(path, ".idx");
                        try
                        {
                            if (File.Exists(sidecarPath))
                            {
                                chunkBytesReclaimed += new FileInfo(sidecarPath).Length;
                                File.Delete(sidecarPath);
                            }
                        }
                        catch (Exception ex)
                        {
                            anyDeleteFailures = true;
                            firstFailureMessage ??= ex.Message;
                        }
                    }
                }
                cleanupDuration = System.Diagnostics.Stopwatch.GetElapsedTime(cleanupStartTicks);
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
                Duration: mergeDuration,
                ChunksDeleted: chunksDeleted,
                ChunkBytesReclaimed: chunkBytesReclaimed));

            // ADR-039 / Phase 2 B3: build MPHF + translation table over the sealed
            // atom set. Disabled when MERCURY_BUILD_MPHF env var is "0" — preserves
            // backward-compatible behavior for callers that don't want the
            // construction overhead, and lets the gradient A/B against no-MPHF
            // baseline cleanly.
            bool buildMphf = atomCount > 0 && Environment.GetEnvironmentVariable("MERCURY_BUILD_MPHF") != "0";
            if (buildMphf)
            {
                BuildMphfFiles(baseFilePath, atomCount, listener);
            }

            if (useDiskBacked)
            {
                // Finalize the resolver's spill so it's drainable. Caller wraps as DiskBackedAssignedIds.
                resolveSorter!.Complete();
                success = true;
                return new SortedAtomStoreBuilder.BuildResult(atomCount, contentBytes, assigned)
                {
                    AssignedIdsResolver = new DiskBackedAssignedIds(resolveSorter, inputCount),
                };
            }

            success = true;
            return new SortedAtomStoreBuilder.BuildResult(atomCount, contentBytes, assigned);
        }
        finally
        {
            // ADR-041 Part 4: emit one BulkTmpCleanupEvent per MergeAndWrite invocation.
            // Trigger="merge_success" when the function returned normally; "merge_exception"
            // when any exception (merge phase, MergeCompletedEvent emit, BuildMphfFiles)
            // propagates out. Per-cycle attribution preserves the signal.
            listener?.OnBulkTmpCleanup(new BulkTmpCleanupEvent(
                Timestamp: DateTimeOffset.UtcNow,
                Trigger: success ? "merge_success" : "merge_exception",
                ChunksDeleted: chunksDeleted,
                ChunkBytesReclaimed: chunkBytesReclaimed,
                ElapsedDuration: cleanupDuration,
                AnyDeleteFailures: anyDeleteFailures,
                FirstFailureMessage: firstFailureMessage));
        }
    }

    // ADR-041 Part 2: bulk-tmp segment assertion helper. Checks for the literal
    // path segment "bulk-tmp" surrounded by directory separators on either platform.
    private static bool IsUnderBulkTmp(string path)
    {
        const string Marker = "bulk-tmp";
        // Normalize the comparison: a "/bulk-tmp/" or "\\bulk-tmp\\" segment anywhere.
        return path.Contains(Path.DirectorySeparatorChar + Marker + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || path.Contains(Path.AltDirectorySeparatorChar + Marker + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// ADR-039 / Phase 2 B3: build MPHF + translation table after atoms.atoms +
    /// atoms.offsets are sealed. Iterates atoms 1..N in sorted order, runs BBHash
    /// construction, writes <c>baseFilePath.mphf</c> + <c>baseFilePath.idx</c>.
    /// </summary>
    /// <remarks>
    /// One-time cost at bulk-load finalize. Atom bytes are read from the just-written
    /// SortedAtomStore via <c>GetAtomSpan</c>; each call's span is valid only until the
    /// next call, so we copy to byte[] for delegate consumption (the BBHash builder
    /// performs multiple passes per level — caching here would help, but at 4 B atoms
    /// the cache itself becomes large; tradeoff deferred to follow-up).
    /// </remarks>
    /// <summary>
    /// Build the MPHF + translation-table files (<c>atoms.mphf</c>, <c>atoms.idx</c>) against
    /// an already-sealed <see cref="SortedAtomStore"/> on disk. Used both by the bulk-load
    /// pipeline (MergeAndWrite) and by the standalone recovery path
    /// (<c>mercury --rebuild-mphf</c>). The recovery path is critical after a Finalize-time
    /// MPHF failure: the parser+merge work is preserved on disk, only MPHF construction
    /// needs to re-run.
    /// </summary>
    internal static void RebuildMphfFromSealedStore(string baseFilePath, Abstractions.IObservabilityListener? listener = null)
    {
        using var atoms = new SortedAtomStore(baseFilePath);
        BuildMphfFiles(baseFilePath, atoms.AtomCount, listener);
    }

    private static void BuildMphfFiles(string baseFilePath, long atomCount, Abstractions.IObservabilityListener? listener)
    {
        var mphfStart = System.Diagnostics.Stopwatch.GetTimestamp();
        // Open the just-written SortedAtomStore to iterate atoms in sorted order.
        using var atoms = new SortedAtomStore(baseFilePath);
        if (atoms.AtomCount != atomCount)
            throw new InvalidOperationException(
                $"BBHash construction: SortedAtomStore reports {atoms.AtomCount} atoms; expected {atomCount}");

        var builder = new Mphf.BBHashBuilder();
        // ADR-042 Part 4: Span-based GetKey API eliminates the per-call byte[] allocation
        // the prior Func<long, byte[]> shape caused. BBHash makes ~2-3 passes per level
        // over every remaining key (~25 levels at 4 B atoms); replacing each per-key
        // ToArray() with a scratch-buffer copy saves ~770 GB of GC churn across a 4 B
        // build. Buffer sized to 64 KB — bounded above by Mercury's parser output limit
        // (16K chars × max 4 bytes/char UTF-8 = 65,536 bytes). The lambda has a
        // defensive fallback for any atom that somehow exceeds this — span.ToArray()
        // is allocated only for the (in-practice impossible) outlier case.
        const int ScratchSize = 64 * 1024;
        var result = builder.Build(atomCount, maxKeyByteLength: ScratchSize,
            (long sortedPos, Span<byte> scratch) =>
            {
                var span = atoms.GetAtomSpan(sortedPos);
                if (span.Length <= scratch.Length)
                {
                    span.CopyTo(scratch);
                    return (ReadOnlySpan<byte>)scratch.Slice(0, span.Length);
                }
                // Outlier path: atom exceeds the 64 KB scratch. Fall back to per-call
                // allocation. Not expected on any practical RDF workload; the parser
                // chokes well below this limit. Defensive belt-and-braces.
                return span.ToArray();
            }, listener);
        var mphfBuildDuration = System.Diagnostics.Stopwatch.GetElapsedTime(mphfStart);

        var mphfPath = baseFilePath + ".mphf";
        var idxPath = baseFilePath + ".idx";
        result.Mphf.WriteTo(mphfPath);
        Mphf.MphfTranslationTable.WriteTo(idxPath, result.Translation);

        var mphfTotalDuration = System.Diagnostics.Stopwatch.GetElapsedTime(mphfStart);
        long mphfBytes = new FileInfo(mphfPath).Length;
        long idxBytes = new FileInfo(idxPath).Length;
        listener?.OnMphfBuildCompleted(new Abstractions.MphfBuildCompletedEvent(
            DateTimeOffset.UtcNow,
            atomCount,
            result.Mphf.Levels.Length,
            result.Mphf.DenseKeys.Length,
            mphfBytes,
            idxBytes,
            mphfBuildDuration,
            mphfTotalDuration));
        // Keep the human-readable summary on stderr for operators tailing the run log;
        // structured per-level + dense-fallback events go through the listener above.
        Console.Error.WriteLine(
            $"[mphf] atoms={atomCount:N0} levels={result.Mphf.Levels.Length} " +
            $"dense={result.Mphf.DenseKeys.Length} " +
            $"build_s={mphfBuildDuration.TotalSeconds:F2} total_s={mphfTotalDuration.TotalSeconds:F2} " +
            $"mphf_bytes={mphfBytes:N0} idx_bytes={idxBytes:N0}");
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
        private readonly long _fileLength;
        private readonly int _chunkIndex;
        private readonly BoundedFileStreamPool _pool;
        private readonly ChunkReadAheadDispatcher? _dispatcher;
        private readonly ChunkReadAheadBuffer? _readahead;
        // ADR-038 Part 1: prevBytes is the running-context for prefix-compressed records.
        private byte[]? _prevBytes;
        // Read buffer for varint header (~19 bytes typical, 32 padded).
        private readonly byte[] _headerBuf = new byte[32];
        public (byte[] Bytes, long InputIdx) Current { get; private set; }
        private bool _disposed;

        public ChunkReader(string path, int chunkIndex, BoundedFileStreamPool pool, ChunkReadAheadDispatcher? dispatcher = null)
        {
            _path = path;
            _chunkIndex = chunkIndex;
            _pool = pool;
            _dispatcher = dispatcher;
            _fileLength = new FileInfo(path).Length;
            Current = default;

            // ADR-038 Part 2: when a dispatcher is provided, set up a per-chunk readahead
            // buffer. The buffer's onBackEmpty callback re-enqueues this chunk for refill
            // after each swap. Initial refill request bootstraps the pipeline.
            if (_dispatcher is not null)
            {
                _readahead = new ChunkReadAheadBuffer(
                    fileLength: _fileLength,
                    onBackEmpty: () => _dispatcher.RequestRefill(_path, _readahead!));
                _dispatcher.RequestRefill(_path, _readahead);
            }
        }

        public bool MoveNext()
        {
            if (_disposed) return false;

            // Dispatch path: read via async readahead buffer (ADR-038 P2).
            // Fallback: synchronous direct file read (preserves cycle 9 behavior + tests
            // that don't construct a dispatcher).
            return _readahead is not null ? MoveNextReadAhead() : MoveNextDirect();
        }

        // Staging buffer — small fixed-size scratch that holds the next N bytes from the
        // readahead buffer. Lets us parse variable-length headers without the over-read
        // bookkeeping that bit the first version. Capacity 64 holds any single record's
        // header (1 byte prefix + 9 byte varint InputIdx + 9 byte varint suffix_len ≤ 19)
        // with margin.
        private const int StageCapacity = 64;
        private readonly byte[] _stage = new byte[StageCapacity];
        private int _stagePos;
        private int _stageLen;

        private void EnsureStage(int minBytes)
        {
            int remaining = _stageLen - _stagePos;
            if (remaining >= minBytes) return;
            // Compact remaining bytes to the start.
            if (remaining > 0) Array.Copy(_stage, _stagePos, _stage, 0, remaining);
            _stagePos = 0;
            _stageLen = remaining;
            // Top up from readahead buffer.
            int got = _readahead!.Read(_stage.AsSpan(_stageLen, _stage.Length - _stageLen));
            _stageLen += got;
        }

        private bool MoveNextReadAhead()
        {
            // EnsureStage with 1 byte sufficient to detect EOF (we need at least the
            // prefix_len byte). If nothing reads, we're done.
            EnsureStage(1);
            if (_stageLen - _stagePos == 0) return false;

            // Ensure room for the worst-case header (1 + 9 + 9 = 19; round up).
            EnsureStage(20);
            if (_stageLen - _stagePos == 0) return false;

            int prefixLen = _stage[_stagePos++];
            ulong inputIdxRaw = ReadVarUInt64(_stage.AsSpan(_stagePos, _stageLen - _stagePos), out int idxBytes);
            _stagePos += idxBytes;
            ulong suffixLenRaw = ReadVarUInt64(_stage.AsSpan(_stagePos, _stageLen - _stagePos), out int sufBytes);
            _stagePos += sufBytes;
            int suffixLen = checked((int)suffixLenRaw);

            int fullLen = prefixLen + suffixLen;
            var bytes = new byte[fullLen];
            if (prefixLen > 0)
            {
                if (_prevBytes is null || _prevBytes.Length < prefixLen)
                    throw new InvalidDataException(
                        $"Chunk {_chunkIndex}: prefix_len={prefixLen} but prevBytes context insufficient");
                Array.Copy(_prevBytes, 0, bytes, 0, prefixLen);
            }
            // Suffix: drain from stage first, then from readahead buffer.
            int suffixFromStage = Math.Min(suffixLen, _stageLen - _stagePos);
            if (suffixFromStage > 0)
            {
                Array.Copy(_stage, _stagePos, bytes, prefixLen, suffixFromStage);
                _stagePos += suffixFromStage;
            }
            int suffixRemaining = suffixLen - suffixFromStage;
            if (suffixRemaining > 0)
            {
                int got = _readahead!.Read(bytes.AsSpan(prefixLen + suffixFromStage, suffixRemaining));
                if (got != suffixRemaining)
                    throw new InvalidDataException($"Chunk {_chunkIndex}: unexpected EOF reading suffix");
            }

            _prevBytes = bytes;
            Current = (bytes, (long)inputIdxRaw);
            return true;
        }

        private bool MoveNextDirect()
        {
            // Fallback path: original direct-read behavior for tests / contexts without
            // a readahead dispatcher. Logic matches B1 implementation.
            // (preserved here for regression safety; not used in production MergeAndWrite)
            var fs = _pool.Get(_path);
            // Track current offset across calls via FileStream position; sync only if
            // the pool restored a different FileStream.
            long offset = fs.Position;
            if (offset >= _fileLength) return false;

            int peekRead = fs.Read(_headerBuf, 0, _headerBuf.Length);
            if (peekRead == 0) return false;

            int hdrIdx = 0;
            int prefixLen = _headerBuf[hdrIdx++];
            ulong inputIdxRaw = ReadVarUInt64(_headerBuf.AsSpan(hdrIdx, peekRead - hdrIdx), out int idxBytes);
            hdrIdx += idxBytes;
            ulong suffixLenRaw = ReadVarUInt64(_headerBuf.AsSpan(hdrIdx, peekRead - hdrIdx), out int sufBytes);
            hdrIdx += sufBytes;
            int suffixLen = checked((int)suffixLenRaw);
            int totalRecordHeaderBytes = hdrIdx;
            int fullLen = prefixLen + suffixLen;
            var bytes = new byte[fullLen];

            if (prefixLen > 0)
            {
                if (_prevBytes is null || _prevBytes.Length < prefixLen)
                    throw new InvalidDataException(
                        $"Chunk {_chunkIndex} record at offset {offset}: prefix_len={prefixLen} but prevBytes context insufficient");
                Array.Copy(_prevBytes, 0, bytes, 0, prefixLen);
            }
            int suffixBytesInPeek = Math.Min(suffixLen, peekRead - totalRecordHeaderBytes);
            if (suffixBytesInPeek > 0)
                Array.Copy(_headerBuf, totalRecordHeaderBytes, bytes, prefixLen, suffixBytesInPeek);
            int suffixRemaining = suffixLen - suffixBytesInPeek;
            if (suffixRemaining > 0)
            {
                int got = 0;
                while (got < suffixRemaining)
                {
                    int n = fs.Read(bytes, prefixLen + suffixBytesInPeek + got, suffixRemaining - got);
                    if (n == 0) throw new InvalidDataException($"Unexpected EOF in chunk {_chunkIndex}");
                    got += n;
                }
            }
            else
            {
                int unconsumed = peekRead - totalRecordHeaderBytes - suffixLen;
                if (unconsumed > 0) fs.Position -= unconsumed;
            }

            _prevBytes = bytes;
            Current = (bytes, (long)inputIdxRaw);
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _readahead?.Dispose();
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
