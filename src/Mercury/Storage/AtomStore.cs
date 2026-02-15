using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using SkyOmega.Mercury.Runtime.Buffers;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// Atom store for efficient string storage and retrieval using memory-mapped files.
/// Stores strings as UTF-8 for optimal space efficiency.
/// Uses 64-bit atom IDs for TB-scale capacity.
/// Supports zero-copy access via memory-mapped files.
/// </summary>
/// <remarks>
/// <para><strong>INTERNAL USE ONLY:</strong> This class is internal because it relies on
/// external synchronization (via <see cref="QuadStore"/>'s ReaderWriterLockSlim) for
/// thread safety. Direct use without proper locking can cause undefined behavior.</para>
///
/// <para><strong>Threading Contract:</strong></para>
/// <list type="bullet">
/// <item><description>All write operations (Intern, InternUtf8) must occur under the caller's
/// exclusive write lock.</description></item>
/// <item><description>All read operations (GetAtomSpan, GetAtomString, GetAtomId) must occur
/// under the caller's shared read lock.</description></item>
/// <item><description>The lock must be held for the entire duration of span usage - spans point
/// directly into memory-mapped memory and become invalid after resize operations.</description></item>
/// </list>
///
/// <para><strong>Why Not Epoch-Based Retirement:</strong> The external locking model was chosen
/// over epoch-based retirement because: (1) QuadStore already requires ReaderWriterLockSlim for
/// index consistency, (2) AtomStore is always accessed through QuadStore, (3) epoch tracking
/// adds complexity and overhead that provides no benefit when external locking is mandatory.</para>
///
/// <para><strong>Resize Safety:</strong> EnsureDataCapacity and EnsureOffsetCapacity perform
/// atomic pointer swaps with memory barriers. Under proper external locking, readers never
/// observe a resize in progress because writers hold exclusive locks.</para>
///
/// <para><strong>Storage Format:</strong> Supports 64-bit blob lengths (exabyte-scale).
/// Single-span retrieval is limited to 2GB by .NET Span&lt;T&gt; constraint.
/// For larger blobs, implement chunked access pattern (see GetAtomSpan remarks).</para>
/// </remarks>
internal sealed unsafe class AtomStore : IDisposable
{
    private const int PageSize = 4096; // 4KB pages
    private const long HashTableSize = 1L << 24; // 16M buckets for TB-scale
    private const long InitialDataSize = 1L << 30; // 1GB initial
    private const long InitialOffsetCapacity = 1L << 20; // 1M atoms initial
    private const int QuadraticProbeLimit = 64; // Quadratic probing reduces clustering
    private const int MaxProbeDistance = 4096; // Extended fallback for high-load scenarios

    /// <summary>
    /// Default maximum atom size (1MB). Prevents resource exhaustion from oversized values.
    /// </summary>
    public const long DefaultMaxAtomSize = 1L << 20; // 1MB

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

    // Cached pointers acquired once during construction/resize (avoids repeated AcquirePointer calls)
    private byte* _dataPtr;
    private readonly object _resizeLock = new();

#if DEBUG
    // Debug-only field to detect threading violations
    // Set to 1 when a resize is in progress; reads during this time indicate missing locks
    private volatile int _resizeInProgress;
#endif

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

    // Buffer manager for pooled allocations
    private readonly IBufferManager _bufferManager;

    // Maximum allowed atom size in bytes
    private readonly long _maxAtomSize;

    public AtomStore(string baseFilePath)
        : this(baseFilePath, null, DefaultMaxAtomSize, InitialDataSize, InitialOffsetCapacity) { }

    public AtomStore(string baseFilePath, IBufferManager? bufferManager)
        : this(baseFilePath, bufferManager, DefaultMaxAtomSize, InitialDataSize, InitialOffsetCapacity) { }

    public AtomStore(string baseFilePath, IBufferManager? bufferManager, long maxAtomSize)
        : this(baseFilePath, bufferManager, maxAtomSize, InitialDataSize, InitialOffsetCapacity) { }

