using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkyOmega.Mercury.Runtime;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// B+Tree index for RDF triples under the Minimal storage profile (ADR-029 Decision 3
/// completion, 2026-05-16): 24-byte keys carrying only Primary/Secondary/Tertiary atom IDs,
/// no graph dimension, no temporal dimension, no versioning, no soft-delete. Suited to
/// single-graph Linked Data endpoints — the simplest RDF store shape Mercury supports.
/// Distinct concrete class per the no-behavior-flags rule (`feedback_no_behavior_flags.md`,
/// 2026-05-16). Closes the four-profile matrix alongside <see cref="TemporalQuadIndex"/>,
/// <see cref="VersionedQuadIndex"/>, and <see cref="ReferenceQuadIndex"/>.
/// </summary>
/// <remarks>
/// <para><strong>INTERNAL USE ONLY:</strong> Accessed through <see cref="QuadStore"/>
/// under its reader/writer lock; not safe for direct concurrent use.</para>
///
/// <para><strong>Layout:</strong> Leaf entries are 24 B (the key itself). Internal entries
/// are 32 B (24 B separator key + 8 B right-child page id). Page size is 16 KB with a 32 B
/// header. Leaves hold up to 681 entries; internals hold up to 510. Single sort order:
/// Primary → Secondary → Tertiary.</para>
///
/// <para><strong>Set semantics:</strong> Re-adding the same (P,S,T) triple is a silent
/// no-op. Mercury's Minimal profile is bulk-load-only at the session API per ADR-029
/// Decision 7 (no WAL, no soft-delete metadata to update).</para>
///
/// <para><strong>Graph dimension:</strong> not stored. QuadStore enforces graph=default
/// at the API boundary for Minimal-profile stores; queries with named-graph constraints
/// are rejected at plan time.</para>
///
/// <para><strong>Backing file:</strong> the FileStream + mmap + page-cache + metadata
/// header are delegated to <see cref="BTreeFile"/> in <c>Mercury.Runtime</c>; this class
/// owns only the B+Tree algorithm and the 24-byte key layout.</para>
/// </remarks>
internal sealed unsafe class MinimalQuadIndex : IQuadIndex
{
    private const int LeafEntryBytes = 24;
    private const int InternalEntryBytes = 32;
    internal const int LeafDegree = (BTreeFile.PageSize - BTreeFile.HeaderBytes) / LeafEntryBytes;          // 681
    internal const int InternalDegree = (BTreeFile.PageSize - BTreeFile.HeaderBytes) / InternalEntryBytes;  // 510

    private readonly BTreeFile _file;
    private readonly IAtomStore _atoms;
    private readonly bool _ownsAtomStore;

    /// <summary>
    /// Magic number stamped into the index file header. Distinct from Reference / Cognitive /
    /// Graph magics so opening a wrong-profile store fails loudly at metadata load.
    /// "MINIMAL\0".
    /// </summary>
    private const long MagicNumber = 0x4D494E494D414C00L;

    /// <summary>Create a Minimal index with a shared IAtomStore (owned by QuadStore).</summary>
    internal MinimalQuadIndex(string filePath, IAtomStore? sharedAtoms,
        long initialSizeBytes = 1L << 30, bool bulkMode = false)
    {
        // Minimal profile targets small-to-mid-scale Linked Data endpoints. The 1 GB
        // bulk-mode floor covers ~28M entries at 24 B/entry × 681 entries/leaf — plenty
        // for typical Minimal workloads.
        _file = new BTreeFile(
            filePath,
            initialSizeBytes,
            bulkMode,
            bulkModeFloor: 1L << 30,
            magicNumber: MagicNumber,
            initRoot: InitializeRootPage);

        if (sharedAtoms != null)
        {
            _atoms = sharedAtoms;
            _ownsAtomStore = false;
        }
        else
        {
            _atoms = new HashAtomStore(filePath + ".atoms");
            _ownsAtomStore = true;
        }
    }

