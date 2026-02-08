using System;
using System.Globalization;
using SkyOmega.Mercury.Sparql.Patterns;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// Groups results and computes aggregates for subqueries with GROUP BY and/or aggregates.
/// Similar to GroupedRow but works with SubSelect instead of SelectClause.
/// </summary>
internal sealed class SubQueryGroupedRow
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
    private readonly int _aggCount;
    private readonly bool[] _aggDistinct;      // DISTINCT flag per aggregate

    // Aggregate accumulators
    private readonly long[] _counts;
    private readonly double[] _sums;
    private readonly double[] _mins;
    private readonly double[] _maxes;
    private readonly decimal[] _decimalSums;    // For precise decimal arithmetic
    private readonly decimal[] _decimalMins;
    private readonly decimal[] _decimalMaxes;
    private readonly bool[] _useDecimal;        // True if all values are decimal (not double/float)
    private readonly string?[] _minLiterals;    // Original literal for MIN (preserve datatype)
    private readonly string?[] _maxLiterals;    // Original literal for MAX (preserve datatype)
    private readonly HashSet<string>?[] _distinctSets;
    private readonly List<string>?[] _concatValues;  // For GROUP_CONCAT
    private readonly string[] _separators;           // For GROUP_CONCAT
    private readonly string?[] _sampleValues;        // For SAMPLE

    public int KeyCount => _keyCount;
    public int AggregateCount => _aggCount;

    public SubQueryGroupedRow(SubSelect subSelect, BindingTable bindings, string source)
    {
        // Store group key values from GROUP BY clause
        _keyCount = subSelect.HasGroupBy ? subSelect.GroupBy.Count : 0;
        _keyHashes = new int[_keyCount];
        _keyValues = new string[_keyCount];

        for (int i = 0; i < _keyCount; i++)
        {
            var (start, len) = subSelect.GroupBy.GetVariable(i);
            var varName = source.AsSpan(start, len);
            _keyHashes[i] = ComputeHash(varName);
            var idx = bindings.FindBinding(varName);
            _keyValues[i] = idx >= 0 ? bindings.GetString(idx).ToString() : "";
        }

        // Initialize aggregate accumulators from SubSelect
        _aggCount = subSelect.AggregateCount;
        _aggHashes = new int[_aggCount];
        _aggValues = new string[_aggCount];
        _aggFunctions = new AggregateFunction[_aggCount];
        _aggVarHashes = new int[_aggCount];
        _aggDistinct = new bool[_aggCount];
        _counts = new long[_aggCount];
        _sums = new double[_aggCount];
        _mins = new double[_aggCount];
        _maxes = new double[_aggCount];
        _decimalSums = new decimal[_aggCount];
        _decimalMins = new decimal[_aggCount];
        _decimalMaxes = new decimal[_aggCount];
        _useDecimal = new bool[_aggCount];
        _minLiterals = new string?[_aggCount];
        _maxLiterals = new string?[_aggCount];
        _distinctSets = new HashSet<string>?[_aggCount];
        _concatValues = new List<string>?[_aggCount];
        _separators = new string[_aggCount];
        _sampleValues = new string?[_aggCount];

        for (int i = 0; i < _aggCount; i++)
        {
            var agg = subSelect.GetAggregate(i);
            _aggFunctions[i] = agg.Function;
            _aggDistinct[i] = agg.Distinct;

            // Hash of alias (result variable name)
            var aliasName = source.AsSpan(agg.AliasStart, agg.AliasLength);
            _aggHashes[i] = ComputeHash(aliasName);

            // Hash of source variable
            if (agg.VariableLength > 0)
            {
                var varName = source.AsSpan(agg.VariableStart, agg.VariableLength);
                _aggVarHashes[i] = ComputeHash(varName);
            }
            else
            {
                // COUNT(*) case
                _aggVarHashes[i] = ComputeHash("*".AsSpan());
            }

            // Initialize accumulators
            _mins[i] = double.MaxValue;
            _maxes[i] = double.MinValue;
            _decimalMins[i] = decimal.MaxValue;
            _decimalMaxes[i] = decimal.MinValue;
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
            if (varHash != ComputeHash("*".AsSpan()))
            {
                var idx = bindings.FindBindingByHash(varHash);
                if (idx >= 0)
                {
                    valueStr = bindings.GetString(idx).ToString();
                    // Use RDF-aware numeric parsing to handle typed literals
                    hasNumValue = TryParseRdfNumeric(valueStr, out numValue, out decimalValue, out isDouble);
                }
                else
                {
                    // Variable not bound - skip for most aggregates
                    if (func != AggregateFunction.Count)
                        continue;
                }
            }

            // Handle DISTINCT
            if (_distinctSets[i] != null)
            {
                var val = valueStr ?? "";
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
                    break;
                case AggregateFunction.Avg:
                    if (hasNumValue)
                    {
                        _sums[i] += numValue;
                        _decimalSums[i] += decimalValue;
                        _counts[i]++;
                    }
                    break;
                case AggregateFunction.Min:
                    if (hasNumValue)
                    {
                        // Use decimal comparison if in decimal mode, else double
                        bool isNewMin = _useDecimal[i]
                            ? decimalValue < _decimalMins[i]
                            : numValue < _mins[i];
                        if (isNewMin)
                        {
                            _mins[i] = numValue;
                            _decimalMins[i] = decimalValue;
                            _minLiterals[i] = valueStr;  // Preserve original literal with datatype
                        }
                    }
                    break;
                case AggregateFunction.Max:
                    if (hasNumValue)
                    {
                        // Use decimal comparison if in decimal mode, else double
                        bool isNewMax = _useDecimal[i]
                            ? decimalValue > _decimalMaxes[i]
                            : numValue > _maxes[i];
                        if (isNewMax)
                        {
                            _maxes[i] = numValue;
                            _decimalMaxes[i] = decimalValue;
                            _maxLiterals[i] = valueStr;  // Preserve original literal with datatype
                        }
                    }
                    break;
                case AggregateFunction.GroupConcat:
                    if (valueStr != null)
                        _concatValues[i]!.Add(valueStr);
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
                AggregateFunction.Min => _minLiterals[i] ?? "",  // Preserve original literal with datatype
                AggregateFunction.Max => _maxLiterals[i] ?? "",  // Preserve original literal with datatype
                AggregateFunction.GroupConcat => _concatValues[i] != null
                    ? string.Join(_separators[i], _concatValues[i]!)
                    : "",
                AggregateFunction.Sample => _sampleValues[i] ?? "",
                _ => ""
            };
        }
    }

    private const string XsdInteger = "http://www.w3.org/2001/XMLSchema#integer";
    private const string XsdDecimal = "http://www.w3.org/2001/XMLSchema#decimal";
    private const string XsdDouble = "http://www.w3.org/2001/XMLSchema#double";

    private static string FormatTypedLiteral(string value, string datatype)
    {
        return $"\"{value}\"^^<{datatype}>";
    }

    private static string FormatDecimal(decimal value)
    {
        return value.ToString("G29", CultureInfo.InvariantCulture);
    }

    public int GetKeyHash(int index) => _keyHashes[index];
    public ReadOnlySpan<char> GetKeyValue(int index) => _keyValues[index];
    public int GetAggregateHash(int index) => _aggHashes[index];
    public ReadOnlySpan<char> GetAggregateValue(int index) => _aggValues[index];

    /// <summary>
    /// Converts this grouped row to a MaterializedRow with all key and aggregate bindings.
    /// </summary>
    public MaterializedRow ToMaterializedRow()
    {
        var bindings = new Binding[16];
        var buffer = new char[512];
        var table = new BindingTable(bindings, buffer);

        // Add group key bindings
        for (int i = 0; i < _keyCount; i++)
        {
            table.BindWithHash(_keyHashes[i], _keyValues[i]);
        }

        // Add aggregate bindings
        for (int i = 0; i < _aggCount; i++)
        {
            table.BindWithHash(_aggHashes[i], _aggValues[i]);
        }

        return new MaterializedRow(table);
    }

    /// <summary>
    /// Convert to MaterializedRow with an additional graph binding.
    /// Used for GRAPH ?g { SELECT (count(*) AS ?c) ... } queries where
    /// the graph variable needs to be bound alongside aggregate results.
    /// </summary>
    public MaterializedRow ToMaterializedRowWithGraphBinding(string graphVarName, string graphIri)
    {
        var bindings = new Binding[16];
        var buffer = new char[512];
        var table = new BindingTable(bindings, buffer);

        // Add graph variable binding first
        table.Bind(graphVarName.AsSpan(), graphIri.AsSpan());

        // Add group key bindings
        for (int i = 0; i < _keyCount; i++)
        {
            table.BindWithHash(_keyHashes[i], _keyValues[i]);
        }

        // Add aggregate bindings
        for (int i = 0; i < _aggCount; i++)
        {
            table.BindWithHash(_aggHashes[i], _aggValues[i]);
        }

        return new MaterializedRow(table);
    }

    private static int ComputeHash(ReadOnlySpan<char> s)
    {
        uint hash = 2166136261;
        foreach (var ch in s)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return (int)hash;
    }

    /// <summary>
    /// Try to parse a numeric value from an RDF literal string.
    /// Returns both double and decimal values for precision control.
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
