using System;
using SkyOmega.Mercury.Abstractions;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// Abstraction over atom-store implementations. Mercury today ships
/// <see cref="HashAtomStore"/> (open-addressing hash table with rehash-on-grow per ADR-028);
/// ADR-034 introduces <see cref="SortedAtomStore"/> for the Reference profile, where the
/// dump-sourced read-after-bulk semantic admits a sorted-vocabulary layout with binary-search
/// (or future BBHash MPHF) lookup. <see cref="QuadStore"/> dispatches between implementations
/// at open time per <see cref="StoreSchema.AtomStoreImplementation"/>.
/// </summary>
/// <remarks>
/// <para>
/// The interface captures the contract every atom-store implementation must honor.
/// Implementation-specific instrumentation (probe-distance histograms for hash, vocabulary
/// build progress for sorted) lives on the concrete classes — accessible via pattern matching
/// where <see cref="QuadStore"/> wires up Phase 7a state producers.
/// </para>
/// <para>
/// Threading: implementations are called under <see cref="QuadStore"/>'s
/// <see cref="System.Threading.ReaderWriterLockSlim"/> per ADR-020 — write operations
/// (<see cref="Intern"/>, <see cref="InternUtf8"/>) under the writer lock; read operations
/// (<see cref="GetAtomId"/>, <see cref="GetAtomSpan"/>) under the reader lock.
/// Implementations may rely on this single-writer contract.
/// </para>
/// </remarks>
internal interface IAtomStore : IDisposable
{
    /// <summary>Current count of distinct atoms in the store.</summary>
    long AtomCount { get; }

    /// <summary>
    /// Resolve <paramref name="value"/> to an atom ID, inserting if not present.
    /// Returns 0 for empty input. Atom IDs are positive 64-bit integers; 0 is the
    /// reserved "no atom" sentinel.
    /// </summary>
    long Intern(ReadOnlySpan<char> value);

    /// <summary>UTF-8 variant of <see cref="Intern"/>; preferred when input is already UTF-8.</summary>
    long InternUtf8(ReadOnlySpan<byte> utf8Value);

    /// <summary>Look up an atom ID without inserting. Returns 0 if not found.</summary>
    long GetAtomId(ReadOnlySpan<char> value);

    /// <summary>UTF-8 variant of <see cref="GetAtomId"/>.</summary>
    long GetAtomIdUtf8(ReadOnlySpan<byte> utf8Value);

    /// <summary>
    /// Get the UTF-8 bytes for an atom ID. Returns an empty span for atom ID 0
    /// (the "no atom" sentinel) or for IDs out of range.
    /// </summary>
    ReadOnlySpan<byte> GetAtomSpan(long atomId);

    /// <summary>
    /// Get the string for an atom ID. Returns an empty string for atom ID 0
    /// or for IDs out of range. UTF-8 → string conversion happens here.
    /// </summary>
    string GetAtomString(long atomId);

    /// <summary>
    /// Aggregate statistics: atom count, total stored bytes, average length.
    /// Returned tuple is a snapshot at call time.
    /// </summary>
    (long AtomCount, long TotalBytes, double AvgLength) GetStatistics();

    /// <summary>Flush in-memory state to disk. No-op for stores already durable per write.</summary>
    void Flush();

    /// <summary>
    /// Reset the store to empty: zero atoms, all underlying files truncated. Used by
    /// <see cref="QuadStore"/>'s clear-store path. Implementations must release any in-memory
    /// state so the store can be re-populated from scratch in the same session.
    /// </summary>
    void Clear();

    /// <summary>
    /// Optional umbrella observer (ADR-035 Decision 1). Receives implementation-specific
    /// discrete events (rehash, file growth, vocabulary build phases) and enables per-Intern
    /// probe-distance recording where applicable.
    /// </summary>
    IObservabilityListener? ObservabilityListener { get; set; }
}
