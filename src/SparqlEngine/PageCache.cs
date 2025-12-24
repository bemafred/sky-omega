using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SparqlEngine.Storage;

/// <summary>
/// LRU page cache for B+Tree pages
/// Zero-allocation design using fixed-size cache
/// </summary>
public sealed unsafe class PageCache : IDisposable
{
    private readonly int _capacity;
    private readonly CacheEntry[] _entries;
    private readonly long[] _pageIds;
    private int _count;
    private int _clock; // For clock algorithm (approximation of LRU)

    public PageCache(int capacity)
    {
        _capacity = capacity;
        _entries = new CacheEntry[capacity];
        _pageIds = new long[capacity];
        _count = 0;
        _clock = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(long pageId, out void* pagePtr)
    {
        // Linear probe (could use hash table for larger caches)
        for (int i = 0; i < _count; i++)
        {
            if (_pageIds[i] == pageId)
            {
                ref var entry = ref _entries[i];
                entry.Referenced = true;
                entry.AccessCount++;
                pagePtr = entry.PagePtr;
                return true;
            }
        }
        
        pagePtr = null;
        return false;
    }

    public void Add(long pageId, void* pagePtr)
    {
        if (_count < _capacity)
        {
            // Simple add
            _pageIds[_count] = pageId;
            _entries[_count] = new CacheEntry
            {
                PagePtr = pagePtr,
                Referenced = true,
                AccessCount = 1
            };
            _count++;
        }
        else
        {
            // Evict using clock algorithm
            var victim = FindVictim();
            _pageIds[victim] = pageId;
            _entries[victim] = new CacheEntry
            {
                PagePtr = pagePtr,
                Referenced = true,
                AccessCount = 1
            };
        }
    }

    private int FindVictim()
    {
        // Clock algorithm (second-chance)
        while (true)
        {
            ref var entry = ref _entries[_clock];
            
            if (!entry.Referenced)
            {
                var victim = _clock;
                _clock = (_clock + 1) % _capacity;
                return victim;
            }
            
            entry.Referenced = false;
            _clock = (_clock + 1) % _capacity;
        }
    }

    public void Clear()
    {
        Array.Clear(_entries, 0, _entries.Length);
        Array.Clear(_pageIds, 0, _pageIds.Length);
        _count = 0;
        _clock = 0;
    }

    public (int Count, int Capacity, long TotalAccesses) GetStatistics()
    {
        long totalAccesses = 0;
        for (int i = 0; i < _count; i++)
        {
            totalAccesses += _entries[i].AccessCount;
        }
        
        return (_count, _capacity, totalAccesses);
    }

    public void Dispose()
    {
        Clear();
    }

    private struct CacheEntry
    {
        public void* PagePtr;
        public bool Referenced;
        public int AccessCount;
    }
}

/// <summary>
/// Multi-index system for SPO, POS, OSP access patterns
/// Each index is a separate B+Tree with different key ordering
/// </summary>
public sealed class MultiIndexStore : IDisposable
{
    private readonly BPlusTreeStore _spoIndex; // Subject-Predicate-Object
    private readonly BPlusTreeStore _posIndex; // Predicate-Object-Subject
    private readonly BPlusTreeStore _ospIndex; // Object-Subject-Predicate
    
    private readonly AtomStore _atoms;

    public MultiIndexStore(string baseDirectory)
    {
        if (!Directory.Exists(baseDirectory))
            Directory.CreateDirectory(baseDirectory);
        
        var spoPath = Path.Combine(baseDirectory, "spo.db");
        var posPath = Path.Combine(baseDirectory, "pos.db");
        var ospPath = Path.Combine(baseDirectory, "osp.db");
        var atomPath = Path.Combine(baseDirectory, "atoms");
        
        _spoIndex = new BPlusTreeStore(spoPath);
        _posIndex = new BPlusTreeStore(posPath);
        _ospIndex = new BPlusTreeStore(ospPath);
        _atoms = new AtomStore(atomPath);
    }

    /// <summary>
    /// Add triple to all indexes
    /// </summary>
    public void Add(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj)
    {
        // Intern strings once
        var s = _atoms.Intern(subject);
        var p = _atoms.Intern(predicate);
        var o = _atoms.Intern(obj);
        
        // Add to all indexes
        _spoIndex.Add(s, p, o);
        _posIndex.Add(p, o, s);
        _ospIndex.Add(o, s, p);
    }

