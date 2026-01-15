// Licensed under the MIT License.

using System.Text.Json;
using System.Xml.Linq;

namespace SkyOmega.Mercury.Tests.W3C;

/// <summary>
/// Parses SPARQL query results from various formats (XML, JSON, CSV, TSV).
/// Implements W3C SPARQL Query Results formats.
/// </summary>
public static class SparqlResultParser
{
    // XML namespace for SPARQL Results
    private static readonly XNamespace SparqlNs = "http://www.w3.org/2005/sparql-results#";

    /// <summary>
    /// Parses SPARQL results from a file, auto-detecting format from extension.
    /// </summary>
    public static async Task<SparqlResultSet> ParseFileAsync(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var content = await File.ReadAllTextAsync(path);

        return extension switch
        {
            ".srx" or ".xml" => ParseXml(content),
            ".srj" or ".json" => ParseJson(content),
            ".csv" => ParseCsv(content),
            ".tsv" => ParseTsv(content),
            _ => throw new NotSupportedException($"Unsupported result format: {extension}")
        };
    }

    /// <summary>
    /// Parses SPARQL Results XML format (application/sparql-results+xml).
    /// See: https://www.w3.org/TR/rdf-sparql-XMLres/
    /// </summary>
    public static SparqlResultSet ParseXml(string xml)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root;

        if (root == null)
            return SparqlResultSet.Empty();

        // Handle both namespaced and non-namespaced elements
        var sparqlElement = root.Name.LocalName == "sparql" ? root : null;
        if (sparqlElement == null)
            return SparqlResultSet.Empty();

        // Check for boolean result (ASK query)
        var booleanElement = sparqlElement.Element(SparqlNs + "boolean")
                          ?? sparqlElement.Element("boolean");
        if (booleanElement != null)
        {
            var value = booleanElement.Value.Trim().ToLowerInvariant();
            return SparqlResultSet.Boolean(value == "true" || value == "1");
        }

        var resultSet = new SparqlResultSet();

        // Parse head/variables
        var head = sparqlElement.Element(SparqlNs + "head")
                ?? sparqlElement.Element("head");
        if (head != null)
        {
            foreach (var varElement in head.Elements(SparqlNs + "variable")
                                          .Concat(head.Elements("variable")))
            {
                var name = varElement.Attribute("name")?.Value;
                if (name != null)
                    resultSet.AddVariable(name);
            }
        }

        // Parse results/bindings
        var results = sparqlElement.Element(SparqlNs + "results")
                   ?? sparqlElement.Element("results");
        if (results != null)
        {
            foreach (var resultElement in results.Elements(SparqlNs + "result")
                                                .Concat(results.Elements("result")))
            {
                var row = new SparqlResultRow();

                foreach (var bindingElement in resultElement.Elements(SparqlNs + "binding")
                                                           .Concat(resultElement.Elements("binding")))
                {
                    var varName = bindingElement.Attribute("name")?.Value;
                    if (varName == null) continue;

                    var binding = ParseXmlBinding(bindingElement);
                    row.Set(varName, binding);
                }

                resultSet.AddRow(row);
            }
        }

