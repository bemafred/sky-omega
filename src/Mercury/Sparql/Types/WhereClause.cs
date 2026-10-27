namespace SkyOmega.Mercury.Sparql.Types;

internal struct WhereClause
{
    /// <summary>Source offset of the WHERE group's opening '{', recorded by the parser; -1 when the query has no
    /// WHERE group (DESCRIBE &lt;iri&gt;). Execution parses the pattern tree from here (ADR-055 D1).</summary>
    public int GroupStart;
}
