using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// B+Tree index for RDF quads under the Reference storage profile (ADR-029 Decision 3):
/// 32-byte keys carrying only atom IDs, no temporal dimension, no per-entry versioning
/// or soft-delete metadata. The whole store is a set of (graph, primary, secondary,
/// tertiary) tuples; inserting an existing key is a silent no-op (ADR-029 Decision 7 —
/// Cases A and B of the temporal index collapse into one rule under "RDF is a set of
/// triples" semantics).
/// </summary>
/// <remarks>
/// <para><strong>INTERNAL USE ONLY:</strong> Accessed through <see cref="QuadStore"/>
/// under its reader/writer lock; not safe for direct concurrent use.</para>
///
/// <para><strong>Layout:</strong> Leaf entries are 32 B (the key itself). Internal
/// entries are 40 B (32 B separator key + 8 B right-child page id). Page size is 16 KB
/// with a 32 B header (<see cref="ReferencePageHeader"/>). Leaves hold up to 511
/// entries; internals hold up to 408. Split logic is uniform — both page types follow
/// the same invariants, only the accessor math differs.</para>
///
/// <para><strong>Concurrency:</strong> Mutations happen under <see cref="QuadStore"/>'s
/// writer lock; this class is not internally synchronized for writes.</para>
/// </remarks>
internal sealed unsafe class ReferenceQuadIndex : IQuadIndex
{
    private const int PageSize = 16384;
    private const int HeaderBytes = 32;
    private const int LeafEntryBytes = 32;
    private const int InternalEntryBytes = 40;
    internal const int LeafDegree = (PageSize - HeaderBytes) / LeafEntryBytes;          // 511
    internal const int InternalDegree = (PageSize - HeaderBytes) / InternalEntryBytes;  // 408

    private readonly FileStream _fileStream;
    private readonly MemoryMappedFile _mmapFile;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly AtomStore _atoms;
    private readonly bool _ownsAtomStore;
    private readonly PageCache _pageCache;

    // Mirror the temporal index's msync-deferral knob for bulk ingest symmetry.
    private bool _deferMsync;

    private byte* _basePtr;
    private long _rootPageId;
    private long _nextPageId;
    private long _quadCount;
    private long _fileCapacity;

    /// <summary>
    /// Magic number stamped into the index file header. Distinct from TemporalQuadIndex's
    /// magic so opening a Cognitive store as Reference (or vice versa) fails loudly at
    /// metadata load rather than corrupting through mismatched key layouts.
    /// </summary>
    private const long MagicNumber = 0x5245464552454E4EL; // "REFERENN"

    /// <summary>Create a reference index with a shared AtomStore (owned by QuadStore).</summary>
    internal ReferenceQuadIndex(string filePath, AtomStore? sharedAtoms,
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

        if (sharedAtoms != null)
        {
            _atoms = sharedAtoms;
            _ownsAtomStore = false;
        }
        else
        {
            _atoms = new AtomStore(filePath + ".atoms");
            _ownsAtomStore = true;
        }

        _pageCache = new PageCache(capacity: 10_000);

        LoadMetadata();
    }

    /// <summary>Internal access to the shared atom store (under QuadStore lock).</summary>
    internal AtomStore Atoms => _atoms;

    #region IQuadIndex

    public long QuadCount => _quadCount;

    public void Flush() => _accessor.Flush();

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
        root->NextLeafOrLeftmostChild = 0;

        FlushPage();
        SaveMetadata();
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

    #endregion

    #region Public add/query

    /// <summary>Add a quad, interning the string forms into atom IDs first.</summary>
    public void Add(ReadOnlySpan<char> primary, ReadOnlySpan<char> secondary,
        ReadOnlySpan<char> tertiary, ReadOnlySpan<char> graph = default)
    {
        var g = graph.IsEmpty ? 0 : _atoms.Intern(graph);
        var a = _atoms.Intern(primary);
        var b = _atoms.Intern(secondary);
        var c = _atoms.Intern(tertiary);
        AddRaw(g, a, b, c);
    }

    /// <summary>
    /// Append a quad by pre-resolved atom IDs. Duplicate keys (exact G/P/S/T match) are
    /// silent no-ops per ADR-029 Decision 7.
    /// </summary>
    internal void AddRaw(long graph, long primary, long secondary, long tertiary)
    {
        var key = new ReferenceKey { Graph = graph, Primary = primary, Secondary = secondary, Tertiary = tertiary };
        InsertIntoTree(key, _rootPageId);
    }

    /// <summary>
    /// Mirrors TemporalQuadIndex.SetDeferMsync — toggled by QuadStore around bulk
    /// phases. Caller is responsible for invoking Flush() before turning deferral off.
    /// </summary>
    internal void SetDeferMsync(bool value) => _deferMsync = value;

