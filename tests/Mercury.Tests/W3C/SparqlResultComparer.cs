// Licensed under the MIT License.

using System.Text;

namespace SkyOmega.Mercury.Tests.W3C;

/// <summary>
/// Compares SPARQL result sets for equivalence.
/// Handles unordered result sets and blank node isomorphism.
/// </summary>
public static class SparqlResultComparer
{
    /// <summary>
    /// Compares two result sets for equivalence.
    /// Returns null if equivalent, or an error message describing the difference.
    /// </summary>
    /// <param name="expected">The expected result set.</param>
    /// <param name="actual">The actual result set.</param>
    /// <param name="ordered">If true, row order matters (for ORDER BY queries).</param>
    public static string? Compare(SparqlResultSet expected, SparqlResultSet actual, bool ordered = false)
    {
        // Handle boolean results (ASK queries)
        if (expected.IsBoolean || actual.IsBoolean)
        {
            if (!expected.IsBoolean)
                return "Expected SELECT results but got ASK result";
            if (!actual.IsBoolean)
                return "Expected ASK result but got SELECT results";
            if (expected.BooleanResult != actual.BooleanResult)
                return $"Boolean result mismatch: expected {expected.BooleanResult}, got {actual.BooleanResult}";
            return null;
        }

        // Check row counts first (quick fail)
        if (expected.Count != actual.Count)
        {
            return $"Row count mismatch: expected {expected.Count}, got {actual.Count}";
        }

        // Empty result sets are equivalent
        if (expected.Count == 0)
            return null;

        // Check that variable sets match (or actual has at least all expected variables)
        var missingVars = expected.Variables.Except(actual.Variables).ToList();
        if (missingVars.Any())
        {
            return $"Missing variables in actual results: {string.Join(", ", missingVars)}";
        }

        // Try to find an isomorphic mapping
        if (ordered)
        {
            return CompareOrdered(expected, actual);
        }
        else
        {
            return CompareUnordered(expected, actual);
        }
    }

    /// <summary>
    /// Compares result sets where order matters.
    /// </summary>
    private static string? CompareOrdered(SparqlResultSet expected, SparqlResultSet actual)
    {
        // Build blank node mapping as we go
        var bnodeMapping = new Dictionary<string, string>();

        for (int i = 0; i < expected.Count; i++)
        {
            var expRow = expected.Rows[i];
            var actRow = actual.Rows[i];

            var error = CompareRows(expRow, actRow, expected.Variables, bnodeMapping);
            if (error != null)
            {
                return $"Row {i}: {error}";
            }
        }

        return null;
    }

    /// <summary>
    /// Compares result sets where order doesn't matter.
    /// Uses backtracking search to find a valid blank node mapping.
    /// </summary>
    private static string? CompareUnordered(SparqlResultSet expected, SparqlResultSet actual)
    {
        // Group rows by structural hash for faster matching
        var actualByHash = actual.Rows
            .Select((row, index) => (row, index))
            .GroupBy(x => x.row.GetStructuralHashCode())
            .ToDictionary(g => g.Key, g => g.ToList());

        // Track which actual rows have been matched
        var matchedActual = new bool[actual.Count];
        var bnodeMapping = new Dictionary<string, string>();

        // Try to match each expected row to an actual row
        if (TryMatchRows(expected.Rows, 0, actualByHash, matchedActual, expected.Variables, bnodeMapping))
        {
            return null; // All rows matched
        }

        // Failed to find a complete matching - generate helpful error message
        return GenerateMismatchMessage(expected, actual, expected.Variables);
    }

