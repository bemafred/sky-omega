using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Sparql.Execution.Expressions;
using SkyOmega.Mercury.Sparql.Execution.Operators;
using ValueType = SkyOmega.Mercury.Sparql.Execution.Expressions.ValueType;

namespace SkyOmega.Mercury.Sparql.Execution;

internal ref partial struct QueryResults
{
    // The query's clauses, on the heap (QueryBuffer), so they stay off the stack
    private Patterns.QueryBuffer? _buffer;
    // ADR-047 C2: prefix mappings for evaluating computed SELECT projections ((expr AS ?var)) on materialized rows when
    // there is no _buffer — the sub-SELECT path (SubSelectStep), which has prefixes but no outer QueryBuffer to lend.
    private readonly PrefixMapping[]? _materializedPrefixes;
    private ReadOnlySpan<char> _source;
    private QuadStore? _store;
    private Binding[]? _bindings;
    private char[]? _stringBuffer;
    private BindingTable _bindingTable;
    private bool _isEmpty;

    // LIMIT/OFFSET support
    private readonly LimitOffsetClause _limitOffset;
    private int _skipped;
    private int _returned;

    // DISTINCT support
    private readonly bool _distinct;
    private HashSet<int>? _seenHashes;

    // UNION support

    // ORDER BY support
    private readonly OrderByClause _orderBy;
    private readonly bool _hasOrderBy;
    private List<MaterializedRow>? _sortedResults;
    private int _sortedIndex;
    private bool _sortedResultsMade; // pre-materialized rows: SELECT expressions evaluated and ORDER BY applied

    // Pre-materialized results support (for grouping pre-materialized data)
    private readonly bool _isMaterialized;
    private int _materializedIndex;




    // For join semantics: track iteration through VALUES rows for current base solution


    // Debug properties
    internal bool HasOrderBy => _hasOrderBy;

    // Expanded term storage for prefix resolution in EXISTS/MINUS evaluation

    // Graph context for EXISTS/MINUS evaluation inside GRAPH clauses
    private string? _graphContext;

    // GROUP BY support
    private readonly bool _hasGroupBy;
    private readonly GroupByClause _groupBy;
    private readonly SelectClause _selectClause;
    private List<GroupedRow>? _groupedResults;
    private int _groupedIndex;

    // A grouped query's trailing VALUES: its data rows, joined with the groups after HAVING (§18.2.4.3), and the data
    // row each entry of _groupedResults was joined with
    private readonly List<MaterializedRow>? _trailingValues;
    private List<MaterializedRow>? _groupedValues;

    // A grouped query's solutions, one per (joined) group, with its SELECT expressions, in ORDER BY's order (§18.2.5)
    private List<MaterializedRow>? _groupedSolutions;

    // HAVING support
    private readonly bool _hasHaving;
    private readonly HavingClause _having;


    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static QueryResults Empty()
    {
        var result = new QueryResults();
        result._isEmpty = true;
        return result;
    }
    /// <summary>
    /// Create QueryResults from pre-materialized rows.
    /// NoInlining prevents stack frame merging with caller (ADR-003).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static QueryResults FromMaterializedSimple(List<MaterializedRow> rows,
        ReadOnlySpan<char> source, QuadStore store, Binding[] bindings, char[] stringBuffer,
        LimitOffsetClause limitOffset = default, bool distinct = false, OrderByClause orderBy = default,
        GroupByClause groupBy = default, SelectClause selectClause = default, HavingClause having = default,
        Patterns.QueryBuffer? buffer = null, PrefixMapping[]? prefixes = null, List<MaterializedRow>? trailingValues = null)
    {
        // ORDER BY sorts the solutions with their SELECT expressions (§18.2.5): the presentation does, on its first row
        // (MoveNextOrdered, or MoveNextGrouped for a grouped query's solutions)
        return new QueryResults(rows, source, store, bindings, stringBuffer,
            limitOffset, distinct, orderBy, groupBy, selectClause, having, buffer, prefixes, trailingValues);
    }

    /// <summary>
    /// Whether a query level groups its solutions: it has GROUP BY, or aggregates (one group, §18.2.4.1). A grouped
    /// query's trailing VALUES is joined with the groups, after HAVING; any other query's with the WHERE's solutions.
    /// </summary>
    internal static bool UsesGrouping(GroupByClause groupBy, SelectClause selectClause)
        => groupBy.HasGroupBy || selectClause.HasRealAggregates;

    private static readonly List<MaterializedRow> EmptyMaterializedRows = new();

    /// <summary>
    /// ADR-047 materialization fix — present aggregate groups the tree FOLDED streaming (no intermediate row List)
    /// through the same grouped path the materializing route uses. <see cref="MoveNextGrouped"/> skips
    /// <see cref="CollectAndGroupResults"/> when <c>_groupedResults</c> is already set, so no rows are collected or
    /// iterated — the pre-finalized groups present directly. Used by QueryExecutor.TryFoldStreamingAggregate for a
    /// simple aggregate (real aggregates, no GROUP BY) over a flat BGP, the tree's one materialization regression.
    /// NoInlining prevents stack frame merging (ADR-003).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static QueryResults FromFinalizedGroups(List<GroupedRow> groups,
        ReadOnlySpan<char> source, QuadStore store, Binding[] bindings, char[] stringBuffer,
        LimitOffsetClause limitOffset, bool distinct, SelectClause selectClause)
    {
        return new QueryResults(groups, source, store, bindings, stringBuffer, limitOffset, distinct, selectClause);
    }

    /// <summary>
    /// Create QueryResults from pre-materialized rows without any large clause structs.
    /// This is the minimal version that avoids stack overflow.
    /// NoInlining prevents the compiler from merging this into a larger stack frame.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static QueryResults FromMaterializedRows(List<MaterializedRow> rows,
        ReadOnlySpan<char> source, QuadStore store, Binding[] bindings, char[] stringBuffer,
        LimitOffsetClause limitOffset = default, bool distinct = false)
    {
        return new QueryResults(rows, source, store, bindings, stringBuffer, limitOffset, distinct);
    }

    /// <summary>
    /// Create QueryResults from a pre-materialized list without source/store references.
    /// This is the most minimal version - just iterates the list directly.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static QueryResults FromMaterializedList(List<MaterializedRow> rows,
        Binding[] bindings, char[] stringBuffer, LimitOffsetClause limitOffset = default, bool distinct = false)
    {
        return new QueryResults(rows, bindings, stringBuffer, limitOffset, distinct);
    }
    /// <summary>
    /// Private constructor for pre-materialized results without pattern.
    /// Used for simple materialized iteration where pattern access is not needed.
    /// </summary>
    private QueryResults(List<MaterializedRow> rows, ReadOnlySpan<char> source,
        QuadStore store, Binding[] bindings, char[] stringBuffer,
        LimitOffsetClause limitOffset, bool distinct, OrderByClause orderBy,
        GroupByClause groupBy, SelectClause selectClause, HavingClause having,
        Patterns.QueryBuffer? buffer = null, PrefixMapping[]? prefixes = null, List<MaterializedRow>? trailingValues = null)
    {
        // ADR-047 cutover: the tree path passes its QueryBuffer so the pre-materialized presentation can evaluate
        // computed SELECT projections ((expr AS ?var)) — EvaluateSelectExpressions needs prefixes. The other _has* flags
        // below stay false, so a non-null _buffer activates nothing else (MINUS/VALUES/FILTER are gated on their own
        // flags). Null is still valid (the pattern itself is not needed to iterate materialized rows). The sub-SELECT
        // path (C2) has no outer buffer to lend, so it passes its prefixes directly via `prefixes`.
        _buffer = buffer;
        _materializedPrefixes = prefixes;
        _source = source;
        _store = store;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _bindingTable = new BindingTable(bindings, stringBuffer);
        _isEmpty = rows.Count == 0;
        _limitOffset = limitOffset;
        _skipped = 0;
        _returned = 0;
        _distinct = distinct;
        _seenHashes = distinct ? new HashSet<int>() : null;
        _orderBy = orderBy;
        _hasOrderBy = true; // Force use of MoveNextOrdered() to iterate pre-collected results
        _sortedResults = rows;
        _sortedIndex = -1;
        _isMaterialized = true;  // Enable iteration from pre-materialized list
        _materializedIndex = -1;
        // A trailing VALUES is joined by the caller with the WHERE's solutions, unless the query groups: then it comes
        // here as data rows, and is joined with the groups after HAVING (CollectAndGroupResults)
        _trailingValues = trailingValues;
        _groupBy = groupBy;
        _selectClause = selectClause;
        _hasGroupBy = UsesGrouping(groupBy, selectClause);
        _groupedResults = null;
        _groupedIndex = -1;
        _having = having;
        _hasHaving = having.HasHaving;
    }

    /// <summary>
    /// ADR-047 materialization fix — constructor for pre-finalized aggregate groups (the streaming fold). Mirrors the
    /// pre-materialized constructor's iteration state, but pre-sets <c>_groupedResults</c> so
    /// <see cref="MoveNextGrouped"/> skips collection and presents the finalized groups directly. The caller has
    /// already folded the BGP into the group(s) and called <see cref="GroupedRow.FinalizeAggregates"/>; this type only
    /// projects and applies LIMIT/OFFSET. The gated path has no GROUP BY / ORDER BY / HAVING (a single global group).
    /// </summary>
    private QueryResults(List<GroupedRow> groups, ReadOnlySpan<char> source,
        QuadStore store, Binding[] bindings, char[] stringBuffer,
        LimitOffsetClause limitOffset, bool distinct, SelectClause selectClause)
    {
        _buffer = null;
        _source = source;
        _store = store;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _bindingTable = new BindingTable(bindings, stringBuffer);
        _isEmpty = groups.Count == 0;
        _limitOffset = limitOffset;
        _skipped = 0;
        _returned = 0;
        _distinct = distinct;
        _seenHashes = distinct ? new HashSet<int>() : null;
        _orderBy = default;
        _hasOrderBy = false;
        _sortedResults = EmptyMaterializedRows; // unused: MoveNextGrouped reads _groupedResults, not _sortedResults
        _sortedIndex = -1;
        _isMaterialized = true;
        _materializedIndex = -1;
        _groupBy = default;
        _selectClause = selectClause;
        _hasGroupBy = true;        // present via MoveNextGrouped
        _groupedResults = groups;  // pre-finalized ⇒ MoveNextGrouped skips CollectAndGroupResults
        _groupedIndex = -1;
        _having = default;
        _hasHaving = false;
    }

    /// <summary>
    /// Minimal private constructor for pre-materialized results without any large clause structs.
    /// Avoids stack overflow by not passing ORDER BY, GROUP BY, SELECT, HAVING clauses.
    /// </summary>
    private QueryResults(List<MaterializedRow> rows, ReadOnlySpan<char> source,
        QuadStore store, Binding[] bindings, char[] stringBuffer,
        LimitOffsetClause limitOffset, bool distinct)
    {
        _buffer = null;
        _source = source;
        _store = store;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _bindingTable = new BindingTable(bindings, stringBuffer);
        _isEmpty = rows.Count == 0;
        _limitOffset = limitOffset;
        _skipped = 0;
        _returned = 0;
        _distinct = distinct;
        _seenHashes = distinct ? new HashSet<int>() : null;
        _orderBy = default;
        _hasOrderBy = true; // Use MoveNextOrdered() to iterate pre-collected results
        _sortedResults = rows;
        _sortedIndex = -1;
        _groupBy = default;
        _selectClause = default;
        _hasGroupBy = false;
        _groupedResults = null;
        _groupedIndex = -1;
        _having = default;
        _hasHaving = false;
    }

    /// <summary>
    /// Most minimal constructor - just for iterating a pre-materialized list.
    /// No source/store references needed.
    /// </summary>
    private QueryResults(List<MaterializedRow> rows, Binding[] bindings, char[] stringBuffer,
        LimitOffsetClause limitOffset, bool distinct)
    {
        _buffer = null;
        _source = default;
        _store = null;
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _bindingTable = new BindingTable(bindings, stringBuffer);
        _isEmpty = rows.Count == 0;
        _limitOffset = limitOffset;
        _skipped = 0;
        _returned = 0;
        _distinct = distinct;
        _seenHashes = distinct ? new HashSet<int>() : null;
        _orderBy = default;
        _hasOrderBy = true;
        _sortedResults = rows;
        _sortedIndex = -1;
        _groupBy = default;
        _selectClause = default;
        _hasGroupBy = false;
        _groupedResults = null;
        _groupedIndex = -1;
        _having = default;
        _hasHaving = false;
    }

    /// <summary>
    /// Current result row with variable bindings.
    /// </summary>
    public readonly BindingTable Current => _bindingTable;

    /// <summary>
    /// Set the graph context for EXISTS/MINUS evaluation inside GRAPH clauses.
    /// When set, EXISTS patterns will query the specified graph instead of the default graph.
    /// </summary>
    internal void SetGraphContext(string? graphIri)
    {
        _graphContext = graphIri;
    }

    /// <summary>
    /// Move to next result row.
    /// </summary>
    public bool MoveNext()
    {
        // An IMPLICIT aggregation (real aggregates, no GROUP BY) over an empty input still yields ONE row — the
        // aggregate identity (COUNT 0, SUM 0, GROUP_CONCAT "", …), SPARQL §18.5 — so it must NOT short-circuit on
        // empty here; it falls through to MoveNextGrouped, whose CollectAndGroupResults synthesizes the default group.
        // Explicit GROUP BY over empty correctly yields zero rows, so it still short-circuits.
        if (_isEmpty && !(_selectClause.HasRealAggregates && !_groupBy.HasGroupBy)) return false;

        // GROUP BY requires collecting all results first, then grouping
        if (_hasGroupBy)
        {
            return MoveNextGrouped();
        }

        // ORDER BY requires collecting all results first, then sorting
        if (_hasOrderBy)
        {
            return MoveNextOrdered();
        }

        // ADR-047 d2: the streaming scan path is gone — every materialized ctor sets _hasOrderBy / _hasGroupBy /
        // so one of the branches above always handles the row presentation. This fallthrough is
        // unreachable; returning false is the safe terminal.
        return false;
    }

    /// <summary>
    /// Try to match optional patterns and extend bindings.
    /// If a pattern doesn't match, we continue without it (left outer join).
    /// </summary>
}

