using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using SkyOmega.Mercury.Runtime.Buffers;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// Trigram-based full-text search index using memory-mapped files.
/// Indexes object literals for efficient text search via <c>text:match</c> SPARQL function.
/// </summary>
/// <remarks>
/// <para><strong>INTERNAL USE ONLY:</strong> This class relies on external synchronization
/// via <see cref="QuadStore"/>'s ReaderWriterLockSlim for thread safety.</para>
///
/// <para><strong>Threading Contract:</strong></para>
/// <list type="bullet">
/// <item><description>All write operations (IndexAtom, RemoveAtom) must occur under the caller's
/// exclusive write lock.</description></item>
/// <item><description>All read operations (QueryCandidates) must occur under the caller's
/// shared read lock.</description></item>
/// </list>
///
/// <para><strong>Trigram Extraction:</strong> Text is normalized to lowercase using
/// <see cref="string.ToLowerInvariant"/> which handles Unicode case-folding (including
/// Swedish å, ä, ö). Trigrams are then extracted as overlapping 3-byte sequences from
/// the UTF-8 encoding.</para>
///
/// <para><strong>Storage Format:</strong></para>
/// <list type="bullet">
/// <item><description><c>.hash</c> - Fixed-size hash table mapping trigram hash to posting list offset</description></item>
/// <item><description><c>.posts</c> - Append-only posting lists containing atom IDs</description></item>
/// </list>
/// </remarks>
internal sealed unsafe class TrigramIndex : IDisposable
{
    // Hash table configuration
    private const int HashTableBuckets = 1 << 20; // 1M buckets (trigram space is ~16M but sparse)
    private const int QuadraticProbeLimit = 64;
    private const int MaxProbeDistance = 1024;

    // File sizes
    private const long InitialPostingSize = 64L << 20; // 64MB initial

    // Minimum text length to index (texts < 3 chars have no trigrams)
    private const int MinTextLength = 3;

    // Maximum posting list size before compaction (4096 atom IDs per trigram)
    private const int MaxPostingListSize = 4096;

    // Initial posting list capacity (pre-allocated slots per trigram)
    private const int InitialPostingListCapacity = 64;

    // Memory-mapped files
    private readonly FileStream _hashFile;
    private readonly FileStream _postingFile;
    private MemoryMappedFile _hashMap;
    private MemoryMappedFile _postingMap;
    private MemoryMappedViewAccessor _hashAccessor;
    private MemoryMappedViewAccessor _postingAccessor;

    // Cached pointers
    private HashBucket* _hashTable;
    private byte* _postingPtr;
    private readonly object _resizeLock = new();

    // Current write position in posting file
    private long _postingPosition;

    // Statistics
    private long _indexedAtomCount;
    private long _totalTrigrams;

    // Buffer manager for pooled allocations
    private readonly IBufferManager _bufferManager;

    // UTF-8 encoder
    private static readonly Encoding Utf8 = Encoding.UTF8;

    /// <summary>
    /// Creates or opens a trigram index at the specified path.
    /// </summary>
    /// <param name="basePath">Base path for index files (without extension).</param>
    /// <param name="bufferManager">Buffer manager for pooled allocations.</param>
    public TrigramIndex(string basePath, IBufferManager? bufferManager = null)
    {
        _bufferManager = bufferManager ?? PooledBufferManager.Shared;

        var hashPath = basePath + ".hash";
        var postingPath = basePath + ".posts";

        // Ensure directory exists
        var dir = Path.GetDirectoryName(basePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // Open/create hash file
        _hashFile = new FileStream(
            hashPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.RandomAccess
        );

        var hashSize = (long)HashTableBuckets * sizeof(HashBucket);
        if (_hashFile.Length == 0)
        {
            _hashFile.SetLength(hashSize);
        }

        // Open/create posting file
        _postingFile = new FileStream(
            postingPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.RandomAccess
        );

        if (_postingFile.Length == 0)
        {
            _postingFile.SetLength(InitialPostingSize);
        }

        // Memory-map files
        _hashMap = MemoryMappedFile.CreateFromFile(
            _hashFile,
            mapName: null,
            capacity: hashSize,
            MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None,
            leaveOpen: true
        );

        _postingMap = MemoryMappedFile.CreateFromFile(
            _postingFile,
            mapName: null,
            capacity: _postingFile.Length,
            MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None,
            leaveOpen: true
        );

        _hashAccessor = _hashMap.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
        _postingAccessor = _postingMap.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

        // Acquire pointers
        byte* hashPtr = null;
        _hashAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref hashPtr);
        _hashTable = (HashBucket*)hashPtr;

        _postingAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _postingPtr);

