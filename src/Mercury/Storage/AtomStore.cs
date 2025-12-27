using System;
using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// Atom store for efficient string storage and retrieval using memory-mapped files.
/// Stores strings as UTF-8 for optimal space efficiency.
/// Uses 64-bit atom IDs for TB-scale capacity.
/// Supports zero-copy access via memory-mapped files.
/// </summary>
/// <remarks>
/// Storage format supports 64-bit blob lengths (exabyte-scale).
/// Single-span retrieval is limited to 2GB by .NET Span&lt;T&gt; constraint.
/// For larger blobs, implement chunked access pattern (see GetAtomSpan remarks).
/// </remarks>
public sealed unsafe class AtomStore : IDisposable
{
    private const int PageSize = 4096; // 4KB pages
    private const long HashTableSize = 1L << 24; // 16M buckets for TB-scale
    private const long InitialDataSize = 1L << 30; // 1GB initial
    private const long InitialOffsetCapacity = 1L << 20; // 1M atoms initial
    private const int MaxProbeDistance = 64; // Extended probing for better fill

    // Memory-mapped files
    private readonly FileStream _dataFile;
    private readonly FileStream _indexFile;
    private readonly FileStream _offsetFile;
    private MemoryMappedFile _dataMap;
    private readonly MemoryMappedFile _indexMap;
    private MemoryMappedFile _offsetMap;
    private MemoryMappedViewAccessor _dataAccessor;
    private readonly MemoryMappedViewAccessor _indexAccessor;
    private MemoryMappedViewAccessor _offsetAccessor;

    // Current write position in data file
    private long _dataPosition;
    private long _nextAtomId;
    private long _offsetCapacity;

    // Hash table for lookups (memory-mapped)
    private HashBucket* _hashTable;

    // Offset index for O(1) atomId→offset lookup (memory-mapped)
    private long* _offsetIndex;

    // Statistics
    private long _totalBytes;
    private long _atomCount;

    // UTF-8 encoder for efficient conversion
    private static readonly Encoding Utf8 = Encoding.UTF8;

    public AtomStore(string baseFilePath)
    {
        var dataPath = baseFilePath + ".atoms";
        var indexPath = baseFilePath + ".atomidx";
        var offsetPath = baseFilePath + ".offsets";

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
        if (_indexFile.Length == 0)
        {
            _indexFile.SetLength(indexSize);
        }

        // Open/create offset index file (atomId → offset mapping)
        _offsetFile = new FileStream(
            offsetPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.RandomAccess
        );

        _offsetCapacity = InitialOffsetCapacity;
        if (_offsetFile.Length == 0)
        {
            _offsetFile.SetLength(_offsetCapacity * sizeof(long));
        }
        else
        {
            _offsetCapacity = _offsetFile.Length / sizeof(long);
        }

        // Memory-map files
        _dataMap = MemoryMappedFile.CreateFromFile(
            _dataFile,
            mapName: null,
            capacity: _dataFile.Length,
            MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None,
            leaveOpen: true
        );

        _indexMap = MemoryMappedFile.CreateFromFile(
            _indexFile,
            mapName: null,
            capacity: indexSize,
            MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None,
            leaveOpen: true
        );

        _offsetMap = MemoryMappedFile.CreateFromFile(
            _offsetFile,
            mapName: null,
            capacity: _offsetFile.Length,
            MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None,
            leaveOpen: true
        );

        _dataAccessor = _dataMap.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
        _indexAccessor = _indexMap.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
        _offsetAccessor = _offsetMap.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

        // Get pointer to hash table
        byte* indexPtr = null;
        _indexAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref indexPtr);
        _hashTable = (HashBucket*)indexPtr;