/// <summary>
/// Materialized row for ORDER BY sorting.
/// Stores binding hashes and values as strings (heap-allocated).
/// </summary>
internal sealed class MaterializedRow
{
    private readonly int[] _hashes;
    private readonly string[] _values;
    private readonly BindingValueType[] _types;   // ADR-047: keep each value's datatype tag (see RestoreBindings)
    private readonly int _count;

    public int BindingCount => _count;

    public MaterializedRow(BindingTable bindings)
    {
        _count = bindings.Count;
        _hashes = new int[_count];
        _values = new string[_count];
        _types = new BindingValueType[_count];

        var bindingSpan = bindings.GetBindings();
        for (int i = 0; i < _count; i++)
        {
            _hashes[i] = bindingSpan[i].VariableNameHash;
            _values[i] = bindings.GetString(i).ToString();
            _types[i] = bindingSpan[i].Type;
        }
    }

    public int GetHash(int index) => _hashes[index];
    public ReadOnlySpan<char> GetValue(int index) => _values[index];
    public BindingValueType GetValueType(int index) => _types[index];

    public ReadOnlySpan<char> GetValueByName(ReadOnlySpan<char> name)
    {
        var hash = VariableName.Hash(name);
        for (int i = 0; i < _count; i++)
        {
            if (_hashes[i] == hash)
                return _values[i];
        }
        return ReadOnlySpan<char>.Empty;
    }