    /// <summary>
    /// Creates an AtomStore with configurable initial sizes.
    /// </summary>
    /// <param name="baseFilePath">Base path for storage files (without extension).</param>
    /// <param name="bufferManager">Buffer manager for pooled allocations.</param>
    /// <param name="maxAtomSize">Maximum size of a single atom in bytes.</param>
    /// <param name="initialDataSize">Initial size for the data file in bytes.</param>
    /// <param name="initialOffsetCapacity">Initial capacity for the offset index (number of atoms).</param>
    public AtomStore(string baseFilePath, IBufferManager? bufferManager, long maxAtomSize,
                     long initialDataSize, long initialOffsetCapacity)
    {
        if (maxAtomSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAtomSize), "Max atom size must be positive");
        if (initialDataSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialDataSize), "Initial data size must be positive");
        if (initialOffsetCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialOffsetCapacity), "Initial offset capacity must be positive");

        _bufferManager = bufferManager ?? PooledBufferManager.Shared;
        _maxAtomSize = maxAtomSize;

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
            _dataFile.SetLength(initialDataSize);
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

        _offsetCapacity = initialOffsetCapacity;
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

        // Acquire data pointer once (released in Dispose)
        _dataAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _dataPtr);

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

        if (byteCount > _maxAtomSize)
            throw new ArgumentException(
                $"Atom size ({byteCount:N0} bytes) exceeds maximum allowed size ({_maxAtomSize:N0} bytes)",
                nameof(value));

        Span<byte> stackBuffer = stackalloc byte[Math.Min(byteCount, 512)];
        var utf8Bytes = _bufferManager.AllocateSmart(byteCount, stackBuffer, out var rentedBuffer);
        try
        {
            Utf8.GetBytes(value, utf8Bytes);
            return InternUtf8(utf8Bytes);
        }
        finally
        {
            rentedBuffer.Dispose();
        }
    }

    /// <summary>
    /// Intern a UTF-8 byte span directly (more efficient for pre-encoded data)
    /// </summary>
    public long InternUtf8(ReadOnlySpan<byte> utf8Value)
    {
        if (utf8Value.IsEmpty)
            return 0;

        if (utf8Value.Length > _maxAtomSize)
            throw new ArgumentException(
                $"Atom size ({utf8Value.Length:N0} bytes) exceeds maximum allowed size ({_maxAtomSize:N0} bytes)",
                nameof(utf8Value));

        var hash = ComputeHashUtf8(utf8Value);
        var bucket = (long)((ulong)hash % (ulong)HashTableSize);

        // Use quadratic probing to reduce clustering
        for (int probe = 0; probe < MaxProbeDistance; probe++)
        {
            var probeOffset = ComputeProbeOffset(probe);
            var currentBucket = (bucket + probeOffset) % HashTableSize;
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
        Span<byte> stackBuffer = stackalloc byte[Math.Min(byteCount, 512)];
        var utf8Bytes = _bufferManager.AllocateSmart(byteCount, stackBuffer, out var rentedBuffer);
        try
        {
            Utf8.GetBytes(value, utf8Bytes);
            return GetAtomIdUtf8(utf8Bytes);
        }
        finally
        {
            rentedBuffer.Dispose();
        }
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

        // Use quadratic probing to match insertion pattern
        for (int probe = 0; probe < MaxProbeDistance; probe++)
        {
            var probeOffset = ComputeProbeOffset(probe);
            var currentBucket = (bucket + probeOffset) % HashTableSize;
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

        var offset = GetAtomOffset(atomId);
        if (offset < 0)
            return ReadOnlySpan<byte>.Empty;

#if DEBUG
        // Debug assertion: detect missing external lock during resize
        // If this fires, the caller is reading without holding a read lock while a write
        // (which triggers resize) is in progress. This is a threading contract violation.
        Debug.Assert(_resizeInProgress == 0,
            "AtomStore.GetAtomSpan called during resize operation. " +
            "This indicates a threading contract violation - caller must hold read lock " +
            "which would block until write lock (held during resize) is released. " +
            "See AtomStore class remarks for threading requirements.");
#endif

        // Read pointer with memory barrier to ensure visibility across threads
        Thread.MemoryBarrier();
        byte* currentPtr = _dataPtr;

        // Read length directly from the pointer (Zero-GC, Zero-Accessor)
        long length = *(long*)(currentPtr + offset);

        return new ReadOnlySpan<byte>(currentPtr + offset + sizeof(long), (int)length);
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

    /// <summary>
    /// Compute probe offset using quadratic probing for first QuadraticProbeLimit,
    /// then linear probing for extended search.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ComputeProbeOffset(int probe)
    {
        // Quadratic probing: 0, 1, 4, 9, 16, 25, ... reduces primary clustering
        if (probe < QuadraticProbeLimit)
            return (long)probe * probe;

        // Linear fallback for extended search (offset from last quadratic position)
        return (long)(QuadraticProbeLimit - 1) * (QuadraticProbeLimit - 1) + (probe - QuadraticProbeLimit + 1);
    }

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
        Span<byte> stackBuffer = stackalloc byte[Math.Min(byteCount, 1024)];
        var utf8Bytes = _bufferManager.AllocateSmart(byteCount, stackBuffer, out var rentedBuffer);
        try
        {
            Utf8.GetBytes(value, utf8Bytes);
            return InsertAtomUtf8(utf8Bytes, hash, bucket);
        }
        finally
        {
            rentedBuffer.Dispose();
        }
    }

    /// <remarks>
    /// Must be called under QuadStore's write lock (single-writer contract, see ADR-020).
    /// </remarks>
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

        // Use cached pointer (acquired during construction/resize)
        utf8Value.CopyTo(new Span<byte>(_dataPtr + offset + sizeof(long), utf8Value.Length));

        // Write to offset index for O(1) atomId→offset lookup
        _offsetIndex[atomId] = offset;

        // Update hash table with quadratic probing
        for (int probe = 0; probe < MaxProbeDistance; probe++)
        {
            var probeOffset = ComputeProbeOffset(probe);
            var currentBucket = (bucket + probeOffset) % HashTableSize;
            ref var entry = ref _hashTable[currentBucket];

            if (entry.AtomId == 0)
            {
                // Write metadata first, publish AtomId last (ADR-020 §2).
                // Under single-writer contract, no CAS needed — plain writes suffice.
                entry.Hash = hash;
                entry.Length = utf8Value.Length;
                entry.Offset = offset;
                entry.AtomId = atomId; // Publish last — signals slot is fully populated

                Interlocked.Increment(ref _atomCount);
                Interlocked.Add(ref _totalBytes, utf8Value.Length);

                return atomId;
            }
        }

        // Hash table truly full - should not happen with 16M buckets and 4096 probes
        var loadFactor = (double)_atomCount / HashTableSize * 100;
        throw new InvalidOperationException(
            $"Hash table overflow at bucket {bucket} after {MaxProbeDistance} probes. " +
            $"Load factor: {loadFactor:F2}%. Consider increasing hash table size.");
    }

    private void EnsureDataCapacity(long requiredSize)
    {
        if (requiredSize <= _dataFile.Length)
            return;

        lock (_resizeLock)
        {
            if (requiredSize <= _dataFile.Length)
                return;

#if DEBUG
            // Mark resize in progress - any concurrent GetAtomSpan calls will trigger assertion
            // This should NEVER happen if external locking is correctly implemented
            Interlocked.Exchange(ref _resizeInProgress, 1);
#endif
            try
            {
                var newSize = Math.Max(_dataFile.Length * 2, requiredSize + InitialDataSize);

                // ADR-020 §4: extend file before creating mapping, so mapped
                // region never transiently exceeds file length.

                // 1. Extend the underlying file length
                _dataFile.SetLength(newSize);

                // 2. Create new map and accessor over the extended file
                var newMap = MemoryMappedFile.CreateFromFile(
                    _dataFile,
                    mapName: null,
                    capacity: newSize,
                    MemoryMappedFileAccess.ReadWrite,
                    HandleInheritability.None,
                    leaveOpen: true
                );

                var newAccessor = newMap.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

                byte* newPtr = null;
                newAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref newPtr);

                // 3. Swap active pointers
                _dataPtr = newPtr;
                Thread.MemoryBarrier();

                // 4. Dispose old mapping/accessor
                _dataAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                _dataAccessor.Dispose();
                _dataMap.Dispose();

                _dataMap = newMap;
                _dataAccessor = newAccessor;
            }
#if DEBUG
            finally
            {
                Interlocked.Exchange(ref _resizeInProgress, 0);
            }
#else
            finally { }
#endif
        }
    }

    private void EnsureOffsetCapacity(long requiredCapacity)
    {
        if (requiredCapacity <= _offsetCapacity)
            return;

        lock (_resizeLock)
        {
            if (requiredCapacity <= _offsetCapacity)
                return;

#if DEBUG
            // Mark resize in progress - any concurrent GetAtomOffset calls will trigger assertion
            Interlocked.Exchange(ref _resizeInProgress, 1);
#endif
            try
            {
                var newCapacity = Math.Max(_offsetCapacity * 2, requiredCapacity + InitialOffsetCapacity);

                // ADR-020 §4: extend file before creating mapping, so mapped
                // region never transiently exceeds file length.

                // 1. Extend the underlying file length
                _offsetFile.SetLength(newCapacity * sizeof(long));

                // 2. Create new map and accessor over the extended file
                var newMap = MemoryMappedFile.CreateFromFile(
                    _offsetFile,
                    mapName: null,
                    capacity: newCapacity * sizeof(long),
                    MemoryMappedFileAccess.ReadWrite,
                    HandleInheritability.None,
                    leaveOpen: true
                );

                var newAccessor = newMap.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

                byte* offsetPtr = null;
                newAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref offsetPtr);
                var newPtr = (long*)offsetPtr;

                // 3. Swap active pointers
                _offsetIndex = newPtr;
                Thread.MemoryBarrier();

                // 4. Dispose old mapping/accessor
                _offsetAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                _offsetAccessor.Dispose();
                _offsetMap.Dispose();

                _offsetMap = newMap;
                _offsetAccessor = newAccessor;
                _offsetCapacity = newCapacity;
            }
#if DEBUG
            finally
            {
                Interlocked.Exchange(ref _resizeInProgress, 0);
            }
#else
            finally { }
#endif
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long GetAtomOffset(long atomId)
    {
        // O(1) direct lookup via offset index
        if (atomId <= 0 || atomId > _nextAtomId)
            return -1;

        // Read pointer with memory barrier to ensure visibility across threads
        Thread.MemoryBarrier();
        long* currentPtr = _offsetIndex;
        return currentPtr[atomId];
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

        // Release all acquired pointers before disposing accessors
        if (_dataPtr != null)
        {
            _dataAccessor?.SafeMemoryMappedViewHandle.ReleasePointer();
            _dataPtr = null;
        }
        if (_hashTable != null)
        {
            _indexAccessor?.SafeMemoryMappedViewHandle.ReleasePointer();
            _hashTable = null;
        }
        if (_offsetIndex != null)
        {
            _offsetAccessor?.SafeMemoryMappedViewHandle.ReleasePointer();
            _offsetIndex = null;
        }

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
    /// Resets the atom store to empty state. All atoms are logically discarded.
    /// File sizes are preserved (memory mappings stay valid).
    /// </summary>
    /// <remarks>
    /// Must be called from QuadStore.Clear() which holds the write lock.
    /// </remarks>
    public void Clear()
    {
        lock (_resizeLock)
        {
            // Reset data position to after header (header is first 1KB)
            _dataPosition = 1024;

            // Reset counters
            _nextAtomId = 0;  // 0 is reserved for empty/default
            _atomCount = 0;
            _totalBytes = 0;

            // Zero the entire hash table (16M buckets × 32 bytes each)
            // Note: sizeof(HashBucket) is 32 bytes (4 × 8-byte longs)
            var hashTableBytes = (int)(HashTableSize * sizeof(HashBucket));
            new Span<byte>(_hashTable, hashTableBytes).Clear();

            // Offset index doesn't need clearing - stale entries are ignored
            // since lookups check atomId <= _nextAtomId

            SaveMetadata();
        }
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
