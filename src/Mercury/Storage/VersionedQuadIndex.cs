using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkyOmega.Mercury.Runtime;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// B+Tree index for RDF quads (graph + triple) that numbers each fact's states and keeps no time. ADR-029 Graph
/// profile, with the model of ADR-064 — concrete index parallel to <see cref="TemporalQuadIndex"/> (Cognitive) and
/// <see cref="ReferenceQuadIndex"/> (Reference). 44-byte entries: the 32 B key (G, S, P, O), the 8 B child pointer and
/// the 4 B version (ADR-064 D1). Nothing in an entry is read from a clock (G9).
/// </summary>
/// <remarks>
/// <para><b>Mutation semantics (ADR-064 M5).</b> Key uniqueness is <c>(G, S, P, O)</c>. The one change is
/// <see cref="NextVersion"/>: the fact goes from the version the store decided on to the next, where an odd version
/// is asserted and an even one retracted. The store decides it from what GSPO holds (ADR-064 M4), so an index changes
/// only when it holds the fact at that version, and applying a change twice changes nothing.</para>
/// <para><b>Query semantics.</b> Single sort order (G → P → S → T). No AS_OF, no
/// time-range queries — the SPARQL planner rejects temporal queries against the
/// Graph profile at plan time. <see cref="Query"/> yields the facts that are asserted, what a client sees;
/// <see cref="QueryHeld"/> yields every fact the index holds, retracted ones too, for the store's own
/// operations.</para>
/// <para><b>Backing file:</b> FileStream + mmap + page-cache + metadata header are
/// delegated to <see cref="BTreeFile"/> in <c>Mercury.Runtime</c>.</para>
/// <para><b>INTERNAL USE ONLY.</b> Public surface is via <see cref="QuadStore"/>.</para>
/// </remarks>
internal sealed unsafe class VersionedQuadIndex : IQuadIndex
{
    /// <summary>The size of an entry: key, child pointer and version (ADR-064 D1).</summary>
    internal const int EntryBytes = 44;

    private const int NodeDegree = (BTreeFile.PageSize - BTreeFile.HeaderBytes) / EntryBytes;   // 371

    /// <summary>
    /// File-format magic for VersionedQuadIndex pages: "GRAPHVER" as long, since ADR-064 D8. The earlier layout's
    /// "GRAPHIDX" (0x4752415048494458), whose entries held two wall-clock times and a deleted mark, is refused.
    /// </summary>
    private const long MagicNumber = 0x4752415048564552L;

    private readonly BTreeFile _file;
    private readonly IAtomStore _atoms;
    private readonly bool _ownsAtomStore;

    /// <summary>Create a versioned quad store with its own atom store.</summary>
    public VersionedQuadIndex(string filePath, long initialSizeBytes = 1L << 30)
        : this(filePath, null, initialSizeBytes, bulkMode: false)
    {
    }

    /// <summary>Create a versioned quad store with a shared atom store.</summary>
    internal VersionedQuadIndex(string filePath, IAtomStore? sharedAtoms,
        long initialSizeBytes = 1L << 30, bool bulkMode = false)
    {
        _file = new BTreeFile(
            filePath,
            initialSizeBytes,
            bulkMode,
            bulkModeFloor: 256L << 30,
            magicNumber: MagicNumber,
            initRoot: InitializeRootPage);

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
    }

    private static void InitializeRootPage(BTreeFile f)
    {
        var root = f.GetPagePointer<VersionedBTreePage>(1);
        root->PageId = 1;
        root->IsLeaf = true;
        root->EntryCount = 0;
        root->ParentPageId = 0;
        root->NextLeaf = 0;
    }

    internal IAtomStore Atoms => _atoms;

    /// <summary>Compare two keys: G → P → S → T (lexicographic).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CompareKeys(in VersionedKey a, in VersionedKey b) => VersionedKey.Compare(in a, in b);