    /// <summary>
    /// Restore this row's bindings into a BindingTable.
    /// Used when re-executing operators with stored results.
    /// </summary>
    public void RestoreBindings(ref BindingTable bindings)
    {
        for (int i = 0; i < _count; i++)
        {
            // Restore WITH the datatype tag: a numeric/boolean binding (a BIND result) keeps "2"+Integer rather than a
            // plain "2", so when it seeds a later pattern scan the scan can match it against the stored typed literal.
            bindings.BindWithHash(_hashes[i], _values[i].AsSpan(), _types[i]);
        }
    }

}

/// <summary>
/// Grouped row for GROUP BY aggregation.
/// Stores group key values and aggregate accumulators.
/// </summary>
internal sealed class GroupedRow
{
    // Group key storage
    private readonly int[] _keyHashes;
    private readonly string[] _keyValues;
    private readonly int _keyCount;

    // Aggregate storage
    private readonly int[] _aggHashes;        // Hash of alias variable name
    private readonly string[] _aggValues;      // Final computed values
    private readonly AggregateFunction[] _aggFunctions;
    private readonly int[] _aggVarHashes;      // Hash of source variable name
    private readonly string?[] _aggExpressions;  // Expression string for complex expressions (null if simple variable)
    private readonly int _aggCount;

