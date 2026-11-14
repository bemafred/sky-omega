using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SkyOmega.Mercury.Runtime;

/// <summary>
/// A single B+Tree's persistent home on disk. One memory-mapped file organized into
/// 16 KB pages: page 0 is reserved for the metadata header (RootPageId + NextPageId
/// + EntryCount + magic, then CountTxId); pages 1+ hold the tree's leaf and internal nodes.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is:</b> the managed file (FileStream + its memory mappings), plus the
/// lifecycle that anchors a B+Tree (root, next-free page, entry count, magic).
/// </para>
/// <para>
/// <b>Growth:</b> the file is mapped in extents. The first extent is the whole file as it
/// is when it is opened. When the tree needs a page past the end, the file is lengthened
/// and the added part is mapped as one more extent. An extent is never unmapped or moved
/// before <see cref="Dispose"/>, so a page pointer stays valid until then: a tree that
/// splits a page may go on using the pointers it held when it asked for the new page.
/// </para>
/// <para>
/// <b>Threads:</b> <see cref="GetPagePointer{T}"/> may be called from any number of threads
/// at once, also while another thread allocates a page. It reads no state that a reader
/// changes. One thread at a time changes the tree; the consumer's lock sees to that.
/// </para>
/// <para>
/// <b>What this is not:</b> the B+Tree algorithm. Consumers (concrete <c>*QuadIndex</c>
/// classes in <c>SkyOmega.Mercury.Storage</c>) define their own key layout, entry
/// layout, comparison, mutation semantics, and split logic. They use this class for
/// the file/mmap/page-allocation/metadata mechanics common to all four ADR-029
/// profile indexes.
/// </para>
/// <para>
/// <b>Cross-profile mismatch detection:</b> each consumer supplies its own
/// <see cref="MagicNumber"/> at construction. <see cref="LoadMetadata"/> refuses to
/// open a file whose stored magic doesn't match — opening a Cognitive store as
/// Reference (or any other mismatch) fails loudly rather than corrupting through
/// mismatched key layouts.
/// </para>
/// <para>
/// <b>Durability mode:</b> <see cref="DeferMsync"/> controls whether
/// <see cref="FlushPage"/> and <see cref="SaveMetadata"/> issue per-write
/// <c>msync</c>. Bulk-load defers; Cognitive single-write fsyncs each.
/// </para>
/// <para>
/// <b>Entry count after a crash (ADR-058):</b> the tree structure (root, next page) is
/// saved whenever it changes, so a killed process leaves a tree that can be walked.
/// The entry count is not saved per insert. It is saved by <see cref="SaveMetadata"/>
/// and by <see cref="CommitEntryCount"/>, which also records <see cref="CountTxId"/>:
/// the WAL transaction up to which the saved count is exact. Recovery uses it to count
/// the entries a crashed process inserted after that transaction (their WAL records
/// replay as no-ops, so they would otherwise never be counted).
/// </para>
/// </remarks>
public sealed unsafe class BTreeFile : IDisposable
{
    /// <summary>B+Tree page size: 16 KB. Same across all four ADR-029 profiles.</summary>
    public const int PageSize = 16384;

    /// <summary>
    /// Metadata header bytes (offset 0 of the file): RootPageId (8) + NextPageId (8) +
    /// EntryCount (8) + MagicNumber (8) = 32 B. Page 0 is reserved for this header;
    /// pages 1+ are tree pages. <see cref="CountTxId"/> follows at offset 32, inside the
    /// reserved page (files written before ADR-058 hold 0 there).
    /// </summary>
    public const int HeaderBytes = 32;

    private const int CountTxIdOffset = 32;

    /// <summary>
    /// One mapped part of the file: the pages from <see cref="FirstPageId"/> on, at <see cref="FirstPage"/>.
    /// </summary>
    private sealed class MappedExtent
    {
        public required MemoryMappedFile Mapping { get; init; }
        public required MemoryMappedViewAccessor Accessor { get; init; }
        public required long FirstPageId { get; init; }
        public byte* FirstPage;
    }

    private readonly long _magicNumber;
    private readonly FileStream _fileStream;
    private readonly MemoryMappedViewAccessor _accessor = null!;   // of the first extent, which holds the header
    private readonly Lock _growth = new();
    private volatile MappedExtent[] _extents = [];
    private long _mappedPageCount;
    private long _rootPageId;
    private long _nextPageId;
    private long _entryCount;
    private long _savedEntryCount;
    private long _countTxId;
    private bool _deferMsync;
    private bool _disposed;

    /// <summary>Current root page id of the B+Tree. Consumer updates after root-split.</summary>
    public long RootPageId { get => _rootPageId; set => _rootPageId = value; }

