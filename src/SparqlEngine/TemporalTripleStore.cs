using System;
using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SparqlEngine.Storage;

namespace SparqlEngine.Temporal;

/// <summary>
/// Temporal triple store supporting valid-time and transaction-time semantics
/// Implements bitemporal data model for Sky Omega
/// 
/// Valid Time (VT): When the fact is true in the real world
/// Transaction Time (TT): When the fact was recorded in the database
/// </summary>
public sealed unsafe class TemporalTripleStore : IDisposable
{
    private const int PageSize = 16384;
    private const int NodeDegree = 204; // (16384 - 32) / 80 bytes per temporal entry
    
    private readonly FileStream _fileStream;
    private readonly MemoryMappedFile _mmapFile;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly AtomStore _atoms;
    private readonly PageCache _pageCache;
    
    private long _rootPageId;
    private long _nextPageId;
    private long _currentTransactionTime;

    public TemporalTripleStore(string filePath, long initialSizeBytes = 1L << 30)
    {
        _fileStream = new FileStream(
            filePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.RandomAccess | FileOptions.WriteThrough
        );
        
        if (_fileStream.Length == 0)
        {
            _fileStream.SetLength(initialSizeBytes);
        }
        
        _mmapFile = MemoryMappedFile.CreateFromFile(
            _fileStream,
            mapName: null,
            capacity: 0,
            MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None,
            leaveOpen: false
        );
        
        _accessor = _mmapFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
        
        var atomFilePath = filePath + ".atoms";
        _atoms = new AtomStore(atomFilePath);
        _pageCache = new PageCache(capacity: 10_000);
        
        LoadMetadata();
        _currentTransactionTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// Add a temporal triple with explicit time bounds
    /// </summary>
    public void Add(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj,
        long validFrom,
        long validTo,
        long? transactionTime = null)
    {
        var s = _atoms.Intern(subject);
        var p = _atoms.Intern(predicate);
        var o = _atoms.Intern(obj);
        var tt = transactionTime ?? _currentTransactionTime;
        
        var temporalKey = new TemporalKey
        {
            SubjectAtom = s,
            PredicateAtom = p,
            ObjectAtom = o,
            ValidFrom = validFrom,
            ValidTo = validTo,
            TransactionTime = tt
        };
        
        InsertIntoTree(temporalKey, _rootPageId);
    }

    /// <summary>
    /// Add a current fact (valid from now to end of time)
    /// </summary>
    public void AddCurrent(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj)
    {
        Add(subject, predicate, obj,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            long.MaxValue);
    }

    /// <summary>
    /// Add a historical fact (valid for specific time period)
    /// </summary>
    public void AddHistorical(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj,
        DateTimeOffset validFrom,
        DateTimeOffset validTo)
    {
        Add(subject, predicate, obj,
            validFrom.ToUnixTimeMilliseconds(),
            validTo.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// Query triples with temporal constraints
    /// </summary>
    public TemporalTripleEnumerator Query(
        int subjectAtom,
        int predicateAtom,
        int objectAtom,
        TemporalQuery temporalQuery)
    {
        var minKey = CreateSearchKey(subjectAtom, predicateAtom, objectAtom, temporalQuery, isMin: true);
        var maxKey = CreateSearchKey(subjectAtom, predicateAtom, objectAtom, temporalQuery, isMin: false);
        
        var leafPageId = FindLeafPage(_rootPageId, minKey);
        
        return new TemporalTripleEnumerator(
            this, 
            leafPageId, 
            minKey, 
            maxKey, 
            temporalQuery);
    }

    /// <summary>
    /// Query current state (as of now)
    /// </summary>
    public TemporalTripleEnumerator QueryCurrent(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj)
    {
        var s = subject.IsEmpty ? -1 : _atoms.GetAtomId(subject);
        var p = predicate.IsEmpty ? -1 : _atoms.GetAtomId(predicate);
        var o = obj.IsEmpty ? -1 : _atoms.GetAtomId(obj);
        
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        return Query(s, p, o, new TemporalQuery
        {
            Type = TemporalQueryType.AsOf,
            AsOfTime = now
        });
    }

    /// <summary>
    /// Query historical state (as of specific time)
    /// </summary>
    public TemporalTripleEnumerator QueryAsOf(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj,
        DateTimeOffset asOfTime)
    {
        var s = subject.IsEmpty ? -1 : _atoms.GetAtomId(subject);
        var p = predicate.IsEmpty ? -1 : _atoms.GetAtomId(predicate);
        var o = obj.IsEmpty ? -1 : _atoms.GetAtomId(obj);
        
        return Query(s, p, o, new TemporalQuery
        {
            Type = TemporalQueryType.AsOf,
            AsOfTime = asOfTime.ToUnixTimeMilliseconds()
        });
    }

    /// <summary>
    /// Query time range (all versions during period)
    /// </summary>
    public TemporalTripleEnumerator QueryRange(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd)
    {
        var s = subject.IsEmpty ? -1 : _atoms.GetAtomId(subject);
        var p = predicate.IsEmpty ? -1 : _atoms.GetAtomId(predicate);
        var o = obj.IsEmpty ? -1 : _atoms.GetAtomId(obj);
        
        return Query(s, p, o, new TemporalQuery
        {
            Type = TemporalQueryType.Range,
            RangeStart = rangeStart.ToUnixTimeMilliseconds(),
            RangeEnd = rangeEnd.ToUnixTimeMilliseconds()
        });
    }

    /// <summary>
    /// Query evolution (all versions ever)
    /// </summary>
    public TemporalTripleEnumerator QueryHistory(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj)
    {
        var s = subject.IsEmpty ? -1 : _atoms.GetAtomId(subject);
        var p = predicate.IsEmpty ? -1 : _atoms.GetAtomId(predicate);
        var o = obj.IsEmpty ? -1 : _atoms.GetAtomId(obj);
        
        return Query(s, p, o, new TemporalQuery
        {
            Type = TemporalQueryType.AllTime
        });
    }

    /// <summary>
    /// Temporal key: SPO + ValidTime + TransactionTime (32 bytes)
    /// Sorted lexicographically for temporal range queries
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TemporalKey : IComparable<TemporalKey>
    {
        public int SubjectAtom;
        public int PredicateAtom;
        public int ObjectAtom;
        public long ValidFrom;      // Valid-time start (milliseconds since epoch)
        public long ValidTo;        // Valid-time end (milliseconds since epoch)
        public long TransactionTime; // Transaction-time (when recorded)

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int CompareTo(TemporalKey other)
        {
            // Primary sort: SPO
            var cmp = SubjectAtom.CompareTo(other.SubjectAtom);
            if (cmp != 0) return cmp;
            
            cmp = PredicateAtom.CompareTo(other.PredicateAtom);
            if (cmp != 0) return cmp;
            
            cmp = ObjectAtom.CompareTo(other.ObjectAtom);
            if (cmp != 0) return cmp;
            
            // Secondary sort: Valid time
            cmp = ValidFrom.CompareTo(other.ValidFrom);
            if (cmp != 0) return cmp;
            
            cmp = ValidTo.CompareTo(other.ValidTo);
            if (cmp != 0) return cmp;
            
            // Tertiary sort: Transaction time
            return TransactionTime.CompareTo(other.TransactionTime);
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
            fixed (TemporalBTreePage* page = &this)
            {
                var entriesPtr = (TemporalBTreeEntry*)((byte*)page + 32);
                return ref entriesPtr[index];
            }
        }
    }

    /// <summary>
    /// B+Tree entry for temporal triple (80 bytes)
    /// Key: 32 bytes + Child/Value: 8 bytes + Metadata: 40 bytes
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
    /// Zero-allocation enumerator for temporal triples
    /// </summary>
    public ref struct TemporalTripleEnumerator
    {
        private readonly TemporalTripleStore _store;
        private long _currentPageId;
        private int _currentSlot;
        private readonly TemporalKey _minKey;
        private readonly TemporalKey _maxKey;
        private readonly TemporalQuery _query;
        private TemporalKey _currentKey;

        internal TemporalTripleEnumerator(
            TemporalTripleStore store,
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
                    
                    // Check spatial bounds
                    if (_currentKey.CompareTo(_maxKey) > 0)
                        return false;
                    
                    if (_currentKey.CompareTo(_minKey) >= 0)
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
            if (entry.IsDeleted)
                return false;
            
            var key = entry.Key;
            
            return _query.Type switch
            {
                TemporalQueryType.AsOf => 
                    key.ValidFrom <= _query.AsOfTime && 
                    key.ValidTo > _query.AsOfTime,
                
                TemporalQueryType.Range =>
                    key.ValidFrom < _query.RangeEnd &&
                    key.ValidTo > _query.RangeStart,
                
                TemporalQueryType.AllTime => true,
                
                _ => false
            };
        }

        public readonly TemporalTriple Current
        {
            get => new TemporalTriple
            {
                SubjectAtom = _currentKey.SubjectAtom,
                PredicateAtom = _currentKey.PredicateAtom,
                ObjectAtom = _currentKey.ObjectAtom,
                ValidFrom = _currentKey.ValidFrom,
                ValidTo = _currentKey.ValidTo,
                TransactionTime = _currentKey.TransactionTime
            };
        }

        public TemporalTripleEnumerator GetEnumerator() => this;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private TemporalBTreePage* GetPage(long pageId)
    {
        if (_pageCache.TryGet(pageId, out var cachedPtr))
            return (TemporalBTreePage*)cachedPtr;
        
        byte* ptr = null;
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        
        var pagePtr = (TemporalBTreePage*)(ptr + pageId * PageSize);
        _pageCache.Add(pageId, pagePtr);
        
        return pagePtr;
    }

    private void InsertIntoTree(TemporalKey key, long pageId)
    {
        var page = GetPage(pageId);
        
        if (page->IsLeaf)
        {
            InsertIntoLeaf(page, key);
        }
        else
        {
            var childPageId = FindChildPage(page, key);
            InsertIntoTree(key, childPageId);
        }
    }

    private void InsertIntoLeaf(TemporalBTreePage* page, TemporalKey key)
    {
        // Find insertion point
        int insertPos = 0;
        while (insertPos < page->EntryCount)
        {
            ref var entry = ref page->GetEntry(insertPos);
            if (key.CompareTo(entry.Key) < 0)
                break;
            insertPos++;
        }
        
        // Check for updates (same SPO, overlapping time)
        if (insertPos > 0)
        {
            ref var prevEntry = ref page->GetEntry(insertPos - 1);
            if (IsSameSPO(key, prevEntry.Key))
            {
                // Handle temporal update
                HandleTemporalUpdate(page, insertPos - 1, key);
                return;
            }
        }
        
        // Check if page is full
        if (page->EntryCount >= NodeDegree)
        {
            SplitLeafPage(page, key);
            return;
        }
        
        // Shift and insert
        for (int i = page->EntryCount; i > insertPos; i--)
        {
            page->GetEntry(i) = page->GetEntry(i - 1);
        }
        
        ref var newEntry = ref page->GetEntry(insertPos);
        newEntry.Key = key;
        newEntry.ChildOrValue = 0;
        newEntry.CreatedAt = _currentTransactionTime;
        newEntry.ModifiedAt = _currentTransactionTime;
        newEntry.Version = 1;
        newEntry.IsDeleted = false;
        
        page->EntryCount++;
        _tripleCount++;
        FlushPage(page);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSameSPO(TemporalKey a, TemporalKey b)
    {
        return a.SubjectAtom == b.SubjectAtom &&
               a.PredicateAtom == b.PredicateAtom &&
               a.ObjectAtom == b.ObjectAtom;
    }

    private void HandleTemporalUpdate(TemporalBTreePage* page, int existingIndex, TemporalKey newKey)
    {
        ref var existing = ref page->GetEntry(existingIndex);
        
        // Temporal update: adjust valid time of existing entry
        if (newKey.ValidFrom < existing.Key.ValidTo)
        {
            // Truncate existing entry
            existing.Key.ValidTo = newKey.ValidFrom;
            existing.ModifiedAt = _currentTransactionTime;
            FlushPage(page);
        }
    }

    private void SplitLeafPage(TemporalBTreePage* page, TemporalKey key)
    {
        // Allocate new page
        var newPageId = AllocatePage();
        var newPage = GetPage(newPageId);
        
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
        
        // Determine which page gets the new key
        ref var midEntry = ref page->GetEntry(midPoint - 1);
        if (key.CompareTo(midEntry.Key) < 0)
        {
            InsertIntoLeaf(page, key);
        }
        else
        {
            InsertIntoLeaf(newPage, key);
        }
        
        // Promote middle key to parent (simplified - would handle root splits properly)
        FlushPage(page);
        FlushPage(newPage);
    }

    private long AllocatePage()
    {
        var pageId = _nextPageId++;
        
        // Extend file if needed
        var requiredSize = (pageId + 1) * PageSize;
        if (requiredSize > _fileStream.Length)
        {
            lock (_fileStream)
            {
                if (requiredSize > _fileStream.Length)
                {
                    _fileStream.SetLength(Math.Max(_fileStream.Length * 2, requiredSize));
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
        int left = 0;
        int right = page->EntryCount - 1;
        
        while (left <= right)
        {
            int mid = left + (right - left) / 2;
            ref var entry = ref page->GetEntry(mid);
            
            var cmp = key.CompareTo(entry.Key);
            if (cmp < 0)
                right = mid - 1;
            else
                left = mid + 1;
        }
        
        if (right < 0)
            return page->GetEntry(0).ChildOrValue;
        
        return page->GetEntry(right).ChildOrValue;
    }

    private static TemporalKey CreateSearchKey(
        int subject, int predicate, int obj,
        TemporalQuery query,
        bool isMin)
    {
        var unboundValue = isMin ? 0 : int.MaxValue;
        var unboundTime = isMin ? 0L : long.MaxValue;
        
        return new TemporalKey
        {
            SubjectAtom = subject < 0 ? unboundValue : subject,
            PredicateAtom = predicate < 0 ? unboundValue : predicate,
            ObjectAtom = obj < 0 ? unboundValue : obj,
            ValidFrom = isMin ? 0 : long.MaxValue,
            ValidTo = isMin ? 0 : long.MaxValue,
            TransactionTime = isMin ? 0 : long.MaxValue
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FlushPage(TemporalBTreePage* page)
    {
        _accessor.Flush();
    }

    private void LoadMetadata()
    {
        if (_fileStream.Length >= PageSize)
        {
            _accessor.Read(0, out _rootPageId);
            _accessor.Read(8, out _nextPageId);
            _accessor.Read(16, out _tripleCount);
        }
        else
        {
            _rootPageId = 1;
            _nextPageId = 2;
            _tripleCount = 0;
            
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
        _accessor.Write(16, _tripleCount);
        _accessor.Flush();
    }

    public void Dispose()
    {
        SaveMetadata();
        _accessor?.Dispose();
        _mmapFile?.Dispose();
        _fileStream?.Dispose();
        _atoms?.Dispose();
        _pageCache?.Dispose();
    }

    public long TripleCount => _tripleCount;
}

/// <summary>
/// Temporal query specification
/// </summary>
public struct TemporalQuery
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
/// Temporal triple with time dimensions
/// </summary>
public struct TemporalTriple
{
    public int SubjectAtom;
    public int PredicateAtom;
    public int ObjectAtom;
    public long ValidFrom;
    public long ValidTo;
    public long TransactionTime;
}