    // Aggregate accumulators
    private readonly long[] _counts;
    private readonly double[] _sums;
    private readonly decimal[] _decimalSums;    // For precise decimal arithmetic
    private readonly string?[] _minTerms;   // MIN/MAX keep the winning term itself, ordered as ORDER BY orders it
    private readonly string?[] _maxTerms;   // (§18.5.1.4–5); re-formatting it broke a join on the result (W3C sq08).
    private readonly bool[] _useDecimal;        // True if all values are decimal (not double/float)
    private readonly HashSet<string>?[] _distinctSets;
    private readonly List<string>?[] _concatValues;  // For GROUP_CONCAT
    private readonly string[] _separators;           // For GROUP_CONCAT
    private readonly string?[] _sampleValues;        // For SAMPLE
    private readonly bool[] _hasError;               // True if aggregate encountered error (e.g., non-numeric for AVG/SUM)

    // Row count for HAVING COUNT(*) when not in SELECT
    private long _rowCount;

    public int KeyCount => _keyCount;
    public int AggregateCount => _aggCount;
    public long RowCount => _rowCount;

    public GroupedRow(GroupByClause groupBy, SelectClause selectClause, BindingTable bindings, string source)
    {
        // Store group key values
        _keyCount = groupBy.Count;
        _keyHashes = new int[_keyCount];
        _keyValues = new string[_keyCount];

        for (int i = 0; i < _keyCount; i++)
        {
            var (start, len) = groupBy.GetVariable(i);
            var varName = source.AsSpan(start, len);
            _keyHashes[i] = VariableName.Hash(varName);
            var idx = bindings.FindBinding(varName);
            _keyValues[i] = idx >= 0 ? bindings.GetString(idx).ToString() : "";
        }

        // Initialize aggregate accumulators
        _aggCount = selectClause.AggregateCount;
        _aggHashes = new int[_aggCount];
        _aggValues = new string[_aggCount];
        _aggFunctions = new AggregateFunction[_aggCount];
        _aggVarHashes = new int[_aggCount];
        _aggExpressions = new string?[_aggCount];
        _counts = new long[_aggCount];
        _sums = new double[_aggCount];
        _decimalSums = new decimal[_aggCount];
        _minTerms = new string?[_aggCount];
        _maxTerms = new string?[_aggCount];
        _useDecimal = new bool[_aggCount];
        _distinctSets = new HashSet<string>?[_aggCount];
        _concatValues = new List<string>?[_aggCount];
        _separators = new string[_aggCount];
        _sampleValues = new string?[_aggCount];
        _hasError = new bool[_aggCount];

        for (int i = 0; i < _aggCount; i++)
        {
            var agg = selectClause.GetAggregate(i);
            _aggFunctions[i] = agg.Function;

            // Hash of alias (result variable name)
            var aliasName = source.AsSpan(agg.AliasStart, agg.AliasLength);
            _aggHashes[i] = VariableName.Hash(aliasName);

            // Hash of source variable/expression
            var varName = source.AsSpan(agg.VariableStart, agg.VariableLength);
            _aggVarHashes[i] = VariableName.Hash(varName);

            // Check if this is a complex expression (not a simple variable)
            // Simple variables start with ? or $, or are just *
            var trimmed = varName.Trim();
            bool isSimpleVariable = trimmed.Length == 0 ||
                                    trimmed[0] == '?' ||
                                    trimmed[0] == '$' ||
                                    (trimmed.Length == 1 && trimmed[0] == '*');
            _aggExpressions[i] = isSimpleVariable ? null : varName.ToString();

            // Initialize accumulators
            _useDecimal[i] = true; // Assume decimal until we see a double/float
            if (agg.Distinct)
            {
                _distinctSets[i] = new HashSet<string>();
            }

            // Initialize GROUP_CONCAT accumulators
            if (agg.Function == AggregateFunction.GroupConcat)
            {
                _concatValues[i] = new List<string>();
                // Extract separator from source, default to space
                _separators[i] = agg.SeparatorLength > 0
                    ? source.Substring(agg.SeparatorStart, agg.SeparatorLength)
                    : " ";
            }
        }
    }