    /// <summary>Next page id that <see cref="AllocatePage"/> will return.</summary>
    public long NextPageId => _nextPageId;

    /// <summary>Number of entries (quads / triples / keys) the B+Tree contains. Consumer increments via <see cref="IncrementEntryCount"/>.</summary>
    public long EntryCount => _entryCount;

    /// <summary>
    /// The WAL transaction up to which the entry count saved on disk is exact (ADR-058).
    /// 0 for files that never had <see cref="CommitEntryCount"/> called.
    /// </summary>
    public long CountTxId => _countTxId;

    /// <summary>Magic number stamped in the file header. Set at construction; unique per consumer.</summary>
    public long MagicNumber => _magicNumber;

    /// <summary>
    /// Toggle whether <see cref="FlushPage"/> and <see cref="SaveMetadata"/> issue
    /// per-operation <c>msync</c>. Bulk-load contracts set this true and call
    /// <see cref="Flush"/> once at the end; cognitive-mode writes keep it false.
    /// </summary>
    public bool DeferMsync { get => _deferMsync; set => _deferMsync = value; }

    /// <summary>
    /// Open or create a B+Tree-format file at <paramref name="filePath"/>. New file
    /// is pre-sized to <paramref name="initialSizeBytes"/>; <paramref name="bulkMode"/>
    /// optionally lifts the initial allocation to <paramref name="bulkModeFloor"/> to
    /// avoid mid-load file growth. <paramref name="magicNumber"/> identifies the
    /// consumer's file format — mismatch on an existing file is a hard error.
    /// </summary>
    /// <param name="filePath">Path on disk.</param>
    /// <param name="initialSizeBytes">Initial file size when creating new. Ignored for existing files.</param>
    /// <param name="bulkMode">When true, opens with <see cref="FileOptions.None"/> (OS write cache batched) and <see cref="DeferMsync"/>=true; lifts initial size to <paramref name="bulkModeFloor"/>. When false, opens with <see cref="FileOptions.WriteThrough"/> for per-op durability.</param>
    /// <param name="bulkModeFloor">Bulk-mode initial-size floor (e.g., 256 GB for Graph, 1 TB for Reference, 1 GB for Minimal). Ignored when <paramref name="bulkMode"/> is false.</param>
    /// <param name="magicNumber">Consumer's file-format identifier. Stored at byte offset 24 of the file; verified on subsequent opens.</param>
    /// <param name="initRoot">Called once when the file is freshly created (no prior magic).
    /// The consumer writes its empty root-page header at <c>GetPagePointer&lt;T&gt;(1)</c>;
    /// the metadata header (RootPageId=1, NextPageId=2, EntryCount=0) is written
    /// before the callback fires. Not called when opening an existing file.</param>
    public BTreeFile(string filePath, long initialSizeBytes, bool bulkMode, long bulkModeFloor, long magicNumber, Action<BTreeFile>? initRoot = null)
    {
        if (filePath is null) throw new ArgumentNullException(nameof(filePath));
        if (initialSizeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(initialSizeBytes));
        if (bulkModeFloor <= 0) throw new ArgumentOutOfRangeException(nameof(bulkModeFloor));

        _magicNumber = magicNumber;
        _deferMsync = bulkMode;

        _fileStream = new FileStream(
            filePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.RandomAccess | (bulkMode ? FileOptions.None : FileOptions.WriteThrough)
        );

        var actualInitialSize = bulkMode
            ? Math.Max(initialSizeBytes, bulkModeFloor)
            : initialSizeBytes;
        if (_fileStream.Length == 0)
            SparseFile.SetLength(_fileStream, actualInitialSize);

        // The first extent: the whole file as it is now. A file whose length is not a whole number of
        // pages ends in a part of a page; that part belongs to no page, and the next extent starts at the
        // page it would have been.
        try
        {
            var first = MapExtent(firstPageId: 0, fileLength: _fileStream.Length);
            _accessor = first.Accessor;
            _extents = [first];
            _mappedPageCount = _fileStream.Length / PageSize;

            LoadMetadata(initRoot);
        }
        catch
        {
            // A file that is refused (another consumer's magic number) or cannot be mapped is closed again.
            Unmap();
            _fileStream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Map the file from page <paramref name="firstPageId"/> to <paramref name="fileLength"/>, which is the
    /// file's length now.
    /// </summary>
    private MappedExtent MapExtent(long firstPageId, long fileLength)
    {
        var mapping = MemoryMappedFile.CreateFromFile(
            _fileStream,
            mapName: null,
            capacity: fileLength,
            MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None,
            leaveOpen: true);
        MemoryMappedViewAccessor? accessor = null;
        try
        {
            var offset = firstPageId * PageSize;
            accessor = mapping.CreateViewAccessor(offset, fileLength - offset, MemoryMappedFileAccess.ReadWrite);
            byte* viewStart = null;
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref viewStart);
            // The view starts at a boundary the operating system chooses, at or before the offset asked for.
            return new MappedExtent
            {
                Mapping = mapping,
                Accessor = accessor,
                FirstPageId = firstPageId,
                FirstPage = viewStart + accessor.PointerOffset
            };
        }
        catch
        {
            accessor?.Dispose();
            mapping.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Read the metadata header at file offset 0. New files get an empty metadata
    /// (RootPageId=1, NextPageId=2, EntryCount=0) and the magic is written.
    /// Existing files with non-matching magic raise <see cref="InvalidDataException"/>.
    /// The consumer is responsible for initializing page 1 (the root leaf) on first open.
    /// </summary>
    /// <remarks>
    /// The consumer detects "first open" by observing <see cref="RootPageId"/>=1 and
    /// <see cref="EntryCount"/>=0 after construction, or by tracking initialization
    /// externally. <c>BTreeFile</c> doesn't write a root page itself because the
    /// page-header struct shape is profile-specific.
    /// </remarks>
    private void LoadMetadata(Action<BTreeFile>? initRoot)
    {
        _accessor.Read(24, out long storedMagic);
        if (storedMagic == _magicNumber)
        {
            _accessor.Read(0, out _rootPageId);
            _accessor.Read(8, out _nextPageId);
            _accessor.Read(16, out _entryCount);
            _accessor.Read(CountTxIdOffset, out _countTxId);
            _savedEntryCount = _entryCount;
        }
        else if (storedMagic != 0)
        {
            throw new InvalidDataException(
                $"BTreeFile magic mismatch at {_fileStream.Name}: stored 0x{storedMagic:X16}, expected 0x{_magicNumber:X16}. " +
                "Cross-profile open is refused by design — reload from source to change profiles.");
        }
        else
        {
            // Fresh file: write the empty-tree metadata, then let the consumer initialize
            // page 1 (root leaf) through its own profile-specific page-header struct.
            _rootPageId = 1;
            _nextPageId = 2;
            _entryCount = 0;
            _countTxId = 0;
            SaveMetadata();
            initRoot?.Invoke(this);
        }
    }

    /// <summary>
    /// Persist the metadata header (RootPageId, NextPageId, EntryCount, MagicNumber)
    /// at file offset 0, with the current entry count. Honors <see cref="DeferMsync"/>:
    /// skips the <c>msync</c> in bulk mode for amortization at <see cref="Flush"/> time.
    /// </summary>
    /// <remarks>
    /// Leaves <see cref="CountTxId"/> as it is. A WAL-backed consumer saves the count
    /// with <see cref="CommitEntryCount"/>, which names the transaction it is exact for.
    /// </remarks>
    public void SaveMetadata()
    {
        _savedEntryCount = _entryCount;
        WriteHeader();
    }

    /// <summary>
    /// Persist the current entry count as exact up to WAL transaction <paramref name="txId"/>
    /// (ADR-058), together with the rest of the header.
    /// </summary>
    /// <param name="txId">The WAL transaction the count is exact for.</param>
    /// <param name="durable">
    /// When false, the header is written to the mapping without an <c>msync</c>. That survives
    /// a killed process. After a power loss the header may show an older count and transaction,
    /// which recovery corrects as long as the WAL still holds the transactions after it, so a
    /// checkpoint must call this with <paramref name="durable"/> true before it truncates the log.
    /// </param>
    public void CommitEntryCount(long txId, bool durable = true)
    {
        _countTxId = txId;
        _savedEntryCount = _entryCount;
        WriteHeader(msync: durable);
    }

    /// <summary>
    /// Take the entry count back to the one last saved with its transaction (ADR-058). A WAL-backed consumer
    /// that closes with a transaction it did not finish applying calls this before <see cref="Dispose"/>,
    /// which saves the count: the saved count then stays exact for <see cref="CountTxId"/>, and recovery
    /// counts the unfinished transaction once.
    /// </summary>
    public void ReturnToSavedEntryCount() => _entryCount = _savedEntryCount;

    /// <summary>
    /// Persist the tree structure (RootPageId, NextPageId) and leave the saved entry count
    /// as it was. Called when the structure changes in the middle of an operation (page
    /// allocation, root split), where the in-memory count may include entries of a
    /// transaction that is not finished (ADR-058).
    /// </summary>
    public void SaveStructure() => WriteHeader();

    private void WriteHeader(bool msync = true)
    {
        _accessor.Write(0, _rootPageId);
        _accessor.Write(8, _nextPageId);
        _accessor.Write(16, _savedEntryCount);
        _accessor.Write(24, _magicNumber);
        _accessor.Write(CountTxIdOffset, _countTxId);
        if (_deferMsync || !msync) return;
        Flush();
    }

    /// <summary>
    /// Allocate a new B+Tree page. Returns the page id; the consumer's tree algorithm
    /// initializes the page's content via <see cref="GetPagePointer{T}"/>. When the page
    /// lies past the end of the file, the file is lengthened (doubled, at least) and the
    /// added part is mapped as one more extent. Persists the structure via
    /// <see cref="SaveStructure"/>.
    /// </summary>
    /// <remarks>
    /// Pointers to pages allocated before stay valid. When the file cannot be lengthened or
    /// mapped, the exception leaves the file as it was: no page id is used up.
    /// </remarks>
    public long AllocatePage()
    {
        lock (_growth)
        {
            var pageId = _nextPageId;
            if (pageId >= _mappedPageCount)
                MapOneMoreExtent(upToPageId: pageId);
            _nextPageId = pageId + 1;
            SaveStructure();
            return pageId;
        }
    }

    private void MapOneMoreExtent(long upToPageId)
    {
        var extents = _extents;
        var lengthNow = _fileStream.Length;
        var lengthNeeded = (upToPageId + 1) * PageSize;
        var newLength = Math.Max(lengthNow * 2, lengthNeeded);
        newLength = (newLength + PageSize - 1) / PageSize * PageSize;

        // The file is lengthened before the added part is mapped, so a mapping never reaches past the
        // end of its file (ADR-020 section 4).
        SparseFile.SetLength(_fileStream, newLength);
        var added = MapExtent(firstPageId: _mappedPageCount, fileLength: newLength);

        var longer = new MappedExtent[extents.Length + 1];
        extents.CopyTo(longer, 0);
        longer[extents.Length] = added;
        _extents = longer;
        _mappedPageCount = newLength / PageSize;
    }

    /// <summary>
    /// Get a typed pointer to a page. The pointer is valid until <see cref="Dispose"/>: a page
    /// is mapped once and never moves. May be called from any number of threads at once.
    /// </summary>
    /// <typeparam name="T">The consumer's page-header struct type (e.g., <c>TemporalBTreePage</c>).</typeparam>
    /// <param name="pageId">Page id (≥ 1 for tree pages; page 0 is the metadata header).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T* GetPagePointer<T>(long pageId) where T : unmanaged
    {
        // The extents are few: the file at least doubles each time it grows. Later extents are the
        // larger ones, so the search starts with the last.
        var extents = _extents;
        for (int i = extents.Length - 1; i > 0; i--)
        {
            var extent = extents[i];
            if (pageId >= extent.FirstPageId)
                return (T*)(extent.FirstPage + (pageId - extent.FirstPageId) * PageSize);
        }
        return (T*)(extents[0].FirstPage + pageId * PageSize);
    }

    /// <summary>
    /// Issue an <c>msync</c> on the whole file — unless <see cref="DeferMsync"/>
    /// is true (bulk mode), in which case the call is a no-op. Consumers call this
    /// after every page write in cognitive mode and once at end-of-bulk-load.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void FlushPage()
    {
        if (_deferMsync) return;
        Flush();
    }

    /// <summary>
    /// Unconditional <c>msync</c> of every extent — flushes pending writes regardless of
    /// <see cref="DeferMsync"/>. Called at end-of-bulk-load.
    /// </summary>
    public void Flush()
    {
        foreach (var extent in _extents)
            extent.Accessor.Flush();
    }

    /// <summary>
    /// Increment <see cref="EntryCount"/> atomically. Consumers call this once per
    /// successful insert. Returns the new count.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long IncrementEntryCount() => System.Threading.Interlocked.Increment(ref _entryCount);

    /// <summary>
    /// Decrement <see cref="EntryCount"/> atomically. Consumers call this once per entry
    /// taken out of the tree. Returns the new count.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long DecrementEntryCount() => System.Threading.Interlocked.Decrement(ref _entryCount);

    /// <summary>
    /// Reset the tree to empty state. Used by <c>Clear()</c> on the consumer side.
    /// File size and mapping are preserved.
    /// Consumer is responsible for re-initializing page 1 (the new empty root).
    /// </summary>
    public void Reset()
    {
        _rootPageId = 1;
        _nextPageId = 2;
        _entryCount = 0;
        _savedEntryCount = 0;
        _countTxId = 0; // the store's WAL is cleared with it, so its TxIds start over
    }

    /// <summary>Persist metadata one last time, unmap every extent, close the file.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SaveMetadata();
        Unmap();
        _fileStream.Dispose();
    }

    private void Unmap()
    {
        foreach (var extent in _extents)
        {
            extent.FirstPage = null;
            extent.Accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            extent.Accessor.Dispose();
            extent.Mapping.Dispose();
        }
    }
}
