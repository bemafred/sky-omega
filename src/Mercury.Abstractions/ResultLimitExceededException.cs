// ResultLimitExceededException.cs
// Exception for the result-set materialization guard (ADR-047 / limits register).
// No external dependencies, only BCL.
// .NET 10 / C# 14

using System;

namespace SkyOmega.Mercury.Abstractions;

/// <summary>
/// Thrown when a query result would materialize more than <see cref="MaxResultRows"/> rows — the fail-fast guard
/// against unbounded result-set materialization exhausting memory (the <c>unbounded-result-materialization</c> limit).
/// SPARQL drains every solution into a fully-materialized result before the caller sees row one; without a bound, a
/// query that returns (or sorts) tens of millions of rows OOMs the process. This guard fails fast and legibly
/// instead: the caller gets a clear error, not a dead process. Matches the field's row caps (e.g. Virtuoso's
/// <c>ResultSetMaxRows</c>). Configurable via <c>StorageOptions.MaxResultRows</c> (0 = unbounded).
/// </summary>
public sealed class ResultLimitExceededException : Exception
{
    /// <summary>The configured row cap that was exceeded (<c>StorageOptions.MaxResultRows</c>).</summary>
    public long MaxResultRows { get; }

    /// <summary>The row count at which the guard tripped (greater than <see cref="MaxResultRows"/>).</summary>
    public long RowsProduced { get; }

    public ResultLimitExceededException(long maxResultRows, long rowsProduced)
        : base($"Query result exceeded the maximum of {maxResultRows:N0} rows (StorageOptions.MaxResultRows). " +
               "Add a LIMIT clause to bound the result, or raise the cap (0 = unbounded). The substrate materializes " +
               "the full result before returning it, so an unbounded result at scale would exhaust memory.")
    {
        MaxResultRows = maxResultRows;
        RowsProduced = rowsProduced;
    }

    /// <summary>
    /// Throw if <paramref name="count"/> exceeds a positive <paramref name="maxResultRows"/>. A cap of 0 (or negative)
    /// is unbounded — no check. Mirrors the BCL guard idiom (e.g. <c>ArgumentNullException.ThrowIfNull</c>) so each
    /// accumulation site is a single, explicit line.
    /// </summary>
    public static void ThrowIfExceeded(long maxResultRows, long count)
    {
        if (maxResultRows > 0 && count > maxResultRows)
            throw new ResultLimitExceededException(maxResultRows, count);
    }
}
