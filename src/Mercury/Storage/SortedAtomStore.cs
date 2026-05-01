using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using SkyOmega.Mercury.Abstractions;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// Read-side <see cref="IAtomStore"/> backed by a sorted vocabulary built via external
/// merge-sort (ADR-034). Designed for the Reference profile's dump-sourced read-after-bulk
/// semantic — no hash table, no rehash drift, dense sequential atom IDs (1..N), binary
/// search lookup. The substrate-level alternative to <see cref="HashAtomStore"/> when
/// the workload is "load once, query many times."
/// </summary>
/// <remarks>
/// <para>
/// Storage layout (two memory-mapped files):
/// </para>
/// <list type="bullet">
///   <item>
///     <c>{base}.atoms</c> — concatenated UTF-8 strings in <b>sorted byte order</b>.
///     No delimiters; boundaries come from the offsets file.
///   </item>
///   <item>
///     <c>{base}.offsets</c> — <c>long[N+1]</c> array. <c>offsets[i]</c> is the byte position
///     in <c>.atoms</c> where atom ID <c>i+1</c> begins. <c>offsets[N]</c> is the total
///     length of <c>.atoms</c> (sentinel). This makes <c>GetAtomSpan(id)</c> a single
///     subtraction: <c>offsets[id] - offsets[id-1]</c>.
///   </item>
/// </list>
/// <para>
/// Atom ID 0 is reserved as the "no atom" sentinel. Real atoms are 1..N. ADR-034
/// Decision 5: dense sequential IDs unblock the bit-packed-atom-ids follow-on.
/// </para>
/// <para>
/// Phase 1B-1 (this commit) ships the read-side. Write operations (<see cref="Intern"/>,
/// <see cref="InternUtf8"/>, <see cref="Clear"/>) throw <see cref="NotSupportedException"/>;
/// stores are populated externally via the builder API (Phase 1B-2). Phase 1B-3 wires
/// QuadStore's Reference bulk-load path to use the builder, completing the pipeline.
/// </para>
/// </remarks>
internal sealed unsafe class SortedAtomStore : IAtomStore
{
    private readonly string _baseFilePath;
    private readonly string _dataPath;
    private readonly string _offsetsPath;

    private FileStream? _dataFile;
    private MemoryMappedFile? _dataMap;
    private MemoryMappedViewAccessor? _dataAccessor;
    private byte* _dataPtr;
    private long _dataLength;

    private FileStream? _offsetsFile;
    private MemoryMappedFile? _offsetsMap;
    private MemoryMappedViewAccessor? _offsetsAccessor;
    private long* _offsetsPtr;
    private long _atomCount;

    private long _totalBytes;
    private bool _disposed;

    private static readonly Encoding Utf8 = Encoding.UTF8;

    /// <summary>
    /// Open an existing sorted-vocabulary store at <paramref name="baseFilePath"/>.
    /// Both <c>{base}.atoms</c> and <c>{base}.offsets</c> must exist; missing files
    /// throw <see cref="FileNotFoundException"/>.
    /// </summary>
    public SortedAtomStore(string baseFilePath)
    {
        _baseFilePath = baseFilePath ?? throw new ArgumentNullException(nameof(baseFilePath));
        _dataPath = baseFilePath + ".atoms";
        _offsetsPath = baseFilePath + ".offsets";

        if (!File.Exists(_dataPath))
            throw new FileNotFoundException($"SortedAtomStore data file not found: {_dataPath}");
        if (!File.Exists(_offsetsPath))
            throw new FileNotFoundException($"SortedAtomStore offsets file not found: {_offsetsPath}");

        OpenMappedFiles();
    }