    /// <summary>
    /// The fact goes from version <paramref name="fromVersion"/> to the next (ADR-064 M5). An index that holds it at
    /// that version holds it at the next one; with <paramref name="fromVersion"/> 0, an index that does not hold it
    /// holds it at version 1. In any other state nothing changes, so applying the change again, to an index at any
    /// earlier point of the log, changes nothing more. Returns whether the index changed.
    /// </summary>
    internal bool NextVersion(long graph, long primary, long secondary, long tertiary, int fromVersion)
    {
        var key = new VersionedKey
        {
            Graph = graph,
            Primary = primary,
            Secondary = secondary,
            Tertiary = tertiary,
        };

        if (fromVersion == 0)
        {
            var heldBefore = _file.EntryCount;
            InsertIntoTree(key, _file.RootPageId);
            return _file.EntryCount > heldBefore;
        }

        var page = GetPage(FindLeafPage(_file.RootPageId, key));
        for (int i = 0; i < page->EntryCount; i++)
        {
            ref var entry = ref page->GetEntry(i);
            int cmp = CompareKeys(in entry.Key, in key);
            if (cmp > 0) break;
            if (cmp < 0) continue;
            if (entry.Version != fromVersion) return false;
            entry.Version = checked(fromVersion + 1);
            _file.FlushPage();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Insert a fact at version 1 when the index does not hold it, and change nothing when it does. Used by the
    /// secondary-index rebuild.
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

        InsertIntoTree(key, _file.RootPageId);
    }

    /// <summary>The WAL transaction up to which the saved entry count is exact (ADR-058).</summary>
    internal long CountTxId => _file.CountTxId;

    /// <summary>
    /// Change the entry count by one without touching the tree. WAL recovery uses it: a decided change counts by the
    /// transaction it belongs to, whatever the page already held (ADR-058, ADR-064 M5).
    /// </summary>
    internal void AdjustRecoveredEntryCount(int delta)
    {
        if (delta > 0) _file.IncrementEntryCount();
        else if (delta < 0) _file.DecrementEntryCount();
    }

    /// <summary>
    /// Query: yields the facts that are asserted, those whose version is odd, matching the (graph, primary,
    /// secondary, tertiary) constraints: what a client sees (ADR-064 M2). Use -1 for "any" on a dimension.
    /// </summary>
    public QuadIndexCursor Query(long graph, long primary, long secondary, long tertiary)
        => QueryInternal(graph, primary, secondary, tertiary, includeRetracted: false);

    /// <summary>
    /// Yields every fact the index holds matching the constraints, retracted ones too, each with its version: the
    /// store's own read of the version (ADR-064 D4).
    /// </summary>
    public QuadIndexCursor QueryHeld(long graph, long primary, long secondary, long tertiary)
        => QueryInternal(graph, primary, secondary, tertiary, includeRetracted: true);

    private QuadIndexCursor QueryInternal(long graph, long primary, long secondary, long tertiary, bool includeRetracted)
    {
        var cursor = new QuadIndexCursor(this);
        ref var state = ref cursor.State<ScanState>();
        state.MinKey = CreateSearchKey(graph, primary, secondary, tertiary, isMin: true);
        state.MaxKey = CreateSearchKey(graph, primary, secondary, tertiary, isMin: false);
        state.PageId = FindLeafPage(_file.RootPageId, state.MinKey);
        state.Slot = 0;
        state.IncludeRetracted = includeRetracted;
        return cursor;
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
    [StructLayout(LayoutKind.Explicit, Size = BTreeFile.PageSize)]
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
    /// B+Tree entry of the Graph profile, <see cref="EntryBytes"/> long (ADR-064 D1): the key, the child pointer of
    /// an internal page, and in a leaf the fact's version, odd when asserted and even when retracted.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct VersionedBTreeEntry
    {
        public VersionedKey Key;
        public long ChildOrValue;
        public int Version;
    }

    /// <summary>What a scan of this index keeps between rows (ADR-062 D3). It holds no object reference.</summary>
    internal struct ScanState
    {
        public long PageId;
        public int Slot;
        public bool IncludeRetracted;
        public VersionedKey MinKey;
        public VersionedKey MaxKey;
    }

    /// <inheritdoc/>
    public bool Advance(ref QuadIndexCursor cursor)
    {
        ref var state = ref cursor.State<ScanState>();

        while (state.PageId != 0)
        {
            var page = GetPage(state.PageId);

            while (state.Slot < page->EntryCount)
            {
                ref var entry = ref page->GetEntry(state.Slot);
                ref var key = ref entry.Key;

                if (CompareKeys(in key, in state.MaxKey) > 0)
                    return false;

                if (CompareKeys(in key, in state.MinKey) >= 0 && (state.IncludeRetracted || entry.Version % 2 == 1))
                {
                    cursor.Graph = key.Graph;
                    cursor.Primary = key.Primary;
                    cursor.Secondary = key.Secondary;
                    cursor.Tertiary = key.Tertiary;
                    cursor.Version = entry.Version;
                    state.Slot++;
                    return true;
                }

                state.Slot++;
            }

            state.PageId = page->NextLeaf;
            state.Slot = 0;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private VersionedBTreePage* GetPage(long pageId) =>
        _file.GetPagePointer<VersionedBTreePage>(pageId);

    private void InsertIntoTree(VersionedKey key, long pageId)
    {
        var result = InsertRecursive(key, pageId);
        if (result.DidSplit && pageId == _file.RootPageId)
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
        var newRootId = _file.AllocatePage();
        var newRoot = GetPage(newRootId);

        newRoot->PageId = newRootId;
        newRoot->IsLeaf = false;
        newRoot->EntryCount = 1;
        newRoot->ParentPageId = 0;
        newRoot->NextLeaf = oldRootPageId; // leftmost child (reusing NextLeaf for internal nodes)

        ref var entry = ref newRoot->GetEntry(0);
        entry.Key = promotedKey;
        entry.ChildOrValue = newRightPageId;
        entry.Version = 0;

        var oldRoot = GetPage(oldRootPageId);
        oldRoot->ParentPageId = newRootId;

        var rightPage = GetPage(newRightPageId);
        rightPage->ParentPageId = newRootId;

        _file.RootPageId = newRootId;

        _file.FlushPage();
        // ADR-058: save the new root now, but not the entry count, which may include
        // entries of a transaction that has not finished applying.
        _file.SaveStructure();
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
                // The index holds the fact already, at some version: nothing changes (ADR-064 M5).
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

        ref var newEntry = ref page->GetEntry(insertPos);
        newEntry.Key = key;
        newEntry.ChildOrValue = 0;
        newEntry.Version = 1;

        page->EntryCount++;
        _file.IncrementEntryCount();
        _file.FlushPage();

        return default;
    }

    private SplitResult SplitLeafPage(VersionedBTreePage* page, VersionedKey key)
    {
        var newPageId = _file.AllocatePage();
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

        _file.FlushPage();

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

        // An internal entry separates two children: it names no fact, so it holds no version.
        ref var newEntry = ref page->GetEntry(insertPos);
        newEntry.Key = key;
        newEntry.ChildOrValue = rightChildPageId;
        newEntry.Version = 0;

        page->EntryCount++;

        var childPage = GetPage(rightChildPageId);
        childPage->ParentPageId = page->PageId;
        _file.FlushPage();

        return default;
    }

    private SplitResult SplitInternalPage(VersionedBTreePage* page, VersionedKey key, long rightChildPageId)
    {
        var newPageId = _file.AllocatePage();
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

        for (int i = 0; i < newPage->EntryCount; i++)
        {
            var childId = newPage->GetEntry(i).ChildOrValue;
            var child = GetPage(childId);
            child->ParentPageId = newPageId;
        }

        if (CompareKeys(in key, in promotedKey) < 0)
        {
            InsertIntoInternal(page, key, rightChildPageId);
        }
        else
        {
            InsertIntoInternal(newPage, key, rightChildPageId);
        }

        _file.FlushPage();

        return new SplitResult
        {
            DidSplit = true,
            PromotedKey = promotedKey,
            NewRightPageId = newPageId
        };
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

    /// <summary>Force all pending mmap writes to disk. Called at end of bulk-load.</summary>
    /// <remarks>
    /// Saves the header with the current entry count first: page allocation saves only the
    /// tree structure (ADR-058), so the count reaches disk here, at commit, or at Dispose.
    /// </remarks>
    public void Flush()
    {
        _file.SaveMetadata();
        _file.Flush();
    }

    /// <summary>Save the entry count as exact up to WAL transaction <paramref name="txId"/> (ADR-058).</summary>
    internal void CommitEntryCount(long txId, bool durable) => _file.CommitEntryCount(txId, durable);

    /// <summary>
    /// Take the entry count back to the one last saved with its transaction, before a close that follows a
    /// transaction this index did not finish applying (ADR-058, ADR-061 M7).
    /// </summary>
    internal void ReturnToSavedEntryCount() => _file.ReturnToSavedEntryCount();

    /// <summary>Toggle deferred-msync mode (used by rebuild paths, mirrors TemporalQuadIndex).</summary>
    internal void SetDeferMsync(bool value) => _file.DeferMsync = value;

    public void Dispose()
    {
        _file.Dispose();
        if (_ownsAtomStore)
            _atoms?.Dispose();
    }

    public long QuadCount => _file.EntryCount;

    /// <summary>
    /// Reset the index to empty state. File size preserved (mmap stays valid).
    /// Must be called from QuadStore.Clear() under the write lock.
    /// </summary>
    public void Clear()
    {
        _file.Reset();
        InitializeRootPage(_file);
        _file.FlushPage();
        _file.SaveMetadata();
    }
}