    /// <summary>Number of times Query pages have been traversed (debug builds only).</summary>
    public ReferenceQuadEnumerator Query(long graph, long primary, long secondary, long tertiary)
    {
        var minKey = BuildSearchKey(graph, primary, secondary, tertiary, isMin: true);
        var maxKey = BuildSearchKey(graph, primary, secondary, tertiary, isMin: false);

        var leafPageId = FindLeafPage(_rootPageId, minKey);
        return new ReferenceQuadEnumerator(this, leafPageId, minKey, maxKey);
    }

    private static ReferenceKey BuildSearchKey(long graph, long primary, long secondary, long tertiary, bool isMin)
    {
        var unbound = isMin ? 0L : long.MaxValue;

        // -2 sentinel propagated from QuadStore's "graph specified but unknown" path:
        // entries have non-negative Graph, so -2/-2 bounds select an empty scan.
        long graphValue;
        if (graph == -2) graphValue = -2;
        else if (graph < 0) graphValue = unbound;
        else graphValue = graph;

        return new ReferenceKey
        {
            Graph = graphValue,
            Primary = primary < 0 ? unbound : primary,
            Secondary = secondary < 0 ? unbound : secondary,
            Tertiary = tertiary < 0 ? unbound : tertiary
        };
    }

    #endregion

    #region Key struct

    /// <summary>
    /// 32-byte key for reference-profile quads. Compared lexicographically in field order:
    /// Graph → Primary → Secondary → Tertiary. QuadStore chooses the atom-to-dimension
    /// mapping per index (GSPO: S→Primary; GPOS: P→Primary; etc.).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct ReferenceKey : IComparable<ReferenceKey>, IEquatable<ReferenceKey>
    {
        public long Graph;
        public long Primary;
        public long Secondary;
        public long Tertiary;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int CompareTo(ReferenceKey other) => Compare(in this, in other);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int Compare(in ReferenceKey a, in ReferenceKey b)
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
        public readonly bool Equals(ReferenceKey other) =>
            Graph == other.Graph && Primary == other.Primary &&
            Secondary == other.Secondary && Tertiary == other.Tertiary;

        public override readonly bool Equals(object? obj) => obj is ReferenceKey k && Equals(k);
        public override readonly int GetHashCode() => HashCode.Combine(Graph, Primary, Secondary, Tertiary);
    }

    /// <summary>
    /// Internal-node entry: separator key plus pointer to the right-child page.
    /// Internal-node leftmost child is stored in the page header's
    /// <see cref="ReferencePageHeader.NextLeafOrLeftmostChild"/> slot.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct ReferenceInternalEntry
    {
        public ReferenceKey Key;     // 32 B
        public long ChildPageId;     //  8 B
    }

