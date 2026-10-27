namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// [25] LimitOffsetClauses ::= LimitClause OffsetClause? | OffsetClause LimitClause? — the slice of a solution sequence
/// (SPARQL 1.1 §15.4–15.5, algebra Slice). Without a LIMIT clause the slice is unbounded; <c>LIMIT 0</c> is a bound of
/// zero and yields no solutions. The default value is the unbounded slice with no offset, which is what a query without
/// these clauses means.
/// </summary>
internal struct LimitOffsetClause
{
    /// <summary>Solutions skipped before the first one returned (OFFSET; 0 when absent).</summary>
    public int Offset;

    /// <summary>The most solutions returned. Meaningful only when <see cref="HasLimit"/>.</summary>
    public int Limit;

    /// <summary>The query has a LIMIT clause.</summary>
    public bool HasLimit;

    /// <summary>The slice has returned all it may, given how many solutions it has returned so far.</summary>
    public readonly bool LimitReached(int returned) => HasLimit && returned >= Limit;

    /// <summary>
    /// The number of solutions the slice reads at most (OFFSET + LIMIT), or null when it is unbounded. A scan or a top-K
    /// heap may stop after this many rows.
    /// </summary>
    public readonly long? RowsRead => HasLimit ? (long)Offset + Limit : null;
}