    private static void InitializeRootPage(BTreeFile f)
    {
        var root = f.GetPagePointer<MinimalPageHeader>(1);
        root->PageId = 1;
        root->IsLeaf = true;
        root->EntryCount = 0;
        root->ParentPageId = 0;
        root->NextLeafOrLeftmostChild = 0;
    }

    internal IAtomStore Atoms => _atoms;

    // ===== IQuadIndex =====

    public long QuadCount => _file.EntryCount;

    public void Flush() => _file.Flush();

    public void Clear()
    {
        _file.Reset();
        InitializeRootPage(_file);
        _file.FlushPage();
        _file.SaveMetadata();
    }

    public void Dispose()
    {
        _file.Dispose();
        if (_ownsAtomStore)
            _atoms?.Dispose();
    }

    internal void SetDeferMsync(bool value) => _file.DeferMsync = value;

    // ===== Public add =====

    /// <summary>Add a triple, interning the string forms into atom IDs first.</summary>
    public void Add(ReadOnlySpan<char> primary, ReadOnlySpan<char> secondary, ReadOnlySpan<char> tertiary)
    {
        var p = _atoms.Intern(primary);
        var s = _atoms.Intern(secondary);
        var t = _atoms.Intern(tertiary);
        AddRaw(p, s, t);
    }

    /// <summary>
    /// Append a triple by pre-resolved atom IDs. Duplicate keys (exact P/S/T match) are
    /// silent no-ops — RDF set semantics enforced at the B+Tree level.
    /// </summary>
    internal void AddRaw(long primary, long secondary, long tertiary)
    {
        var key = new MinimalKey { Primary = primary, Secondary = secondary, Tertiary = tertiary };
        InsertIntoTree(key, _file.RootPageId);
    }

    // ===== Query =====

    /// <summary>
    /// Query: yields all entries matching the (primary, secondary, tertiary) constraints.
    /// Use -1 for "any" on a dimension. Minimal has only one index (single GSPO
    /// orientation), so non-subject-bound queries scan the entire range.
    /// </summary>
    public MinimalQuadEnumerator Query(long primary, long secondary, long tertiary)
    {
        var minKey = CreateSearchKey(primary, secondary, tertiary, isMin: true);
        var maxKey = CreateSearchKey(primary, secondary, tertiary, isMin: false);
        var leafPageId = FindLeafPage(_file.RootPageId, minKey);
        return new MinimalQuadEnumerator(this, leafPageId, minKey, maxKey);
    }

    private static MinimalKey CreateSearchKey(long primary, long secondary, long tertiary, bool isMin)
    {
        var unbound = isMin ? 0L : long.MaxValue;
        return new MinimalKey
        {
            Primary = primary < 0 ? unbound : primary,
            Secondary = secondary < 0 ? unbound : secondary,
            Tertiary = tertiary < 0 ? unbound : tertiary,
        };
    }

    // ===== Types =====

