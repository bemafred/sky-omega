using System;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// Documentation contract for scan operators.
///
/// All scan operators in the query execution pipeline conform to this interface shape,
/// but ref struct types cannot formally implement interfaces. Instead, we use duck typing
/// with consistent method signatures.
///
/// <para>
/// <b>Contract:</b> Any type conforming to IScan must provide:
/// <list type="bullet">
///   <item><c>bool MoveNext(ref BindingTable bindings)</c> - Advance to next result, binding variables</item>
///   <item><c>void Dispose()</c> - Release resources (enumerators, pooled buffers)</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Conforming types:</b>
/// <list type="bullet">
///   <item><see cref="TriplePatternScan"/> - Single triple pattern against QuadStore</item>
///   <item><see cref="MultiPatternScan"/> - Nested loop join of multiple patterns</item>
///   <item><see cref="SlotTriplePatternScan"/> - Slot-based single pattern scan</item>
///   <item><see cref="SlotMultiPatternScan"/> - Slot-based multi-pattern scan</item>
///   <item><see cref="VariableGraphScan"/> - GRAPH ?var pattern iteration</item>
///   <item><see cref="SubQueryScan"/> - Nested SELECT subquery</item>
///   <item><see cref="SubQueryJoinScan"/> - Subquery joined with outer patterns</item>
///   <item><see cref="DefaultGraphUnionScan"/> - FROM clause union across graphs</item>
///   <item><see cref="CrossGraphMultiPatternScan"/> - Cross-graph pattern joins</item>
///   <item><see cref="ServiceScan"/> - SERVICE clause remote execution</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Usage pattern:</b>
/// <code>
/// var scan = new TriplePatternScan(store, source, pattern, bindings);
/// try
/// {
///     while (scan.MoveNext(ref bindings))
///     {
///         // Process current bindings
///     }
/// }
/// finally
/// {
///     scan.Dispose();
/// }
/// </code>
/// </para>
///
/// <para>
/// <b>C# 13+ Generic Constraints:</b>
/// With <c>allows ref struct</c>, generic methods can accept any conforming scan:
/// <code>
/// void ProcessScan&lt;TScan&gt;(ref TScan scan, ref BindingTable bindings)
///     where TScan : struct, IDisposable, allows ref struct
/// {
///     while (scan.MoveNext(ref bindings)) { /* process */ }
/// }
/// </code>
/// </para>
/// </summary>
/// <remarks>
/// This interface exists for documentation and IDE support. The actual scan operators
/// are ref structs that use duck typing to conform to this contract.
///
/// See: docs/mercury-adr-service-scan-interface.md for architectural context.
/// </remarks>
public interface IScan : IDisposable
{
    /// <summary>
    /// Advances the scan to the next result, updating bindings with new variable values.
    /// </summary>
    /// <param name="bindings">The binding table to update with variable bindings from the current result.</param>
    /// <returns>true if a result was found; false if the scan is exhausted.</returns>
    /// <remarks>
    /// Implementations should:
    /// <list type="bullet">
    ///   <item>Preserve existing bindings that are not being updated</item>
    ///   <item>Check for binding consistency (same variable must have same value)</item>
    ///   <item>Truncate bindings to initial count on each attempt before binding new values</item>
    /// </list>
    /// </remarks>
    bool MoveNext(ref BindingTable bindings);
}
