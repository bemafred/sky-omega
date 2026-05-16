using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.MemoryMappedFiles;
using SkyOmega.Bcl.Collections;

namespace SkyOmega.Mercury.Storage.Mphf;

/// <summary>
/// Translation table mapping <c>mphf_pos → sorted_pos</c>. ADR-039: required because
/// BBHash assigns positions in [0, N) by its own hash algorithm, not by the input
/// key's sorted order. The table lets the lookup path resolve from MPHF output to
/// our atom ID.
/// </summary>
/// <remarks>
/// <para>
/// <b>File format:</b> a contiguous packed array of <c>uint32</c> (little-endian),
/// one entry per MPHF position. Entry <c>i</c> = sorted-position atom ID for the
/// key whose <c>bbhash() == i</c>. <c>uint32</c> is sufficient since
/// <c>sorted_pos ≤ N ≤ 4 B ≤ 2³²</c>. Total file size = <c>4 × N</c> bytes.
/// </para>
/// <para>
/// <b>Header:</b> 16-byte preamble — magic + version + entry-count + reserved.
/// File-format magic 0x49445800 ("IDX\0"). Version bumped on incompatible change.
/// </para>
/// <para>
/// <b>Read path:</b> mmap'd at store-open via
/// <see cref="MemoryMappedFile.CreateFromFile(string, FileMode, string)"/>. Lookup
/// is a single mmap'd uint32 read at <c>16 + 4 × mphf_pos</c>. Cold pages stay on
/// disk; hot pages occupy RAM. On a 128 GB host, the 16 GB table at 4 B atoms can
/// fit fully resident; on smaller hosts only the working set occupies memory.
/// </para>
/// </remarks>
internal sealed class MphfTranslationTable : IDisposable
{
    public const uint Magic = 0x49445800u;
    public const uint Version = 1;
    public const int HeaderBytes = 16;

    private readonly MemoryMappedFile _mmap;
    private readonly MemoryMappedViewAccessor _view;
    public long EntryCount { get; }

    private MphfTranslationTable(MemoryMappedFile mmap, MemoryMappedViewAccessor view, long entryCount)
    {
        _mmap = mmap;
        _view = view;
        EntryCount = entryCount;
    }

    /// <summary>
    /// Open an existing <c>atoms.idx</c> file via memory-mapping. Validates magic + version.
    /// </summary>
    public static MphfTranslationTable Open(string path)
    {
        var mmap = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null);
        var view = mmap.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        uint magic = view.ReadUInt32(0);
        // mmap reads are native-endian; convert to LE if needed. On Apple Silicon /
        // x64, native is LE; magic was stored big-endian intentionally so we can detect
        // wrong-endianness platforms.
        magic = BinaryPrimitives.ReverseEndianness(magic);
        if (magic != Magic)
        {
            view.Dispose();
            mmap.Dispose();
            throw new InvalidDataException($"atoms.idx: bad magic 0x{magic:X8} (expected 0x{Magic:X8})");
        }
        uint version = view.ReadUInt32(4);
        if (version != Version)
        {
            view.Dispose();
            mmap.Dispose();
            throw new InvalidDataException($"atoms.idx: unsupported version {version}");
        }
        long entryCount = view.ReadInt64(8);
        return new MphfTranslationTable(mmap, view, entryCount);
    }

    /// <summary>
    /// Lookup the sorted-position atom ID for an MPHF position.
    /// O(1) — single mmap'd uint32 read.
    /// </summary>
    public long Get(long mphfPos)
    {
        if (mphfPos < 0 || mphfPos >= EntryCount)
            throw new ArgumentOutOfRangeException(nameof(mphfPos), $"mphfPos={mphfPos} out of range [0, {EntryCount})");
        return _view.ReadUInt32(HeaderBytes + 4 * mphfPos);
    }

    /// <summary>
    /// Write a translation table to disk. Caller provides the mphf_pos → sorted_pos
    /// mapping as a <see cref="ChunkedArray{T}"/> of <c>long</c> (sorted-positions fit
    /// in uint32 at ≤ 4 B atoms). ChunkedArray is required because at production scale
    /// (≥ 2.15 B atoms) BCL <c>long[]</c> exceeds the int32 element-count cap.
    /// </summary>
    public static void WriteTo(string path, ChunkedArray<long> mphfPosToSortedPos)
    {
        if (mphfPosToSortedPos is null) throw new ArgumentNullException(nameof(mphfPosToSortedPos));
        long n = mphfPosToSortedPos.Length;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);

        Span<byte> hdr = stackalloc byte[HeaderBytes];
        BinaryPrimitives.WriteUInt32BigEndian(hdr.Slice(0, 4), Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(4, 4), Version);
        BinaryPrimitives.WriteInt64LittleEndian(hdr.Slice(8, 8), n);
        fs.Write(hdr);

        // Write entries in 1 MB batches to amortize I/O.
        const int BatchSize = 256 * 1024;  // 256K entries × 4 bytes = 1 MB
        var batch = new byte[BatchSize * 4];
        for (long offset = 0; offset < n; offset += BatchSize)
        {
            long end = Math.Min(offset + BatchSize, n);
            int len = (int)(end - offset);
            for (int i = 0; i < len; i++)
            {
                long sortedPos = mphfPosToSortedPos[offset + i];
                if (sortedPos < 0 || sortedPos > uint.MaxValue)
                    throw new InvalidOperationException(
                        $"Translation table entry {offset + i} out of range for uint32: {sortedPos}");
                BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(i * 4, 4), (uint)sortedPos);
            }
            fs.Write(batch, 0, len * 4);
        }
    }

    public void Dispose()
    {
        _view?.Dispose();
        _mmap?.Dispose();
    }

    /// <summary>
    /// ADR-042 Part 2: create an <c>atoms.idx</c> file at full size and mmap it for
    /// streaming write. The returned <see cref="MphfTranslationWriter"/> exposes an
    /// <see cref="IMphfTranslationSink"/> that <see cref="BBHashBuilder"/> writes
    /// each <c>(mphf_pos, input_idx)</c> pair into directly. Eliminates the 32 GB
    /// in-memory <see cref="ChunkedArray{T}"/> at N=4 B atoms — the kernel's page
    /// cache handles working-set residency under memory pressure.
    /// </summary>
    public static MphfTranslationWriter CreateForWriting(string path, long entryCount)
    {
        if (entryCount < 0) throw new ArgumentOutOfRangeException(nameof(entryCount));
        long fileLength = HeaderBytes + 4L * entryCount;

        // Create the file at full size first via a regular FileStream (SetLength).
        // Cannot SetLength via the mmap once it's opened; do it up-front.
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.None))
        {
            fs.SetLength(fileLength);
            // Write the header now so the mmap doesn't have to.
            Span<byte> hdr = stackalloc byte[HeaderBytes];
            BinaryPrimitives.WriteUInt32BigEndian(hdr.Slice(0, 4), Magic);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(4, 4), Version);
            BinaryPrimitives.WriteInt64LittleEndian(hdr.Slice(8, 8), entryCount);
            fs.Write(hdr);
            fs.Flush(flushToDisk: false);
        }

        var mmap = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null,
            capacity: 0, MemoryMappedFileAccess.ReadWrite);
        var view = mmap.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
        return new MphfTranslationWriter(mmap, view, entryCount);
    }
}