    public void UpdateAggregates(BindingTable bindings, string source)
    {
        // Track total rows for HAVING COUNT(*) when not in SELECT aggregates
        _rowCount++;

        for (int i = 0; i < _aggCount; i++)
        {
            var func = _aggFunctions[i];
            var varHash = _aggVarHashes[i];

            // Find the value for this aggregate's variable
            string? valueStr = null;
            double numValue = 0;
            decimal decimalValue = 0;
            bool hasNumValue = false;
            bool isDouble = false;

            // For COUNT(*), we don't need a specific variable
            if (varHash != VariableName.Hash("*".AsSpan()))
            {
                // Check if we have a complex expression that needs evaluation
                var expr = _aggExpressions[i];
                if (expr != null)
                {
                    // Evaluate the complex expression (e.g., IF(isNumeric(?p), ?p, COALESCE(...)))
                    var evaluator = new FilterEvaluator(expr.AsSpan());
                    // UpdateAggregates has no buffer/prefixes in scope — the aggregate-inner-expression path keeps the
                    // hardcoded xsd/rdf/rdfs prefixes only (a prologue-prefixed constant inside an aggregate is rare).
                    var value = evaluator.EvaluateToValue(
                        bindings.GetBindings(),
                        bindings.Count,
                        bindings.GetStringBuffer(),
                        null, ReadOnlySpan<char>.Empty);

                    // An error is skipped by every aggregate, COUNT included (§18.5.1.2 counts bound, non-error
                    // values); a value is read as its term, as a bound variable is
                    if (value.Type == ValueType.Unbound)
                        continue;
                    else
                    {
                        valueStr = value.Term();
                        hasNumValue = TryParseRdfNumeric(valueStr, out numValue, out decimalValue, out isDouble);
                    }
                }
                else
                {
                    // Simple variable lookup
                    var idx = bindings.FindBindingByHash(varHash);
                    if (idx >= 0)
                    {
                        valueStr = bindings.GetString(idx).ToString();
                        // Use RDF-aware numeric parsing to handle typed literals like "1"^^<xsd:integer>
                        hasNumValue = TryParseRdfNumeric(valueStr, out numValue, out decimalValue, out isDouble);
                    }
                    else
                    {
                        // An unbound variable is skipped by every aggregate, COUNT included (§18.5.1.2)
                        continue;
                    }
                }
            }

            // Handle DISTINCT
            if (_distinctSets[i] != null)
            {
                string val;
                if (varHash == VariableName.Hash("*".AsSpan()))
                {
                    // COUNT(DISTINCT *) - build key from all bound variables in the row
                    var keyBuilder = new System.Text.StringBuilder();
                    for (int j = 0; j < bindings.Count; j++)
                    {
                        if (j > 0) keyBuilder.Append('\0');
                        keyBuilder.Append(bindings.GetString(j));
                    }
                    val = keyBuilder.ToString();
                }
                else
                {
                    val = valueStr ?? "";
                }
                if (!_distinctSets[i]!.Add(val))
                    continue; // Already seen this value
            }

            // If we encounter a double/float value, switch to double mode
            if (hasNumValue && isDouble)
                _useDecimal[i] = false;

            // Update accumulator based on function
            switch (func)
            {
                case AggregateFunction.Count:
                    _counts[i]++;
                    break;
                case AggregateFunction.Sum:
                    if (hasNumValue)
                    {
                        _sums[i] += numValue;
                        _decimalSums[i] += decimalValue;
                    }
                    else if (valueStr != null)
                    {
                        // Bound but not numeric - SPARQL error propagation
                        _hasError[i] = true;
                    }
                    break;
                case AggregateFunction.Avg:
                    if (hasNumValue)
                    {
                        _sums[i] += numValue;
                        _decimalSums[i] += decimalValue;
                        _counts[i]++;
                    }
                    else if (valueStr != null)
                    {
                        // Bound but not numeric - SPARQL error propagation
                        _hasError[i] = true;
                    }
                    break;
                case AggregateFunction.Min:
                    if (valueStr != null &&
                        (_minTerms[i] == null || MaterializedRowComparer.CompareValues(valueStr, _minTerms[i]) < 0))
                        _minTerms[i] = valueStr;
                    break;
                case AggregateFunction.Max:
                    if (valueStr != null &&
                        (_maxTerms[i] == null || MaterializedRowComparer.CompareValues(valueStr, _maxTerms[i]) > 0))
                        _maxTerms[i] = valueStr;
                    break;
                case AggregateFunction.GroupConcat:
                    // GROUP_CONCAT concatenates the STR() lexical value of each term (SPARQL §18.5.1.7), not the raw
                    // RDF term — so an IRI joins without <>, a literal without its quotes / datatype / language tag.
                    if (valueStr != null)
                        _concatValues[i]!.Add(LexicalOf(valueStr));
                    break;
                case AggregateFunction.Sample:
                    // SAMPLE returns an arbitrary value - we take the first one
                    if (_sampleValues[i] == null && valueStr != null)
                        _sampleValues[i] = valueStr;
                    break;
            }
        }
    }