    /// <summary>
    /// Query using optimal index based on bound variables
    /// </summary>
    public MultiIndexEnumerator Query(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj)
    {
        // Determine which variables are bound
        var subjectBound = !subject.IsEmpty && subject[0] != '?';
        var predicateBound = !predicate.IsEmpty && predicate[0] != '?';
        var objectBound = !obj.IsEmpty && obj[0] != '?';
        
        // Select optimal index
        BPlusTreeStore selectedIndex;
        int atom1, atom2, atom3;
        IndexType indexType;
        
        if (subjectBound)
        {
            // Use SPO index
            selectedIndex = _spoIndex;
            indexType = IndexType.SPO;
            atom1 = _atoms.GetAtomId(subject);
            atom2 = predicateBound ? _atoms.GetAtomId(predicate) : -1;
            atom3 = objectBound ? _atoms.GetAtomId(obj) : -1;
        }
        else if (predicateBound)
        {
            // Use POS index
            selectedIndex = _posIndex;
            indexType = IndexType.POS;
            atom1 = _atoms.GetAtomId(predicate);
            atom2 = objectBound ? _atoms.GetAtomId(obj) : -1;
            atom3 = -1; // Subject unbound
        }
        else if (objectBound)
        {
            // Use OSP index
            selectedIndex = _ospIndex;
            indexType = IndexType.OSP;
            atom1 = _atoms.GetAtomId(obj);
            atom2 = -1; // Subject unbound
            atom3 = -1; // Predicate unbound
        }
        else
        {
            // Full scan - use SPO
            selectedIndex = _spoIndex;
            indexType = IndexType.SPO;
            atom1 = atom2 = atom3 = -1;
        }
        
        var enumerator = selectedIndex.Query(atom1, atom2, atom3);
        return new MultiIndexEnumerator(enumerator, indexType, _atoms);
    }

    public (long TripleCount, int AtomCount, long TotalBytes) GetStatistics()
    {
        var tripleCount = _spoIndex.TripleCount;
        var (atomCount, totalBytes, _) = _atoms.GetStatistics();
        return (tripleCount, atomCount, totalBytes);
    }

    public void Dispose()
    {
        _spoIndex?.Dispose();
        _posIndex?.Dispose();
        _ospIndex?.Dispose();
        _atoms?.Dispose();
    }
}

/// <summary>
/// Enumerator that remaps results from different indexes back to SPO order
/// </summary>
public ref struct MultiIndexEnumerator
{
    private BPlusTreeStore.TripleEnumerator _baseEnumerator;
    private readonly IndexType _indexType;
    private readonly AtomStore _atoms;

    internal MultiIndexEnumerator(
        BPlusTreeStore.TripleEnumerator baseEnumerator,
        IndexType indexType,
        AtomStore atoms)
    {
        _baseEnumerator = baseEnumerator;
        _indexType = indexType;
        _atoms = atoms;
    }

    public bool MoveNext() => _baseEnumerator.MoveNext();

    public readonly ResolvedTriple Current
    {
        get
        {
            var triple = _baseEnumerator.Current;
            
            // Remap based on index type
            int s, p, o;
            
            switch (_indexType)
            {
                case IndexType.SPO:
                    s = triple.SubjectAtom;
                    p = triple.PredicateAtom;
                    o = triple.ObjectAtom;
                    break;
                
                case IndexType.POS:
                    p = triple.SubjectAtom;
                    o = triple.PredicateAtom;
                    s = triple.ObjectAtom;
                    break;
                
                case IndexType.OSP:
                    o = triple.SubjectAtom;
                    s = triple.PredicateAtom;
                    p = triple.ObjectAtom;
                    break;
                
                default:
                    s = p = o = 0;
                    break;
            }
            
            return new ResolvedTriple
            {
                Subject = _atoms.GetAtomString(s),
                Predicate = _atoms.GetAtomString(p),
                Object = _atoms.GetAtomString(o)
            };
        }
    }

    public MultiIndexEnumerator GetEnumerator() => this;
}

public enum IndexType
{
    SPO,
    POS,
    OSP
}

public readonly ref struct ResolvedTriple
{
    public readonly ReadOnlySpan<char> Subject;
    public readonly ReadOnlySpan<char> Predicate;
    public readonly ReadOnlySpan<char> Object;
}