        return resultSet;
    }

    private static SparqlBinding ParseXmlBinding(XElement bindingElement)
    {
        // Try URI
        var uri = bindingElement.Element(SparqlNs + "uri")
               ?? bindingElement.Element("uri");
        if (uri != null)
            return SparqlBinding.Uri(uri.Value);

        // Try BNode
        var bnode = bindingElement.Element(SparqlNs + "bnode")
                 ?? bindingElement.Element("bnode");
        if (bnode != null)
            return SparqlBinding.BNode(bnode.Value);

        // Try Literal
        var literal = bindingElement.Element(SparqlNs + "literal")
                   ?? bindingElement.Element("literal");
        if (literal != null)
        {
            var value = literal.Value;
            var datatype = literal.Attribute("datatype")?.Value;
            var lang = literal.Attribute(XNamespace.Xml + "lang")?.Value;

            if (lang != null)
                return SparqlBinding.LangLiteral(value, lang);
            if (datatype != null)
                return SparqlBinding.TypedLiteral(value, datatype);
            return SparqlBinding.Literal(value);
        }

        return SparqlBinding.Unbound;
    }

    /// <summary>
    /// Parses SPARQL Results JSON format (application/sparql-results+json).
    /// See: https://www.w3.org/TR/sparql11-results-json/
    /// </summary>
    public static SparqlResultSet ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Check for boolean result (ASK query)
        if (root.TryGetProperty("boolean", out var booleanProp))
        {
            return SparqlResultSet.Boolean(booleanProp.GetBoolean());
        }

        var resultSet = new SparqlResultSet();

        // Parse head/vars
        if (root.TryGetProperty("head", out var head) &&
            head.TryGetProperty("vars", out var vars))
        {
            foreach (var varElement in vars.EnumerateArray())
            {
                var name = varElement.GetString();
                if (name != null)
                    resultSet.AddVariable(name);
            }
        }

        // Parse results/bindings
        if (root.TryGetProperty("results", out var results) &&
            results.TryGetProperty("bindings", out var bindings))
        {
            foreach (var bindingElement in bindings.EnumerateArray())
            {
                var row = new SparqlResultRow();

                foreach (var prop in bindingElement.EnumerateObject())
                {
                    var varName = prop.Name;
                    var binding = ParseJsonBinding(prop.Value);
                    row.Set(varName, binding);
                }

                resultSet.AddRow(row);
            }
        }

        return resultSet;
    }

    private static SparqlBinding ParseJsonBinding(JsonElement element)
    {
        if (!element.TryGetProperty("type", out var typeProp))
            return SparqlBinding.Unbound;

        var type = typeProp.GetString();
        var value = element.TryGetProperty("value", out var valueProp)
            ? valueProp.GetString() ?? ""
            : "";

        return type switch
        {
            "uri" => SparqlBinding.Uri(value),
            "bnode" => SparqlBinding.BNode(value),
            "literal" or "typed-literal" => ParseJsonLiteral(element, value),
            _ => SparqlBinding.Unbound
        };
    }

    private static SparqlBinding ParseJsonLiteral(JsonElement element, string value)
    {
        // Check for language tag
        if (element.TryGetProperty("xml:lang", out var langProp))
        {
            var lang = langProp.GetString();
            if (lang != null)
                return SparqlBinding.LangLiteral(value, lang);
        }

        // Check for datatype
        if (element.TryGetProperty("datatype", out var dtProp))
        {
            var datatype = dtProp.GetString();
            if (datatype != null)
                return SparqlBinding.TypedLiteral(value, datatype);
        }

        return SparqlBinding.Literal(value);
    }

    /// <summary>
    /// Parses SPARQL Results CSV format (text/csv).
    /// Note: CSV format loses type information (everything becomes a string).
    /// </summary>
    public static SparqlResultSet ParseCsv(string csv)
    {
        var resultSet = new SparqlResultSet();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length == 0)
            return resultSet;

        // First line is header with variable names
        var headers = ParseCsvLine(lines[0]);
        foreach (var header in headers)
        {
            // Remove leading ? if present
            var varName = header.StartsWith('?') ? header[1..] : header;
            resultSet.AddVariable(varName);
        }

        // Remaining lines are data rows
        for (int i = 1; i < lines.Length; i++)
        {
            var values = ParseCsvLine(lines[i]);
            var row = new SparqlResultRow();

            for (int j = 0; j < Math.Min(headers.Length, values.Length); j++)
            {
                var varName = headers[j].StartsWith('?') ? headers[j][1..] : headers[j];
                var value = values[j];

                if (string.IsNullOrEmpty(value))
                {
                    row.Set(varName, SparqlBinding.Unbound);
                }
                else if (value.StartsWith('<') && value.EndsWith('>'))
                {
                    // IRI
                    row.Set(varName, SparqlBinding.Uri(value[1..^1]));
                }
                else if (value.StartsWith("_:"))
                {
                    // Blank node
                    row.Set(varName, SparqlBinding.BNode(value[2..]));
                }
                else
                {
                    // Treat as literal (CSV loses type info)
                    row.Set(varName, SparqlBinding.Literal(value));
                }
            }

            resultSet.AddRow(row);
        }

        return resultSet;
    }

    private static string[] ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    // Escaped quote
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                values.Add(current.ToString().Trim());
                current.Clear();
            }
            else if (c != '\r')
            {
                current.Append(c);
            }
        }

        values.Add(current.ToString().Trim());
        return values.ToArray();
    }

    /// <summary>
    /// Parses SPARQL Results TSV format (text/tab-separated-values).
    /// TSV preserves RDF term syntax (angle brackets, quotes, etc.).
    /// </summary>
    public static SparqlResultSet ParseTsv(string tsv)
    {
        var resultSet = new SparqlResultSet();
        var lines = tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length == 0)
            return resultSet;

        // First line is header with variable names (prefixed with ?)
        var headers = lines[0].Split('\t');
        foreach (var header in headers)
        {
            var trimmed = header.Trim();
            var varName = trimmed.StartsWith('?') ? trimmed[1..] : trimmed;
            resultSet.AddVariable(varName);
        }

        // Remaining lines are data rows
        for (int i = 1; i < lines.Length; i++)
        {
            var values = lines[i].Split('\t');
            var row = new SparqlResultRow();

            for (int j = 0; j < Math.Min(headers.Length, values.Length); j++)
            {
                var varName = headers[j].Trim();
                if (varName.StartsWith('?'))
                    varName = varName[1..];

                var value = values[j].Trim();
                var binding = ParseTsvValue(value);
                row.Set(varName, binding);
            }

            resultSet.AddRow(row);
        }

        return resultSet;
    }

    private static SparqlBinding ParseTsvValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return SparqlBinding.Unbound;

        // IRI: <http://example.org/resource>
        if (value.StartsWith('<') && value.EndsWith('>'))
            return SparqlBinding.Uri(value[1..^1]);

        // Blank node: _:label
        if (value.StartsWith("_:"))
            return SparqlBinding.BNode(value[2..]);

        // Literal: "value", "value"@lang, "value"^^<datatype>
        if (value.StartsWith('"'))
        {
            // Find the closing quote (handling escaped quotes)
            int closeQuote = FindClosingQuote(value);
            if (closeQuote > 0)
            {
                var literalValue = UnescapeString(value[1..closeQuote]);
                var suffix = value[(closeQuote + 1)..];

                if (suffix.StartsWith('@'))
                {
                    return SparqlBinding.LangLiteral(literalValue, suffix[1..]);
                }

                if (suffix.StartsWith("^^"))
                {
                    var datatype = suffix[2..];
                    if (datatype.StartsWith('<') && datatype.EndsWith('>'))
                        datatype = datatype[1..^1];
                    return SparqlBinding.TypedLiteral(literalValue, datatype);
                }

                return SparqlBinding.Literal(literalValue);
            }
        }

        // Plain value (unquoted) - treat as literal
        return SparqlBinding.Literal(value);
    }

    private static int FindClosingQuote(string value)
    {
        for (int i = 1; i < value.Length; i++)
        {
            if (value[i] == '"' && value[i - 1] != '\\')
                return i;
        }
        return -1;
    }

    private static string UnescapeString(string value)
    {
        if (!value.Contains('\\'))
            return value;

        var sb = new System.Text.StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[i + 1];
                switch (next)
                {
                    case 'n': sb.Append('\n'); i++; break;
                    case 'r': sb.Append('\r'); i++; break;
                    case 't': sb.Append('\t'); i++; break;
                    case '"': sb.Append('"'); i++; break;
                    case '\\': sb.Append('\\'); i++; break;
                    default: sb.Append(value[i]); break;
                }
            }
            else
            {
                sb.Append(value[i]);
            }
        }
        return sb.ToString();
    }
}
