using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkyOmega.Mercury.Runtime;

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
/// through QuadStore's public API, not directly through TemporalQuadIndex.</para>
/// </remarks>
internal sealed unsafe class TemporalQuadIndex : IQuadIndex
{
    private const int NodeDegree = 185; // (16384 - 32) / 88 bytes per temporal entry
    private const long MagicNumber = 0x54454D504F52414C; // "TEMPORAL" as long

    private readonly BTreeFile _file;
    private readonly IAtomStore _atoms;
    private readonly bool _ownsAtomStore;
    private readonly KeyComparer _comparer;
    private readonly KeySortOrder _sortOrder;

    /// <summary>
    /// Create a temporal quad store with its own atom store
    /// </summary>
    public TemporalQuadIndex(string filePath, long initialSizeBytes = 1L << 30)
        : this(filePath, null, initialSizeBytes, KeySortOrder.EntityFirst)
    {
    }

    /// <summary>
    /// Create a temporal quad store with a shared atom store.
    /// </summary>
    /// <remarks>
    /// Internal: IAtomStore parameter requires external synchronization.
    /// Use this constructor only when sharing an IAtomStore across indexes.
    /// </remarks>
    internal TemporalQuadIndex(string filePath, IAtomStore? sharedAtoms, long initialSizeBytes = 1L << 30,
        KeySortOrder sortOrder = KeySortOrder.EntityFirst, bool bulkMode = false)
    {
        _sortOrder = sortOrder;
        _comparer = sortOrder == KeySortOrder.TimeFirst
            ? TemporalKey.CompareTimeFirst
            : TemporalKey.CompareEntityFirst;

        // In bulk mode, pre-size the file to 256 GB so AllocatePage can extend the
        // underlying file via SetLength without ever needing to remap. The file is
        // sparse (SparseFile.SetLength), so disk usage tracks actual touched pages.
        // macOS per-process VM ceiling is ~64 TB so 256 GB per index leaves comfortable
        // headroom for full Wikidata. Cognitive mode keeps the original initial — small
        // stores never grow past it, large cognitive stores stay rare.
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
        var root = f.GetPagePointer<TemporalBTreePage>(1);
        root->PageId = 1;
        root->IsLeaf = true;
        root->EntryCount = 0;
        root->ParentPageId = 0;
        root->NextLeaf = 0;
    }