    public void FinalizeAggregates()
    {
        for (int i = 0; i < _aggCount; i++)
        {
            // Check for error first - return empty (no binding) for numeric aggregates with errors
            if (_hasError[i])
            {
                var func = _aggFunctions[i];
                if (func == AggregateFunction.Sum || func == AggregateFunction.Avg)
                {
                    _aggValues[i] = "";
                    continue;
                }
            }

            _aggValues[i] = _aggFunctions[i] switch
            {
                AggregateFunction.Count => FormatTypedLiteral(_counts[i].ToString(), XsdInteger),
                AggregateFunction.Sum => _useDecimal[i]
                    ? FormatTypedLiteral(FormatDecimal(_decimalSums[i]), XsdDecimal)
                    : FormatTypedLiteral(_sums[i].ToString(CultureInfo.InvariantCulture), XsdDouble),
                AggregateFunction.Avg => _counts[i] > 0
                    ? (_useDecimal[i]
                        ? FormatTypedLiteral(FormatDecimal(_decimalSums[i] / _counts[i]), XsdDecimal)
                        : FormatTypedLiteral((_sums[i] / _counts[i]).ToString(CultureInfo.InvariantCulture), XsdDouble))
                    : FormatTypedLiteral("0", XsdInteger),
                // MIN/MAX return the winning term itself, so MAX(?y) of xsd:integer values can join back (sq08)
                AggregateFunction.Min => _minTerms[i] ?? "",
                AggregateFunction.Max => _maxTerms[i] ?? "",
                AggregateFunction.GroupConcat => _concatValues[i] != null
                    ? "\"" + string.Join(_separators[i], _concatValues[i]!) + "\""  // a simple (xsd:string) literal
                    : "\"\"",
                AggregateFunction.Sample => _sampleValues[i] ?? "",
                _ => ""
            };
        }
    }