        // Load metadata
        LoadMetadata();
    }

    /// <summary>
    /// Index an atom's text content for full-text search.
    /// </summary>
    /// <param name="atomId">The atom ID to index.</param>
    /// <param name="utf8Text">The UTF-8 text content (may include RDF literal syntax with quotes).</param>
    public void IndexAtom(long atomId, ReadOnlySpan<byte> utf8Text)
    {
        if (atomId <= 0 || utf8Text.Length < MinTextLength)
            return;

        // Extract the literal value (remove quotes and language/datatype if present)
        var literalValue = ExtractLiteralValue(utf8Text);
        if (literalValue.Length < MinTextLength)
            return;

        // Decode UTF-8 to string for proper Unicode case-folding
        var text = Utf8.GetString(literalValue);

        // Normalize to lowercase (handles Swedish å, ä, ö correctly)
        var normalized = text.ToLowerInvariant();

        // Re-encode to UTF-8 for trigram extraction
        var normalizedByteCount = Utf8.GetByteCount(normalized);
        Span<byte> stackBuffer = stackalloc byte[Math.Min(normalizedByteCount, 512)];
        var normalizedBytes = _bufferManager.AllocateSmart(normalizedByteCount, stackBuffer, out var rentedBuffer);
        try
        {
            Utf8.GetBytes(normalized, normalizedBytes);

            // Extract and index trigrams
            var trigrams = ExtractTrigrams(normalizedBytes);
            foreach (var trigram in trigrams)
            {
                AddToPostingList(trigram, atomId);
            }

            Interlocked.Increment(ref _indexedAtomCount);
        }
        finally
        {
            rentedBuffer.Dispose();
        }
    }

    /// <summary>
    /// Remove an atom from the trigram index (lazy tombstone).
    /// </summary>
    /// <param name="atomId">The atom ID to remove.</param>
    /// <remarks>
    /// Removal is deferred - atoms are marked as deleted and cleaned up during compaction.
    /// For now, this is a no-op since we don't support deletion (append-only design).
    /// </remarks>
    public void RemoveAtom(long atomId)
    {
        // TODO: Implement lazy deletion if needed
        // For now, trigram index is append-only like the rest of the storage layer
    }

    /// <summary>
    /// Query the index for candidate atom IDs that may match the search query.
    /// </summary>
    /// <param name="searchQuery">The search query text.</param>
    /// <returns>List of candidate atom IDs. May contain false positives.</returns>
    /// <remarks>
    /// Results should be verified with actual string matching in the FILTER evaluation.
    /// The trigram index provides pre-filtering to reduce the search space.
    /// </remarks>
    public List<long> QueryCandidates(ReadOnlySpan<char> searchQuery)
    {
        var result = new List<long>();

        if (searchQuery.Length < MinTextLength)
            return result;

        // Normalize query to lowercase
        var normalized = searchQuery.ToString().ToLowerInvariant();

        // Encode to UTF-8
        var normalizedByteCount = Utf8.GetByteCount(normalized);
        var normalizedBytes = new byte[normalizedByteCount];
        Utf8.GetBytes(normalized, normalizedBytes);

        // Extract trigrams from query
        var queryTrigrams = ExtractTrigrams(normalizedBytes);
        if (queryTrigrams.Count == 0)
            return result;

        // Find intersection of all posting lists
        HashSet<long>? candidates = null;

        foreach (var trigram in queryTrigrams)
        {
            var postingList = GetPostingList(trigram);

            if (candidates == null)
            {
                candidates = new HashSet<long>(postingList);
            }
            else
            {
                candidates.IntersectWith(postingList);
            }

            // Early termination if no candidates remain
            if (candidates.Count == 0)
                return result;
        }

        if (candidates != null)
        {
            result.AddRange(candidates);
        }

        return result;
    }

    /// <summary>
    /// Check if an atom is a candidate match for the search query.
    /// </summary>
    /// <param name="atomId">The atom ID to check.</param>
    /// <param name="searchQuery">The search query text.</param>
    /// <returns>True if the atom may match (requires verification); false if definitely not.</returns>
    public bool IsCandidateMatch(long atomId, ReadOnlySpan<char> searchQuery)
    {
        if (searchQuery.Length < MinTextLength)
            return true; // Can't pre-filter, must verify

        // Normalize query to lowercase
        var normalized = searchQuery.ToString().ToLowerInvariant();

        // Encode to UTF-8
        var normalizedByteCount = Utf8.GetByteCount(normalized);
        Span<byte> stackBuffer = stackalloc byte[Math.Min(normalizedByteCount, 512)];
        var normalizedBytes = _bufferManager.AllocateSmart(normalizedByteCount, stackBuffer, out var rentedBuffer);
        try
        {
            Utf8.GetBytes(normalized, normalizedBytes);

            // Extract trigrams from query
            var queryTrigrams = ExtractTrigrams(normalizedBytes);
            if (queryTrigrams.Count == 0)
                return true; // Can't pre-filter

            // Check if atom appears in ALL trigram posting lists
            foreach (var trigram in queryTrigrams)
            {
                if (!PostingListContains(trigram, atomId))
                    return false;
            }

            return true;
        }
        finally
        {
            rentedBuffer.Dispose();
        }
    }

    /// <summary>
    /// Flush index data to disk.
    /// </summary>
    public void Flush()
    {
        SaveMetadata();
        _hashAccessor.Flush();
        _postingAccessor.Flush();
    }

    /// <summary>
    /// Get index statistics.
    /// </summary>
    public (long IndexedAtomCount, long TotalTrigrams) GetStatistics()
    {
        return (_indexedAtomCount, _totalTrigrams);
    }

    /// <summary>
    /// Extract trigrams from UTF-8 text.
    /// </summary>
    private static HashSet<uint> ExtractTrigrams(ReadOnlySpan<byte> utf8Text)
    {
        var trigrams = new HashSet<uint>();

        if (utf8Text.Length < MinTextLength)
            return trigrams;

        for (int i = 0; i <= utf8Text.Length - 3; i++)
        {
            uint trigram = ((uint)utf8Text[i] << 16) | ((uint)utf8Text[i + 1] << 8) | utf8Text[i + 2];
            trigrams.Add(trigram);
        }

        return trigrams;
    }

    /// <summary>
    /// Extract literal value from RDF literal syntax.
    /// Removes surrounding quotes and language/datatype suffixes.
    /// </summary>
    private static ReadOnlySpan<byte> ExtractLiteralValue(ReadOnlySpan<byte> utf8Text)
    {
        if (utf8Text.IsEmpty)
            return utf8Text;

        // Check for quoted string
        if (utf8Text[0] != '"')
            return utf8Text; // Not a literal, return as-is

        // Find closing quote (handle escaped quotes)
        int endQuote = -1;
        for (int i = 1; i < utf8Text.Length; i++)
        {
            if (utf8Text[i] == '"' && (i == 1 || utf8Text[i - 1] != '\\'))
            {
                endQuote = i;
                break;
            }
        }

        if (endQuote <= 0)
            return utf8Text; // Malformed, return as-is

        // Return content between quotes
        return utf8Text.Slice(1, endQuote - 1);
    }

    /// <summary>
    /// Compute hash for a trigram value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ComputeTrigramHash(uint trigram)
    {
        // FNV-1a hash
        ulong hash = 14695981039346656037ul;
        hash = (hash ^ (trigram & 0xFF)) * 1099511628211ul;
        hash = (hash ^ ((trigram >> 8) & 0xFF)) * 1099511628211ul;
        hash = (hash ^ ((trigram >> 16) & 0xFF)) * 1099511628211ul;
        return (long)hash;
    }

    /// <summary>
    /// Add an atom ID to a trigram's posting list.
    /// </summary>
    private void AddToPostingList(uint trigram, long atomId)
    {
        var hash = ComputeTrigramHash(trigram);
        var bucket = (int)((ulong)hash % (ulong)HashTableBuckets);

        // Find existing bucket or empty slot
        for (int probe = 0; probe < MaxProbeDistance; probe++)
        {
            var probeOffset = ComputeProbeOffset(probe);
            var currentBucket = (bucket + probeOffset) % HashTableBuckets;
            ref var entry = ref _hashTable[currentBucket];

            // Empty bucket - create new posting list
            if (entry.Trigram == 0 && entry.PostingOffset == 0)
            {
                var offset = AllocatePostingList(atomId);
                entry.Trigram = trigram;
                entry.PostingOffset = offset;
                entry.PostingCount = 1;
                Interlocked.Increment(ref _totalTrigrams);
                return;
            }

            // Found existing trigram - append to posting list
            if (entry.Trigram == trigram)
            {
                AppendToPostingList(ref entry, atomId);
                return;
            }
        }

        // Hash table full - should not happen with 1M buckets
        throw new InvalidOperationException($"Trigram hash table overflow after {MaxProbeDistance} probes.");
    }

    /// <summary>
    /// Allocate a new posting list with a single atom ID.
    /// Pre-allocates space for InitialPostingListCapacity entries.
    /// </summary>
    private long AllocatePostingList(long atomId)
    {
        // Posting list format: [count:4][capacity:4][atomId0:8][atomId1:8]...
        var listSize = sizeof(int) + sizeof(int) + InitialPostingListCapacity * sizeof(long);
        var offset = Interlocked.Add(ref _postingPosition, listSize) - listSize;

        EnsurePostingCapacity(offset + listSize);

        *(int*)(_postingPtr + offset) = 1;                             // count
        *(int*)(_postingPtr + offset + sizeof(int)) = InitialPostingListCapacity; // capacity
        *(long*)(_postingPtr + offset + sizeof(int) + sizeof(int)) = atomId;

        return offset;
    }

    /// <summary>
    /// Append an atom ID to an existing posting list.
    /// </summary>
    private void AppendToPostingList(ref HashBucket entry, long atomId)
    {
        var offset = entry.PostingOffset;
        var count = *(int*)(_postingPtr + offset);
        var capacity = *(int*)(_postingPtr + offset + sizeof(int));

        // Check if already in list (deduplication)
        var atomsPtr = (long*)(_postingPtr + offset + sizeof(int) + sizeof(int));
        for (int i = 0; i < count; i++)
        {
            if (atomsPtr[i] == atomId)
                return; // Already indexed
        }

        // Check if list is at capacity - need to reallocate
        if (count >= capacity)
        {
            if (count >= MaxPostingListSize)
                return; // Skip - at maximum size

            // Allocate new list with double capacity
            var newCapacity = Math.Min(capacity * 2, MaxPostingListSize);
            var newListSize = sizeof(int) + sizeof(int) + newCapacity * sizeof(long);
            var newOffset = Interlocked.Add(ref _postingPosition, newListSize) - newListSize;

            EnsurePostingCapacity(newOffset + newListSize);

            // Copy existing entries
            var newAtomsPtr = (long*)(_postingPtr + newOffset + sizeof(int) + sizeof(int));
            for (int i = 0; i < count; i++)
            {
                newAtomsPtr[i] = atomsPtr[i];
            }

            // Add new entry
            newAtomsPtr[count] = atomId;
            *(int*)(_postingPtr + newOffset) = count + 1;
            *(int*)(_postingPtr + newOffset + sizeof(int)) = newCapacity;

            // Update bucket to point to new list
            entry.PostingOffset = newOffset;
            entry.PostingCount = count + 1;
            return;
        }

        // Append atom ID within existing capacity
        atomsPtr[count] = atomId;
        *(int*)(_postingPtr + offset) = count + 1;
        entry.PostingCount = count + 1;
    }

    /// <summary>
    /// Get posting list for a trigram.
    /// </summary>
    private List<long> GetPostingList(uint trigram)
    {
        var result = new List<long>();
        var hash = ComputeTrigramHash(trigram);
        var bucket = (int)((ulong)hash % (ulong)HashTableBuckets);

        for (int probe = 0; probe < MaxProbeDistance; probe++)
        {
            var probeOffset = ComputeProbeOffset(probe);
            var currentBucket = (bucket + probeOffset) % HashTableBuckets;
            ref var entry = ref _hashTable[currentBucket];

            // Empty bucket - trigram not found
            if (entry.Trigram == 0 && entry.PostingOffset == 0)
                return result;

            // Found trigram - return posting list
            if (entry.Trigram == trigram)
            {
                var offset = entry.PostingOffset;
                var count = *(int*)(_postingPtr + offset);
                // Skip capacity field (sizeof(int)) to get to atom IDs
                var atomsPtr = (long*)(_postingPtr + offset + sizeof(int) + sizeof(int));

                for (int i = 0; i < count; i++)
                {
                    result.Add(atomsPtr[i]);
                }

                return result;
            }
        }

        return result;
    }

    /// <summary>
    /// Check if posting list contains a specific atom ID.
    /// </summary>
    private bool PostingListContains(uint trigram, long atomId)
    {
        var hash = ComputeTrigramHash(trigram);
        var bucket = (int)((ulong)hash % (ulong)HashTableBuckets);

        for (int probe = 0; probe < MaxProbeDistance; probe++)
        {
            var probeOffset = ComputeProbeOffset(probe);
            var currentBucket = (bucket + probeOffset) % HashTableBuckets;
            ref var entry = ref _hashTable[currentBucket];

            // Empty bucket - trigram not found
            if (entry.Trigram == 0 && entry.PostingOffset == 0)
                return false;

            // Found trigram - search posting list
            if (entry.Trigram == trigram)
            {
                var offset = entry.PostingOffset;
                var count = *(int*)(_postingPtr + offset);
                // Skip capacity field (sizeof(int)) to get to atom IDs
                var atomsPtr = (long*)(_postingPtr + offset + sizeof(int) + sizeof(int));

                for (int i = 0; i < count; i++)
                {
                    if (atomsPtr[i] == atomId)
                        return true;
                }

                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Compute probe offset using quadratic probing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeProbeOffset(int probe)
    {
        if (probe < QuadraticProbeLimit)
            return probe * probe;

        return (QuadraticProbeLimit - 1) * (QuadraticProbeLimit - 1) + (probe - QuadraticProbeLimit + 1);
    }

    /// <summary>
    /// Ensure posting file has sufficient capacity.
    /// </summary>
    private void EnsurePostingCapacity(long requiredSize)
    {
        if (requiredSize <= _postingFile.Length)
            return;

        lock (_resizeLock)
        {
            if (requiredSize <= _postingFile.Length)
                return;

            var newSize = Math.Max(_postingFile.Length * 2, requiredSize + InitialPostingSize);

            // Create new map and accessor
            var newMap = MemoryMappedFile.CreateFromFile(
                _postingFile,
                mapName: null,
                capacity: newSize,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None,
                leaveOpen: true
            );

            var newAccessor = newMap.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

            byte* newPtr = null;
            newAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref newPtr);

            // Atomic swap
            _postingPtr = newPtr;
            Thread.MemoryBarrier();

            // Extend file
            _postingFile.SetLength(newSize);

            // Cleanup old
            _postingAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _postingAccessor.Dispose();
            _postingMap.Dispose();

            _postingMap = newMap;
            _postingAccessor = newAccessor;
        }
    }

    private const long MagicNumber = 0x54524947_52414D53L; // "TRIGRAMS" as long

    private void LoadMetadata()
    {
        if (_postingFile.Length < 64)
        {
            _postingPosition = 64; // Reserve header space
            _indexedAtomCount = 0;
            _totalTrigrams = 0;
            return;
        }

        _postingAccessor.Read(32, out long magic);

        if (magic == MagicNumber)
        {
            _postingAccessor.Read(0, out _postingPosition);
            _postingAccessor.Read(8, out _indexedAtomCount);
            _postingAccessor.Read(16, out _totalTrigrams);

            if (_postingPosition < 64)
                _postingPosition = 64;
        }
        else
        {
            _postingPosition = 64;
            _indexedAtomCount = 0;
            _totalTrigrams = 0;
            SaveMetadata();
        }
    }

    private void SaveMetadata()
    {
        _postingAccessor.Write(0, _postingPosition);
        _postingAccessor.Write(8, _indexedAtomCount);
        _postingAccessor.Write(16, _totalTrigrams);
        _postingAccessor.Write(32, MagicNumber);
    }

    /// <summary>
    /// Resets the trigram index to empty state. All indexed data is logically discarded.
    /// File sizes are preserved (memory mappings stay valid).
    /// </summary>
    /// <remarks>
    /// Must be called from QuadStore.Clear() which holds the write lock.
    /// </remarks>
    public void Clear()
    {
        lock (_resizeLock)
        {
            // Zero the entire hash table
            var hashTableBytes = HashTableBuckets * sizeof(HashBucket);
            new Span<byte>(_hashTable, hashTableBytes).Clear();

            // Reset posting position to after header
            _postingPosition = 64;

            // Reset counters
            _indexedAtomCount = 0;
            _totalTrigrams = 0;

            SaveMetadata();
        }
    }

    public void Dispose()
    {
        SaveMetadata();

        // Release pointers
        if (_hashTable != null)
        {
            _hashAccessor?.SafeMemoryMappedViewHandle.ReleasePointer();
            _hashTable = null;
        }
        if (_postingPtr != null)
        {
            _postingAccessor?.SafeMemoryMappedViewHandle.ReleasePointer();
            _postingPtr = null;
        }

        _hashAccessor?.Dispose();
        _postingAccessor?.Dispose();
        _hashMap?.Dispose();
        _postingMap?.Dispose();
        _hashFile?.Dispose();
        _postingFile?.Dispose();
    }

    /// <summary>
    /// Hash bucket entry for trigram index.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct HashBucket
    {
        public uint Trigram;       // 24-bit trigram value (3 bytes packed into uint)
        public int PostingCount;   // Number of atoms in posting list
        public long PostingOffset; // Offset into posting file
    }
}
