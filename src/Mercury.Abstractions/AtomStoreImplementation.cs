namespace SkyOmega.Mercury.Abstractions;

/// <summary>
/// Backing implementation for the atom store of a Mercury store. Chosen at store creation,
/// durable in <c>store-schema.json</c>, and immutable for the lifetime of the store.
/// Profile-dispatched at <see cref="StoreSchema.ForProfile"/>: Reference can opt into the
/// sorted-vocabulary path when the read-mostly contract holds; the other profiles always
/// use the hash table because they need incremental Intern.
/// </summary>
/// <remarks>
/// See <see href="../docs/adrs/mercury/ADR-034-sorted-atom-store-for-reference.md">ADR-034</see>.
/// New stores default to <see cref="Hash"/>; existing stores written before this field was
/// introduced are read as <see cref="Hash"/> for backward compatibility — Mercury's
/// open-time schema migration tolerates the missing field.
/// </remarks>
public enum AtomStoreImplementation
{
    /// <summary>
    /// Open-addressing hash table with rehash-on-grow (ADR-028). Supports incremental
    /// <see cref="System.IObservable{T}"/>-style Intern and works for all profiles.
    /// Default for backward compatibility — existing stores opened without the schema
    /// field present are read as <c>Hash</c>.
    /// </summary>
    Hash,

    /// <summary>
    /// Sorted vocabulary built via external merge-sort (ADR-034). No hash table, no rehash
    /// drift, dense sequential atom IDs (1..N), binary-search lookup. Reference profile only;
    /// single-bulk-load contract per ADR-034 Decision 7 (ADR-029 Decision 7's appendable-bulk
    /// semantic is suspended for <c>Sorted</c>-backed stores).
    /// </summary>
    Sorted
}
