using System;
using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace SkyOmega.Mercury.Storage;

/// <summary>
/// B+Tree index for RDF quads (graph + triple) with bitemporal semantics.
///
/// Valid Time (VT): When the fact is true in the real world
/// Transaction Time (TT): When the fact was recorded in the database
/// </summary>
/// <remarks>
/// <para><strong>INTERNAL USE ONLY:</strong> This class is internal because it is an
/// implementation detail of <see cref="QuadStore"/>. Users should access storage
/// through QuadStore's public API, not directly through QuadIndex.</para>
/// </remarks>
internal sealed unsafe class QuadIndex : IDisposable
{
    private const int PageSize = 16384;
    private const int NodeDegree = 185; // (16384 - 32) / 88 bytes per temporal entry

    private readonly FileStream _fileStream;
    private readonly MemoryMappedFile _mmapFile;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly AtomStore _atoms;
    private readonly bool _ownsAtomStore;
    private readonly PageCache _pageCache;
    private readonly KeyComparer _comparer;
    private readonly KeySortOrder _sortOrder;
    private readonly bool _bulkMode;

    // Cached base pointer acquired once during construction (avoids repeated AcquirePointer calls)
    private byte* _basePtr;

    private long _rootPageId;
    private long _nextPageId;
    private long _quadCount;
    // Tracked file capacity. Reading FileStream.Length on every page allocation
    // issued fstat() — up to 0.5% of bulk-load time on macOS. Mirror SetLength
    // so the hot path in AllocatePage stays in process.
    private long _fileCapacity;

    /// <summary>
    /// Create a temporal quad store with its own atom store
    /// </summary>
    public QuadIndex(string filePath, long initialSizeBytes = 1L << 30)
        : this(filePath, null, initialSizeBytes, KeySortOrder.EntityFirst)
    {
    }

    /// <summary>
    /// Create a temporal quad store with a shared atom store.
    /// </summary>
    /// <remarks>
    /// Internal: AtomStore parameter requires external synchronization.
    /// Use this constructor only when sharing an AtomStore across indexes.
    /// </remarks>
    internal QuadIndex(string filePath, AtomStore? sharedAtoms, long initialSizeBytes = 1L << 30,
        KeySortOrder sortOrder = KeySortOrder.EntityFirst, bool bulkMode = false)
    {
        _sortOrder = sortOrder;
        _bulkMode = bulkMode;
        _comparer = sortOrder == KeySortOrder.TimeFirst
            ? TemporalKey.CompareTimeFirst
            : TemporalKey.CompareEntityFirst;
        // Bulk mode: FileOptions.None lets the OS write cache batch B+Tree page
        // writes; durability is deferred to QuadStore.FlushToDisk() at end of load.
        // Cognitive mode: FileOptions.WriteThrough makes every page write reach
        // storage immediately, matching the WAL's per-write fsync discipline.
        // Same pattern WriteAheadLog already uses for its own file open.
        _fileStream = new FileStream(
            filePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.RandomAccess | (bulkMode ? FileOptions.None : FileOptions.WriteThrough)
        );

        // In bulk mode, pre-size the file to a large mmap capacity so AllocatePage
        // can extend the underlying file via SetLength without ever needing to
        // remap. Sparse-file behavior (APFS, ZFS, NTFS) means disk usage tracks
        // actual touched pages, not this size. macOS per-process VM ceiling is
        // ~64 TB so 256 GB per index leaves comfortable headroom for full Wikidata.
        // Cognitive mode keeps the original 1 GB initial — small stores never grow
        // past it, large cognitive stores stay rare.
        var actualInitialSize = bulkMode
            ? Math.Max(initialSizeBytes, 256L << 30)  // 256 GB
            : initialSizeBytes;

        if (_fileStream.Length == 0)
        {
            _fileStream.SetLength(actualInitialSize);
        }
        _fileCapacity = _fileStream.Length;

        _mmapFile = MemoryMappedFile.CreateFromFile(
            _fileStream,
            mapName: null,
            capacity: 0,
            MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None,
            leaveOpen: false
        );

        _accessor = _mmapFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

        // Acquire base pointer once (released in Dispose)
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _basePtr);

        if (sharedAtoms != null)
        {
            _atoms = sharedAtoms;
            _ownsAtomStore = false;
        }
        else
        {
            var atomFilePath = filePath + ".atoms";
            _atoms = new AtomStore(atomFilePath);
            _ownsAtomStore = true;
        }

        _pageCache = new PageCache(capacity: 10_000);

