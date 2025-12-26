using System;
using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SparqlEngine.Storage;

/// <summary>
/// Memory-mapped B+Tree for TB-scale triple storage with zero-GC design
/// Uses memory-mapped files for persistence and mmap for zero-copy access
/// </summary>
public sealed unsafe class BPlusTreeStore : IDisposable
{
    private const int PageSize = 16384; // 16KB pages - common SSD block size
    private const int NodeDegree = 341; // (16384 - 32 header) / 48 bytes per entry
    private const int MaxKeySize = 256; // Max atom string length
    
    // Memory-mapped file handles
    private readonly FileStream _fileStream;
    private readonly MemoryMappedFile _mmapFile;
    private readonly MemoryMappedViewAccessor _accessor;
    
    // Atom storage for string interning
    private readonly AtomStore _atoms;
    
    // Root page ID
    private long _rootPageId;
    private long _nextPageId;
    
    // Page cache (LRU with fixed size, pooled)
    private readonly PageCache _pageCache;
    
    // Statistics
    private long _tripleCount;

    public BPlusTreeStore(string filePath, long initialSizeBytes = 1L << 30) // 1GB initial
    {
        // Create or open the data file
        _fileStream = new FileStream(
            filePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.RandomAccess | FileOptions.WriteThrough
        );
        
        // Set initial size
        if (_fileStream.Length == 0)
        {
            _fileStream.SetLength(initialSizeBytes);
        }
        
        // Memory-map the file
        _mmapFile = MemoryMappedFile.CreateFromFile(
            _fileStream,
            mapName: null,
            capacity: 0, // Use file size
            MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None,
            leaveOpen: false
        );
        
        _accessor = _mmapFile.CreateViewAccessor(
            offset: 0,
            size: 0, // Map entire file
            MemoryMappedFileAccess.ReadWrite
        );
        
        // Initialize atom store
        var atomFilePath = filePath + ".atoms";
        _atoms = new AtomStore(atomFilePath);
        
        // Initialize page cache
        _pageCache = new PageCache(capacity: 10_000); // ~160MB cache
        
        // Load or initialize metadata
        LoadMetadata();
    }

    /// <summary>
    /// Add a triple using atom IDs (strings pre-interned)
    /// </summary>
    public void Add(int subjectAtom, int predicateAtom, int objectAtom)
    {
        // Create composite key: SPO order for primary index
        var key = CreateCompositeKey(subjectAtom, predicateAtom, objectAtom);
        
        // Insert into B+Tree
        InsertIntoTree(key, _rootPageId);
        
        _tripleCount++;
    }

    /// <summary>
    /// Add a triple by interning strings automatically
    /// </summary>
    public void Add(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj)
    {
        var subjectAtom = _atoms.Intern(subject);
        var predicateAtom = _atoms.Intern(predicate);
        var objectAtom = _atoms.Intern(obj);
        
        Add(subjectAtom, predicateAtom, objectAtom);
    }

    /// <summary>
    /// Query triples by pattern - returns enumerator over memory-mapped data
    /// </summary>
    public TripleEnumerator Query(int subjectAtom, int predicateAtom, int objectAtom)
    {
        // Determine search key based on bound variables
        var minKey = CreateSearchKey(subjectAtom, predicateAtom, objectAtom, isMin: true);
        var maxKey = CreateSearchKey(subjectAtom, predicateAtom, objectAtom, isMin: false);
        
        // Find starting leaf page
        var leafPageId = FindLeafPage(_rootPageId, minKey);
        
        return new TripleEnumerator(this, leafPageId, minKey, maxKey);
    }

    /// <summary>
    /// Zero-allocation enumerator over memory-mapped B+Tree leaves
    /// </summary>
    public ref struct TripleEnumerator
    {
        private readonly BPlusTreeStore _store;
        private long _currentPageId;
        private int _currentSlot;
        private readonly CompositeKey _minKey;
        private readonly CompositeKey _maxKey;
        private CompositeKey _currentKey;

        internal TripleEnumerator(
            BPlusTreeStore store,
            long startPageId,
            CompositeKey minKey,
            CompositeKey maxKey)
        {
            _store = store;
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
                var page = _store.GetPage(_currentPageId);
                
                // Scan current page
                while (_currentSlot < page->EntryCount)
                {
                    ref var entry = ref page->GetEntry(_currentSlot);
                    _currentKey = entry.Key;
                    
                    // Check if within range
                    if (_currentKey.CompareTo(_maxKey) > 0)
                        return false;
                    
                    if (_currentKey.CompareTo(_minKey) >= 0)
                    {
                        _currentSlot++;
                        return true;
                    }
                    
                    _currentSlot++;
                }
                
                // Move to next leaf page
                _currentPageId = page->NextLeaf;
                _currentSlot = 0;
            }
            
            return false;
        }