    /// <summary>
    /// Get the atom store (for shared access).
    /// </summary>
    /// <remarks>
    /// Internal: IAtomStore requires external synchronization via the owning
    /// QuadStore's read/write locks.
    /// </remarks>
    internal IAtomStore Atoms => _atoms;

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
        long memorizedAt,
        long forgottenAt,
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
            MemorizedAt = memorizedAt,
            ForgottenAt = forgottenAt,
            LegacyTransactionTime = transactionTime
        };

        InsertIntoTree(temporalKey, _file.RootPageId);
    }

    /// <summary>What an insert did. WAL recovery needs to tell a replayed entry from a new one (ADR-058).</summary>
    internal enum InsertOutcome
    {
        /// <summary>A new row was written.</summary>
        Inserted,
        /// <summary>The same Add (same MemorizedAt and LegacyTransactionTime) was already in the tree: a WAL replay.</summary>
        AlreadyPresent,
        /// <summary>A current entry with the same dimensions exists (RDF idempotency); nothing was written.</summary>
        Duplicate,
    }

    private InsertOutcome _lastInsertOutcome;

    /// <summary>
    /// Insert a key with pre-resolved atom IDs. Used by the WAL apply and replay paths and
    /// by secondary index rebuild, to avoid re-interning strings whose atom IDs are known.
    /// </summary>
    internal InsertOutcome AddRaw(long graph, long primary, long secondary, long tertiary,
        long memorizedAt, long forgottenAt, long transactionTime)
    {
        var temporalKey = new TemporalKey
        {
            Graph = graph,
            Primary = primary,
            Secondary = secondary,
            Tertiary = tertiary,
            MemorizedAt = memorizedAt,
            ForgottenAt = forgottenAt,
            LegacyTransactionTime = transactionTime
        };

        _lastInsertOutcome = InsertOutcome.Inserted;
        InsertIntoTree(temporalKey, _file.RootPageId);
        return _lastInsertOutcome;
    }

    /// <summary>The WAL transaction up to which the saved entry count is exact (ADR-058).</summary>
    internal long CountTxId => _file.CountTxId;

    /// <summary>Save the entry count as exact up to WAL transaction <paramref name="txId"/> (ADR-058).</summary>
    internal void CommitEntryCount(long txId, bool durable) => _file.CommitEntryCount(txId, durable);

    /// <summary>
    /// Count an entry that WAL recovery found already present: a crashed process inserted
    /// it after the saved count was last committed (ADR-058).
    /// </summary>
    internal void CountRecoveredEntry() => _file.IncrementEntryCount();

    /// <summary>
    /// End a version (ADR-054 D1): the live entry with these dimensions and exactly <paramref name="memorizedAt"/> stops
    /// holding at <paramref name="end"/>. Returns false, changing nothing, when no such entry exists or it already ends
    /// at or before <paramref name="end"/>, so applying the same End twice (WAL replay) is a no-op.
    /// </summary>
    internal bool EndRaw(long graph, long primary, long secondary, long tertiary,
        long memorizedAt, long end, long transactionTime)
    {
        var entry = FindLiveEntry(graph, primary, secondary, tertiary, memorizedAt, forgottenAt: null);
        if (entry == null || entry->Key.ForgottenAt <= end)
            return false;

        entry->Key.ForgottenAt = end;
        entry->LegacyModifiedAt = transactionTime;
        _file.FlushPage();
        return true;
    }

    /// <summary>
    /// Retract a version (ADR-054 D2): the live entry with these dimensions and exactly [memorizedAt, forgottenAt) is marked
    /// as recorded in error. Returns false when no such live entry exists.
    /// </summary>
    internal bool RetractRaw(long graph, long primary, long secondary, long tertiary,
        long memorizedAt, long forgottenAt, long transactionTime)
    {
        var entry = FindLiveEntry(graph, primary, secondary, tertiary, memorizedAt, forgottenAt);
        if (entry == null)
            return false;

        entry->LegacyIsDeleted = true;
        entry->LegacyModifiedAt = transactionTime;
        _file.FlushPage();
        return true;
    }

    // ---- ADR-061 M5, M7: the write rule's lookup, and the four decided changes --------------------------------------

    /// <summary>The stored form of "no forgotten time": what a remembered period's ForgottenAt holds.</summary>
    internal static readonly long OpenForgottenAt = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    /// <summary>A ForgottenAt at or above this is "no forgotten time" (year 9000 in Unix milliseconds).</summary>
    private const long OpenThreshold = 253370764800000L;

    /// <summary>
    /// What the write rule needs to know about one quad at one clock reading (ADR-061 M5).
    /// </summary>
    /// <param name="HoldingMemorizedAt">The memorized time of the entry that holds at the reading, or null.</param>
    /// <param name="HoldingIsOpen">Whether that entry has no forgotten time. An entry that holds and is not open
    /// was written with a stated end, which only <c>Add</c> with a stated time can do.</param>
    /// <param name="ForgottenAtReadingMemorizedAt">The memorized time of the entry whose forgotten time is exactly
    /// the reading, or null.</param>
    internal readonly record struct QuadAtReading(long? HoldingMemorizedAt, bool HoldingIsOpen, long? ForgottenAtReadingMemorizedAt);

    /// <summary>
    /// Look at one quad at clock reading <paramref name="at"/>: one walk over its entries. Only an EntityFirst index
    /// keeps a quad's entries together, so QuadStore asks its GSPO index.
    /// </summary>
    internal QuadAtReading FindQuadAtReading(long graph, long primary, long secondary, long tertiary, long at)
    {
        if (_sortOrder != KeySortOrder.EntityFirst)
            throw new InvalidOperationException("FindQuadAtReading needs an EntityFirst index: a TimeFirst index does not keep a quad's entries together.");

        var lo = new TemporalKey { Graph = graph, Primary = primary, Secondary = secondary, Tertiary = tertiary,
            MemorizedAt = long.MinValue, ForgottenAt = long.MinValue, LegacyTransactionTime = long.MinValue };
        var hi = new TemporalKey { Graph = graph, Primary = primary, Secondary = secondary, Tertiary = tertiary,
            MemorizedAt = long.MaxValue, ForgottenAt = long.MaxValue, LegacyTransactionTime = long.MaxValue };

        long? holding = null;
        bool holdingIsOpen = false;
        long? forgottenAtReading = null;
        for (var pageId = FindLeafPage(_file.RootPageId, lo); pageId != 0;)
        {
            var page = GetPage(pageId);
            for (int i = 0; i < page->EntryCount; i++)
            {
                ref var entry = ref page->GetEntry(i);
                if (CompareKeys(in entry.Key, in lo) < 0) continue;
                if (CompareKeys(in entry.Key, in hi) > 0)
                    return new QuadAtReading(holding, holdingIsOpen, forgottenAtReading);
                if (entry.LegacyIsDeleted) continue;
                if (entry.Key.MemorizedAt <= at && entry.Key.ForgottenAt > at)
                {
                    holding = entry.Key.MemorizedAt;
                    holdingIsOpen = entry.Key.ForgottenAt >= OpenThreshold;
                }
                else if (entry.Key.ForgottenAt == at)
                {
                    forgottenAtReading = entry.Key.MemorizedAt;
                }
            }
            pageId = page->NextLeaf;
        }
        return new QuadAtReading(holding, holdingIsOpen, forgottenAtReading);
    }

    // Set while BeginPeriod inserts. A decided period is inserted as it is: the neighbour logic of InsertIntoLeaf
    // decides nothing for it. That logic stays for Add with a stated time until ADR-061 Step 6 removes it.
    private bool _insertingDecidedPeriod;

    /// <summary>
    /// A period of the quad begins at <paramref name="memorizedAt"/> (ADR-061 M7). Returns false, changing nothing,
    /// when the quad already has an entry that began then.
    /// </summary>
    internal bool BeginPeriod(long graph, long primary, long secondary, long tertiary, long memorizedAt)
    {
        if (FindLiveEntry(graph, primary, secondary, tertiary, memorizedAt, forgottenAt: null) != null)
            return false;

        var key = new TemporalKey
        {
            Graph = graph,
            Primary = primary,
            Secondary = secondary,
            Tertiary = tertiary,
            MemorizedAt = memorizedAt,
            ForgottenAt = OpenForgottenAt,
            LegacyTransactionTime = memorizedAt
        };

        _insertingDecidedPeriod = true;
        try
        {
            InsertIntoTree(key, _file.RootPageId);
        }
        finally
        {
            _insertingDecidedPeriod = false;
        }
        return true;
    }

    /// <summary>
    /// The period of the quad that began at <paramref name="memorizedAt"/> ends at <paramref name="forgottenAt"/>
    /// (ADR-061 M7). Returns false, changing nothing, when there is no such entry or it is not open.
    /// </summary>
    internal bool EndPeriod(long graph, long primary, long secondary, long tertiary, long memorizedAt, long forgottenAt)
    {
        var entry = FindLiveEntry(graph, primary, secondary, tertiary, memorizedAt, forgottenAt: null);
        if (entry == null || entry->Key.ForgottenAt < OpenThreshold)
            return false;

        entry->Key.ForgottenAt = forgottenAt;
        entry->LegacyModifiedAt = forgottenAt;
        _file.FlushPage();
        return true;
    }

    /// <summary>
    /// The period of the quad that began at <paramref name="memorizedAt"/> and ended at <paramref name="forgottenAt"/>
    /// continues: it has no forgotten time again (ADR-061 M2, M7). Returns false, changing nothing, when there is
    /// no such entry or its forgotten time is another.
    /// </summary>
    internal bool ContinuePeriod(long graph, long primary, long secondary, long tertiary, long memorizedAt, long forgottenAt)
    {
        var entry = FindLiveEntry(graph, primary, secondary, tertiary, memorizedAt, forgottenAt: null);
        if (entry == null || entry->Key.ForgottenAt != forgottenAt)
            return false;

        entry->Key.ForgottenAt = OpenForgottenAt;
        entry->LegacyModifiedAt = entry->Key.LegacyTransactionTime;
        _file.FlushPage();
        return true;
    }

    /// <summary>
    /// The period of the quad that began at <paramref name="memorizedAt"/> is removed: its entry leaves the tree
    /// (ADR-061 M2, M7). Returns false, changing nothing, when there is no such entry or it is not open. The leaf
    /// keeps its place in the tree when it becomes empty; the keys above it stay valid as bounds.
    /// </summary>
    internal bool RemovePeriod(long graph, long primary, long secondary, long tertiary, long memorizedAt)
    {
        var lo = new TemporalKey { Graph = graph, Primary = primary, Secondary = secondary, Tertiary = tertiary,
            MemorizedAt = memorizedAt, ForgottenAt = long.MinValue, LegacyTransactionTime = long.MinValue };
        var hi = new TemporalKey { Graph = graph, Primary = primary, Secondary = secondary, Tertiary = tertiary,
            MemorizedAt = memorizedAt, ForgottenAt = long.MaxValue, LegacyTransactionTime = long.MaxValue };

        for (var pageId = FindLeafPage(_file.RootPageId, lo); pageId != 0;)
        {
            var page = GetPage(pageId);
            for (int i = 0; i < page->EntryCount; i++)
            {
                ref var entry = ref page->GetEntry(i);
                if (CompareKeys(in entry.Key, in lo) < 0) continue;
                if (CompareKeys(in entry.Key, in hi) > 0) return false;
                if (entry.LegacyIsDeleted) continue;
                if (entry.Key.ForgottenAt < OpenThreshold) return false;

                for (int j = i; j < page->EntryCount - 1; j++)
                    page->GetEntry(j) = page->GetEntry(j + 1);
                page->EntryCount--;
                _file.DecrementEntryCount();
                _file.FlushPage();
                return true;
            }
            pageId = page->NextLeaf;
        }
        return false;
    }

    /// <summary>
    /// Change the entry count by one without touching the tree. WAL recovery uses it: a decided change counts by the
    /// transaction it belongs to, whatever the page already held (ADR-058, ADR-061 M7).
    /// </summary>
    internal void AdjustRecoveredEntryCount(int delta)
    {
        if (delta > 0) _file.IncrementEntryCount();
        else if (delta < 0) _file.DecrementEntryCount();
    }

    /// <summary>
    /// Replay a delete record written before ADR-054 (operation 2 with [MinValue, MaxValue], from DeleteCurrent): mark
    /// the first live entry whose interval overlaps, which is what that record did when it was written. Nothing writes
    /// such a record any more; only <see cref="QuadStore"/> recovery calls this.
    /// </summary>
    internal bool ReplayPreAdr054Delete(long graph, long primary, long secondary, long tertiary,
        long memorizedAt, long forgottenAt, long transactionTime)
    {
        var key = new TemporalKey
        {
            Graph = graph,
            Primary = primary,
            Secondary = secondary,
            Tertiary = tertiary,
            MemorizedAt = memorizedAt,
            ForgottenAt = forgottenAt,
            LegacyTransactionTime = transactionTime
        };

        return DeleteFromTree(key);
    }

    /// <summary>
    /// The MemorizedAt of the live version that holds at <paramref name="at"/> (MemorizedAt ≤ at &lt; ForgottenAt), or null.
    /// Walks every version of the quad across leaf pages. Only an EntityFirst index keeps a quad's versions together,
    /// so QuadStore asks its GSPO index.
    /// </summary>
    internal long? FindVersionHoldingAt(long graph, long primary, long secondary, long tertiary, long at)
    {
        if (_sortOrder != KeySortOrder.EntityFirst)
            throw new InvalidOperationException("FindVersionHoldingAt needs an EntityFirst index: a TimeFirst index does not keep a quad's versions together.");

        var lo = new TemporalKey { Graph = graph, Primary = primary, Secondary = secondary, Tertiary = tertiary,
            MemorizedAt = long.MinValue, ForgottenAt = long.MinValue, LegacyTransactionTime = long.MinValue };
        var hi = new TemporalKey { Graph = graph, Primary = primary, Secondary = secondary, Tertiary = tertiary,
            MemorizedAt = long.MaxValue, ForgottenAt = long.MaxValue, LegacyTransactionTime = long.MaxValue };

        for (var pageId = FindLeafPage(_file.RootPageId, lo); pageId != 0;)
        {
            var page = GetPage(pageId);
            for (int i = 0; i < page->EntryCount; i++)
            {
                ref var entry = ref page->GetEntry(i);
                if (CompareKeys(in entry.Key, in lo) < 0) continue;
                if (CompareKeys(in entry.Key, in hi) > 0) return null;
                if (!entry.LegacyIsDeleted && entry.Key.MemorizedAt <= at && entry.Key.ForgottenAt > at)
                    return entry.Key.MemorizedAt;
            }
            pageId = page->NextLeaf;
        }
        return null;
    }

    /// <summary>
    /// The live entry with these dimensions and exactly this MemorizedAt (and, when given, this ForgottenAt), or null.
    /// Entries sharing dimensions and MemorizedAt are adjacent in both sort orders; the walk follows the leaf chain
    /// because they can straddle a page boundary.
    /// </summary>
    private TemporalBTreeEntry* FindLiveEntry(long graph, long primary, long secondary, long tertiary,
        long memorizedAt, long? forgottenAt)
    {
        var lo = new TemporalKey { Graph = graph, Primary = primary, Secondary = secondary, Tertiary = tertiary,
            MemorizedAt = memorizedAt, ForgottenAt = forgottenAt ?? long.MinValue, LegacyTransactionTime = long.MinValue };
        var hi = new TemporalKey { Graph = graph, Primary = primary, Secondary = secondary, Tertiary = tertiary,
            MemorizedAt = memorizedAt, ForgottenAt = forgottenAt ?? long.MaxValue, LegacyTransactionTime = long.MaxValue };

        for (var pageId = FindLeafPage(_file.RootPageId, lo); pageId != 0;)
        {
            var page = GetPage(pageId);
            for (int i = 0; i < page->EntryCount; i++)
            {
                ref var entry = ref page->GetEntry(i);
                if (CompareKeys(in entry.Key, in lo) < 0) continue;
                if (CompareKeys(in entry.Key, in hi) > 0) return null;
                if (!entry.LegacyIsDeleted)
                    return (TemporalBTreeEntry*)Unsafe.AsPointer(ref entry);
            }
            pageId = page->NextLeaf;
        }
        return null;
    }

    /// <summary>Whether a live entry with these dimensions and exactly [memorizedAt, forgottenAt) exists.</summary>
    internal bool HoldsLiveEntry(long graph, long primary, long secondary, long tertiary, long memorizedAt, long forgottenAt) =>
        FindLiveEntry(graph, primary, secondary, tertiary, memorizedAt, forgottenAt) != null;

    /// <summary>What <see cref="RepairDeleted"/> does with one flagged entry (ADR-054 D7).</summary>
    internal enum DeletedEntryRepair : byte
    {
        /// <summary>Leave the entry as it is.</summary>
        Leave,
        /// <summary>The fact ended when it was deleted: ForgottenAt = LegacyModifiedAt, LegacyIsDeleted = false.</summary>
        End,
        /// <summary>The delete was the mistake: LegacyIsDeleted = false, ForgottenAt unchanged.</summary>
        Restore
    }

    /// <summary>
    /// Visit every LegacyIsDeleted leaf entry and apply what <paramref name="decide"/> returns for it. The callback gets the
    /// entry's dimensions in this index's order, its MemorizedAt, ForgottenAt and LegacyModifiedAt (the deletion's transaction
    /// time). Each index is repaired on its own entries, so an interrupted repair can be rerun: entries already
    /// repaired are no longer flagged. Returns the number of entries changed.
    /// </summary>
    internal int RepairDeleted(Func<long, long, long, long, long, long, long, DeletedEntryRepair> decide)
    {
        var first = new TemporalKey { Graph = long.MinValue, Primary = long.MinValue, Secondary = long.MinValue,
            Tertiary = long.MinValue, MemorizedAt = long.MinValue, ForgottenAt = long.MinValue, LegacyTransactionTime = long.MinValue };
        int changed = 0;
        for (var pageId = FindLeafPage(_file.RootPageId, first); pageId != 0;)
        {
            var page = GetPage(pageId);
            for (int i = 0; i < page->EntryCount; i++)
            {
                ref var entry = ref page->GetEntry(i);
                if (!entry.LegacyIsDeleted) continue;
                var k = entry.Key;
                switch (decide(k.Graph, k.Primary, k.Secondary, k.Tertiary, k.MemorizedAt, k.ForgottenAt, entry.LegacyModifiedAt))
                {
                    case DeletedEntryRepair.End:
                        entry.Key.ForgottenAt = entry.LegacyModifiedAt;
                        entry.LegacyIsDeleted = false;
                        changed++;
                        break;
                    case DeletedEntryRepair.Restore:
                        entry.LegacyIsDeleted = false;
                        changed++;
                        break;
                }
            }
            pageId = page->NextLeaf;
        }
        _file.Flush();
        return changed;
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
        DateTimeOffset memorizedAt,
        DateTimeOffset forgottenAt,
        long transactionTime = 0,
        ReadOnlySpan<char> graph = default)
    {
        var tt = transactionTime != 0 ? transactionTime : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Add(primary, secondary, tertiary,
            memorizedAt.ToUnixTimeMilliseconds(),
            forgottenAt.ToUnixTimeMilliseconds(),
            transactionTime: tt,
            graph: graph);
    }

    /// <summary>
    /// Retract the version with exactly these time bounds (ADR-054 D2).
    /// Returns true if that live version was found and retracted, false otherwise.
    /// </summary>
    public bool Retract(
        ReadOnlySpan<char> primary,
        ReadOnlySpan<char> secondary,
        ReadOnlySpan<char> tertiary,
        long memorizedAt,
        long forgottenAt,
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

        return RetractRaw(g, a, b, c, memorizedAt, forgottenAt, tt);
    }

    /// <summary>
    /// Retract the version valid for exactly this period (ADR-054 D2).
    /// Returns true if that live version was found and retracted, false otherwise.
    /// </summary>
    public bool RetractHistorical(
        ReadOnlySpan<char> primary,
        ReadOnlySpan<char> secondary,
        ReadOnlySpan<char> tertiary,
        DateTimeOffset memorizedAt,
        DateTimeOffset forgottenAt,
        long transactionTime = 0,
        ReadOnlySpan<char> graph = default)
    {
        return Retract(primary, secondary, tertiary,
            memorizedAt.ToUnixTimeMilliseconds(),
            forgottenAt.ToUnixTimeMilliseconds(),
            transactionTime,
            graph);
    }

    /// <summary>
    /// Find and mark a triple as deleted in the B+Tree.
    /// </summary>
    private bool DeleteFromTree(TemporalKey key)
    {
        var leafPageId = FindLeafPage(_file.RootPageId, key);
        var page = GetPage(leafPageId);

        // Scan leaf page for matching entry
        for (int i = 0; i < page->EntryCount; i++)
        {
            ref var entry = ref page->GetEntry(i);

            // Check if this is the exact entry (same graph + dimensions and overlapping time)
            if (IsSameDimensions(entry.Key, key) && !entry.LegacyIsDeleted)
            {
                // Check for time overlap
                if (entry.Key.MemorizedAt <= key.ForgottenAt && entry.Key.ForgottenAt >= key.MemorizedAt)
                {
                    entry.LegacyIsDeleted = true;
                    entry.LegacyModifiedAt = key.LegacyTransactionTime;
                    _file.FlushPage();
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

        var leafPageId = FindLeafPage(_file.RootPageId, minKey);

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
    /// <param name="now">The store's clock reading, in Unix milliseconds. The index has no clock of its own.</param>
    internal TemporalQuadEnumerator QueryCurrentAllGraphs(long now)
    {

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
    /// Selected once at TemporalQuadIndex construction; the JIT optimizes monomorphic delegate call sites.
    /// </summary>
    internal enum KeySortOrder
    {
        /// <summary>Graph → Primary → Secondary → Tertiary → MemorizedAt → ForgottenAt → LegacyTransactionTime</summary>
        EntityFirst,

        /// <summary>MemorizedAt → Graph → Primary → Secondary → Tertiary → ForgottenAt → LegacyTransactionTime</summary>
        TimeFirst
    }

    /// <summary>
    /// Delegate type for key comparison. Selected once at construction, stored as a field.
    /// </summary>
    internal delegate int KeyComparer(in TemporalKey a, in TemporalKey b);

    /// <summary>
    /// Temporal key: Graph + 3 generic dimensions + MemorizedAt + ForgottenAt + LegacyTransactionTime (56 bytes)
    /// TemporalQuadIndex is a generic multi-dimensional B+Tree. The RDF-to-dimension mapping
    /// (Subject→Primary, Predicate→Secondary, Object→Tertiary) lives in QuadStore.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct TemporalKey : IComparable<TemporalKey>
    {
        public long Graph;           // 64-bit graph ID (0 = default graph)
        public long Primary;         // 64-bit, first dimension (e.g., subject in GSPO)
        public long Secondary;       // 64-bit, second dimension (e.g., predicate in GSPO)
        public long Tertiary;        // 64-bit, third dimension (e.g., object in GSPO)
        // The version's two times on the memory's own axis (ADR-061 D1, D11), in milliseconds since the epoch.
        // Until ADR-061 D2 is done a caller can still state them, and they then hold a stated valid time.
        public long MemorizedAt;     // When the version was memorized
        public long ForgottenAt;     // When it was forgotten; the far future while it is remembered
        // Legacy (ADR-061 D11): the batch's clock reading, in milliseconds since the epoch. Still read as the last sort
        // tie-breaker and to recognize a WAL replay of the same Add (InsertIntoLeaf, Case A). Leaves with the stored
        // layout change (ADR-061, the later part, L1).
        public long LegacyTransactionTime;

        /// <summary>
        /// Default comparison: EntityFirst order.
        /// Used by IComparable for cases where no delegate is available.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int CompareTo(TemporalKey other) => CompareEntityFirst(in this, in other);

        /// <summary>
        /// Graph → Primary → Secondary → Tertiary → MemorizedAt → ForgottenAt → LegacyTransactionTime
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

            cmp = a.MemorizedAt.CompareTo(b.MemorizedAt);
            if (cmp != 0) return cmp;

            cmp = a.ForgottenAt.CompareTo(b.ForgottenAt);
            if (cmp != 0) return cmp;

            return a.LegacyTransactionTime.CompareTo(b.LegacyTransactionTime);
        }

        /// <summary>
        /// MemorizedAt → Graph → Primary → Secondary → Tertiary → ForgottenAt → LegacyTransactionTime
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int CompareTimeFirst(in TemporalKey a, in TemporalKey b)
        {
            var cmp = a.MemorizedAt.CompareTo(b.MemorizedAt);
            if (cmp != 0) return cmp;

            cmp = a.Graph.CompareTo(b.Graph);
            if (cmp != 0) return cmp;

            cmp = a.Primary.CompareTo(b.Primary);
            if (cmp != 0) return cmp;

            cmp = a.Secondary.CompareTo(b.Secondary);
            if (cmp != 0) return cmp;

            cmp = a.Tertiary.CompareTo(b.Tertiary);
            if (cmp != 0) return cmp;

            cmp = a.ForgottenAt.CompareTo(b.ForgottenAt);
            if (cmp != 0) return cmp;

            return a.LegacyTransactionTime.CompareTo(b.LegacyTransactionTime);
        }
    }

    /// <summary>
    /// B+Tree page for temporal triples (16KB)
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = BTreeFile.PageSize)]
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
        
        // Legacy (ADR-061 D11): the four fields below leave the entry with the stored layout change (ADR-061, the later
        // part, L1). New code does not read them.
        public long LegacyCreatedAt;       // The write's transaction time. Written, read by nothing.
        public long LegacyModifiedAt;      // Transaction time of the last End or Retract, else of the insert. Read to date
                                           // a retraction: RetractedAt (until Step 6) and the ADR-054 repair.
        public int LegacyVersion;          // Always 1. Written, read by nothing.
        public bool LegacyIsDeleted;       // Set by Retract (ADR-054 D2); every scan skips the entry, and the ADR-054
                                           // repair visits it. Nothing writes it once Retract goes (Step 6).
        public byte Flags;           // Future use
        public short Reserved;       // Padding
    }

    /// <summary>
    /// Zero-allocation enumerator for temporal triples.
    /// Changed from ref struct to struct to enable pooled array storage (ADR-011).
    /// </summary>
    internal struct TemporalQuadEnumerator
    {
        private readonly TemporalQuadIndex _store;
        private long _currentPageId;
        private int _currentSlot;
        private readonly TemporalKey _minKey;
        private readonly TemporalKey _maxKey;
        private readonly TemporalQuery _query;
        private TemporalKey _currentKey;
        private bool _currentLegacyIsDeleted;
        private long _currentLegacyModifiedAt;

        internal TemporalQuadEnumerator(
            TemporalQuadIndex store,
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
            _currentLegacyIsDeleted = false;
            _currentLegacyModifiedAt = 0;
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
                    _currentLegacyIsDeleted = entry.LegacyIsDeleted;
                    _currentLegacyModifiedAt = entry.LegacyModifiedAt;

                    // Check spatial bounds using the index's sort order
                    if (_store.CompareKeys(in _currentKey, in _maxKey) > 0)
                        return false;

                    if (_store.CompareKeys(in _currentKey, in _minKey) >= 0)
                    {
                        // Check temporal bounds AND the bound spatial dimensions. The spatial check is
                        // load-bearing for the TGSP (time-leading) index that DURING/Range selects: there
                        // MemorizedAt leads, so the graph/S/P/O key range cannot gate those dimensions and a
                        // pure CompareKeys scan would leak entries from every graph/S/P/O in the time window
                        // (the DURING over-count bug). For the spatial-leading indexes (GSPO/GPOS/GOSP) the
                        // key range already constrains them, so MatchesSpatialBounds is a redundant no-op.
                        if (MatchesTemporalQuery(entry) && MatchesSpatialBounds(in _currentKey))
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
                    !entry.LegacyIsDeleted &&
                    key.MemorizedAt <= _query.AsOfTime &&
                    key.ForgottenAt > _query.AsOfTime,

                TemporalQueryType.Range =>
                    !entry.LegacyIsDeleted &&
                    key.MemorizedAt < _query.RangeEnd &&
                    key.ForgottenAt > _query.RangeStart,

                // AllTime (history/audit) includes deleted entries
                TemporalQueryType.AllTime => true,

                _ => false
            };
        }

        // Post-filter the bound graph/S/P/O dimensions. A dimension is BOUND iff its min and max
        // search-key values coincide (CreateSearchKey sets both to the term's value; an unbound
        // dimension spans [0, long.MaxValue], so min != max and the check is skipped). On the TGSP
        // time-leading index the key range cannot constrain these dimensions, so this is what actually
        // applies the graph/S/P/O filter for DURING; on spatial-leading indexes it is a no-op.
        private readonly bool MatchesSpatialBounds(in TemporalKey key)
            => (_minKey.Graph     != _maxKey.Graph     || key.Graph     == _minKey.Graph)
            && (_minKey.Primary   != _maxKey.Primary   || key.Primary   == _minKey.Primary)
            && (_minKey.Secondary != _maxKey.Secondary || key.Secondary == _minKey.Secondary)
            && (_minKey.Tertiary  != _maxKey.Tertiary  || key.Tertiary  == _minKey.Tertiary);

        public readonly TemporalQuad Current
        {
            get => new TemporalQuad
            {
                Graph = _currentKey.Graph,
                Primary = _currentKey.Primary,
                Secondary = _currentKey.Secondary,
                Tertiary = _currentKey.Tertiary,
                MemorizedAt = _currentKey.MemorizedAt,
                ForgottenAt = _currentKey.ForgottenAt,
                LegacyTransactionTime = _currentKey.LegacyTransactionTime,
                LegacyIsDeleted = _currentLegacyIsDeleted,
                LegacyModifiedAt = _currentLegacyModifiedAt
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
        return _file.GetPagePointer<TemporalBTreePage>(pageId);
    }

    private void InsertIntoTree(TemporalKey key, long pageId)
    {
        var result = InsertRecursive(key, pageId);

        // If root split, create new root
        if (result.DidSplit && pageId == _file.RootPageId)
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
        var newRootId = _file.AllocatePage();
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

        _file.RootPageId = newRootId;

        _file.FlushPage();
        // ADR-058: save the new root now, but not the entry count, which may include
        // entries of a transaction that has not finished applying.
        _file.SaveStructure();
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

        // Check for updates (same GSPO dimensions). Not for a decided period (ADR-061 M7): BeginPeriod inserts it as
        // it is.
        if (insertPos > 0 && !_insertingDecidedPeriod)
        {
            ref var prevEntry = ref page->GetEntry(insertPos - 1);
            if (IsSameDimensions(key, prevEntry.Key))
            {
                // Case A: the same Add applied again (WAL replay) — same MemorizedAt and LegacyTransactionTime. ForgottenAt is not
                // compared: an End may have shortened it since (ADR-054 D1), and the replayed Add must not bring the
                // ended version back.
                if (prevEntry.Key.MemorizedAt == key.MemorizedAt &&
                    prevEntry.Key.LegacyTransactionTime == key.LegacyTransactionTime)
                {
                    _lastInsertOutcome = InsertOutcome.AlreadyPresent;
                    return default;
                }

                // Case B: Both entries are "current" (ForgottenAt is far future) — RDF idempotency. A retracted entry is
                // not current and never blocks an insert (ADR-054 D3).
                const long FarFutureThreshold = 253370764800000L; // Year 9000 in Unix ms
                if (!prevEntry.LegacyIsDeleted && prevEntry.Key.ForgottenAt >= FarFutureThreshold && key.ForgottenAt >= FarFutureThreshold)
                {
                    _lastInsertOutcome = InsertOutcome.Duplicate;
                    return default;
                }

                // Case C: Same dimensions, different temporal key — truncate a live predecessor, then insert new row
                if (!prevEntry.LegacyIsDeleted)
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
        newEntry.LegacyCreatedAt = key.LegacyTransactionTime;
        newEntry.LegacyModifiedAt = key.LegacyTransactionTime;
        newEntry.LegacyVersion = 1;
        newEntry.LegacyIsDeleted = false;

        page->EntryCount++;
        _file.IncrementEntryCount();
        _file.FlushPage();

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
        if (newKey.MemorizedAt < existing.Key.ForgottenAt)
        {
            // Truncate existing entry
            existing.Key.ForgottenAt = newKey.MemorizedAt;
            existing.LegacyModifiedAt = newKey.LegacyTransactionTime;
            _file.FlushPage();
        }
    }

    private SplitResult SplitLeafPage(TemporalBTreePage* page, TemporalKey key)
    {
        // Allocate new page for right half
        var newPageId = _file.AllocatePage();
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

        _file.FlushPage();
        _file.FlushPage();

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
        newEntry.LegacyCreatedAt = key.LegacyTransactionTime;
        newEntry.LegacyModifiedAt = key.LegacyTransactionTime;
        newEntry.LegacyVersion = 1;
        newEntry.LegacyIsDeleted = false;

        page->EntryCount++;

        // Update parent pointer of the new child
        var childPage = GetPage(rightChildPageId);
        childPage->ParentPageId = page->PageId;
        _file.FlushPage();

        _file.FlushPage();

        return default; // No split
    }

    private SplitResult SplitInternalPage(TemporalBTreePage* page, TemporalKey key, long rightChildPageId)
    {
        // Allocate new page for right half
        var newPageId = _file.AllocatePage();
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
        _file.FlushPage();

        for (int i = 0; i < newPage->EntryCount; i++)
        {
            var childId = newPage->GetEntry(i).ChildOrValue;
            var child = GetPage(childId);
            child->ParentPageId = newPageId;
            _file.FlushPage();
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

        _file.FlushPage();
        _file.FlushPage();

        // Return split result for parent to handle
        return new SplitResult
        {
            DidSplit = true,
            PromotedKey = promotedKey,
            NewRightPageId = newPageId
        };
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

        // For TimeFirst sort order, MemorizedAt is the leading dimension.
        // Temporal overlap semantics: MemorizedAt < rangeEnd AND ForgottenAt > rangeStart.
        // minKey.MemorizedAt stays 0 because entries starting before the query window
        // may still overlap (their ForgottenAt extends into the window).
        // maxKey.MemorizedAt = rangeEnd/asOfTime to skip entries that can't overlap.
        // This narrows the scan from O(N) to O(log N + k') where k' = entries with
        // MemorizedAt < rangeEnd. MatchesTemporalQuery post-filters ForgottenAt > rangeStart.
        long memorizedAt;
        if (_sortOrder == KeySortOrder.TimeFirst && !isMin)
        {
            memorizedAt = query.Type switch
            {
                TemporalQueryType.Range => query.RangeEnd,
                TemporalQueryType.AsOf => query.AsOfTime,
                _ => long.MaxValue
            };
        }
        else
        {
            memorizedAt = unboundValue;
        }

        return new TemporalKey
        {
            Graph = graphValue,
            Primary = primary < 0 ? unboundValue : primary,
            Secondary = secondary < 0 ? unboundValue : secondary,
            Tertiary = tertiary < 0 ? unboundValue : tertiary,
            MemorizedAt = memorizedAt,
            ForgottenAt = isMin ? 0 : long.MaxValue,
            LegacyTransactionTime = isMin ? 0 : long.MaxValue
        };
    }

    /// <summary>
    /// Force all pending mmap writes to disk. Called once at the end of a bulk
    /// load to flush dirty pages that BTreeFile.FlushPage skipped in bulk mode.
    /// </summary>
    /// <remarks>
    /// Saves the header with the current entry count first: page allocation saves only the
    /// tree structure (ADR-058), so the count reaches disk here, at commit, or at Dispose.
    /// </remarks>
    public void Flush()
    {
        _file.SaveMetadata();
        _file.Flush();
    }

    /// <summary>
    /// Toggle whether BTreeFile.FlushPage / SaveMetadata issue per-operation msyncs.
    /// Used by QuadStore.RebuildSecondaryIndexes to borrow the bulk-load msync-deferral
    /// semantics during secondary-index construction — same pattern, same durability
    /// contract ("call Flush() when done"). Caller MUST invoke Flush() before turning
    /// deferral off again, or dirty mmap pages may never reach disk.
    /// </summary>
    internal void SetDeferMsync(bool value) => _file.DeferMsync = value;

    public void Dispose()
    {
        _file.Dispose();
        if (_ownsAtomStore)
            _atoms?.Dispose();
    }

    public long QuadCount => _file.EntryCount;

    /// <summary>
    /// Resets the index to empty state. All data is logically discarded.
    /// File size is preserved (memory mapping stays valid).
    /// </summary>
    /// <remarks>
    /// Must be called from QuadStore.Clear() which holds the write lock.
    /// </remarks>
    public void Clear()
    {
        _file.Reset();
        InitializeRootPage(_file);
        _file.FlushPage();
        _file.SaveMetadata();
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
    public long MemorizedAt;
    public long ForgottenAt;
    // Legacy (ADR-061 D11): the three fields below are copies of the entry's fields of the same names and leave with them.
    public long LegacyTransactionTime;
    public bool LegacyIsDeleted;      // Retracted (ADR-054 D2); visible in AllTime queries only. Goes with Retract (Step 6).
    public long LegacyModifiedAt;     // Transaction time of the last End or Retract, else of the insert
}
