using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// B+Tree index for RDF quads (graph + triple) with versioning and soft-delete but
/// no bitemporal semantics. ADR-029 Graph profile — concrete index parallel to
/// <see cref="TemporalQuadIndex"/> (Cognitive) and <see cref="ReferenceQuadIndex"/>
/// (Reference). 64-byte entries: 32 B key (G,S,P,O) + 8 B ChildOrValue + 24 B
/// versioning metadata (CreatedAt, ModifiedAt, Version, IsDeleted).
/// </summary>
/// <remarks>
/// <para><b>Mutation semantics.</b> Key uniqueness is <c>(G, S, P, O)</c>:</para>
/// <list type="bullet">
///   <item>Insert with key not present → new entry, <c>Version=1</c>,
///   <c>CreatedAt=ModifiedAt=now</c>, <c>IsDeleted=false</c>.</item>
///   <item>Insert with key present and <c>!IsDeleted</c> → no-op (RDF set semantics).</item>
///   <item>Insert with key present and <c>IsDeleted</c> → un-delete: bump
///   <c>Version</c>, set <c>ModifiedAt=now</c>, <c>IsDeleted=false</c>.</item>
///   <item>Delete with key present and <c>!IsDeleted</c> → soft-delete: bump
///   <c>Version</c>, set <c>ModifiedAt=now</c>, <c>IsDeleted=true</c>; return <c>true</c>.</item>
///   <item>Delete with key missing or already <c>IsDeleted</c> → return <c>false</c>.</item>
/// </list>
/// <para><b>Query semantics.</b> Single sort order (G → P → S → T). No AS_OF, no
/// time-range queries — the SPARQL planner rejects temporal queries against the
/// Graph profile at plan time. Soft-deleted entries are filtered out by default;
/// audit access (include deleted) is via the explicit <see cref="QueryAllVersions"/>
/// surface.</para>
/// <para><b>INTERNAL USE ONLY.</b> Public surface is via <see cref="QuadStore"/>.</para>
/// </remarks>
internal sealed unsafe class VersionedQuadIndex : IQuadIndex
{
    private const int PageSize = 16384;
    private const int NodeDegree = 255; // (16384 - 32) / 64 bytes per versioned entry

    /// <summary>File-format magic for VersionedQuadIndex pages. "GRAPHIDX" as long.</summary>
    private const long MagicNumber = 0x4752415048494458L;

    private readonly FileStream _fileStream;
    private readonly MemoryMappedFile _mmapFile;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly IAtomStore _atoms;
    private readonly bool _ownsAtomStore;
    private readonly PageCache _pageCache;
    private bool _deferMsync;

    private byte* _basePtr;

    private long _rootPageId;
    private long _nextPageId;
    private long _quadCount;
    private long _fileCapacity;

    /// <summary>Create a versioned quad store with its own atom store.</summary>
    public VersionedQuadIndex(string filePath, long initialSizeBytes = 1L << 30)
        : this(filePath, null, initialSizeBytes, bulkMode: false)
    {
    }

    /// <summary>Create a versioned quad store with a shared atom store.</summary>
    internal VersionedQuadIndex(string filePath, IAtomStore? sharedAtoms,
        long initialSizeBytes = 1L << 30, bool bulkMode = false)
    {
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
            ? Math.Max(initialSizeBytes, 256L << 30)
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
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _basePtr);

        if (sharedAtoms != null)
        {
            _atoms = sharedAtoms;
            _ownsAtomStore = false;
        }
        else
        {
            var atomFilePath = filePath + ".atoms";
            _atoms = new HashAtomStore(atomFilePath);
            _ownsAtomStore = true;
        }

        _pageCache = new PageCache(capacity: 10_000);