    private void OpenMappedFiles()
    {
        _dataFile = new FileStream(_dataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _dataLength = _dataFile.Length;
        if (_dataLength > 0)
        {
            _dataMap = MemoryMappedFile.CreateFromFile(_dataFile, mapName: null, capacity: _dataLength,
                MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
            _dataAccessor = _dataMap.CreateViewAccessor(0, _dataLength, MemoryMappedFileAccess.Read);
            byte* ptr = null;
            _dataAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            _dataPtr = ptr;
        }

        _offsetsFile = new FileStream(_offsetsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long offsetsLen = _offsetsFile.Length;
        if (offsetsLen < sizeof(long))
            throw new InvalidDataException($"SortedAtomStore offsets file too small: {offsetsLen} bytes");
        if (offsetsLen % sizeof(long) != 0)
            throw new InvalidDataException($"SortedAtomStore offsets file not int64-aligned: {offsetsLen} bytes");

        _offsetsMap = MemoryMappedFile.CreateFromFile(_offsetsFile, mapName: null, capacity: offsetsLen,
            MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
        _offsetsAccessor = _offsetsMap.CreateViewAccessor(0, offsetsLen, MemoryMappedFileAccess.Read);
        byte* offBytePtr = null;
        _offsetsAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref offBytePtr);
        _offsetsPtr = (long*)offBytePtr;

        // Total entries = offsetsLen / 8. Layout: offsets[0..N] where offsets[i] = byte
        // start of atom (i+1), and offsets[N] = total bytes (sentinel). So atom count =
        // entries - 1.
        long entries = offsetsLen / sizeof(long);
        _atomCount = entries - 1;
        if (_atomCount < 0) _atomCount = 0;

        // Sanity: offsets[N] must equal data file length.
        if (_atomCount > 0)
        {
            long sentinel = _offsetsPtr[_atomCount];
            if (sentinel != _dataLength)
                throw new InvalidDataException(
                    $"SortedAtomStore: offsets sentinel ({sentinel}) does not match data length ({_dataLength})");
            // ADR-034 Round 2 prefix compression: each atom entry has a 1-byte prefix_len
            // header; subtract atomCount to get the atom-content bytes (the value the
            // pre-compression API contract returned and tests assert against).
            _totalBytes = sentinel - _atomCount;
        }
    }

    public long AtomCount => _atomCount;

    public IObservabilityListener? ObservabilityListener { get; set; }

    /// <summary>
    /// Get the UTF-8 bytes for an atom ID. ID 0 returns empty (the "no atom" sentinel).
    /// IDs in [1, AtomCount] resolve in O(1) via two offset reads + a span construction.
    /// </summary>
    /// <summary>
    /// Return the UTF-8 bytes of the atom with the given ID. ADR-034 Round 2 prefix-compressed
    /// format: each entry's first byte is <c>prefix_len</c>; if zero, the rest of the entry
    /// is the full atom (anchor); if non-zero, the atom is reconstructed from its predecessor
    /// in sort order by walking back to the nearest anchor and concatenating up to this ID.
    /// </summary>
    /// <remarks>
    /// <b>Span lifetime contract:</b> for ANCHOR atoms (every Nth ID), the returned span
    /// points directly into the mmap'd file (zero-copy). For COMPRESSED atoms, the span
    /// points into a thread-local reconstruction buffer; <b>the span is invalidated by the
    /// next <see cref="GetAtomSpan"/> call on the same thread.</b> Callers must consume
    /// the span (copy / compare / encode) before the next call. All in-tree consumers
    /// satisfy this — the binary search in <see cref="GetAtomIdUtf8"/> compares each probe
    /// before the next call, and downstream callers (<see cref="GetAtomString"/>,
    /// <c>QuadStore</c>'s atom decoders) immediately copy or stringify.
    /// </remarks>
    public ReadOnlySpan<byte> GetAtomSpan(long atomId)
    {
        ThrowIfDisposed();
        if (atomId < 1 || atomId > _atomCount) return ReadOnlySpan<byte>.Empty;

        long start = _offsetsPtr[atomId - 1];
        long end = _offsetsPtr[atomId];
        long entryLen = end - start;
        if (entryLen <= 0) return ReadOnlySpan<byte>.Empty;

        // Entry layout: 1 byte prefix_len + suffix bytes (or full bytes for anchor).
        byte prefixLen = _dataPtr[start];
        int suffixLen = (int)(entryLen - 1);

        if (prefixLen == 0)
        {
            // Anchor atom — zero-copy span over mmap.
            return new ReadOnlySpan<byte>(_dataPtr + start + 1, suffixLen);
        }

        // Compressed atom — walk back to the nearest anchor and reconstruct.
        return ReconstructCompressed(atomId);
    }

    [ThreadStatic] private static byte[]? _reconstructBuffer;

    /// <summary>
    /// Walk back to the nearest anchor (atom ID with <c>(id-1) % AnchorInterval == 0</c>),
    /// then reconstruct each compressed atom forward up to <paramref name="targetId"/> by
    /// concatenating its predecessor's prefix with its own suffix. Returns a span over
    /// the thread-local reconstruction buffer.
    /// </summary>
    private ReadOnlySpan<byte> ReconstructCompressed(long targetId)
    {
        long anchorId = ((targetId - 1) / SortedAtomStoreExternalBuilder.PrefixCompressionAnchorInterval)
                        * SortedAtomStoreExternalBuilder.PrefixCompressionAnchorInterval + 1;

        // Anchor entry: 1 byte (prefix_len=0) + full atom bytes.
        long anchorStart = _offsetsPtr[anchorId - 1];
        long anchorEnd = _offsetsPtr[anchorId];
        int anchorLen = (int)(anchorEnd - anchorStart - 1);

        var buffer = _reconstructBuffer;
        if (buffer is null || buffer.Length < 4096)
        {
            buffer = new byte[4096];
            _reconstructBuffer = buffer;
        }

        // Copy anchor bytes into buffer.
        EnsureBufferSize(ref buffer, anchorLen);
        new ReadOnlySpan<byte>(_dataPtr + anchorStart + 1, anchorLen).CopyTo(buffer);
        int currentLen = anchorLen;

        // Walk forward, applying each compressed entry's prefix + suffix.
        for (long id = anchorId + 1; id <= targetId; id++)
        {
            long s = _offsetsPtr[id - 1];
            long e = _offsetsPtr[id];
            byte prefixLen = _dataPtr[s];
            int suffixLen = (int)(e - s - 1);
            int newLen = prefixLen + suffixLen;
            EnsureBufferSize(ref buffer, newLen);
            if (suffixLen > 0)
                new ReadOnlySpan<byte>(_dataPtr + s + 1, suffixLen).CopyTo(buffer.AsSpan(prefixLen));
            currentLen = newLen;
        }

        return buffer.AsSpan(0, currentLen);
    }

    private void EnsureBufferSize(ref byte[] buffer, int required)
    {
        if (buffer.Length >= required) return;
        int newSize = Math.Max(buffer.Length * 2, required);
        var grown = new byte[newSize];
        buffer.AsSpan(0, buffer.Length).CopyTo(grown);
        buffer = grown;
        _reconstructBuffer = grown;
    }

    public string GetAtomString(long atomId)
    {
        var span = GetAtomSpan(atomId);
        if (span.IsEmpty) return string.Empty;
        return Utf8.GetString(span);
    }

    public long GetAtomIdUtf8(ReadOnlySpan<byte> utf8Value)
    {
        ThrowIfDisposed();
        if (utf8Value.IsEmpty) return 0;
        if (_atomCount == 0) return 0;

        // Standard binary search on sorted UTF-8 byte order. log2(4 B) ≈ 32 probes
        // at the Wikidata-scale upper bound; each probe is one offsets read + one
        // SequenceCompareTo over the candidate atom's bytes.
        long lo = 1;
        long hi = _atomCount;
        while (lo <= hi)
        {
            long mid = lo + ((hi - lo) >> 1);
            var midSpan = GetAtomSpanUnchecked(mid);
            int cmp = midSpan.SequenceCompareTo(utf8Value);
            if (cmp == 0) return mid;
            if (cmp < 0) lo = mid + 1;
            else hi = mid - 1;
        }
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<byte> GetAtomSpanUnchecked(long atomId)
    {
        // Same prefix-compression handling as GetAtomSpan; kept inline for the binary
        // search hot path. Anchor: zero-copy; compressed: thread-local buffer.
        long start = _offsetsPtr[atomId - 1];
        long end = _offsetsPtr[atomId];
        byte prefixLen = _dataPtr[start];
        int suffixLen = (int)(end - start - 1);
        if (prefixLen == 0)
            return new ReadOnlySpan<byte>(_dataPtr + start + 1, suffixLen);
        return ReconstructCompressed(atomId);
    }

    public long GetAtomId(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty) return 0;
        // Encode to UTF-8 with a stack buffer when the value is small enough; otherwise
        // a one-shot heap allocation. The query hot path on Reference profile pays for
        // the encoding here once per atom string the parser produces, which is amortized
        // against the (much larger) cost of binary search across a billion-atom vocabulary.
        int byteCount = Utf8.GetByteCount(value);
        if (byteCount <= 1024)
        {
            Span<byte> stackBuf = stackalloc byte[byteCount];
            Utf8.GetBytes(value, stackBuf);
            return GetAtomIdUtf8(stackBuf);
        }
        else
        {
            byte[] heap = new byte[byteCount];
            Utf8.GetBytes(value, heap);
            return GetAtomIdUtf8(heap);
        }
    }

    public long Intern(ReadOnlySpan<char> value)
    {
        // Phase 1B-1: read-only. Returns existing IDs but cannot create new ones.
        // The caller normally goes through QuadStore which is profile-dispatched; on a
        // SortedAtomStore-backed Reference store, the bulk-load path (forthcoming in
        // Phase 1B-3) uses the builder API instead of this surface, so a write
        // attempting to land here is a programming error.
        long id = GetAtomId(value);
        if (id != 0) return id;
        throw new NotSupportedException(
            "SortedAtomStore is read-only; new atoms cannot be added at runtime. " +
            "Use SortedAtomStoreBuilder to create the store.");
    }

    public long InternUtf8(ReadOnlySpan<byte> utf8Value)
    {
        long id = GetAtomIdUtf8(utf8Value);
        if (id != 0) return id;
        throw new NotSupportedException(
            "SortedAtomStore is read-only; new atoms cannot be added at runtime. " +
            "Use SortedAtomStoreBuilder to create the store.");
    }

    public (long AtomCount, long TotalBytes, double AvgLength) GetStatistics()
    {
        var avg = _atomCount > 0 ? (double)_totalBytes / _atomCount : 0;
        return (_atomCount, _totalBytes, avg);
    }

    public void Flush()
    {
        // Read-only mapped file — no in-memory state to flush. Builder writes are durable
        // via FileStream.Flush() in the build path.
    }

    public void Clear()
    {
        // ADR-034 Decision 7: SortedAtomStore stores are single-bulk-load. Clear is not
        // supported; recreate the store to discard contents.
        throw new NotSupportedException(
            "SortedAtomStore is read-only after build. To replace contents, recreate the store.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_dataPtr != null && _dataAccessor is not null)
        {
            _dataAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _dataPtr = null;
        }
        _dataAccessor?.Dispose();
        _dataMap?.Dispose();
        _dataFile?.Dispose();

        if (_offsetsPtr != null && _offsetsAccessor is not null)
        {
            _offsetsAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _offsetsPtr = null;
        }
        _offsetsAccessor?.Dispose();
        _offsetsMap?.Dispose();
        _offsetsFile?.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SortedAtomStore));
    }
}