/// <summary>
/// ADR-042 Part 2: mmap-backed streaming writer for <c>atoms.idx</c>. Exposes an
/// <see cref="IMphfTranslationSink"/> for <see cref="BBHashBuilder.Build(long, int, GetKeyDelegate, IMphfTranslationSink, IObservabilityListener?)"/>
/// to write each <c>(mphf_pos, input_idx)</c> pair into directly. Dispose flushes
/// the mmap and releases the file handle.
/// </summary>
internal sealed class MphfTranslationWriter : IDisposable
{
    private readonly MemoryMappedFile _mmap;
    private readonly MemoryMappedViewAccessor _view;
    private readonly long _entryCount;
    private readonly Sink _sink;
    private bool _disposed;

    internal MphfTranslationWriter(MemoryMappedFile mmap, MemoryMappedViewAccessor view, long entryCount)
    {
        _mmap = mmap;
        _view = view;
        _entryCount = entryCount;
        _sink = new Sink(view, entryCount);
    }

    /// <summary>The sink that <see cref="BBHashBuilder"/> writes translation entries into.</summary>
    public IMphfTranslationSink AsSink() => _sink;

    /// <summary>Number of translation entries this writer was sized for.</summary>
    public long EntryCount => _entryCount;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.Flush();
        _view.Dispose();
        _mmap.Dispose();
    }

    /// <summary>
    /// Inner sink implementation. Writes each translation entry as a uint32 to the
    /// mmap'd view at offset <c>HeaderBytes + 4 × mphfPos</c>. Validates that
    /// <paramref name="inputIdx"/> fits in uint32 — bounded by atom count which is
    /// ≤ 4 B ≤ 2³² at any practical scale.
    /// </summary>
    private sealed class Sink : IMphfTranslationSink
    {
        private readonly MemoryMappedViewAccessor _view;
        private readonly long _entryCount;
        public Sink(MemoryMappedViewAccessor view, long entryCount) { _view = view; _entryCount = entryCount; }
        public void Set(long mphfPos, long inputIdx)
        {
            if ((ulong)mphfPos >= (ulong)_entryCount)
                throw new ArgumentOutOfRangeException(nameof(mphfPos), $"mphfPos={mphfPos} out of range [0, {_entryCount})");
            if (inputIdx < 0 || inputIdx > uint.MaxValue)
                throw new InvalidOperationException(
                    $"Translation table entry {mphfPos} out of range for uint32: {inputIdx}");
            _view.Write(MphfTranslationTable.HeaderBytes + 4L * mphfPos, (uint)inputIdx);
        }
    }
}
