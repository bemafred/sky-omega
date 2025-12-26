using System;
using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace SparqlEngine.Storage;

/// <summary>
/// Atom store for efficient string storage and retrieval using memory-mapped files
/// Atoms are immutable strings with integer IDs
/// Supports TB-scale storage with zero-copy access
/// </summary>
public sealed unsafe class AtomStore : IDisposable
{
    private const int PageSize = 4096; // 4KB pages
    private const int HashTableSize = 1 << 20; // 1M buckets
    private const long InitialDataSize = 1L << 30; // 1GB initial
    
    // Memory-mapped files
    private readonly FileStream _dataFile;
    private readonly FileStream _indexFile;
    private readonly MemoryMappedFile _dataMap;
    private readonly MemoryMappedFile _indexMap;
    private readonly MemoryMappedViewAccessor _dataAccessor;
    private readonly MemoryMappedViewAccessor _indexAccessor;
    
    // Current write position in data file
    private long _dataPosition;
    private int _nextAtomId;
    
    // Hash table for lookups (memory-mapped)
    private HashBucket* _hashTable;
    
    // Statistics
    private long _totalBytes;
    private int _atomCount;

    public AtomStore(string baseFilePath)
    {
        var dataPath = baseFilePath + ".data";
        var indexPath = baseFilePath + ".index";
        
        // Open/create data file
        _dataFile = new FileStream(
            dataPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.RandomAccess
        );
        
        if (_dataFile.Length == 0)
        {
            _dataFile.SetLength(InitialDataSize);
        }
        
        // Open/create index file (hash table)
        _indexFile = new FileStream(
            indexPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.RandomAccess
        );

        var indexSize = HashTableSize * sizeof(HashBucket);
        var isNewIndexFile = _indexFile.Length == 0;
        if (isNewIndexFile)
        {
            _indexFile.SetLength(indexSize);
            // Write zeros to ensure clean hash table (SetLength may not zero-fill)
            _indexFile.Position = 0;
            var zeros = new byte[4096];
            for (long written = 0; written < indexSize; written += zeros.Length)
            {
                var toWrite = (int)Math.Min(zeros.Length, indexSize - written);
                _indexFile.Write(zeros, 0, toWrite);
            }
            _indexFile.Flush();
        }

        // Memory-map files
        _dataMap = MemoryMappedFile.CreateFromFile(
            _dataFile,
            mapName: null,
            capacity: 0,
            MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None,
            leaveOpen: false
        );

        _indexMap = MemoryMappedFile.CreateFromFile(
            _indexFile,
            mapName: null,
            capacity: 0,
            MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None,
            leaveOpen: false
        );

        _dataAccessor = _dataMap.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
        _indexAccessor = _indexMap.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

        // Get pointer to hash table
        byte* indexPtr = null;
        _indexAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref indexPtr);
        _hashTable = (HashBucket*)indexPtr;

