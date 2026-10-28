using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Sparql.Execution.Expressions;
using SkyOmega.Mercury.Sparql.Execution.Operators;
using ValueType = SkyOmega.Mercury.Sparql.Execution.Expressions.ValueType;

namespace SkyOmega.Mercury.Sparql.Execution;

internal ref partial struct QueryResults
{
    /// <summary>
    /// Compute a hash of bindings for DISTINCT checking.
    /// For SELECT *, hashes all bindings.
    /// For SELECT ?x ?y, hashes only the projected variables.
    /// Uses FNV-1a hash combined across binding values.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ComputeBindingsHash()
    {
        unchecked
        {
            int hash = (int)2166136261; // FNV offset basis

            // SELECT *, or a clause projecting nothing by name: hash all bindings
            if (_selectClause.SelectAll || (_selectClause.ProjectedVariableCount == 0 && _selectClause.AggregateCount == 0))
            {
                for (int i = 0; i < _bindingTable.Count; i++)
                {
                    var value = _bindingTable.GetString(i);
                    foreach (var ch in value)
                    {
                        hash = (hash ^ ch) * 16777619; // FNV prime
                    }
                    hash = (hash ^ '|') * 16777619; // Separator between bindings
                }
            }
            else
            {
                // Only hash the projected variables for DISTINCT
                for (int i = 0; i < _selectClause.ProjectedVariableCount; i++)
                {
                    var (start, length) = _selectClause.GetProjectedVariable(i);
                    if (length > 0)
                    {
                        var varName = _source.Slice(start, length);
                        var bindingIdx = _bindingTable.FindBinding(varName);
                        if (bindingIdx >= 0)
                        {
                            var value = _bindingTable.GetString(bindingIdx);
                            foreach (var ch in value)
                            {
                                hash = (hash ^ ch) * 16777619; // FNV prime
                            }
                        }
                        hash = (hash ^ '|') * 16777619; // Separator between bindings
                    }
                }

                // ... and the aliases of projected expressions, e.g. ?n in (COUNT(?s) AS ?n)
                for (int i = 0; i < _selectClause.AggregateCount; i++)
                {
                    var agg = _selectClause.GetAggregate(i);
                    if (agg.AliasLength == 0)
                        continue;
                    var bindingIdx = _bindingTable.FindBinding(_source.Slice(agg.AliasStart, agg.AliasLength));
                    if (bindingIdx >= 0)
                    {
                        foreach (var ch in _bindingTable.GetString(bindingIdx))
                            hash = (hash ^ ch) * 16777619;
                    }
                    hash = (hash ^ '|') * 16777619;
                }
            }

            return hash;
        }
    }

    /// <summary>
    /// Evaluate non-aggregate SELECT expressions (e.g., (HOURS(?date) AS ?x)).
    /// These are stored with AggregateFunction.None and should be evaluated per row.
    /// </summary>
    private void EvaluateSelectExpressions()
    {
        if (_selectClause.AggregateCount == 0) return;

        for (int i = 0; i < _selectClause.AggregateCount; i++)
        {
            var agg = _selectClause.GetAggregate(i);

            // Only evaluate non-aggregate expressions (Function == None)
            if (agg.Function != AggregateFunction.None) continue;

            // Skip if no expression to evaluate
            if (agg.VariableLength == 0) continue;

            // Get expression and alias
            var expr = _source.Slice(agg.VariableStart, agg.VariableLength);
            var aliasName = _source.Slice(agg.AliasStart, agg.AliasLength);

            // Get base IRI for relative IRI resolution
            var baseIri = _buffer != null && _buffer.BaseUriLength > 0
                ? _source.Slice(_buffer.BaseUriStart, _buffer.BaseUriLength)
                : ReadOnlySpan<char>.Empty;

            // Evaluate the expression via the unified EvaluateToValue. Prefixes come from the outer buffer (main path) or,
            // when there is none, from the materialized prefixes the sub-SELECT path lends (C2).
            var evaluator = new FilterEvaluator(expr);
            var value = evaluator.EvaluateToValue(
                _bindingTable.GetBindings(),
                _bindingTable.Count,
                _bindingTable.GetStringBuffer(),
                _buffer?.Prefixes ?? _materializedPrefixes, _source, baseIri);

            // Bind the result to the alias variable
            switch (value.Type)
            {
                case ValueType.Integer:
                    _bindingTable.Bind(aliasName, value.IntegerValue);
                    break;
                case ValueType.Double:
                    _bindingTable.Bind(aliasName, value.DoubleValue);
                    break;
                case ValueType.Boolean:
                    _bindingTable.Bind(aliasName, value.BooleanValue);
                    break;
                case ValueType.String:
                case ValueType.BlankNode:
                case ValueType.Uri:
                    _bindingTable.Bind(aliasName, value.StringValue);
                    break;
            }
        }
    }

    public void Dispose()
    {
    }
}
