using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

namespace SkyOmega.Mercury.Runtime;

/// <summary>
/// A single B+Tree's persistent home on disk. One memory-mapped file organized into
/// 16 KB pages: page 0 is reserved for a 32-byte metadata header (magic + RootPageId
/// + NextPageId + EntryCount); pages 1+ hold the tree's leaf and internal nodes.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is:</b> the managed file (FileStream + MemoryMappedFile + accessor
/// + pinned base pointer), plus the lifecycle that anchors a B+Tree (root, next-free
/// page, entry count, magic).
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
/// </remarks>
public sealed unsafe class BTreeFile : IDisposable
{
    /// <summary>B+Tree page size: 16 KB. Same across all four ADR-029 profiles.</summary>
    public const int PageSize = 16384;

    /// <summary>
    /// Metadata header bytes (offset 0 of the file): RootPageId (8) + NextPageId (8) +
    /// EntryCount (8) + MagicNumber (8) = 32 B. Page 0 is reserved for this header
    /// even though only 32 bytes are used; pages 1+ are tree pages.
    /// </summary>
    public const int HeaderBytes = 32;

    private readonly long _magicNumber;
    private readonly FileStream _fileStream;
    private readonly MemoryMappedFile _mmapFile;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly PageCache _pageCache;
    private byte* _basePtr;
    private long _fileCapacity;
    private long _rootPageId;
    private long _nextPageId;
    private long _entryCount;
    private bool _deferMsync;
    private bool _disposed;

    /// <summary>Raw base pointer to the mmap'd region. Consumers use this for page-pointer math.</summary>
    public byte* BasePtr => _basePtr;

    /// <summary>Current root page id of the B+Tree. Consumer updates after root-split.</summary>
    public long RootPageId { get => _rootPageId; set => _rootPageId = value; }

    /// <summary>Next page id that <see cref="AllocatePage"/> will return.</summary>
    public long NextPageId => _nextPageId;

    /// <summary>Number of entries (quads / triples / keys) the B+Tree contains. Consumer increments via <see cref="IncrementEntryCount"/>.</summary>
    public long EntryCount => _entryCount;

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
    /// <param name="pageCacheCapacity">LRU page cache size (default 10,000 pages).</param>
    /// <param name="initRoot">Called once when the file is freshly created (no prior magic).
    /// The consumer writes its empty root-page header at <c>GetPagePointer&lt;T&gt;(1)</c>;
    /// the metadata header (RootPageId=1, NextPageId=2, EntryCount=0) is written
    /// before the callback fires. Not called when opening an existing file.</param>
    public BTreeFile(string filePath, long initialSizeBytes, bool bulkMode, long bulkModeFloor, long magicNumber, Action<BTreeFile>? initRoot = null, int pageCacheCapacity = 10_000)
    {
        if (filePath is null) throw new ArgumentNullException(nameof(filePath));
        if (initialSizeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(initialSizeBytes));
        if (bulkModeFloor <= 0) throw new ArgumentOutOfRangeException(nameof(bulkModeFloor));
        if (pageCacheCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(pageCacheCapacity));

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
            _fileStream.SetLength(actualInitialSize);
        _fileCapacity = _fileStream.Length;

        _mmapFile = MemoryMappedFile.CreateFromFile(
            _fileStream,
            mapName: null,
            capacity: 0,
            MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None,
            leaveOpen: false);

        _accessor = _mmapFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _basePtr);

        _pageCache = new PageCache(pageCacheCapacity);

        LoadMetadata(initRoot);
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
            SaveMetadata();
            initRoot?.Invoke(this);
        }
    }

    /// <summary>
    /// Persist the metadata header (RootPageId, NextPageId, EntryCount, MagicNumber)
    /// at file offset 0. Honors <see cref="DeferMsync"/>: skips the <c>msync</c> in
    /// bulk mode for amortization at <see cref="Flush"/> time.
    /// </summary>
    public void SaveMetadata()
    {
        _accessor.Write(0, _rootPageId);
        _accessor.Write(8, _nextPageId);
        _accessor.Write(16, _entryCount);
        _accessor.Write(24, _magicNumber);
        if (_deferMsync) return;
        _accessor.Flush();
    }

    /// <summary>
    /// Allocate a new B+Tree page. Returns the page id; the consumer's tree algorithm
    /// initializes the page's content via <see cref="GetPagePointer{T}"/>. Grows the
    /// file (doubles capacity, minimum required) when the new page exceeds the
    /// currently-allocated extent. Persists metadata via <see cref="SaveMetadata"/>.
    /// </summary>
    public long AllocatePage()
    {
        var pageId = System.Threading.Interlocked.Increment(ref _nextPageId) - 1;
        var requiredSize = (pageId + 1) * PageSize;
        if (requiredSize > _fileCapacity)
        {
            lock (_fileStream)
            {
                if (requiredSize > _fileCapacity)
                {
                    var newSize = Math.Max(_fileCapacity * 2, requiredSize);
                    _fileStream.SetLength(newSize);
                    _fileCapacity = newSize;
                }
            }
        }
        SaveMetadata();
        return pageId;
    }

    /// <summary>
    /// Get a typed pointer to a page. The page-cache layer makes repeated accesses
    /// to hot pages O(1) at the cost of an unmanaged byte* per cached entry; the
    /// pointer math (<c>_basePtr + pageId * PageSize</c>) is constant either way.
    /// </summary>
    /// <typeparam name="T">The consumer's page-header struct type (e.g., <c>TemporalBTreePage</c>).</typeparam>
    /// <param name="pageId">Page id (≥ 1 for tree pages; page 0 is the metadata header).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T* GetPagePointer<T>(long pageId) where T : unmanaged
    {
        if (_pageCache.TryGet(pageId, out var cached))
            return (T*)cached;
        var ptr = _basePtr + pageId * PageSize;
        _pageCache.Add(pageId, ptr);
        return (T*)ptr;
    }

    /// <summary>
    /// Issue an <c>msync</c> on the whole mapped region — unless <see cref="DeferMsync"/>
    /// is true (bulk mode), in which case the call is a no-op. Consumers call this
    /// after every page write in cognitive mode and once at end-of-bulk-load.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void FlushPage()
    {
        if (_deferMsync) return;
        _accessor.Flush();
    }

    /// <summary>Unconditional <c>msync</c> — flushes pending writes regardless of <see cref="DeferMsync"/>. Called at end-of-bulk-load.</summary>
    public void Flush() => _accessor.Flush();

    /// <summary>
    /// Increment <see cref="EntryCount"/> atomically. Consumers call this once per
    /// successful insert. Returns the new count.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long IncrementEntryCount() => System.Threading.Interlocked.Increment(ref _entryCount);

    /// <summary>
    /// Reset the tree to empty state. Used by <c>Clear()</c> on the consumer side.
    /// File size is preserved; the page cache is cleared (stale pointers).
    /// Consumer is responsible for re-initializing page 1 (the new empty root).
    /// </summary>
    public void Reset()
    {
        _pageCache.Clear();
        _rootPageId = 1;
        _nextPageId = 2;
        _entryCount = 0;
    }

    /// <summary>Persist metadata one last time, release the pinned pointer, dispose mmap + file.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SaveMetadata();
        if (_basePtr != null)
        {
            _accessor?.SafeMemoryMappedViewHandle.ReleasePointer();
            _basePtr = null;
        }
        _accessor?.Dispose();
        _mmapFile?.Dispose();
        _fileStream?.Dispose();
        _pageCache?.Dispose();
    }
}