    private const string XsdInteger = "http://www.w3.org/2001/XMLSchema#integer";
    private const string XsdDecimal = "http://www.w3.org/2001/XMLSchema#decimal";
    private const string XsdDouble = "http://www.w3.org/2001/XMLSchema#double";

    /// <summary>
    /// Format a value as a typed RDF literal: "value"^^&lt;datatype&gt;
    /// </summary>
    private static string FormatTypedLiteral(string value, string datatype)
    {
        return $"\"{value}\"^^<{datatype}>";
    }

    /// <summary>
    /// The STR() lexical value of an RDF term: an IRI's content without the &lt;&gt;, a literal's lexical form without
    /// the surrounding quotes / <c>^^datatype</c> / <c>@lang</c> tag. GROUP_CONCAT concatenates these, not raw terms.
    /// </summary>
    private static string LexicalOf(string term)
    {
        if (term.Length >= 2 && term[0] == '<' && term[^1] == '>')
            return term.Substring(1, term.Length - 2);
        if (term.Length >= 2 && term[0] == '"')
        {
            int close = term.LastIndexOf('"');
            if (close > 0) return term.Substring(1, close - 1);
        }
        return term;
    }

    /// <summary>
    /// Format a decimal value, removing trailing zeros but keeping at least one decimal place
    /// if the original value had decimals.
    /// </summary>
    private static string FormatDecimal(decimal value)
    {
        // Use G29 to get up to 29 significant digits without trailing zeros
        var str = value.ToString("G29", CultureInfo.InvariantCulture);
        return str;
    }