    /// <summary>
    /// 24-byte key for Minimal-profile triples. Lexicographic compare: P → S → T.
    /// No graph dimension.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct MinimalKey : IComparable<MinimalKey>, IEquatable<MinimalKey>
    {
        public long Primary;
        public long Secondary;
        public long Tertiary;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int CompareTo(MinimalKey other) => Compare(in this, in other);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int Compare(in MinimalKey a, in MinimalKey b)
        {
            var cmp = a.Primary.CompareTo(b.Primary);
            if (cmp != 0) return cmp;
            cmp = a.Secondary.CompareTo(b.Secondary);
            if (cmp != 0) return cmp;
            return a.Tertiary.CompareTo(b.Tertiary);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool Equals(MinimalKey other) =>
            Primary == other.Primary && Secondary == other.Secondary && Tertiary == other.Tertiary;

        public override readonly bool Equals(object? obj) => obj is MinimalKey k && Equals(k);
        public override readonly int GetHashCode() => HashCode.Combine(Primary, Secondary, Tertiary);
    }

    /// <summary>Internal-node entry: separator key + right-child page pointer.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct MinimalInternalEntry
    {
        public MinimalKey Key;     // 24 B
        public long ChildPageId;   //  8 B
    }

    /// <summary>32-byte page header shared by leaf and internal pages.</summary>
    [StructLayout(LayoutKind.Explicit, Size = BTreeFile.HeaderBytes)]
    private struct MinimalPageHeader
    {
        [FieldOffset(0)] public long PageId;
        [FieldOffset(8)] public bool IsLeaf;
        [FieldOffset(10)] public short EntryCount;
        [FieldOffset(16)] public long ParentPageId;
        [FieldOffset(24)] public long NextLeafOrLeftmostChild;
    }

    /// <summary>Pointer-based view of a page.</summary>
    private readonly struct PageView
    {
        public readonly byte* Base;
        public PageView(byte* basePtr) => Base = basePtr;

        public ref MinimalPageHeader Header
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref Unsafe.AsRef<MinimalPageHeader>(Base);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref MinimalKey LeafEntry(int index) =>
            ref ((MinimalKey*)(Base + BTreeFile.HeaderBytes))[index];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref MinimalInternalEntry InternalEntry(int index) =>
            ref ((MinimalInternalEntry*)(Base + BTreeFile.HeaderBytes))[index];
    }

    /// <summary>Zero-allocation enumerator for a bounded leaf-page scan.</summary>
    internal struct MinimalQuadEnumerator
    {
        private readonly MinimalQuadIndex _index;
        private long _currentPageId;
        private int _currentSlot;
        private readonly MinimalKey _minKey;
        private readonly MinimalKey _maxKey;
        private MinimalKey _currentKey;

        internal MinimalQuadEnumerator(MinimalQuadIndex index, long startPageId,
            MinimalKey minKey, MinimalKey maxKey)
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

                    if (MinimalKey.Compare(in _currentKey, in _maxKey) > 0)
                        return false;

                    if (MinimalKey.Compare(in _currentKey, in _minKey) >= 0)
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

        public readonly MinimalKey Current => _currentKey;

        public MinimalQuadEnumerator GetEnumerator() => this;
    }

    // ===== Page access =====

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private MinimalPageHeader* GetPage(long pageId) =>
        _file.GetPagePointer<MinimalPageHeader>(pageId);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PageView GetView(long pageId) => new((byte*)GetPage(pageId));

    // ===== B+Tree ops =====

    private struct SplitResult
    {
        public bool DidSplit;
        public MinimalKey PromotedKey;
        public long NewRightPageId;
    }

    private void InsertIntoTree(MinimalKey key, long pageId)
    {
        var result = InsertRecursive(key, pageId);
        if (result.DidSplit && pageId == _file.RootPageId)
            CreateNewRoot(pageId, result.PromotedKey, result.NewRightPageId);
    }

    private SplitResult InsertRecursive(MinimalKey key, long pageId)
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

    private SplitResult InsertIntoLeaf(PageView view, MinimalKey key)
    {
        int count = view.Header.EntryCount;
        int insertPos = 0;
        while (insertPos < count)
        {
            var cmp = MinimalKey.Compare(in key, in view.LeafEntry(insertPos));
            if (cmp < 0) break;
            if (cmp == 0)
                return default; // RDF set semantics — exact duplicate is a no-op.
            insertPos++;
        }

        if (count >= LeafDegree)
            return SplitLeafPage(view, key);

        for (int i = count; i > insertPos; i--)
            view.LeafEntry(i) = view.LeafEntry(i - 1);

        view.LeafEntry(insertPos) = key;
        view.Header.EntryCount = (short)(count + 1);
        _file.IncrementEntryCount();
        _file.FlushPage();
        return default;
    }

    private SplitResult SplitLeafPage(PageView page, MinimalKey key)
    {
        var newPageId = _file.AllocatePage();
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

        if (MinimalKey.Compare(in key, in promoted) < 0)
            InsertIntoLeaf(page, key);
        else
            InsertIntoLeaf(newView, key);

        _file.FlushPage();
        return new SplitResult { DidSplit = true, PromotedKey = promoted, NewRightPageId = newPageId };
    }

    private SplitResult InsertIntoInternal(PageView view, MinimalKey key, long rightChildPageId)
    {
        int count = view.Header.EntryCount;
        int insertPos = 0;
        while (insertPos < count)
        {
            if (MinimalKey.Compare(in key, in view.InternalEntry(insertPos).Key) < 0)
                break;
            insertPos++;
        }

        if (count >= InternalDegree)
            return SplitInternalPage(view, key, rightChildPageId);

        for (int i = count; i > insertPos; i--)
            view.InternalEntry(i) = view.InternalEntry(i - 1);

        view.InternalEntry(insertPos) = new MinimalInternalEntry { Key = key, ChildPageId = rightChildPageId };
        view.Header.EntryCount = (short)(count + 1);

        var child = GetPage(rightChildPageId);
        child->ParentPageId = view.Header.PageId;

        _file.FlushPage();
        return default;
    }

    private SplitResult SplitInternalPage(PageView page, MinimalKey key, long rightChildPageId)
    {
        var newPageId = _file.AllocatePage();
        var newView = GetView(newPageId);
        ref var newHeader = ref newView.Header;
        ref var pageHeader = ref page.Header;

        newHeader.PageId = newPageId;
        newHeader.IsLeaf = false;
        newHeader.EntryCount = 0;
        newHeader.ParentPageId = pageHeader.ParentPageId;

        int mid = InternalDegree / 2;
        var promoted = page.InternalEntry(mid).Key;

        newHeader.NextLeafOrLeftmostChild = page.InternalEntry(mid).ChildPageId;

        for (int i = mid + 1; i < pageHeader.EntryCount; i++)
            newView.InternalEntry(i - mid - 1) = page.InternalEntry(i);

        newHeader.EntryCount = (short)(pageHeader.EntryCount - mid - 1);
        pageHeader.EntryCount = (short)mid;

        var leftmost = GetPage(newHeader.NextLeafOrLeftmostChild);
        leftmost->ParentPageId = newPageId;

        for (int i = 0; i < newHeader.EntryCount; i++)
        {
            var childId = newView.InternalEntry(i).ChildPageId;
            var child = GetPage(childId);
            child->ParentPageId = newPageId;
        }

        if (MinimalKey.Compare(in key, in promoted) < 0)
            InsertIntoInternal(page, key, rightChildPageId);
        else
            InsertIntoInternal(newView, key, rightChildPageId);

        _file.FlushPage();
        return new SplitResult { DidSplit = true, PromotedKey = promoted, NewRightPageId = newPageId };
    }

    private void CreateNewRoot(long oldRootPageId, MinimalKey promotedKey, long newRightPageId)
    {
        var newRootId = _file.AllocatePage();
        var newRoot = GetView(newRootId);
        ref var header = ref newRoot.Header;

        header.PageId = newRootId;
        header.IsLeaf = false;
        header.EntryCount = 1;
        header.ParentPageId = 0;
        header.NextLeafOrLeftmostChild = oldRootPageId;

        newRoot.InternalEntry(0) = new MinimalInternalEntry
        {
            Key = promotedKey,
            ChildPageId = newRightPageId
        };

        var oldRoot = GetPage(oldRootPageId);
        oldRoot->ParentPageId = newRootId;

        var right = GetPage(newRightPageId);
        right->ParentPageId = newRootId;

        _file.RootPageId = newRootId;
        _file.FlushPage();
        _file.SaveMetadata();
    }

    private long FindLeafPage(long pageId, MinimalKey key)
    {
        while (true)
        {
            var view = GetView(pageId);
            if (view.Header.IsLeaf) return pageId;
            pageId = FindChildPage(view, key);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long FindChildPage(PageView view, MinimalKey key)
    {
        int left = 0, right = view.Header.EntryCount - 1;
        while (left <= right)
        {
            int mid = left + ((right - left) >> 1);
            var cmp = MinimalKey.Compare(in key, in view.InternalEntry(mid).Key);
            if (cmp < 0) right = mid - 1;
            else left = mid + 1;
        }
        if (right < 0) return view.Header.NextLeafOrLeftmostChild;
        return view.InternalEntry(right).ChildPageId;
    }
}