        LoadMetadata();
    }

    internal IAtomStore Atoms => _atoms;

    /// <summary>Compare two keys: G → P → S → T (lexicographic).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CompareKeys(in VersionedKey a, in VersionedKey b) => VersionedKey.Compare(in a, in b);

    /// <summary>
    /// Add a quad. Resolves atoms via the shared store. If the key is already
    /// present and not deleted, no-op; if deleted, un-delete (bump version).
    /// </summary>
    public void Add(
        ReadOnlySpan<char> primary,
        ReadOnlySpan<char> secondary,
        ReadOnlySpan<char> tertiary,
        ReadOnlySpan<char> graph = default)
    {
        var g = graph.IsEmpty ? 0 : _atoms.Intern(graph);
        var p = _atoms.Intern(primary);
        var s = _atoms.Intern(secondary);
        var t = _atoms.Intern(tertiary);
        AddRaw(g, p, s, t);
    }

    /// <summary>
    /// Add a quad with pre-resolved atom IDs. Used by QuadStore replay paths +
    /// secondary-index rebuild — atom IDs are already known.
    /// </summary>
    internal void AddRaw(long graph, long primary, long secondary, long tertiary)
    {
        var key = new VersionedKey
        {
            Graph = graph,
            Primary = primary,
            Secondary = secondary,
            Tertiary = tertiary,
        };

        InsertIntoTree(key, _rootPageId);
    }

    /// <summary>
    /// Soft-delete a quad with pre-resolved atom IDs. Returns true if the entry was
    /// found and live (and is now soft-deleted); false if not found or already deleted.
    /// </summary>
    internal bool DeleteRaw(long graph, long primary, long secondary, long tertiary)
    {
        var key = new VersionedKey
        {
            Graph = graph,
            Primary = primary,
            Secondary = secondary,
            Tertiary = tertiary,
        };

        return DeleteFromTree(key);
    }

    /// <summary>
    /// Soft-delete a triple via spans. Returns true if found and live; false otherwise.
    /// </summary>
    public bool Delete(
        ReadOnlySpan<char> primary,
        ReadOnlySpan<char> secondary,
        ReadOnlySpan<char> tertiary,
        ReadOnlySpan<char> graph = default)
    {
        var g = graph.IsEmpty ? 0 : _atoms.GetAtomId(graph);
        var p = _atoms.GetAtomId(primary);
        var s = _atoms.GetAtomId(secondary);
        var t = _atoms.GetAtomId(tertiary);

        if (p == 0 || s == 0 || t == 0)
            return false;
        if (!graph.IsEmpty && g == 0)
            return false;

        return DeleteRaw(g, p, s, t);
    }

    /// <summary>
    /// Query: yields all live entries matching the (graph, primary, secondary, tertiary)
    /// constraints. Use -1 for "any" on a dimension. Soft-deleted entries are filtered out.
    /// </summary>
    public VersionedQuadEnumerator Query(long graph, long primary, long secondary, long tertiary)
        => QueryInternal(graph, primary, secondary, tertiary, includeDeleted: false);

    /// <summary>
    /// Audit query: yields all entries (including soft-deleted) matching the constraints.
    /// Use for history / soft-delete audit.
    /// </summary>
    public VersionedQuadEnumerator QueryAllVersions(long graph, long primary, long secondary, long tertiary)
        => QueryInternal(graph, primary, secondary, tertiary, includeDeleted: true);

    private VersionedQuadEnumerator QueryInternal(long graph, long primary, long secondary, long tertiary, bool includeDeleted)
    {
        var minKey = CreateSearchKey(graph, primary, secondary, tertiary, isMin: true);
        var maxKey = CreateSearchKey(graph, primary, secondary, tertiary, isMin: false);
        var leafPageId = FindLeafPage(_rootPageId, minKey);
        return new VersionedQuadEnumerator(this, leafPageId, minKey, maxKey, includeDeleted);
    }

    private static VersionedKey CreateSearchKey(long graph, long primary, long secondary, long tertiary, bool isMin)
    {
        var unboundValue = isMin ? 0L : long.MaxValue;
        long graphValue;
        if (graph == -2)
            graphValue = -2; // "not found, match nothing"
        else if (graph < 0)
            graphValue = unboundValue;
        else
            graphValue = graph;

        return new VersionedKey
        {
            Graph = graphValue,
            Primary = primary < 0 ? unboundValue : primary,
            Secondary = secondary < 0 ? unboundValue : secondary,
            Tertiary = tertiary < 0 ? unboundValue : tertiary,
        };
    }

    private bool DeleteFromTree(VersionedKey key)
    {
        var leafPageId = FindLeafPage(_rootPageId, key);
        var page = GetPage(leafPageId);

        for (int i = 0; i < page->EntryCount; i++)
        {
            ref var entry = ref page->GetEntry(i);
            int cmp = CompareKeys(in entry.Key, in key);
            if (cmp == 0)
            {
                if (entry.IsDeleted) return false;
                entry.IsDeleted = true;
                entry.Version = checked(entry.Version + 1);
                entry.ModifiedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                FlushPage(page);
                return true;
            }
            if (cmp > 0) break;
        }
        return false;
    }

    private struct SplitResult
    {
        public bool DidSplit;
        public VersionedKey PromotedKey;
        public long NewRightPageId;
    }

    /// <summary>
    /// 32-byte key for Graph-profile quads. Compared lexicographically: G → P → S → T.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct VersionedKey : IComparable<VersionedKey>, IEquatable<VersionedKey>
    {
        public long Graph;
        public long Primary;
        public long Secondary;
        public long Tertiary;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int CompareTo(VersionedKey other) => Compare(in this, in other);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int Compare(in VersionedKey a, in VersionedKey b)
        {
            var cmp = a.Graph.CompareTo(b.Graph);
            if (cmp != 0) return cmp;
            cmp = a.Primary.CompareTo(b.Primary);
            if (cmp != 0) return cmp;
            cmp = a.Secondary.CompareTo(b.Secondary);
            if (cmp != 0) return cmp;
            return a.Tertiary.CompareTo(b.Tertiary);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool Equals(VersionedKey other) =>
            Graph == other.Graph && Primary == other.Primary &&
            Secondary == other.Secondary && Tertiary == other.Tertiary;

        public override readonly bool Equals(object? obj) => obj is VersionedKey k && Equals(k);
        public override readonly int GetHashCode() => HashCode.Combine(Graph, Primary, Secondary, Tertiary);
    }

    /// <summary>B+Tree page for versioned triples (16 KB).</summary>
    [StructLayout(LayoutKind.Explicit, Size = PageSize)]
    private struct VersionedBTreePage
    {
        [FieldOffset(0)] public long PageId;
        [FieldOffset(8)] public bool IsLeaf;
        [FieldOffset(9)] public short EntryCount;
        [FieldOffset(16)] public long ParentPageId;
        [FieldOffset(24)] public long NextLeaf;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly ref VersionedBTreeEntry GetEntry(int index)
        {
            var ptr = (byte*)Unsafe.AsPointer(ref Unsafe.AsRef(in this));
            return ref ((VersionedBTreeEntry*)(ptr + 32))[index];
        }
    }

    /// <summary>
    /// B+Tree entry for versioned triple (64 bytes).
    /// Key: 32 bytes + Child/Value: 8 bytes + Metadata: 24 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VersionedBTreeEntry
    {
        public VersionedKey Key;
        public long ChildOrValue;

        public long CreatedAt;
        public long ModifiedAt;
        public int Version;
        public bool IsDeleted;
        public byte Flags;
        public short Reserved;
    }

    /// <summary>
    /// Zero-allocation enumerator for versioned triples. Live entries only by default;
    /// QueryAllVersions sets <c>includeDeleted=true</c> for audit access.
    /// </summary>
    internal struct VersionedQuadEnumerator
    {
        private readonly VersionedQuadIndex _store;
        private long _currentPageId;
        private int _currentSlot;
        private readonly VersionedKey _minKey;
        private readonly VersionedKey _maxKey;
        private readonly bool _includeDeleted;
        private VersionedKey _currentKey;
        private bool _currentIsDeleted;
        private int _currentVersion;
        private long _currentCreatedAt;
        private long _currentModifiedAt;

        internal VersionedQuadEnumerator(
            VersionedQuadIndex store,
            long startPageId,
            VersionedKey minKey,
            VersionedKey maxKey,
            bool includeDeleted)
        {
            _store = store;
            _currentPageId = startPageId;
            _currentSlot = 0;
            _minKey = minKey;
            _maxKey = maxKey;
            _includeDeleted = includeDeleted;
            _currentKey = default;
            _currentIsDeleted = false;
            _currentVersion = 0;
            _currentCreatedAt = 0;
            _currentModifiedAt = 0;
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
                    _currentVersion = entry.Version;
                    _currentCreatedAt = entry.CreatedAt;
                    _currentModifiedAt = entry.ModifiedAt;

                    if (CompareKeys(in _currentKey, in _maxKey) > 0)
                        return false;

                    if (CompareKeys(in _currentKey, in _minKey) >= 0)
                    {
                        if (_includeDeleted || !_currentIsDeleted)
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

        public readonly VersionedQuad Current => new VersionedQuad
        {
            Graph = _currentKey.Graph,
            Primary = _currentKey.Primary,
            Secondary = _currentKey.Secondary,
            Tertiary = _currentKey.Tertiary,
            CreatedAt = _currentCreatedAt,
            ModifiedAt = _currentModifiedAt,
            Version = _currentVersion,
            IsDeleted = _currentIsDeleted,
        };

        public VersionedQuadEnumerator GetEnumerator() => this;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private VersionedBTreePage* GetPage(long pageId)
    {
        if (_pageCache.TryGet(pageId, out var cachedPtr))
            return (VersionedBTreePage*)cachedPtr;

        var pagePtr = (VersionedBTreePage*)(_basePtr + pageId * PageSize);
        _pageCache.Add(pageId, pagePtr);
        return pagePtr;
    }

    private void InsertIntoTree(VersionedKey key, long pageId)
    {
        var result = InsertRecursive(key, pageId);
        if (result.DidSplit && pageId == _rootPageId)
        {
            CreateNewRoot(pageId, result.PromotedKey, result.NewRightPageId);
        }
    }

    private SplitResult InsertRecursive(VersionedKey key, long pageId)
    {
        var page = GetPage(pageId);

        if (page->IsLeaf)
        {
            return InsertIntoLeaf(page, key);
        }

        var childPageId = FindChildPage(page, key);
        var childResult = InsertRecursive(key, childPageId);

        if (childResult.DidSplit)
        {
            return InsertIntoInternal(page, childResult.PromotedKey, childResult.NewRightPageId);
        }

        return default;
    }

    private void CreateNewRoot(long oldRootPageId, VersionedKey promotedKey, long newRightPageId)
    {
        var newRootId = AllocatePage();
        var newRoot = GetPage(newRootId);

        newRoot->PageId = newRootId;
        newRoot->IsLeaf = false;
        newRoot->EntryCount = 1;
        newRoot->ParentPageId = 0;
        newRoot->NextLeaf = oldRootPageId; // leftmost child (reusing NextLeaf for internal nodes)

        ref var entry = ref newRoot->GetEntry(0);
        entry.Key = promotedKey;
        entry.ChildOrValue = newRightPageId;

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

    private SplitResult InsertIntoLeaf(VersionedBTreePage* page, VersionedKey key)
    {
        // Find insertion position.
        int insertPos = 0;
        while (insertPos < page->EntryCount)
        {
            ref var entry = ref page->GetEntry(insertPos);
            int cmp = CompareKeys(in key, in entry.Key);
            if (cmp == 0)
            {
                // Duplicate key. RDF idempotency: live entry → no-op. Soft-deleted
                // entry → un-delete (bump version, set ModifiedAt). Either way no
                // new row is inserted.
                if (entry.IsDeleted)
                {
                    entry.IsDeleted = false;
                    entry.Version = checked(entry.Version + 1);
                    entry.ModifiedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    FlushPage(page);
                }
                return default;
            }
            if (cmp < 0) break;
            insertPos++;
        }

        // Page-full → split.
        if (page->EntryCount >= NodeDegree)
        {
            return SplitLeafPage(page, key);
        }

        // Shift and insert new entry.
        for (int i = page->EntryCount; i > insertPos; i--)
        {
            page->GetEntry(i) = page->GetEntry(i - 1);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ref var newEntry = ref page->GetEntry(insertPos);
        newEntry.Key = key;
        newEntry.ChildOrValue = 0;
        newEntry.CreatedAt = now;
        newEntry.ModifiedAt = now;
        newEntry.Version = 1;
        newEntry.IsDeleted = false;
        newEntry.Flags = 0;
        newEntry.Reserved = 0;

        page->EntryCount++;
        System.Threading.Interlocked.Increment(ref _quadCount);
        FlushPage(page);

        return default;
    }

    private SplitResult SplitLeafPage(VersionedBTreePage* page, VersionedKey key)
    {
        var newPageId = AllocatePage();
        var newPage = GetPage(newPageId);

        newPage->PageId = newPageId;
        newPage->IsLeaf = true;
        newPage->EntryCount = 0;
        newPage->ParentPageId = page->ParentPageId;
        newPage->NextLeaf = page->NextLeaf;
        page->NextLeaf = newPageId;

        var midPoint = NodeDegree / 2;

        for (int i = midPoint; i < page->EntryCount; i++)
        {
            newPage->GetEntry(i - midPoint) = page->GetEntry(i);
        }

        newPage->EntryCount = (short)(page->EntryCount - midPoint);
        page->EntryCount = (short)midPoint;

        var promotedKey = newPage->GetEntry(0).Key;

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

        return new SplitResult
        {
            DidSplit = true,
            PromotedKey = promotedKey,
            NewRightPageId = newPageId
        };
    }

    private SplitResult InsertIntoInternal(VersionedBTreePage* page, VersionedKey key, long rightChildPageId)
    {
        int insertPos = 0;
        while (insertPos < page->EntryCount)
        {
            ref var entry = ref page->GetEntry(insertPos);
            if (CompareKeys(in key, in entry.Key) < 0) break;
            insertPos++;
        }

        if (page->EntryCount >= NodeDegree)
        {
            return SplitInternalPage(page, key, rightChildPageId);
        }

        for (int i = page->EntryCount; i > insertPos; i--)
        {
            page->GetEntry(i) = page->GetEntry(i - 1);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ref var newEntry = ref page->GetEntry(insertPos);
        newEntry.Key = key;
        newEntry.ChildOrValue = rightChildPageId;
        newEntry.CreatedAt = now;
        newEntry.ModifiedAt = now;
        newEntry.Version = 1;
        newEntry.IsDeleted = false;

        page->EntryCount++;

        var childPage = GetPage(rightChildPageId);
        childPage->ParentPageId = page->PageId;
        FlushPage(childPage);
        FlushPage(page);

        return default;
    }

    private SplitResult SplitInternalPage(VersionedBTreePage* page, VersionedKey key, long rightChildPageId)
    {
        var newPageId = AllocatePage();
        var newPage = GetPage(newPageId);

        newPage->PageId = newPageId;
        newPage->IsLeaf = false;
        newPage->EntryCount = 0;
        newPage->ParentPageId = page->ParentPageId;

        var midPoint = NodeDegree / 2;
        var promotedKey = page->GetEntry(midPoint).Key;

        newPage->NextLeaf = page->GetEntry(midPoint).ChildOrValue;

        for (int i = midPoint + 1; i < page->EntryCount; i++)
        {
            newPage->GetEntry(i - midPoint - 1) = page->GetEntry(i);
        }

        newPage->EntryCount = (short)(page->EntryCount - midPoint - 1);
        page->EntryCount = (short)midPoint;

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

    private long FindLeafPage(long pageId, VersionedKey key)
    {
        var page = GetPage(pageId);
        if (page->IsLeaf) return pageId;
        var childPageId = FindChildPage(page, key);
        return FindLeafPage(childPageId, key);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long FindChildPage(VersionedBTreePage* page, VersionedKey key)
    {
        // Binary search: NextLeaf = leftmost child; entry[i].Key = separator,
        // entry[i].ChildOrValue = right child of that separator.
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

        if (right < 0)
            return page->NextLeaf;

        return page->GetEntry(right).ChildOrValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FlushPage(VersionedBTreePage* page)
    {
        if (_deferMsync) return;
        _accessor.Flush();
    }

    /// <summary>Force all pending mmap writes to disk. Called at end of bulk-load.</summary>
    public void Flush() => _accessor.Flush();

    /// <summary>Toggle deferred-msync mode (used by rebuild paths, mirrors TemporalQuadIndex).</summary>
    internal void SetDeferMsync(bool value) => _deferMsync = value;

    private void LoadMetadata()
    {
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
        if (_deferMsync) return;
        _accessor.Flush();
    }

    public void Dispose()
    {
        SaveMetadata();

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
    /// Reset the index to empty state. File size preserved (mmap stays valid).
    /// Must be called from QuadStore.Clear() under the write lock.
    /// </summary>
    public void Clear()
    {
        _pageCache.Clear();

        _rootPageId = 1;
        _nextPageId = 2;
        _quadCount = 0;

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
/// Versioned quad — projected from a <see cref="VersionedQuadIndex"/> entry.
/// No temporal fields; mutation audit only.
/// </summary>
internal struct VersionedQuad
{
    public long Graph;
    public long Primary;
    public long Secondary;
    public long Tertiary;
    public long CreatedAt;
    public long ModifiedAt;
    public int Version;
    public bool IsDeleted;
}