    public int GetKeyHash(int index) => _keyHashes[index];
    public ReadOnlySpan<char> GetKeyValue(int index) => _keyValues[index];
    public int GetAggregateHash(int index) => _aggHashes[index];
    public ReadOnlySpan<char> GetAggregateValue(int index) => _aggValues[index];


    /// <summary>
    /// Try to parse a numeric value from an RDF literal string.
    /// Handles formats: "42", "3.14", "1"^^&lt;xsd:integer&gt;, "2.0"^^&lt;xsd:decimal&gt;
    /// Returns both double and decimal representations, and indicates if the value
    /// is a double (scientific notation or xsd:double/xsd:float type).
    /// </summary>
    private static bool TryParseRdfNumeric(string str, out double doubleResult, out decimal decimalResult, out bool isDouble)
    {
        doubleResult = 0;
        decimalResult = 0;
        isDouble = false;

        if (string.IsNullOrEmpty(str))
            return false;

        string valueStr;
        string? datatype = null;

        // Handle typed literals: "value"^^<datatype>
        if (str.StartsWith('"'))
        {
            // Find end of quoted value - it's either ^^ (typed), @ (language), or closing quote
            int endQuote = str.IndexOf('"', 1);
            if (endQuote <= 0)
                return false;

            // Extract the value between quotes
            valueStr = str.Substring(1, endQuote - 1);

            // Check for datatype
            var suffix = str.AsSpan(endQuote + 1);
            if (suffix.StartsWith("^^"))
            {
                var dtStr = suffix.Slice(2).ToString();
                if (dtStr.StartsWith('<') && dtStr.EndsWith('>'))
                    datatype = dtStr.Substring(1, dtStr.Length - 2);
                else
                    datatype = dtStr;
            }
        }
        else
        {
            valueStr = str;
        }

        // Determine if this is a double/float type
        isDouble = datatype != null &&
            (datatype.EndsWith("double", StringComparison.OrdinalIgnoreCase) ||
             datatype.EndsWith("float", StringComparison.OrdinalIgnoreCase));

        // Also check for scientific notation (indicates double)
        if (!isDouble && (valueStr.Contains('e') || valueStr.Contains('E')))
            isDouble = true;

        // Try to parse as double (always)
        if (!double.TryParse(valueStr, NumberStyles.Float, CultureInfo.InvariantCulture, out doubleResult))
            return false;

        // Try to parse as decimal (may fail for very large/small numbers or scientific notation)
        if (!decimal.TryParse(valueStr, NumberStyles.Float, CultureInfo.InvariantCulture, out decimalResult))
        {
            // If decimal parse fails, use the double value converted to decimal
            // This will lose precision but at least gives us a value
            try
            {
                decimalResult = (decimal)doubleResult;
            }
            catch
            {
                decimalResult = 0;
                isDouble = true; // Force double mode since decimal can't represent this
            }
        }

        return true;
    }
}

/// <summary>
/// Results from CONSTRUCT query execution. Yields constructed triples.
/// Must be disposed to return pooled resources.
/// </summary>