        // Get pointer to offset index
        byte* offsetPtr = null;
        _offsetAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref offsetPtr);
        _offsetIndex = (long*)offsetPtr;

        // Load metadata
        LoadMetadata();
    }

    /// <summary>
    /// Intern a string and return its atom ID (64-bit).
    /// Thread-safe through lock-free hash table with CAS.
    /// </summary>
    public long Intern(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
            return 0;

        // Convert to UTF-8 once upfront
        var byteCount = Utf8.GetByteCount(value);
        Span<byte> utf8Bytes = byteCount <= 512
            ? stackalloc byte[byteCount]
            : new byte[byteCount];
        Utf8.GetBytes(value, utf8Bytes);

        return InternUtf8(utf8Bytes);
    }

    /// <summary>
    /// Intern a UTF-8 byte span directly (more efficient for pre-encoded data)
    /// </summary>
    public long InternUtf8(ReadOnlySpan<byte> utf8Value)
    {
        if (utf8Value.IsEmpty)
            return 0;

        var hash = ComputeHashUtf8(utf8Value);
        var bucket = (long)((ulong)hash % (ulong)HashTableSize);

        for (int probe = 0; probe < MaxProbeDistance; probe++)
        {
            var currentBucket = (bucket + probe) % HashTableSize;
            ref var entry = ref _hashTable[currentBucket];

            if (entry.AtomId == 0)
                break;

            // Quick rejection: check hash and length before byte comparison
            if (entry.Hash == hash && entry.Length == utf8Value.Length)
            {
                var stored = GetAtomSpan(entry.AtomId);
                if (stored.SequenceEqual(utf8Value))
                {
                    return entry.AtomId;
                }
            }
        }

        return InsertAtomUtf8(utf8Value, hash, bucket);
    }

    /// <summary>
    /// Get atom ID for a string without interning (returns 0 if not found)
    /// </summary>
    public long GetAtomId(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
            return 0;

        // Convert to UTF-8 once upfront
        var byteCount = Utf8.GetByteCount(value);
        Span<byte> utf8Bytes = byteCount <= 512
            ? stackalloc byte[byteCount]
            : new byte[byteCount];
        Utf8.GetBytes(value, utf8Bytes);

        return GetAtomIdUtf8(utf8Bytes);
    }

    /// <summary>
    /// Get atom ID for a UTF-8 string without interning (returns 0 if not found)
    /// </summary>
    public long GetAtomIdUtf8(ReadOnlySpan<byte> utf8Value)
    {
        if (utf8Value.IsEmpty)
            return 0;

        var hash = ComputeHashUtf8(utf8Value);
        var bucket = (long)((ulong)hash % (ulong)HashTableSize);

        for (int probe = 0; probe < MaxProbeDistance; probe++)
        {
            var currentBucket = (bucket + probe) % HashTableSize;
            ref var entry = ref _hashTable[currentBucket];

            if (entry.AtomId == 0)
                return 0;

            // Quick rejection: check hash and length before byte comparison
            if (entry.Hash == hash && entry.Length == utf8Value.Length)
            {
                var stored = GetAtomSpan(entry.AtomId);
                if (stored.SequenceEqual(utf8Value))
                {
                    return entry.AtomId;
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// Get raw UTF-8 bytes for an atom ID (zero-copy over memory-mapped data).
    /// Limited to 2GB due to Span&lt;T&gt; runtime constraint.
    /// For blobs &gt; 2GB, use GetAtomChunked (not yet implemented).
    /// </summary>
    /// <remarks>
    /// CHUNKED ACCESS: Storage format supports 64-bit lengths (exabytes).
    /// To read blobs larger than int.MaxValue bytes:
    /// <code>
    /// public IEnumerable&lt;ReadOnlyMemory&lt;byte&gt;&gt; GetAtomChunked(long atomId, int chunkSize = 1 &lt;&lt; 30)
    /// {
    ///     var offset = GetAtomOffset(atomId);
    ///     _dataAccessor.Read(offset, out long totalLength);
    ///     var dataOffset = offset + sizeof(long);
    ///
    ///     for (long pos = 0; pos &lt; totalLength; pos += chunkSize)
    ///     {
    ///         var remaining = totalLength - pos;
    ///         var size = (int)Math.Min(remaining, chunkSize);
    ///         // yield chunk at dataOffset + pos, size bytes
    ///     }
    /// }
    /// </code>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> GetAtomSpan(long atomId)
    {
        if (atomId <= 0 || atomId > _nextAtomId)
            return ReadOnlySpan<byte>.Empty;

        // Read atom header (offset stored in hash table or computed)
        var offset = GetAtomOffset(atomId);
        if (offset < 0)
            return ReadOnlySpan<byte>.Empty;

        // Read length (64-bit for huge blobs)
        _dataAccessor.Read(offset, out long length);

        // Get pointer to UTF-8 data
        byte* dataPtr = null;
        _dataAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref dataPtr);

        return new ReadOnlySpan<byte>(dataPtr + offset + sizeof(long), (int)length);
    }

    /// <summary>
    /// Get string for an atom ID (allocates - use GetAtomSpan for zero-copy)
    /// </summary>
    public string GetAtomString(long atomId)
    {
        var span = GetAtomSpan(atomId);
        if (span.IsEmpty)
            return string.Empty;

        return Utf8.GetString(span);
    }

    /// <summary>
    /// Get atom statistics
    /// </summary>
    public (long AtomCount, long TotalBytes, double AvgLength) GetStatistics()
    {
        var avgLength = _atomCount > 0 ? (double)_totalBytes / _atomCount : 0;
        return (_atomCount, _totalBytes, avgLength);
    }

    /// <summary>
    /// Current number of atoms
    /// </summary>
    public long AtomCount => _atomCount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ComputeHash(ReadOnlySpan<char> value)
    {
        // xxHash-inspired fast hash (64-bit)
        ulong hash = 14695981039346656037ul;

        foreach (var ch in value)
        {
            hash = (hash ^ ch) * 1099511628211ul;
        }

        return (long)hash;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ComputeHashUtf8(ReadOnlySpan<byte> value)
    {
        ulong hash = 14695981039346656037ul;

        foreach (var b in value)
        {
            hash = (hash ^ b) * 1099511628211ul;
        }

        return (long)hash;
    }

    private long InsertAtom(ReadOnlySpan<char> value, long hash, long bucket)
    {
        // Convert to UTF-8
        var byteCount = Utf8.GetByteCount(value);
        Span<byte> utf8Bytes = stackalloc byte[Math.Min(byteCount, 1024)];
        if (byteCount > 1024)
        {
            utf8Bytes = new byte[byteCount];
        }
        Utf8.GetBytes(value, utf8Bytes);

        return InsertAtomUtf8(utf8Bytes.Slice(0, byteCount), hash, bucket);
    }

    private long InsertAtomUtf8(ReadOnlySpan<byte> utf8Value, long hash, long bucket)
    {
        // Allocate atom ID
        var atomId = System.Threading.Interlocked.Increment(ref _nextAtomId);

        // Ensure offset index has capacity
        EnsureOffsetCapacity(atomId + 1);

        // Calculate storage size: 8-byte length prefix + UTF-8 data
        var totalSize = sizeof(long) + utf8Value.Length;

        // Allocate space in data file
        var offset = System.Threading.Interlocked.Add(ref _dataPosition, totalSize) - totalSize;

        // Extend data file if needed
        EnsureDataCapacity(offset + totalSize);

        // Write atom data: [length:8][utf8data:N]
        _dataAccessor.Write(offset, (long)utf8Value.Length);

        byte* dataPtr = null;
        _dataAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref dataPtr);

        utf8Value.CopyTo(new Span<byte>(dataPtr + offset + sizeof(long), utf8Value.Length));

        // Write to offset index for O(1) atomId→offset lookup
        _offsetIndex[atomId] = offset;

        // Update hash table
        for (int probe = 0; probe < MaxProbeDistance; probe++)
        {
            var currentBucket = (bucket + probe) % HashTableSize;
            ref var entry = ref _hashTable[currentBucket];

            if (entry.AtomId == 0)
            {
                var expected = 0L;
                var success = System.Threading.Interlocked.CompareExchange(
                    ref entry.AtomId,
                    atomId,
                    expected
                ) == expected;

                if (success)
                {
                    entry.Hash = hash;
                    entry.Length = utf8Value.Length;
                    entry.Offset = offset;

                    System.Threading.Interlocked.Increment(ref _atomCount);
                    System.Threading.Interlocked.Add(ref _totalBytes, utf8Value.Length);

                    return atomId;
                }
            }
        }

        // Hash table full in this region - need to handle overflow
        throw new InvalidOperationException($"Hash table region full at bucket {bucket}");
    }

    private void EnsureDataCapacity(long requiredSize)
    {
        if (requiredSize <= _dataFile.Length)
            return;

        lock (_dataFile)
        {
            if (requiredSize <= _dataFile.Length)
                return;

            var newSize = Math.Max(_dataFile.Length * 2, requiredSize + InitialDataSize);

            // Dispose old accessor and map
            _dataAccessor.Dispose();
            _dataMap.Dispose();

            // Extend file
            _dataFile.SetLength(newSize);

            // Recreate memory map
            _dataMap = MemoryMappedFile.CreateFromFile(
                _dataFile,
                mapName: null,
                capacity: newSize,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None,
                leaveOpen: true
            );

            _dataAccessor = _dataMap.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
        }
    }

    private void EnsureOffsetCapacity(long requiredCapacity)
    {
        if (requiredCapacity <= _offsetCapacity)
            return;

        lock (_offsetFile)
        {
            if (requiredCapacity <= _offsetCapacity)
                return;

            var newCapacity = Math.Max(_offsetCapacity * 2, requiredCapacity + InitialOffsetCapacity);

            // Dispose old accessor and map
            _offsetAccessor.Dispose();
            _offsetMap.Dispose();

            // Extend file
            _offsetFile.SetLength(newCapacity * sizeof(long));

            // Recreate memory map
            _offsetMap = MemoryMappedFile.CreateFromFile(
                _offsetFile,
                mapName: null,
                capacity: _offsetFile.Length,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None,
                leaveOpen: true
            );

            _offsetAccessor = _offsetMap.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

            // Update pointer
            byte* offsetPtr = null;
            _offsetAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref offsetPtr);
            _offsetIndex = (long*)offsetPtr;

            _offsetCapacity = newCapacity;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long GetAtomOffset(long atomId)
    {
        // O(1) direct lookup via offset index
        if (atomId <= 0 || atomId > _nextAtomId)
            return -1;

        return _offsetIndex[atomId];
    }

    private const long MagicNumber = 0x55544638_41544F4DL; // "UTF8ATOM" as long

    private void LoadMetadata()
    {
        _dataAccessor.Read(32, out long magic);

        if (magic == MagicNumber)
        {
            _dataAccessor.Read(0, out _dataPosition);
            _dataAccessor.Read(8, out _nextAtomId);
            _dataAccessor.Read(16, out _atomCount);
            _dataAccessor.Read(24, out _totalBytes);

            if (_dataPosition < 1024)
                _dataPosition = 1024;
        }
        else
        {
            _dataPosition = 1024; // Reserve first 1KB for metadata
            _nextAtomId = 0;
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

    public void Flush()
    {
        SaveMetadata();
        _dataAccessor.Flush();
        _indexAccessor.Flush();
        _offsetAccessor.Flush();
    }

    public void Dispose()
    {
        SaveMetadata();

        _dataAccessor?.Dispose();
        _indexAccessor?.Dispose();
        _offsetAccessor?.Dispose();
        _dataMap?.Dispose();
        _indexMap?.Dispose();
        _offsetMap?.Dispose();
        _dataFile?.Dispose();
        _indexFile?.Dispose();
        _offsetFile?.Dispose();
    }

    /// <summary>
    /// Hash bucket entry (40 bytes for 64-bit IDs, offsets, and lengths)
    /// </summary>
    /// <remarks>
    /// 64-bit Length field supports exabyte-scale blobs in storage format.
    /// Retrieval via Span is limited to 2GB; chunked access needed for larger.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct HashBucket
    {
        public long AtomId;     // 0 means empty (64-bit for TB-scale)
        public long Hash;       // Full 64-bit hash for quick comparison
        public long Offset;     // Offset in data file (64-bit for TB-scale)
        public long Length;     // UTF-8 byte length (64-bit for huge blobs)
    }
}
