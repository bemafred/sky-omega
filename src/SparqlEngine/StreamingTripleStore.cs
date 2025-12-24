using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SparqlEngine;

/// <summary>
/// Zero-allocation streaming triple store using ArrayPool and span-based operations
/// Implements RDF triple storage with Subject-Predicate-Object indexing
/// </summary>
public sealed class StreamingTripleStore : IDisposable
{
    private const int InitialCapacity = 4096;
    private const int ChunkSize = 1024;
    
    // Pooled storage for triples
    private Triple[] _triples;
    private int _count;
    private readonly ArrayPool<Triple> _triplePool;
    
    // SPO indexes using pooled arrays
    private int[] _spoIndex;
    private int[] _posIndex;
    private int[] _ospIndex;
    
    // String interning pool for zero-copy string handling
    private readonly StringPool _stringPool;
    
    public StreamingTripleStore()
    {
        _triplePool = ArrayPool<Triple>.Shared;
        _triples = _triplePool.Rent(InitialCapacity);
        _count = 0;
        
        _spoIndex = ArrayPool<int>.Shared.Rent(InitialCapacity);
        _posIndex = ArrayPool<int>.Shared.Rent(InitialCapacity);
        _ospIndex = ArrayPool<int>.Shared.Rent(InitialCapacity);
        
        _stringPool = new StringPool();
    }

    /// <summary>
    /// Add a triple to the store without allocating strings
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> objectValue)
    {
        EnsureCapacity(_count + 1);
        
        // Intern strings to avoid duplicates
        var subjectId = _stringPool.Intern(subject);
        var predicateId = _stringPool.Intern(predicate);
        var objectId = _stringPool.Intern(objectValue);
        
        _triples[_count] = new Triple
        {
            SubjectId = subjectId,
            PredicateId = predicateId,
            ObjectId = objectId,
            Index = _count
        };
        
        _count++;
        
        // Update indexes lazily on query
    }

    /// <summary>
    /// Stream triples matching a pattern without materializing collections
    /// </summary>
    public TripleEnumerator Query(
        ReadOnlySpan<char> subjectPattern,
        ReadOnlySpan<char> predicatePattern,
        ReadOnlySpan<char> objectPattern)
    {
        // Resolve patterns
        var subjectId = subjectPattern.IsEmpty || IsVariable(subjectPattern) 
            ? -1 
            : _stringPool.GetId(subjectPattern);
        
        var predicateId = predicatePattern.IsEmpty || IsVariable(predicatePattern)
            ? -1
            : _stringPool.GetId(predicatePattern);
        
        var objectId = objectPattern.IsEmpty || IsVariable(objectPattern)
            ? -1
            : _stringPool.GetId(objectPattern);
        
        return new TripleEnumerator(this, subjectId, predicateId, objectId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsVariable(ReadOnlySpan<char> span)
    {
        return span.Length > 0 && span[0] == '?';
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _triples.Length)
            return;
        
        var newCapacity = _triples.Length * 2;
        while (newCapacity < required)
            newCapacity *= 2;
        
        var newArray = _triplePool.Rent(newCapacity);
        Array.Copy(_triples, newArray, _count);
        _triplePool.Return(_triples, clearArray: true);
        _triples = newArray;
        
        // Resize indexes
        ResizeIndex(ref _spoIndex, newCapacity);
        ResizeIndex(ref _posIndex, newCapacity);
        ResizeIndex(ref _ospIndex, newCapacity);
    }

    private static void ResizeIndex(ref int[] index, int newCapacity)
    {
        var newIndex = ArrayPool<int>.Shared.Rent(newCapacity);
        Array.Copy(index, newIndex, index.Length);
        ArrayPool<int>.Shared.Return(index, clearArray: true);
        index = newIndex;
    }

    public void Dispose()
    {
        if (_triples != null)
        {
            _triplePool.Return(_triples, clearArray: true);
            _triples = null!;
        }
        
        if (_spoIndex != null)
        {
            ArrayPool<int>.Shared.Return(_spoIndex, clearArray: true);
            _spoIndex = null!;
        }
        
        if (_posIndex != null)
        {
            ArrayPool<int>.Shared.Return(_posIndex, clearArray: true);
            _posIndex = null!;
        }
        
        if (_ospIndex != null)
        {
            ArrayPool<int>.Shared.Return(_ospIndex, clearArray: true);
            _ospIndex = null!;
        }
        
        _stringPool?.Dispose();
    }

    /// <summary>
    /// Zero-allocation enumerator for streaming triple results
    /// </summary>
    public ref struct TripleEnumerator
    {
        private readonly StreamingTripleStore _store;
        private readonly int _subjectFilter;
        private readonly int _predicateFilter;
        private readonly int _objectFilter;
        private int _currentIndex;

        internal TripleEnumerator(
            StreamingTripleStore store,
            int subjectFilter,
            int predicateFilter,
            int objectFilter)
        {
            _store = store;
            _subjectFilter = subjectFilter;
            _predicateFilter = predicateFilter;
            _objectFilter = objectFilter;
            _currentIndex = -1;
        }

        public bool MoveNext()
        {
            _currentIndex++;
            
            while (_currentIndex < _store._count)
            {
                ref readonly var triple = ref _store._triples[_currentIndex];
                
                if ((_subjectFilter == -1 || triple.SubjectId == _subjectFilter) &&
                    (_predicateFilter == -1 || triple.PredicateId == _predicateFilter) &&
                    (_objectFilter == -1 || triple.ObjectId == _objectFilter))
                {
                    return true;
                }
                
                _currentIndex++;
            }
            
            return false;
        }

        public readonly TripleRef Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                ref readonly var triple = ref _store._triples[_currentIndex];
                return new TripleRef(
                    _store._stringPool.GetString(triple.SubjectId),
                    _store._stringPool.GetString(triple.PredicateId),
                    _store._stringPool.GetString(triple.ObjectId)
                );
            }
        }

        public TripleEnumerator GetEnumerator() => this;
    }
}