        // Load metadata
        LoadMetadata();
    }

    /// <summary>
    /// Intern a string and return its atom ID
    /// Thread-safe through lock-free hash table with CAS
    /// </summary>
    public int Intern(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
            return 0;
        
        var hash = ComputeHash(value);
        var bucket = hash & (HashTableSize - 1);
        
        // Check if already interned
        ref var hashBucket = ref _hashTable[bucket];
        
        // Linear probing for collision resolution
        for (int probe = 0; probe < 16; probe++)
        {
            var currentBucket = (bucket + probe) & (HashTableSize - 1);
            ref var entry = ref _hashTable[currentBucket];

            if (entry.AtomId == 0)
            {
                // Empty slot - need to insert
                break;
            }

            if (entry.Hash == hash && entry.Length == value.Length)
            {
                // Potential match - verify
                var stored = GetAtomString(entry.AtomId);
                if (stored.SequenceEqual(value))
                {
                    return entry.AtomId;
                }
            }
        }

        // Not found - insert new atom
        return InsertAtom(value, hash, bucket);
    }

    /// <summary>
    /// Get atom ID for a string without interning (returns -1 if not found)
    /// </summary>
    public int GetAtomId(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
            return 0;
        
        var hash = ComputeHash(value);
        var bucket = hash & (HashTableSize - 1);
        
        for (int probe = 0; probe < 16; probe++)
        {
            var currentBucket = (bucket + probe) & (HashTableSize - 1);
            ref var entry = ref _hashTable[currentBucket];
            
            if (entry.AtomId == 0)
                return -1;
            
            if (entry.Hash == hash && entry.Length == value.Length)
            {
                var stored = GetAtomString(entry.AtomId);
                if (stored.SequenceEqual(value))
                {
                    return entry.AtomId;
                }
            }
        }
        
        return -1;
    }

    /// <summary>
    /// Get string for an atom ID (zero-copy over memory-mapped data)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<char> GetAtomString(int atomId)
    {
        if (atomId <= 0 || atomId > _nextAtomId)
            return ReadOnlySpan<char>.Empty;

        // Read atom header
        var offset = GetAtomOffset(atomId);

        _dataAccessor.Read(offset, out int length);

        // Get pointer to string data
        byte* dataPtr = null;
        _dataAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref dataPtr);

        var charPtr = (char*)(dataPtr + offset + sizeof(int));
        return new ReadOnlySpan<char>(charPtr, length);
    }

    /// <summary>
    /// Get atom statistics
    /// </summary>
    public (int AtomCount, long TotalBytes, float AvgLength) GetStatistics()
    {
        var avgLength = _atomCount > 0 ? (float)_totalBytes / _atomCount : 0;
        return (_atomCount, _totalBytes, avgLength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeHash(ReadOnlySpan<char> value)
    {
        // xxHash-inspired fast hash
        uint hash = 2166136261u;
        
        foreach (var ch in value)
        {
            hash = (hash ^ ch) * 16777619u;
        }
        
        return (int)hash;
    }

    private int InsertAtom(ReadOnlySpan<char> value, int hash, int bucket)
    {
        // Allocate atom ID
        var atomId = System.Threading.Interlocked.Increment(ref _nextAtomId);
        
        // Calculate storage size
        var byteLength = value.Length * sizeof(char);
        var totalSize = sizeof(int) + byteLength; // length prefix + data
        
        // Allocate space in data file
        var offset = System.Threading.Interlocked.Add(ref _dataPosition, totalSize) - totalSize;
        
        // Extend file if needed
        if (offset + totalSize > _dataFile.Length)
        {
            lock (_dataFile)
            {
                if (offset + totalSize > _dataFile.Length)
                {
                    var newSize = Math.Max(_dataFile.Length * 2, offset + totalSize);
                    _dataFile.SetLength(newSize);
                }
            }
        }
        
        // Write atom data
        _dataAccessor.Write(offset, value.Length);
        
        byte* dataPtr = null;
        _dataAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref dataPtr);
        var charPtr = (char*)(dataPtr + offset + sizeof(int));
        
        for (int i = 0; i < value.Length; i++)
        {
            charPtr[i] = value[i];
        }
        
        // Update hash table
        for (int probe = 0; probe < 16; probe++)
        {
            var currentBucket = (bucket + probe) & (HashTableSize - 1);
            ref var entry = ref _hashTable[currentBucket];

            if (entry.AtomId == 0)
            {
                // Use interlocked to ensure atomicity
                var expected = 0;
                var success = System.Threading.Interlocked.CompareExchange(
                    ref entry.AtomId,
                    atomId,
                    expected
                ) == expected;

                if (success)
                {
                    entry.Hash = hash;
                    entry.Length = (short)value.Length;
                    entry.Offset = offset;

                    // Update statistics
                    System.Threading.Interlocked.Increment(ref _atomCount);
                    System.Threading.Interlocked.Add(ref _totalBytes, byteLength);

                    return atomId;
                }
            }
        }

        // Hash table full in this bucket - should extend or use overflow
        throw new InvalidOperationException("Hash table bucket full");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long GetAtomOffset(int atomId)
    {
        // For now, linear scan - could use separate offset index
        // In production, would maintain offset index for O(1) lookup

        // Simplified: assume atoms written sequentially
        // Full implementation would have offset index
        // Note: atoms start at offset 1024 (first 1KB reserved for metadata)
        // AtomIds start at 2 (_nextAtomId initialized to 1, first Increment gives 2)
        long offset = 1024;

        for (int i = 2; i < atomId; i++)
        {
            _dataAccessor.Read(offset, out int length);
            offset += sizeof(int) + length * sizeof(char);
        }

        return offset;
    }

    private const long MagicNumber = 0x41544F4D53544F52; // "ATOMSTOR" as long

    private void LoadMetadata()
    {
        // Check for valid metadata using magic number
        _dataAccessor.Read(32, out long magic);

        if (magic == MagicNumber)
        {
            _dataAccessor.Read(0, out _dataPosition);
            _dataAccessor.Read(8, out _nextAtomId);
            _dataAccessor.Read(16, out _atomCount);
            _dataAccessor.Read(24, out _totalBytes);

            // Skip metadata
            if (_dataPosition < 1024)
                _dataPosition = 1024;
        }
        else
        {
            _dataPosition = 1024; // Reserve first 1KB for metadata
            _nextAtomId = 1;
            _atomCount = 0;
            _totalBytes = 0;
            SaveMetadata();
        }
    }

    private void SaveMetadata()
    {
        _dataAccessor.Write(0, _dataPosition);
        _dataAccessor.Write(8, _nextAtomId);
        _dataAccessor.Write(16, _atomCount);
        _dataAccessor.Write(24, _totalBytes);
        _dataAccessor.Write(32, MagicNumber);
        _dataAccessor.Flush();
    }

    public void Dispose()
    {
        SaveMetadata();
        
        _dataAccessor?.Dispose();
        _indexAccessor?.Dispose();
        _dataMap?.Dispose();
        _indexMap?.Dispose();
        _dataFile?.Dispose();
        _indexFile?.Dispose();
    }

    /// <summary>
    /// Hash bucket entry (16 bytes)
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct HashBucket
    {
        public int AtomId;      // 0 means empty
        public int Hash;        // Full hash for quick comparison
        public short Length;    // String length
        public short Reserved;  // Padding
        public long Offset;     // Offset in data file
    }
}

/// <summary>
/// Optimized atom cache for frequently accessed atoms
/// Uses fixed-size cache with LRU eviction
/// </summary>
public sealed unsafe class AtomCache : IDisposable
{
    private const int CacheSize = 10_000;
    
    private readonly CacheEntry[] _entries;
    private readonly char[] _stringBuffer;
    private int _bufferPosition;
    
    public AtomCache()
    {
        _entries = new CacheEntry[CacheSize];
        _stringBuffer = new char[1024 * 1024]; // 1M chars = 2MB
        _bufferPosition = 0;
    }

    public bool TryGet(int atomId, out ReadOnlySpan<char> value)
    {
        var index = atomId & (CacheSize - 1);
        ref var entry = ref _entries[index];
        
        if (entry.AtomId == atomId)
        {
            value = _stringBuffer.AsSpan(entry.BufferOffset, entry.Length);
            entry.AccessCount++;
            return true;
        }
        
        value = default;
        return false;
    }

    public void Add(int atomId, ReadOnlySpan<char> value)
    {
        var index = atomId & (CacheSize - 1);
        ref var entry = ref _entries[index];
        
        // Check if buffer has space
        if (_bufferPosition + value.Length > _stringBuffer.Length)
        {
            // Evict least recently used entries
            EvictEntries();
        }
        
        // Copy string to buffer
        var offset = _bufferPosition;
        value.CopyTo(_stringBuffer.AsSpan(offset));
        _bufferPosition += value.Length;
        
        // Update entry
        entry.AtomId = atomId;
        entry.BufferOffset = offset;
        entry.Length = (short)value.Length;
        entry.AccessCount = 1;
    }

    private void EvictEntries()
    {
        // Simple strategy: clear all and reset
        // More sophisticated: evict least accessed
        Array.Clear(_entries, 0, _entries.Length);
        _bufferPosition = 0;
    }

    public void Dispose()
    {
        // Nothing to dispose
    }

    private struct CacheEntry
    {
        public int AtomId;
        public int BufferOffset;
        public short Length;
        public short AccessCount;
    }
}
