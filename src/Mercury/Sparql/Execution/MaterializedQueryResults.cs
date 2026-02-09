using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// Lightweight query results iterator for pre-materialized results.
/// This struct is ~200 bytes vs ~22KB for full QueryResults.
/// Use this for GRAPH, subquery, and other paths that materialize results.
/// </summary>
/// <remarks>
/// <para><strong>INTERNAL USE ONLY:</strong> This struct is internal because it is an
/// implementation detail of query result materialization for subqueries and GRAPH clauses.</para>
/// </remarks>
internal ref struct MaterializedQueryResults
{
    private readonly List<MaterializedRow> _rows;
    private readonly Binding[] _bindings;
    private readonly char[] _stringBuffer;
    private BindingTable _bindingTable;

    // LIMIT/OFFSET support
    private readonly int _limit;
    private readonly int _offset;
    private int _skipped;
    private int _returned;

    // DISTINCT support
    private readonly bool _distinct;
    private HashSet<int>? _seenHashes;

    // Current position
    private int _index;
    private bool _isEmpty;

    public static MaterializedQueryResults Empty()
    {
        return new MaterializedQueryResults(null, null!, null!, 0, 0, false);
    }

    internal MaterializedQueryResults(List<MaterializedRow>? rows, Binding[] bindings, char[] stringBuffer,
        int limit = 0, int offset = 0, bool distinct = false)
    {
        _rows = rows ?? new List<MaterializedRow>();
        _bindings = bindings;
        _stringBuffer = stringBuffer;
        _bindingTable = new BindingTable(bindings, stringBuffer);
        _limit = limit;
        _offset = offset;
        _skipped = 0;
        _returned = 0;
        _distinct = distinct;
        _seenHashes = distinct ? new HashSet<int>() : null;
        _index = -1;
        _isEmpty = rows == null || rows.Count == 0;
    }

    public BindingTable Current => _bindingTable;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (_isEmpty) return false;

        while (true)
        {
            _index++;
            if (_index >= _rows.Count) return false;

            // Copy row to binding table - MaterializedRow stores hashes and values
            var row = _rows[_index];
            _bindingTable.Clear();
            for (int i = 0; i < row.BindingCount; i++)
            {
                // MaterializedRow stores pre-hashed bindings
                _bindingTable.BindWithHash(row.GetHash(i), row.GetValue(i));
            }

            // Handle OFFSET
            if (_offset > 0 && _skipped < _offset)
            {
                _skipped++;
                continue;
            }

            // Handle DISTINCT
            if (_distinct && _seenHashes != null)
            {
                var hash = ComputeBindingHash();
                if (!_seenHashes.Add(hash))
                    continue;
            }

            // Handle LIMIT
            if (_limit > 0)
            {
                if (_returned >= _limit)
                    return false;
                _returned++;
            }

            return true;
        }
    }

    private int ComputeBindingHash()
    {
        int hash = 17;
        for (int i = 0; i < _bindingTable.Count; i++)
        {
            hash = hash * 31 + _bindingTable.GetString(i).GetHashCode();
        }
        return hash;
    }

    public void Dispose()
    {
        // Nothing to dispose - bindings arrays are owned by caller
    }
}