        public readonly Triple Current
        {
            get
            {
                return new Triple
                {
                    SubjectAtom = _currentKey.SubjectAtom,
                    PredicateAtom = _currentKey.PredicateAtom,
                    ObjectAtom = _currentKey.ObjectAtom
                };
            }
        }

        public TripleEnumerator GetEnumerator() => this;
    }

    /// <summary>
    /// B+Tree page structure (16KB)
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = PageSize)]
    private struct BTreePage
    {
        [FieldOffset(0)] public long PageId;
        [FieldOffset(8)] public bool IsLeaf;
        [FieldOffset(9)] public short EntryCount;
        [FieldOffset(16)] public long ParentPageId;
        [FieldOffset(24)] public long NextLeaf; // For leaf pages
        
        // Entries start at offset 32
        // Each entry: 12 bytes key (3x int) + 8 bytes value/child pointer = 20 bytes
        // Can fit 341 entries per page
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly ref BTreeEntry GetEntry(int index)
        {
            if (index >= NodeDegree)
                throw new IndexOutOfRangeException();
            
            fixed (BTreePage* page = &this)
            {
                var entriesPtr = (BTreeEntry*)((byte*)page + 32);
                return ref entriesPtr[index];
            }
        }
    }

    /// <summary>
    /// B+Tree entry (20 bytes)
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BTreeEntry
    {
        public CompositeKey Key;
        public long ChildOrValue; // Child page ID for internal nodes, value for leaves
    }

    /// <summary>
    /// Composite key for triple storage (12 bytes)
    /// SPO order: Subject-Predicate-Object
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct CompositeKey : IComparable<CompositeKey>
    {
        public int SubjectAtom;
        public int PredicateAtom;
        public int ObjectAtom;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int CompareTo(CompositeKey other)
        {
            // Lexicographic comparison: S, then P, then O
            var cmp = SubjectAtom.CompareTo(other.SubjectAtom);
            if (cmp != 0) return cmp;
            
            cmp = PredicateAtom.CompareTo(other.PredicateAtom);
            if (cmp != 0) return cmp;
            
            return ObjectAtom.CompareTo(other.ObjectAtom);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static CompositeKey CreateCompositeKey(int subject, int predicate, int obj)
    {
        return new CompositeKey
        {
            SubjectAtom = subject,
            PredicateAtom = predicate,
            ObjectAtom = obj
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static CompositeKey CreateSearchKey(int subject, int predicate, int obj, bool isMin)
    {
        // -1 means unbound (wildcard)
        // For min key: use 0 for unbound positions
        // For max key: use int.MaxValue for unbound positions
        var unboundValue = isMin ? 0 : int.MaxValue;
        
        return new CompositeKey
        {
            SubjectAtom = subject < 0 ? unboundValue : subject,
            PredicateAtom = predicate < 0 ? unboundValue : predicate,
            ObjectAtom = obj < 0 ? unboundValue : obj
        };
    }

    /// <summary>
    /// Get page from memory-mapped file (zero-copy)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private BTreePage* GetPage(long pageId)
    {
        // Check cache first
        if (_pageCache.TryGet(pageId, out var cachedPtr))
            return (BTreePage*)cachedPtr;
        
        // Memory-map page directly
        byte* ptr = null;
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        
        var pagePtr = (BTreePage*)(ptr + pageId * PageSize);
        
        // Add to cache
        _pageCache.Add(pageId, pagePtr);
        
        return pagePtr;
    }

    private void InsertIntoTree(CompositeKey key, long pageId)
    {
        var page = GetPage(pageId);
        
        if (page->IsLeaf)
        {
            // Insert into leaf
            InsertIntoLeaf(page, key);
        }
        else
        {
            // Find child to descend into
            var childPageId = FindChildPage(page, key);
            InsertIntoTree(key, childPageId);
        }
    }

    private void InsertIntoLeaf(BTreePage* page, CompositeKey key)
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
        
        // Check for duplicates
        if (insertPos > 0)
        {
            ref var prevEntry = ref page->GetEntry(insertPos - 1);
            if (key.CompareTo(prevEntry.Key) == 0)
                return; // Already exists
        }
        
        // Check if page is full
        if (page->EntryCount >= NodeDegree)
        {
            SplitLeafPage(page, key);
            return;
        }
        
        // Shift entries to make room
        for (int i = page->EntryCount; i > insertPos; i--)
        {
            page->GetEntry(i) = page->GetEntry(i - 1);
        }
        
        // Insert new entry
        ref var newEntry = ref page->GetEntry(insertPos);
        newEntry.Key = key;
        newEntry.ChildOrValue = 0; // Could store additional data here
        
        page->EntryCount++;
        
        // Flush page to disk
        FlushPage(page);
    }

    private long FindLeafPage(long pageId, CompositeKey key)
    {
        var page = GetPage(pageId);
        
        if (page->IsLeaf)
            return pageId;
        
        // Binary search for child
        var childPageId = FindChildPage(page, key);
        return FindLeafPage(childPageId, key);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long FindChildPage(BTreePage* page, CompositeKey key)
    {
        // Binary search
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
        
        // Return appropriate child
        if (right < 0)
            return page->GetEntry(0).ChildOrValue;
        
        return page->GetEntry(right).ChildOrValue;
    }

    private void SplitLeafPage(BTreePage* page, CompositeKey key)
    {
        // Allocate new page
        var newPageId = AllocatePage();
        var newPage = GetPage(newPageId);
        
        newPage->IsLeaf = true;
        newPage->EntryCount = 0;
        newPage->ParentPageId = page->ParentPageId;
        newPage->NextLeaf = page->NextLeaf;
        page->NextLeaf = newPageId;
        
        // Split entries
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
        
        // Promote middle key to parent
        ref var promoteEntry = ref newPage->GetEntry(0);
        PromoteToParent(page->ParentPageId, promoteEntry.Key, newPageId);
        
        FlushPage(page);
        FlushPage(newPage);
    }

    private void PromoteToParent(long parentPageId, CompositeKey key, long rightChildPageId)
    {
        // Simplified - full implementation would handle root splits
        if (parentPageId == 0)
        {
            // Create new root
            var newRootId = AllocatePage();
            var newRoot = GetPage(newRootId);
            
            newRoot->IsLeaf = false;
            newRoot->EntryCount = 1;
            newRoot->ParentPageId = 0;
            
            ref var entry = ref newRoot->GetEntry(0);
            entry.Key = key;
            entry.ChildOrValue = rightChildPageId;
            
            _rootPageId = newRootId;
            SaveMetadata();
        }
    }

    private long AllocatePage()
    {
        var pageId = _nextPageId++;
        
        // Extend file if needed
        var requiredSize = (pageId + 1) * PageSize;
        if (requiredSize > _fileStream.Length)
        {
            _fileStream.SetLength(_fileStream.Length * 2);
        }
        
        SaveMetadata();
        return pageId;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FlushPage(BTreePage* page)
    {
        // Memory-mapped I/O handles flushing, but we can force it
        _accessor.Flush();
    }

    private const long MagicNumber = 0x4250545245455F31; // "BPTREE_1" as long

    private void LoadMetadata()
    {
        // Check for valid metadata using magic number
        _accessor.Read(24, out long magic);

        if (magic == MagicNumber)
        {
            // Existing database - load metadata
            _accessor.Read(0, out _rootPageId);
            _accessor.Read(8, out _nextPageId);
            _accessor.Read(16, out _tripleCount);
        }
        else
        {
            // Initialize new file
            _rootPageId = 1;
            _nextPageId = 2;
            _tripleCount = 0;

            // Initialize root page
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
        _accessor.Write(24, MagicNumber);
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

public struct Triple
{
    public int SubjectAtom;
    public int PredicateAtom;
    public int ObjectAtom;
}