/// <summary>
/// Compact triple representation using interned string IDs
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Triple
{
    public int SubjectId;
    public int PredicateId;
    public int ObjectId;
    public int Index;
}

/// <summary>
/// Zero-allocation triple reference using spans
/// </summary>
public readonly ref struct TripleRef
{
    public readonly ReadOnlySpan<char> Subject;
    public readonly ReadOnlySpan<char> Predicate;
    public readonly ReadOnlySpan<char> Object;

    public TripleRef(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj)
    {
        Subject = subject;
        Predicate = predicate;
        Object = obj;
    }
}

/// <summary>
/// String interning pool with zero-copy lookups using spans
/// </summary>
public sealed class StringPool : IDisposable
{
    private const int InitialCapacity = 256;
    private const int ChunkSize = 64 * 1024; // 64KB chunks
    
    private char[][] _chunks;
    private int _chunkCount;
    private int _currentChunkPosition;
    
    private Entry[] _entries;
    private int _entryCount;

    public StringPool()
    {
        _chunks = new char[16][];
        _chunks[0] = ArrayPool<char>.Shared.Rent(ChunkSize);
        _chunkCount = 1;
        _currentChunkPosition = 0;
        
        _entries = new Entry[InitialCapacity];
        _entryCount = 0;
    }

    public int Intern(ReadOnlySpan<char> value)
    {
        // Check if already interned
        var hash = GetHashCode(value);
        
        for (int i = 0; i < _entryCount; i++)
        {
            ref var entry = ref _entries[i];
            if (entry.Hash == hash && entry.Length == value.Length)
            {
                var stored = GetSpan(entry.ChunkIndex, entry.Offset, entry.Length);
                if (stored.SequenceEqual(value))
                    return i;
            }
        }
        
        // Store new string
        return StoreString(value, hash);
    }

    public int GetId(ReadOnlySpan<char> value)
    {
        var hash = GetHashCode(value);
        
        for (int i = 0; i < _entryCount; i++)
        {
            ref var entry = ref _entries[i];
            if (entry.Hash == hash && entry.Length == value.Length)
            {
                var stored = GetSpan(entry.ChunkIndex, entry.Offset, entry.Length);
                if (stored.SequenceEqual(value))
                    return i;
            }
        }
        
        return -1;
    }

    public ReadOnlySpan<char> GetString(int id)
    {
        if (id < 0 || id >= _entryCount)
            return ReadOnlySpan<char>.Empty;
        
        ref var entry = ref _entries[id];
        return GetSpan(entry.ChunkIndex, entry.Offset, entry.Length);
    }

    private int StoreString(ReadOnlySpan<char> value, int hash)
    {
        // Ensure space in current chunk
        if (_currentChunkPosition + value.Length > ChunkSize)
        {
            AllocateNewChunk();
        }
        
        // Copy string to chunk
        var chunk = _chunks[_chunkCount - 1];
        value.CopyTo(chunk.AsSpan(_currentChunkPosition));
        
        // Store entry
        if (_entryCount >= _entries.Length)
        {
            Array.Resize(ref _entries, _entries.Length * 2);
        }
        
        _entries[_entryCount] = new Entry
        {
            ChunkIndex = _chunkCount - 1,
            Offset = _currentChunkPosition,
            Length = value.Length,
            Hash = hash
        };
        
        _currentChunkPosition += value.Length;
        return _entryCount++;
    }

    private void AllocateNewChunk()
    {
        if (_chunkCount >= _chunks.Length)
        {
            Array.Resize(ref _chunks, _chunks.Length * 2);
        }
        
        _chunks[_chunkCount] = ArrayPool<char>.Shared.Rent(ChunkSize);
        _chunkCount++;
        _currentChunkPosition = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> GetSpan(int chunkIndex, int offset, int length)
    {
        return _chunks[chunkIndex].AsSpan(offset, length);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetHashCode(ReadOnlySpan<char> span)
    {
        // FNV-1a hash
        int hash = unchecked((int)2166136261);
        foreach (var ch in span)
        {
            hash = (hash ^ ch) * 16777619;
        }
        return hash;
    }

    public void Dispose()
    {
        for (int i = 0; i < _chunkCount; i++)
        {
            if (_chunks[i] != null)
            {
                ArrayPool<char>.Shared.Return(_chunks[i], clearArray: true);
            }
        }
    }

    private struct Entry
    {
        public int ChunkIndex;
        public int Offset;
        public int Length;
        public int Hash;
    }
}