        LoadMetadata();
    }

    /// <summary>
    /// Get the atom store (for shared access).
    /// </summary>
    /// <remarks>
    /// Internal: AtomStore requires external synchronization via the owning
    /// QuadStore's read/write locks.
    /// </remarks>
    internal AtomStore Atoms => _atoms;

    /// <summary>
    /// The sort order used by this index instance.
    /// </summary>
    internal KeySortOrder SortOrder => _sortOrder;

    /// <summary>
    /// Compare two keys using this index's sort order.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int CompareKeys(in TemporalKey a, in TemporalKey b) => _comparer(in a, in b);

    /// <summary>
    /// Add a temporal quad with explicit time bounds
    /// </summary>
    public void Add(
        ReadOnlySpan<char> primary,
        ReadOnlySpan<char> secondary,
        ReadOnlySpan<char> tertiary,
        long validFrom,
        long validTo,
        long transactionTime,
        ReadOnlySpan<char> graph = default)
    {
        var g = graph.IsEmpty ? 0 : _atoms.Intern(graph);
        var a = _atoms.Intern(primary);
        var b = _atoms.Intern(secondary);
        var c = _atoms.Intern(tertiary);

        var temporalKey = new TemporalKey
        {
            Graph = g,
            Primary = a,
            Secondary = b,
            Tertiary = c,
            ValidFrom = validFrom,
            ValidTo = validTo,
            TransactionTime = transactionTime
        };

        InsertIntoTree(temporalKey, _rootPageId);
    }

    /// <summary>
    /// Insert a key with pre-resolved atom IDs. Used during secondary index rebuild
    /// to avoid re-interning billions of strings — atom IDs are already known from
    /// the primary index scan.
    /// </summary>
    internal void AddRaw(long graph, long primary, long secondary, long tertiary,
        long validFrom, long validTo, long transactionTime)
    {
        var temporalKey = new TemporalKey
        {
            Graph = graph,
            Primary = primary,
            Secondary = secondary,
            Tertiary = tertiary,
            ValidFrom = validFrom,
            ValidTo = validTo,
            TransactionTime = transactionTime
        };

        InsertIntoTree(temporalKey, _rootPageId);
    }

    /// <summary>
    /// Soft-delete a key with pre-resolved atom IDs. Used by QuadStore when replaying
    /// WAL records or materializing a batch — the IDs are already known, so we skip
    /// the string lookup roundtrip that the public Delete(span) path does.
    /// Returns true if the entry was found and marked deleted.
    /// </summary>
    internal bool DeleteRaw(long graph, long primary, long secondary, long tertiary,
        long validFrom, long validTo, long transactionTime)
    {
        var key = new TemporalKey
        {
            Graph = graph,
            Primary = primary,
            Secondary = secondary,
            Tertiary = tertiary,
            ValidFrom = validFrom,
            ValidTo = validTo,
            TransactionTime = transactionTime
        };

        return DeleteFromTree(key);
    }

    /// <summary>
    /// Add a current fact (valid from now to end of time)
    /// </summary>
    public void AddCurrent(
        ReadOnlySpan<char> primary,
        ReadOnlySpan<char> secondary,
        ReadOnlySpan<char> tertiary,
        ReadOnlySpan<char> graph = default)
    {
        Add(primary, secondary, tertiary,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            long.MaxValue,
            transactionTime: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            graph: graph);
    }

    /// <summary>
    /// Add a historical fact (valid for specific time period)
    /// </summary>
    public void AddHistorical(
        ReadOnlySpan<char> primary,
        ReadOnlySpan<char> secondary,
        ReadOnlySpan<char> tertiary,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        long transactionTime = 0,
        ReadOnlySpan<char> graph = default)
    {
        var tt = transactionTime != 0 ? transactionTime : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Add(primary, secondary, tertiary,
            validFrom.ToUnixTimeMilliseconds(),
            validTo.ToUnixTimeMilliseconds(),
            transactionTime: tt,
            graph: graph);
    }

    /// <summary>
    /// Soft-delete a triple with explicit time bounds.
    /// Returns true if the triple was found and deleted, false otherwise.
    /// </summary>
    public bool Delete(
        ReadOnlySpan<char> primary,
        ReadOnlySpan<char> secondary,
        ReadOnlySpan<char> tertiary,
        long validFrom,
        long validTo,
        long transactionTime = 0,
        ReadOnlySpan<char> graph = default)
    {
        // Look up atom IDs - if any don't exist, the triple doesn't exist
        var g = graph.IsEmpty ? 0 : _atoms.GetAtomId(graph);
        var a = _atoms.GetAtomId(primary);
        var b = _atoms.GetAtomId(secondary);
        var c = _atoms.GetAtomId(tertiary);

        if (a == 0 || b == 0 || c == 0)
            return false;
        if (!graph.IsEmpty && g == 0)
            return false;

        var tt = transactionTime != 0 ? transactionTime : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var key = new TemporalKey
        {
            Graph = g,
            Primary = a,
            Secondary = b,
            Tertiary = c,
            ValidFrom = validFrom,
            ValidTo = validTo,
            TransactionTime = tt
        };

        return DeleteFromTree(key);
    }

    /// <summary>
    /// Soft-delete a historical triple (valid for specific time period).
    /// Returns true if the triple was found and deleted, false otherwise.
    /// </summary>
    public bool DeleteHistorical(
        ReadOnlySpan<char> primary,
        ReadOnlySpan<char> secondary,
        ReadOnlySpan<char> tertiary,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        long transactionTime = 0,
        ReadOnlySpan<char> graph = default)
    {
        return Delete(primary, secondary, tertiary,
            validFrom.ToUnixTimeMilliseconds(),
            validTo.ToUnixTimeMilliseconds(),
            transactionTime,
            graph);
    }

    /// <summary>
    /// Find and mark a triple as deleted in the B+Tree.
    /// </summary>
    private bool DeleteFromTree(TemporalKey key)
    {
        var leafPageId = FindLeafPage(_rootPageId, key);
        var page = GetPage(leafPageId);

        // Scan leaf page for matching entry
        for (int i = 0; i < page->EntryCount; i++)
        {
            ref var entry = ref page->GetEntry(i);

            // Check if this is the exact entry (same graph + dimensions and overlapping time)
            if (IsSameDimensions(entry.Key, key) && !entry.IsDeleted)
            {
                // Check for time overlap
                if (entry.Key.ValidFrom <= key.ValidTo && entry.Key.ValidTo >= key.ValidFrom)
                {
                    entry.IsDeleted = true;
                    entry.ModifiedAt = key.TransactionTime;
                    FlushPage(page);
                    return true;
                }
            }

            // If we've passed the key range, stop searching
            if (CompareKeys(in entry.Key, in key) > 0)
                break;
        }

        return false;
    }

    /// <summary>
    /// Query triples with temporal constraints
    /// </summary>
    public TemporalQuadEnumerator Query(
        long graph,
        long primary,
        long secondary,
        long tertiary,
        TemporalQuery temporalQuery)
    {
        var minKey = CreateSearchKey(graph, primary, secondary, tertiary, temporalQuery, isMin: true);
        var maxKey = CreateSearchKey(graph, primary, secondary, tertiary, temporalQuery, isMin: false);

        var leafPageId = FindLeafPage(_rootPageId, minKey);

        return new TemporalQuadEnumerator(
            this,
            leafPageId,
            minKey,
            maxKey,
            temporalQuery);
    }

    /// <summary>
    /// Query current state (as of now)
    /// </summary>
    public TemporalQuadEnumerator QueryCurrent(
        ReadOnlySpan<char> primary,
        ReadOnlySpan<char> secondary,
        ReadOnlySpan<char> tertiary,
        ReadOnlySpan<char> graph = default)
    {
        var g = ResolveGraphAtom(graph);
        var a = primary.IsEmpty ? -1 : _atoms.GetAtomId(primary);
        var b = secondary.IsEmpty ? -1 : _atoms.GetAtomId(secondary);
        var c = tertiary.IsEmpty ? -1 : _atoms.GetAtomId(tertiary);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return Query(g, a, b, c, new TemporalQuery
        {
            Type = TemporalQueryType.AsOf,
            AsOfTime = now
        });
    }

    /// <summary>
    /// Query current state across all graphs (default + named).
    /// Internal method for named graph enumeration.
    /// </summary>
    internal TemporalQuadEnumerator QueryCurrentAllGraphs()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // g = -1 means wildcard for graph (all graphs)
        return Query(-1, -1, -1, -1, new TemporalQuery
        {
            Type = TemporalQueryType.AsOf,
            AsOfTime = now
        });
    }

    /// <summary>
    /// Query historical state (as of specific time)
    /// </summary>
    public TemporalQuadEnumerator QueryAsOf(
        ReadOnlySpan<char> primary,
        ReadOnlySpan<char> secondary,
        ReadOnlySpan<char> tertiary,
        DateTimeOffset asOfTime,
        ReadOnlySpan<char> graph = default)
    {
        var g = ResolveGraphAtom(graph);
        var a = primary.IsEmpty ? -1 : _atoms.GetAtomId(primary);
        var b = secondary.IsEmpty ? -1 : _atoms.GetAtomId(secondary);
        var c = tertiary.IsEmpty ? -1 : _atoms.GetAtomId(tertiary);

        return Query(g, a, b, c, new TemporalQuery
        {
            Type = TemporalQueryType.AsOf,
            AsOfTime = asOfTime.ToUnixTimeMilliseconds()
        });
    }

    /// <summary>
    /// Query time range (all versions during period)
    /// </summary>
    public TemporalQuadEnumerator QueryRange(
        ReadOnlySpan<char> primary,
        ReadOnlySpan<char> secondary,
        ReadOnlySpan<char> tertiary,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        ReadOnlySpan<char> graph = default)
    {
        var g = ResolveGraphAtom(graph);
        var a = primary.IsEmpty ? -1 : _atoms.GetAtomId(primary);
        var b = secondary.IsEmpty ? -1 : _atoms.GetAtomId(secondary);
        var c = tertiary.IsEmpty ? -1 : _atoms.GetAtomId(tertiary);

        return Query(g, a, b, c, new TemporalQuery
        {
            Type = TemporalQueryType.Range,
            RangeStart = rangeStart.ToUnixTimeMilliseconds(),
            RangeEnd = rangeEnd.ToUnixTimeMilliseconds()
        });
    }

    /// <summary>
    /// Query evolution (all versions ever)
    /// </summary>
    public TemporalQuadEnumerator QueryHistory(
        ReadOnlySpan<char> primary,
        ReadOnlySpan<char> secondary,
        ReadOnlySpan<char> tertiary,
        ReadOnlySpan<char> graph = default)
    {
        var g = ResolveGraphAtom(graph);
        var a = primary.IsEmpty ? -1 : _atoms.GetAtomId(primary);
        var b = secondary.IsEmpty ? -1 : _atoms.GetAtomId(secondary);
        var c = tertiary.IsEmpty ? -1 : _atoms.GetAtomId(tertiary);

        return Query(g, a, b, c, new TemporalQuery
        {
            Type = TemporalQueryType.AllTime
        });
    }

    /// <summary>
    /// Resolve graph span to atom ID.
    /// Empty graph = 0 (default graph)
    /// Non-empty graph = atom ID, or -2 if not found (will match nothing)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ResolveGraphAtom(ReadOnlySpan<char> graph)
    {
        if (graph.IsEmpty)
            return 0; // Default graph

        var atomId = _atoms.GetAtomId(graph);
        // If graph was specified but not found, return -2 to prevent matching default graph
        return atomId == 0 ? -2 : atomId;
    }

    /// <summary>
    /// Result of an insert operation that may have caused a page split
    /// </summary>
    private struct SplitResult
    {
        public bool DidSplit;
        public TemporalKey PromotedKey;
        public long NewRightPageId;
    }

    /// <summary>
    /// Sort order for TemporalKey comparisons.
    /// Selected once at QuadIndex construction; the JIT optimizes monomorphic delegate call sites.
    /// </summary>
    internal enum KeySortOrder
    {
        /// <summary>Graph → Primary → Secondary → Tertiary → ValidFrom → ValidTo → TransactionTime</summary>
        EntityFirst,

        /// <summary>ValidFrom → Graph → Primary → Secondary → Tertiary → ValidTo → TransactionTime</summary>
        TimeFirst
    }

    /// <summary>
    /// Delegate type for key comparison. Selected once at construction, stored as a field.
    /// </summary>
    internal delegate int KeyComparer(in TemporalKey a, in TemporalKey b);

    /// <summary>
    /// Temporal key: Graph + 3 generic dimensions + ValidTime + TransactionTime (56 bytes)
    /// QuadIndex is a generic multi-dimensional B+Tree. The RDF-to-dimension mapping
    /// (Subject→Primary, Predicate→Secondary, Object→Tertiary) lives in QuadStore.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct TemporalKey : IComparable<TemporalKey>
    {
        public long Graph;           // 64-bit graph ID (0 = default graph)
        public long Primary;         // 64-bit, first dimension (e.g., subject in GSPO)
        public long Secondary;       // 64-bit, second dimension (e.g., predicate in GSPO)
        public long Tertiary;        // 64-bit, third dimension (e.g., object in GSPO)
        public long ValidFrom;       // Valid-time start (milliseconds since epoch)
        public long ValidTo;         // Valid-time end (milliseconds since epoch)
        public long TransactionTime; // Transaction-time (when recorded)

        /// <summary>
        /// Default comparison: EntityFirst order.
        /// Used by IComparable for cases where no delegate is available.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int CompareTo(TemporalKey other) => CompareEntityFirst(in this, in other);

        /// <summary>
        /// Graph → Primary → Secondary → Tertiary → ValidFrom → ValidTo → TransactionTime
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int CompareEntityFirst(in TemporalKey a, in TemporalKey b)
        {
            var cmp = a.Graph.CompareTo(b.Graph);
            if (cmp != 0) return cmp;

            cmp = a.Primary.CompareTo(b.Primary);
            if (cmp != 0) return cmp;

            cmp = a.Secondary.CompareTo(b.Secondary);
            if (cmp != 0) return cmp;

            cmp = a.Tertiary.CompareTo(b.Tertiary);
            if (cmp != 0) return cmp;

            cmp = a.ValidFrom.CompareTo(b.ValidFrom);
            if (cmp != 0) return cmp;

            cmp = a.ValidTo.CompareTo(b.ValidTo);
            if (cmp != 0) return cmp;

            return a.TransactionTime.CompareTo(b.TransactionTime);
        }

        /// <summary>
        /// ValidFrom → Graph → Primary → Secondary → Tertiary → ValidTo → TransactionTime
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int CompareTimeFirst(in TemporalKey a, in TemporalKey b)
        {
            var cmp = a.ValidFrom.CompareTo(b.ValidFrom);
            if (cmp != 0) return cmp;

            cmp = a.Graph.CompareTo(b.Graph);
            if (cmp != 0) return cmp;

            cmp = a.Primary.CompareTo(b.Primary);
            if (cmp != 0) return cmp;

            cmp = a.Secondary.CompareTo(b.Secondary);
            if (cmp != 0) return cmp;

            cmp = a.Tertiary.CompareTo(b.Tertiary);
            if (cmp != 0) return cmp;

            cmp = a.ValidTo.CompareTo(b.ValidTo);
            if (cmp != 0) return cmp;

            return a.TransactionTime.CompareTo(b.TransactionTime);
        }
    }

    /// <summary>
    /// B+Tree page for temporal triples (16KB)
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = PageSize)]
    private struct TemporalBTreePage
    {
        [FieldOffset(0)] public long PageId;
        [FieldOffset(8)] public bool IsLeaf;
        [FieldOffset(9)] public short EntryCount;
        [FieldOffset(16)] public long ParentPageId;
        [FieldOffset(24)] public long NextLeaf;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly ref TemporalBTreeEntry GetEntry(int index)
        {
            var ptr = (byte*)Unsafe.AsPointer(ref Unsafe.AsRef(in this));
            return ref ((TemporalBTreeEntry*)(ptr + 32))[index];
        }
    }

    /// <summary>
    /// B+Tree entry for temporal triple (88 bytes)
    /// Key: 56 bytes + Child/Value: 8 bytes + Metadata: 24 bytes
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TemporalBTreeEntry
    {
        public TemporalKey Key;
        public long ChildOrValue;
        
        // Metadata for temporal operations
        public long CreatedAt;       // When this version was created
        public long ModifiedAt;      // When this version was modified
        public int Version;          // Version number
        public bool IsDeleted;       // Soft delete flag
        public byte Flags;           // Future use
        public short Reserved;       // Padding
    }

    /// <summary>
    /// Zero-allocation enumerator for temporal triples.
    /// Changed from ref struct to struct to enable pooled array storage (ADR-011).
    /// </summary>
    internal struct TemporalQuadEnumerator
    {
        private readonly QuadIndex _store;
        private long _currentPageId;
        private int _currentSlot;
        private readonly TemporalKey _minKey;
        private readonly TemporalKey _maxKey;
        private readonly TemporalQuery _query;
        private TemporalKey _currentKey;
        private bool _currentIsDeleted;

        internal TemporalQuadEnumerator(
            QuadIndex store,
            long startPageId,
            TemporalKey minKey,
            TemporalKey maxKey,
            TemporalQuery query)
        {
            _store = store;
            _currentPageId = startPageId;
            _currentSlot = 0;
            _minKey = minKey;
            _maxKey = maxKey;
            _query = query;
            _currentKey = default;
            _currentIsDeleted = false;
        }

        public bool MoveNext()
        {
            while (_currentPageId != 0)
            {
                var page = _store.GetPage(_currentPageId);

                while (_currentSlot < page->EntryCount)
                {
                    ref var entry = ref page->GetEntry(_currentSlot);
                    _currentKey = entry.Key;
                    _currentIsDeleted = entry.IsDeleted;

                    // Check spatial bounds using the index's sort order
                    if (_store.CompareKeys(in _currentKey, in _maxKey) > 0)
                        return false;

                    if (_store.CompareKeys(in _currentKey, in _minKey) >= 0)
                    {
                        // Check temporal bounds
                        if (MatchesTemporalQuery(entry))
                        {
                            _currentSlot++;
                            return true;
                        }
                    }

                    _currentSlot++;
                }

                _currentPageId = page->NextLeaf;
                _currentSlot = 0;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly bool MatchesTemporalQuery(in TemporalBTreeEntry entry)
        {
            var key = entry.Key;

            return _query.Type switch
            {
                // AsOf and Range queries exclude deleted entries
                TemporalQueryType.AsOf =>
                    !entry.IsDeleted &&
                    key.ValidFrom <= _query.AsOfTime &&
                    key.ValidTo > _query.AsOfTime,

                TemporalQueryType.Range =>
                    !entry.IsDeleted &&
                    key.ValidFrom < _query.RangeEnd &&
                    key.ValidTo > _query.RangeStart,

                // AllTime (history/audit) includes deleted entries
                TemporalQueryType.AllTime => true,

                _ => false
            };
        }

        public readonly TemporalQuad Current
        {
            get => new TemporalQuad
            {
                Graph = _currentKey.Graph,
                Primary = _currentKey.Primary,
                Secondary = _currentKey.Secondary,
                Tertiary = _currentKey.Tertiary,
                ValidFrom = _currentKey.ValidFrom,
                ValidTo = _currentKey.ValidTo,
                TransactionTime = _currentKey.TransactionTime,
                IsDeleted = _currentIsDeleted
            };
        }

        public TemporalQuadEnumerator GetEnumerator() => this;
    }

#if DEBUG
    private long _pageAccessCount;

    /// <summary>
    /// Number of page accesses since last reset. Debug builds only.
    /// </summary>
    internal long PageAccessCount => _pageAccessCount;

    /// <summary>
    /// Reset the page access counter. Debug builds only.
    /// </summary>
    internal void ResetPageAccessCount() => _pageAccessCount = 0;
#endif

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private TemporalBTreePage* GetPage(long pageId)
    {
#if DEBUG
        System.Threading.Interlocked.Increment(ref _pageAccessCount);
#endif
        if (_pageCache.TryGet(pageId, out var cachedPtr))
            return (TemporalBTreePage*)cachedPtr;

        // Use cached base pointer (acquired during construction, released in Dispose)
        var pagePtr = (TemporalBTreePage*)(_basePtr + pageId * PageSize);
        _pageCache.Add(pageId, pagePtr);

        return pagePtr;
    }

    private void InsertIntoTree(TemporalKey key, long pageId)
    {
        var result = InsertRecursive(key, pageId);

        // If root split, create new root
        if (result.DidSplit && pageId == _rootPageId)
        {
            CreateNewRoot(pageId, result.PromotedKey, result.NewRightPageId);
        }
    }

    private SplitResult InsertRecursive(TemporalKey key, long pageId)
    {
        var page = GetPage(pageId);

        if (page->IsLeaf)
        {
            return InsertIntoLeaf(page, key);
        }
        else
        {
            var childPageId = FindChildPage(page, key);
            var childResult = InsertRecursive(key, childPageId);

            // If child split, insert promoted key into this internal node
            if (childResult.DidSplit)
            {
                return InsertIntoInternal(page, childResult.PromotedKey, childResult.NewRightPageId);
            }

            return default; // No split
        }
    }

    private void CreateNewRoot(long oldRootPageId, TemporalKey promotedKey, long newRightPageId)
    {
        var newRootId = AllocatePage();
        var newRoot = GetPage(newRootId);

        newRoot->PageId = newRootId;
        newRoot->IsLeaf = false;
        newRoot->EntryCount = 1;
        newRoot->ParentPageId = 0;
        newRoot->NextLeaf = 0;

        // First entry points to old root (left child), key is the promoted key
        ref var entry = ref newRoot->GetEntry(0);
        entry.Key = promotedKey;
        entry.ChildOrValue = newRightPageId; // Right child after this key

        // Store left child pointer in a special location (entry -1 concept)
        // For B+Tree, we use entry[0].ChildOrValue for right child of key[0]
        // We need a separate left-most child pointer - store in first entry's metadata
        // Alternative: use entry count + 1 slots where slot 0 is leftmost child

        // Simpler approach: first entry holds (key, rightChild), leftmost child stored separately
        // We'll encode leftmost child in the page header's NextLeaf field (repurposed for internal nodes)
        newRoot->NextLeaf = oldRootPageId; // Leftmost child pointer (reusing NextLeaf for internal nodes)

        // Update parent pointers
        var oldRoot = GetPage(oldRootPageId);
        oldRoot->ParentPageId = newRootId;

        var rightPage = GetPage(newRightPageId);
        rightPage->ParentPageId = newRootId;

        _rootPageId = newRootId;

        FlushPage(newRoot);
        FlushPage(oldRoot);
        FlushPage(rightPage);
        SaveMetadata();
    }

    private SplitResult InsertIntoLeaf(TemporalBTreePage* page, TemporalKey key)
    {
        // Find insertion point
        int insertPos = 0;
        while (insertPos < page->EntryCount)
        {
            ref var entry = ref page->GetEntry(insertPos);
            if (CompareKeys(in key, in entry.Key) < 0)
                break;
            insertPos++;
        }

        // Check for updates (same GSPO dimensions)
        if (insertPos > 0)
        {
            ref var prevEntry = ref page->GetEntry(insertPos - 1);
            if (IsSameDimensions(key, prevEntry.Key))
            {
                // Case A: Exact full-key duplicate (all 7 TemporalKey fields match) — replayed no-op
                if (prevEntry.Key.ValidFrom == key.ValidFrom &&
                    prevEntry.Key.ValidTo == key.ValidTo &&
                    prevEntry.Key.TransactionTime == key.TransactionTime)
                    return default;

                // Case B: Both entries are "current" (ValidTo is far future) — RDF idempotency
                const long FarFutureThreshold = 253370764800000L; // Year 9000 in Unix ms
                if (prevEntry.Key.ValidTo >= FarFutureThreshold && key.ValidTo >= FarFutureThreshold)
                    return default;

                // Case C: Same dimensions, different temporal key — truncate predecessor, then insert new row
                HandleTemporalUpdate(page, insertPos - 1, key);
                // Fall through to insert the new committed row
            }
        }

        // Check if page is full - need to split
        if (page->EntryCount >= NodeDegree)
        {
            return SplitLeafPage(page, key);
        }

        // Shift and insert
        for (int i = page->EntryCount; i > insertPos; i--)
        {
            page->GetEntry(i) = page->GetEntry(i - 1);
        }

        ref var newEntry = ref page->GetEntry(insertPos);
        newEntry.Key = key;
        newEntry.ChildOrValue = 0;
        newEntry.CreatedAt = key.TransactionTime;
        newEntry.ModifiedAt = key.TransactionTime;
        newEntry.Version = 1;
        newEntry.IsDeleted = false;

        page->EntryCount++;
        System.Threading.Interlocked.Increment(ref _quadCount);
        FlushPage(page);

        return default; // No split
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSameDimensions(TemporalKey a, TemporalKey b)
    {
        return a.Graph == b.Graph &&
               a.Primary == b.Primary &&
               a.Secondary == b.Secondary &&
               a.Tertiary == b.Tertiary;
    }

    private void HandleTemporalUpdate(TemporalBTreePage* page, int existingIndex, TemporalKey newKey)
    {
        ref var existing = ref page->GetEntry(existingIndex);

        // Temporal update: adjust valid time of existing entry
        if (newKey.ValidFrom < existing.Key.ValidTo)
        {
            // Truncate existing entry
            existing.Key.ValidTo = newKey.ValidFrom;
            existing.ModifiedAt = newKey.TransactionTime;
            FlushPage(page);
        }
    }

    private SplitResult SplitLeafPage(TemporalBTreePage* page, TemporalKey key)
    {
        // Allocate new page for right half
        var newPageId = AllocatePage();
        var newPage = GetPage(newPageId);

        newPage->PageId = newPageId;
        newPage->IsLeaf = true;
        newPage->EntryCount = 0;
        newPage->ParentPageId = page->ParentPageId;
        newPage->NextLeaf = page->NextLeaf;
        page->NextLeaf = newPageId;

        // Split entries at midpoint
        var midPoint = NodeDegree / 2;

        for (int i = midPoint; i < page->EntryCount; i++)
        {
            newPage->GetEntry(i - midPoint) = page->GetEntry(i);
        }

        newPage->EntryCount = (short)(page->EntryCount - midPoint);
        page->EntryCount = (short)midPoint;

        // The key to promote is the first key of the new (right) page
        var promotedKey = newPage->GetEntry(0).Key;

        // Insert the new key into the appropriate page
        // (recursive call is safe - pages now have room after split)
        if (CompareKeys(in key, in promotedKey) < 0)
        {
            InsertIntoLeaf(page, key);
        }
        else
        {
            InsertIntoLeaf(newPage, key);
        }

        FlushPage(page);
        FlushPage(newPage);

        // Return split result for parent to handle
        return new SplitResult
        {
            DidSplit = true,
            PromotedKey = promotedKey,
            NewRightPageId = newPageId
        };
    }

    private SplitResult InsertIntoInternal(TemporalBTreePage* page, TemporalKey key, long rightChildPageId)
    {
        // Find insertion point
        int insertPos = 0;
        while (insertPos < page->EntryCount)
        {
            ref var entry = ref page->GetEntry(insertPos);
            if (CompareKeys(in key, in entry.Key) < 0)
                break;
            insertPos++;
        }

        // Check if page is full - need to split
        if (page->EntryCount >= NodeDegree)
        {
            return SplitInternalPage(page, key, rightChildPageId);
        }

        // Shift and insert
        for (int i = page->EntryCount; i > insertPos; i--)
        {
            page->GetEntry(i) = page->GetEntry(i - 1);
        }

        ref var newEntry = ref page->GetEntry(insertPos);
        newEntry.Key = key;
        newEntry.ChildOrValue = rightChildPageId;
        newEntry.CreatedAt = key.TransactionTime;
        newEntry.ModifiedAt = key.TransactionTime;
        newEntry.Version = 1;
        newEntry.IsDeleted = false;

        page->EntryCount++;

        // Update parent pointer of the new child
        var childPage = GetPage(rightChildPageId);
        childPage->ParentPageId = page->PageId;
        FlushPage(childPage);

        FlushPage(page);

        return default; // No split
    }

    private SplitResult SplitInternalPage(TemporalBTreePage* page, TemporalKey key, long rightChildPageId)
    {
        // Allocate new page for right half
        var newPageId = AllocatePage();
        var newPage = GetPage(newPageId);

        newPage->PageId = newPageId;
        newPage->IsLeaf = false;
        newPage->EntryCount = 0;
        newPage->ParentPageId = page->ParentPageId;
        newPage->NextLeaf = 0; // Will be set to leftmost child of right page

        // Split entries at midpoint
        var midPoint = NodeDegree / 2;

        // The middle key will be promoted, not copied to right page
        var promotedKey = page->GetEntry(midPoint).Key;

        // Copy entries after midpoint to new page
        // Note: for internal nodes, the child pointer of entry[midPoint] becomes
        // the leftmost child of the new page
        newPage->NextLeaf = page->GetEntry(midPoint).ChildOrValue; // Leftmost child of right page

        for (int i = midPoint + 1; i < page->EntryCount; i++)
        {
            newPage->GetEntry(i - midPoint - 1) = page->GetEntry(i);
        }

        newPage->EntryCount = (short)(page->EntryCount - midPoint - 1);
        page->EntryCount = (short)midPoint;

        // Update parent pointers for moved children
        // Update leftmost child of new page
        var leftmostChild = GetPage(newPage->NextLeaf);
        leftmostChild->ParentPageId = newPageId;
        FlushPage(leftmostChild);

        for (int i = 0; i < newPage->EntryCount; i++)
        {
            var childId = newPage->GetEntry(i).ChildOrValue;
            var child = GetPage(childId);
            child->ParentPageId = newPageId;
            FlushPage(child);
        }

        // Insert the new key into the appropriate page
        if (CompareKeys(in key, in promotedKey) < 0)
        {
            InsertIntoInternal(page, key, rightChildPageId);
        }
        else
        {
            InsertIntoInternal(newPage, key, rightChildPageId);
        }

        FlushPage(page);
        FlushPage(newPage);

        // Return split result for parent to handle
        return new SplitResult
        {
            DidSplit = true,
            PromotedKey = promotedKey,
            NewRightPageId = newPageId
        };
    }

    private long AllocatePage()
    {
        var pageId = System.Threading.Interlocked.Increment(ref _nextPageId) - 1;

        // Extend file if needed — uses tracked capacity to avoid fstat() on every
        // page allocation (profile showed ~0.5% of bulk-load time in FStat).
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

    private long FindLeafPage(long pageId, TemporalKey key)
    {
        var page = GetPage(pageId);
        
        if (page->IsLeaf)
            return pageId;
        
        var childPageId = FindChildPage(page, key);
        return FindLeafPage(childPageId, key);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long FindChildPage(TemporalBTreePage* page, TemporalKey key)
    {
        // Binary search to find the correct child pointer
        // Internal node layout:
        //   NextLeaf = leftmost child (for keys < entry[0].Key)
        //   entry[i].Key = separator key
        //   entry[i].ChildOrValue = right child of separator key

        int left = 0;
        int right = page->EntryCount - 1;

        while (left <= right)
        {
            int mid = left + (right - left) / 2;
            ref var entry = ref page->GetEntry(mid);

            var cmp = CompareKeys(in key, in entry.Key);
            if (cmp < 0)
                right = mid - 1;
            else
                left = mid + 1;
        }

        // If key < all separator keys, use leftmost child (stored in NextLeaf)
        if (right < 0)
            return page->NextLeaf;

        // Otherwise use the child pointer of the found separator
        return page->GetEntry(right).ChildOrValue;
    }

    private TemporalKey CreateSearchKey(
        long graph, long primary, long secondary, long tertiary,
        TemporalQuery query,
        bool isMin)
    {
        var unboundValue = isMin ? 0L : long.MaxValue;

        // Special case: -2 means "not found, match nothing"
        // Set to -2 for both min and max so no entries will match
        long graphValue;
        if (graph == -2)
            graphValue = -2; // Will never match any entry (entries have non-negative Graph)
        else if (graph < 0)
            graphValue = unboundValue;
        else
            graphValue = graph;

        // For TimeFirst sort order, ValidFrom is the leading dimension.
        // Temporal overlap semantics: ValidFrom < rangeEnd AND ValidTo > rangeStart.
        // minKey.ValidFrom stays 0 because entries starting before the query window
        // may still overlap (their ValidTo extends into the window).
        // maxKey.ValidFrom = rangeEnd/asOfTime to skip entries that can't overlap.
        // This narrows the scan from O(N) to O(log N + k') where k' = entries with
        // ValidFrom < rangeEnd. MatchesTemporalQuery post-filters ValidTo > rangeStart.
        long validFrom;
        if (_sortOrder == KeySortOrder.TimeFirst && !isMin)
        {
            validFrom = query.Type switch
            {
                TemporalQueryType.Range => query.RangeEnd,
                TemporalQueryType.AsOf => query.AsOfTime,
                _ => long.MaxValue
            };
        }
        else
        {
            validFrom = unboundValue;
        }

        return new TemporalKey
        {
            Graph = graphValue,
            Primary = primary < 0 ? unboundValue : primary,
            Secondary = secondary < 0 ? unboundValue : secondary,
            Tertiary = tertiary < 0 ? unboundValue : tertiary,
            ValidFrom = validFrom,
            ValidTo = isMin ? 0 : long.MaxValue,
            TransactionTime = isMin ? 0 : long.MaxValue
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FlushPage(TemporalBTreePage* page)
    {
        // In bulk mode, defer all msync to a single Flush() at load completion.
        // _accessor.Flush() on macOS is msync of the ENTIRE mapped region (not a
        // page) — calling it per page write produces O(N×region_size) work and
        // saturates the SSD random-write IOPS at ~5,500/sec, capping bulk-load
        // throughput at ~1,000 triples/sec independent of CPU. The OS page cache
        // holds dirty pages; QuadStore.FlushToDisk() at end of bulk load issues
        // the single msync. Cognitive mode keeps per-page durability.
        if (_bulkMode) return;
        _accessor.Flush();
    }

    /// <summary>
    /// Force all pending mmap writes to disk. Called once at the end of a bulk
    /// load to flush dirty pages that FlushPage skipped in bulk mode.
    /// </summary>
    public void Flush()
    {
        _accessor.Flush();
    }

    private const long MagicNumber = 0x54454D504F52414C; // "TEMPORAL" as long

    private void LoadMetadata()
    {
        // Check for valid metadata using magic number
        _accessor.Read(24, out long magic);

        if (magic == MagicNumber)
        {
            _accessor.Read(0, out _rootPageId);
            _accessor.Read(8, out _nextPageId);
            _accessor.Read(16, out _quadCount);
        }
        else
        {
            _rootPageId = 1;
            _nextPageId = 2;
            _quadCount = 0;

            var root = GetPage(_rootPageId);
            root->PageId = _rootPageId;
            root->IsLeaf = true;
            root->EntryCount = 0;
            root->ParentPageId = 0;
            root->NextLeaf = 0;

            SaveMetadata();
        }
    }

    private void SaveMetadata()
    {
        _accessor.Write(0, _rootPageId);
        _accessor.Write(8, _nextPageId);
        _accessor.Write(16, _quadCount);
        _accessor.Write(24, MagicNumber);
        _accessor.Flush();
    }

    public void Dispose()
    {
        SaveMetadata();

        // Release acquired pointer before disposing accessor
        if (_basePtr != null)
        {
            _accessor?.SafeMemoryMappedViewHandle.ReleasePointer();
            _basePtr = null;
        }

        _accessor?.Dispose();
        _mmapFile?.Dispose();
        _fileStream?.Dispose();
        if (_ownsAtomStore)
            _atoms?.Dispose();
        _pageCache?.Dispose();
    }

    public long QuadCount => _quadCount;

    /// <summary>
    /// Resets the index to empty state. All data is logically discarded.
    /// File size is preserved (memory mapping stays valid).
    /// </summary>
    /// <remarks>
    /// Must be called from QuadStore.Clear() which holds the write lock.
    /// </remarks>
    public void Clear()
    {
        // Clear page cache first (contains pointers to old pages)
        _pageCache.Clear();

        // Reset to initial state: root at page 1, next allocation at page 2
        _rootPageId = 1;
        _nextPageId = 2;
        _quadCount = 0;

        // Reinitialize root page as empty leaf
        var root = GetPage(_rootPageId);
        root->PageId = _rootPageId;
        root->IsLeaf = true;
        root->EntryCount = 0;
        root->ParentPageId = 0;
        root->NextLeaf = 0;

        FlushPage(root);
        SaveMetadata();
    }
}

/// <summary>
/// Temporal query specification
/// </summary>
internal struct TemporalQuery
{
    public TemporalQueryType Type;
    public long AsOfTime;
    public long RangeStart;
    public long RangeEnd;
}

public enum TemporalQueryType
{
    AsOf,      // Point-in-time query
    Range,     // Time range query
    AllTime    // All versions
}

/// <summary>
/// Temporal quad with generic dimension fields and time dimensions.
/// The RDF-to-dimension mapping (Primary=subject, Secondary=predicate, etc.)
/// depends on the index type and is resolved in QuadStore's TemporalResultEnumerator.
/// </summary>
internal struct TemporalQuad
{
    public long Graph;          // 64-bit graph ID (0 = default graph)
    public long Primary;        // 64-bit, first dimension
    public long Secondary;      // 64-bit, second dimension
    public long Tertiary;       // 64-bit, third dimension
    public long ValidFrom;
    public long ValidTo;
    public long TransactionTime;
    public bool IsDeleted;      // Soft-delete flag (visible in AllTime queries)
}
