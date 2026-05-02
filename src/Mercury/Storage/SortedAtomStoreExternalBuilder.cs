using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

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
    /// Default chunk size: 256 MB. Holds ~2-5 M records for typical Wikidata atom lengths
    /// (avg ~50 bytes), a few hundred chunk files for full Wikidata's 4 B atoms.
    /// </summary>
    public const long DefaultChunkSizeBytes = 256L * 1024 * 1024;

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
    public const int MergeFileStreamPoolSize = 64;

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
                chunkFiles.Add(SpillOneChunk(tempDir, buffer, chunkFiles.Count));
                buffer.Clear();
                bufferBytes = 0;
            }
        }
        if (buffer.Count > 0)
            chunkFiles.Add(SpillOneChunk(tempDir, buffer, chunkFiles.Count));

        return globalIdx;
    }

    /// <summary>
    /// Spill one in-memory buffer of <c>(bytes, inputIdx)</c> records as a sorted chunk
    /// file under <paramref name="tempDir"/>. Returns the path. Made <c>internal</c> so
    /// <see cref="SortedAtomBulkBuilder"/> can spill incrementally during <c>AddTriple</c>
    /// (ADR-034 Phase 1B-5e streaming-input fix).
    /// </summary>
    internal static string SpillOneChunk(string tempDir, List<(byte[] Bytes, long InputIdx)> buffer, int chunkIndex)
    {
        // Sort by UTF-8 byte order; ties broken by input index for stable output.
        buffer.Sort((a, b) =>
        {
            int cmp = a.Bytes.AsSpan().SequenceCompareTo(b.Bytes);
            if (cmp != 0) return cmp;
            return a.InputIdx.CompareTo(b.InputIdx);
        });

        var path = Path.Combine(tempDir, $"chunk-{chunkIndex:D6}.bin");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
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
        }
        return path;
    }

    /// <summary>
    /// Pass-2 merge over already-spilled chunk files. K-way merge with dedup; assigns
    /// dense atom IDs in alphabetical sort order; writes <c>{base}.atoms</c> + <c>{base}.offsets</c>.
    /// Made <c>internal</c> so <see cref="SortedAtomBulkBuilder"/> can call it directly with
    /// chunks accumulated during streaming <c>AddTriple</c> (ADR-034 Phase 1B-5e).
    /// </summary>
    internal static SortedAtomStoreBuilder.BuildResult MergeAndWrite(
        string baseFilePath, List<string> chunkFiles, long inputCount,
        ExternalSorter<ResolveRecord, ResolveRecordChunkSorter>? resolveSorter)
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
        var dataPath = baseFilePath + ".atoms";
        var offsetsPath = baseFilePath + ".offsets";

        // Open one ChunkReader per chunk file. The priority queue holds the front
        // record from each chunk; pop, emit/dedupe, advance the popped chunk's reader.
        // ChunkReaders share a bounded LRU pool of FileStream handles so total open-FD
        // count stays under the OS ulimit even with 10K+ chunks (21.3B Wikidata scale).
        using var streamPool = new BoundedFileStreamPool(MergeFileStreamPoolSize);
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
                    resolveSorter!.Add(new ResolveRecord(rec.InputIdx, atomCount));
                else
                    assigned[checked((int)rec.InputIdx)] = atomCount;

                if (readers[readerIdx].MoveNext())
                    pq.Enqueue(readerIdx, new ChunkPriorityKey(readers[readerIdx].Current.Bytes, readerIdx));
            }

            dataFs.Flush(flushToDisk: true);
            offsetsFs.Flush(flushToDisk: true);
        }
        finally
        {
            foreach (var r in readers) r.Dispose();
        }

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