    /// <summary>
    /// Recursive backtracking search for row matching with blank node isomorphism.
    /// </summary>
    private static bool TryMatchRows(
        IReadOnlyList<SparqlResultRow> expectedRows,
        int expectedIndex,
        Dictionary<int, List<(SparqlResultRow row, int index)>> actualByHash,
        bool[] matchedActual,
        IReadOnlyList<string> variables,
        Dictionary<string, string> bnodeMapping)
    {
        if (expectedIndex >= expectedRows.Count)
            return true; // All expected rows matched

        var expRow = expectedRows[expectedIndex];
        var hash = expRow.GetStructuralHashCode();

        // Try rows with matching structural hash first
        if (actualByHash.TryGetValue(hash, out var candidates))
        {
            foreach (var (actRow, actIndex) in candidates)
            {
                if (matchedActual[actIndex])
                    continue; // Already matched

                // Save current mapping state for backtracking
                var mappingSnapshot = new Dictionary<string, string>(bnodeMapping);

                if (RowsMatch(expRow, actRow, variables, bnodeMapping))
                {
                    matchedActual[actIndex] = true;

                    if (TryMatchRows(expectedRows, expectedIndex + 1, actualByHash, matchedActual, variables, bnodeMapping))
                    {
                        return true; // Found complete matching
                    }

                    // Backtrack
                    matchedActual[actIndex] = false;
                    bnodeMapping.Clear();
                    foreach (var kv in mappingSnapshot)
                        bnodeMapping[kv.Key] = kv.Value;
                }
            }
        }

        // Try all unmatched rows (hash collision or different structure)
        for (int i = 0; i < matchedActual.Length; i++)
        {
            if (matchedActual[i])
                continue;

            // Skip if we already tried this row via hash match
            if (actualByHash.TryGetValue(hash, out var hashCandidates) &&
                hashCandidates.Any(c => c.index == i))
                continue;

            var mappingSnapshot = new Dictionary<string, string>(bnodeMapping);

            // Get the actual row - need to iterate through actualByHash values
            SparqlResultRow? actRow = null;
            foreach (var group in actualByHash.Values)
            {
                var match = group.FirstOrDefault(x => x.index == i);
                if (match.row != null)
                {
                    actRow = match.row;
                    break;
                }
            }

            if (actRow == null)
                continue;

            if (RowsMatch(expRow, actRow, variables, bnodeMapping))
            {
                matchedActual[i] = true;

                if (TryMatchRows(expectedRows, expectedIndex + 1, actualByHash, matchedActual, variables, bnodeMapping))
                {
                    return true;
                }

                matchedActual[i] = false;
                bnodeMapping.Clear();
                foreach (var kv in mappingSnapshot)
                    bnodeMapping[kv.Key] = kv.Value;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if two rows match, potentially updating blank node mapping.
    /// </summary>
    private static bool RowsMatch(
        SparqlResultRow expected,
        SparqlResultRow actual,
        IReadOnlyList<string> variables,
        Dictionary<string, string> bnodeMapping)
    {
        foreach (var varName in variables)
        {
            var expBinding = expected[varName];
            var actBinding = actual[varName];

            if (!BindingsMatch(expBinding, actBinding, bnodeMapping))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if two bindings match, handling blank node isomorphism.
    /// </summary>
    private static bool BindingsMatch(
        SparqlBinding expected,
        SparqlBinding actual,
        Dictionary<string, string> bnodeMapping)
    {
        // Both unbound
        if (expected.Type == RdfTermType.Unbound && actual.Type == RdfTermType.Unbound)
            return true;

        // One unbound, other bound - allow actual to have extra bindings
        if (expected.Type == RdfTermType.Unbound)
            return true; // Expected unbound, actual has value - OK (extra binding)

        if (actual.Type == RdfTermType.Unbound)
            return false; // Expected bound, actual unbound - mismatch

        // Types must match
        if (expected.Type != actual.Type)
            return false;

        // URIs must match exactly
        if (expected.Type == RdfTermType.Uri)
            return expected.Value == actual.Value;

        // Blank nodes use isomorphism mapping
        if (expected.Type == RdfTermType.BNode)
        {
            if (bnodeMapping.TryGetValue(expected.Value, out var mappedLabel))
            {
                // Expected blank node already mapped - check consistency
                return mappedLabel == actual.Value;
            }

            // Check if actual label is already mapped to a different expected label
            if (bnodeMapping.ContainsValue(actual.Value))
            {
                // This actual label is already used for a different expected label
                return false;
            }

            // New mapping
            bnodeMapping[expected.Value] = actual.Value;
            return true;
        }

        // Literals must match value, datatype, and language
        if (expected.Type == RdfTermType.Literal)
        {
            if (expected.Value != actual.Value)
                return false;

            // Normalize datatype comparison
            var expDatatype = NormalizeDatatype(expected.Datatype);
            var actDatatype = NormalizeDatatype(actual.Datatype);

            // Allow numeric type flexibility - if values are numerically equal,
            // treat plain literals as compatible with numeric types
            if (expDatatype != actDatatype)
            {
                if (!AreNumericTypesCompatible(expected.Value, expDatatype, actDatatype))
                    return false;
            }

            // Language tags are case-insensitive
            var expLang = expected.Language?.ToLowerInvariant();
            var actLang = actual.Language?.ToLowerInvariant();
            if (expLang != actLang)
                return false;

            return true;
        }

        return false;
    }

    /// <summary>
    /// Normalizes datatype IRIs for comparison.
    /// </summary>
    private static string? NormalizeDatatype(string? datatype)
    {
        if (string.IsNullOrEmpty(datatype))
            return null;

        // Common XSD datatype shortcuts
        if (datatype == "http://www.w3.org/2001/XMLSchema#string")
            return null; // xsd:string is the default, equivalent to no datatype

        return datatype;
    }

    /// <summary>
    /// Checks if numeric types are compatible for comparison.
    /// Allows plain literals to match numeric types if the value parses correctly.
    /// </summary>
    private static bool AreNumericTypesCompatible(string value, string? expectedType, string? actualType)
    {
        // XSD numeric type IRIs
        const string xsdInteger = "http://www.w3.org/2001/XMLSchema#integer";
        const string xsdDecimal = "http://www.w3.org/2001/XMLSchema#decimal";
        const string xsdDouble = "http://www.w3.org/2001/XMLSchema#double";
        const string xsdFloat = "http://www.w3.org/2001/XMLSchema#float";

        var numericTypes = new HashSet<string?>
        {
            xsdInteger, xsdDecimal, xsdDouble, xsdFloat, null // null = plain literal
        };

        // If both types are in the numeric family, allow compatibility if value parses
        if (numericTypes.Contains(expectedType) && numericTypes.Contains(actualType))
        {
            // For integer expected, value must parse as integer
            if (expectedType == xsdInteger)
                return long.TryParse(value, out _);

            // For decimal/double/float, value must parse as number
            return double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _);
        }

        // Boolean compatibility
        const string xsdBoolean = "http://www.w3.org/2001/XMLSchema#boolean";
        if ((expectedType == xsdBoolean && actualType == null) ||
            (expectedType == null && actualType == xsdBoolean))
        {
            return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("false", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Compares two rows and returns an error message if they don't match.
    /// </summary>
    private static string? CompareRows(
        SparqlResultRow expected,
        SparqlResultRow actual,
        IReadOnlyList<string> variables,
        Dictionary<string, string> bnodeMapping)
    {
        foreach (var varName in variables)
        {
            var expBinding = expected[varName];
            var actBinding = actual[varName];

            if (!BindingsMatch(expBinding, actBinding, bnodeMapping))
            {
                return $"?{varName}: expected {expBinding}, got {actBinding}";
            }
        }

        return null;
    }

    /// <summary>
    /// Generates a helpful mismatch message showing differences.
    /// </summary>
    private static string GenerateMismatchMessage(
        SparqlResultSet expected,
        SparqlResultSet actual,
        IReadOnlyList<string> variables)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Result sets do not match.");
        sb.AppendLine();
        sb.AppendLine("Expected rows:");
        foreach (var row in expected.Rows.Take(5))
        {
            sb.AppendLine($"  {row}");
        }
        if (expected.Count > 5)
            sb.AppendLine($"  ... ({expected.Count - 5} more)");

        sb.AppendLine();
        sb.AppendLine("Actual rows:");
        foreach (var row in actual.Rows.Take(5))
        {
            sb.AppendLine($"  {row}");
        }
        if (actual.Count > 5)
            sb.AppendLine($"  ... ({actual.Count - 5} more)");

        return sb.ToString();
    }

    /// <summary>
    /// Compares two RDF graphs for isomorphism (for CONSTRUCT/DESCRIBE results).
    /// </summary>
    public static string? CompareGraphs(SparqlGraphResult expected, SparqlGraphResult actual)
    {
        if (expected.Count != actual.Count)
        {
            return $"Triple count mismatch: expected {expected.Count}, got {actual.Count}";
        }

        if (expected.Count == 0)
            return null;

        // Try to find isomorphic mapping
        var bnodeMapping = new Dictionary<string, string>();
        var matchedActual = new bool[actual.Count];

        foreach (var expTriple in expected.Triples)
        {
            bool found = false;
            for (int i = 0; i < actual.Count; i++)
            {
                if (matchedActual[i])
                    continue;

                var actTriple = actual.Triples[i];
                var mappingSnapshot = new Dictionary<string, string>(bnodeMapping);

                if (BindingsMatch(expTriple.Subject, actTriple.Subject, bnodeMapping) &&
                    BindingsMatch(expTriple.Predicate, actTriple.Predicate, bnodeMapping) &&
                    BindingsMatch(expTriple.Object, actTriple.Object, bnodeMapping))
                {
                    matchedActual[i] = true;
                    found = true;
                    break;
                }

                // Restore mapping on mismatch
                bnodeMapping.Clear();
                foreach (var kv in mappingSnapshot)
                    bnodeMapping[kv.Key] = kv.Value;
            }

            if (!found)
            {
                return $"No matching triple found for: {expTriple.Subject} {expTriple.Predicate} {expTriple.Object}";
            }
        }

        return null;
    }
}