    /// <summary>
    /// 32-byte page header shared by leaf and internal pages. On leaf pages the
    /// <see cref="NextLeafOrLeftmostChild"/> field is the next-leaf link; on internal pages
    /// it is the leftmost child pointer (B+Tree separator layout).
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = HeaderBytes)]
    private struct ReferencePageHeader
    {
        [FieldOffset(0)] public long PageId;
        [FieldOffset(8)] public bool IsLeaf;
        [FieldOffset(10)] public short EntryCount;
        [FieldOffset(16)] public long ParentPageId;
        [FieldOffset(24)] public long NextLeafOrLeftmostChild;
    }

    /// <summary>Pointer-based view of a page — leaf or internal depending on IsLeaf.</summary>
    private readonly struct PageView
    {
        public readonly byte* Base;
        public PageView(byte* basePtr) => Base = basePtr;

        public ref ReferencePageHeader Header
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref Unsafe.AsRef<ReferencePageHeader>(Base);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref ReferenceKey LeafEntry(int index) =>
            ref ((ReferenceKey*)(Base + HeaderBytes))[index];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref ReferenceInternalEntry InternalEntry(int index) =>
            ref ((ReferenceInternalEntry*)(Base + HeaderBytes))[index];
    }

    #endregion

    #region Page access / metadata

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReferencePageHeader* GetPage(long pageId)
    {
        if (_pageCache.TryGet(pageId, out var cached))
            return (ReferencePageHeader*)cached;
        var ptr = _basePtr + pageId * PageSize;
        _pageCache.Add(pageId, ptr);
        return (ReferencePageHeader*)ptr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PageView GetView(long pageId) => new((byte*)GetPage(pageId));

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FlushPage()
    {
        if (_deferMsync) return;
        _accessor.Flush();
    }

    private void LoadMetadata()
    {
        _accessor.Read(24, out long magic);
        if (magic == MagicNumber)
        {
            _accessor.Read(0, out _rootPageId);
            _accessor.Read(8, out _nextPageId);
            _accessor.Read(16, out _quadCount);
        }
        else if (magic != 0)
        {
            throw new InvalidDataException(
                $"Index file magic 0x{magic:X16} is not a Reference-profile index " +
                "(expected REFERENN marker). Opening a Cognitive or otherwise-shaped index " +
                "as Reference is refused by design — reload from source to change profiles.");
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
            root->NextLeafOrLeftmostChild = 0;

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

    #endregion

    #region B+Tree ops

    private struct SplitResult
    {
        public bool DidSplit;
        public ReferenceKey PromotedKey;
        public long NewRightPageId;
    }

    private void InsertIntoTree(ReferenceKey key, long pageId)
    {
        var result = InsertRecursive(key, pageId);
        if (result.DidSplit && pageId == _rootPageId)
            CreateNewRoot(pageId, result.PromotedKey, result.NewRightPageId);
    }

    private SplitResult InsertRecursive(ReferenceKey key, long pageId)
    {
        var view = GetView(pageId);
        if (view.Header.IsLeaf)
            return InsertIntoLeaf(view, key);

        var childId = FindChildPage(view, key);
        var childResult = InsertRecursive(key, childId);
        if (childResult.DidSplit)
            return InsertIntoInternal(view, childResult.PromotedKey, childResult.NewRightPageId);
        return default;
    }

    private SplitResult InsertIntoLeaf(PageView view, ReferenceKey key)
    {
        int count = view.Header.EntryCount;
        int insertPos = 0;
        while (insertPos < count)
        {
            var cmp = ReferenceKey.Compare(in key, in view.LeafEntry(insertPos));
            if (cmp < 0) break;
            if (cmp == 0)
                return default; // ADR-029 §7: exact duplicate is a silent no-op.
            insertPos++;
        }

        if (count >= LeafDegree)
            return SplitLeafPage(view, key, insertPos);

        for (int i = count; i > insertPos; i--)
            view.LeafEntry(i) = view.LeafEntry(i - 1);

        view.LeafEntry(insertPos) = key;
        view.Header.EntryCount = (short)(count + 1);
        System.Threading.Interlocked.Increment(ref _quadCount);
        FlushPage();
        return default;
    }

    private SplitResult SplitLeafPage(PageView page, ReferenceKey key, int insertPosHint)
    {
        var newPageId = AllocatePage();
        var newView = GetView(newPageId);
        ref var newHeader = ref newView.Header;
        ref var pageHeader = ref page.Header;

        newHeader.PageId = newPageId;
        newHeader.IsLeaf = true;
        newHeader.EntryCount = 0;
        newHeader.ParentPageId = pageHeader.ParentPageId;
        newHeader.NextLeafOrLeftmostChild = pageHeader.NextLeafOrLeftmostChild;
        pageHeader.NextLeafOrLeftmostChild = newPageId;

        int mid = LeafDegree / 2;
        for (int i = mid; i < pageHeader.EntryCount; i++)
            newView.LeafEntry(i - mid) = page.LeafEntry(i);

        newHeader.EntryCount = (short)(pageHeader.EntryCount - mid);
        pageHeader.EntryCount = (short)mid;

        var promoted = newView.LeafEntry(0);

        // Insert the newcomer into whichever half it now belongs in.
        if (ReferenceKey.Compare(in key, in promoted) < 0)
            InsertIntoLeaf(page, key);
        else
            InsertIntoLeaf(newView, key);

        FlushPage();
        return new SplitResult { DidSplit = true, PromotedKey = promoted, NewRightPageId = newPageId };
    }

    private SplitResult InsertIntoInternal(PageView view, ReferenceKey key, long rightChildPageId)
    {
        int count = view.Header.EntryCount;
        int insertPos = 0;
        while (insertPos < count)
        {
            if (ReferenceKey.Compare(in key, in view.InternalEntry(insertPos).Key) < 0)
                break;
            insertPos++;
        }

        if (count >= InternalDegree)
            return SplitInternalPage(view, key, rightChildPageId);

        for (int i = count; i > insertPos; i--)
            view.InternalEntry(i) = view.InternalEntry(i - 1);

        view.InternalEntry(insertPos) = new ReferenceInternalEntry { Key = key, ChildPageId = rightChildPageId };
        view.Header.EntryCount = (short)(count + 1);

        var child = GetPage(rightChildPageId);
        child->ParentPageId = view.Header.PageId;

        FlushPage();
        return default;
    }

    private SplitResult SplitInternalPage(PageView page, ReferenceKey key, long rightChildPageId)
    {
        var newPageId = AllocatePage();
        var newView = GetView(newPageId);
        ref var newHeader = ref newView.Header;
        ref var pageHeader = ref page.Header;

        newHeader.PageId = newPageId;
        newHeader.IsLeaf = false;
        newHeader.EntryCount = 0;
        newHeader.ParentPageId = pageHeader.ParentPageId;

        int mid = InternalDegree / 2;
        var promoted = page.InternalEntry(mid).Key;

        // Middle key's child pointer becomes leftmost child of the right half.
        newHeader.NextLeafOrLeftmostChild = page.InternalEntry(mid).ChildPageId;

        for (int i = mid + 1; i < pageHeader.EntryCount; i++)
            newView.InternalEntry(i - mid - 1) = page.InternalEntry(i);

        newHeader.EntryCount = (short)(pageHeader.EntryCount - mid - 1);
        pageHeader.EntryCount = (short)mid;

        // Reparent moved children.
        var leftmost = GetPage(newHeader.NextLeafOrLeftmostChild);
        leftmost->ParentPageId = newPageId;

        for (int i = 0; i < newHeader.EntryCount; i++)
        {
            var childId = newView.InternalEntry(i).ChildPageId;
            var child = GetPage(childId);
            child->ParentPageId = newPageId;
        }

        if (ReferenceKey.Compare(in key, in promoted) < 0)
            InsertIntoInternal(page, key, rightChildPageId);
        else
            InsertIntoInternal(newView, key, rightChildPageId);

        FlushPage();
        return new SplitResult { DidSplit = true, PromotedKey = promoted, NewRightPageId = newPageId };
    }

    private void CreateNewRoot(long oldRootPageId, ReferenceKey promotedKey, long newRightPageId)
    {
        var newRootId = AllocatePage();
        var newRoot = GetView(newRootId);
        ref var header = ref newRoot.Header;

        header.PageId = newRootId;
        header.IsLeaf = false;
        header.EntryCount = 1;
        header.ParentPageId = 0;
        header.NextLeafOrLeftmostChild = oldRootPageId;

        newRoot.InternalEntry(0) = new ReferenceInternalEntry
        {
            Key = promotedKey,
            ChildPageId = newRightPageId
        };

        var oldRoot = GetPage(oldRootPageId);
        oldRoot->ParentPageId = newRootId;

        var right = GetPage(newRightPageId);
        right->ParentPageId = newRootId;

        _rootPageId = newRootId;
        FlushPage();
        SaveMetadata();
    }

    private long FindLeafPage(long pageId, ReferenceKey key)
    {
        while (true)
        {
            var view = GetView(pageId);
            if (view.Header.IsLeaf) return pageId;
            pageId = FindChildPage(view, key);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long FindChildPage(PageView view, ReferenceKey key)
    {
        int left = 0, right = view.Header.EntryCount - 1;
        while (left <= right)
        {
            int mid = left + ((right - left) >> 1);
            var cmp = ReferenceKey.Compare(in key, in view.InternalEntry(mid).Key);
            if (cmp < 0) right = mid - 1;
            else left = mid + 1;
        }
        if (right < 0) return view.Header.NextLeafOrLeftmostChild;
        return view.InternalEntry(right).ChildPageId;
    }

    #endregion

    #region Enumerator

    /// <summary>
    /// Zero-allocation enumerator for a bounded leaf-page scan. The enumerator walks
    /// leaves via <see cref="ReferencePageHeader.NextLeafOrLeftmostChild"/> until a key
    /// exceeds the max bound.
    /// </summary>
    internal struct ReferenceQuadEnumerator
    {
        private readonly ReferenceQuadIndex _index;
        private long _currentPageId;
        private int _currentSlot;
        private readonly ReferenceKey _minKey;
        private readonly ReferenceKey _maxKey;
        private ReferenceKey _currentKey;

        internal ReferenceQuadEnumerator(ReferenceQuadIndex index, long startPageId,
            ReferenceKey minKey, ReferenceKey maxKey)
        {
            _index = index;
            _currentPageId = startPageId;
            _currentSlot = 0;
            _minKey = minKey;
            _maxKey = maxKey;
            _currentKey = default;
        }

        public bool MoveNext()
        {
            while (_currentPageId != 0)
            {
                var view = _index.GetView(_currentPageId);
                ref var header = ref view.Header;

                while (_currentSlot < header.EntryCount)
                {
                    _currentKey = view.LeafEntry(_currentSlot);

                    if (ReferenceKey.Compare(in _currentKey, in _maxKey) > 0)
                        return false;

                    if (ReferenceKey.Compare(in _currentKey, in _minKey) >= 0)
                    {
                        _currentSlot++;
                        return true;
                    }

                    _currentSlot++;
                }

                _currentPageId = header.NextLeafOrLeftmostChild;
                _currentSlot = 0;
            }
            return false;
        }

        public readonly ReferenceKey Current => _currentKey;

        public ReferenceQuadEnumerator GetEnumerator() => this;
    }

    #endregion
}
