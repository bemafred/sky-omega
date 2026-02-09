// SparqlJsonResultParser.cs
// SPARQL Query Results JSON Format parser
// Based on W3C SPARQL 1.1 Query Results JSON Format
// https://www.w3.org/TR/sparql11-results-json/
// No external dependencies, only BCL
// .NET 10 / C# 14

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Results;

/// <summary>
/// Represents a SPARQL result value with type information.
/// </summary>
public readonly struct SparqlResultValue
{
    /// <summary>The value type (uri, literal, bnode).</summary>
    public readonly SparqlValueType Type;

    /// <summary>The value string.</summary>
    public readonly string Value;

    /// <summary>The datatype IRI for typed literals (null if not typed).</summary>
    public readonly string? Datatype;

    /// <summary>The language tag for language-tagged literals (null if not tagged).</summary>
    public readonly string? Language;

    public SparqlResultValue(SparqlValueType type, string value, string? datatype = null, string? language = null)
    {
        Type = type;
        Value = value;
        Datatype = datatype;
        Language = language;
    }

    /// <summary>
    /// Convert to N-Triples/SPARQL term format.
    /// </summary>
    public string ToTermString()
    {
        return Type switch
        {
            SparqlValueType.Uri => $"<{Value}>",
            SparqlValueType.BlankNode => Value.StartsWith("_:") ? Value : $"_:{Value}",
            SparqlValueType.Literal when !string.IsNullOrEmpty(Language) => $"\"{EscapeString(Value)}\"@{Language}",
            SparqlValueType.Literal when !string.IsNullOrEmpty(Datatype) => $"\"{EscapeString(Value)}\"^^<{Datatype}>",
            SparqlValueType.Literal => $"\"{EscapeString(Value)}\"",
            _ => Value
        };
    }

    private static string EscapeString(string value)
    {
        if (value.IndexOfAny(['"', '\\', '\n', '\r', '\t']) < 0)
            return value;

        var sb = new System.Text.StringBuilder(value.Length + 10);
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    public override string ToString() => ToTermString();
}

/// <summary>
/// SPARQL value types.
/// </summary>
public enum SparqlValueType
{
    /// <summary>URI/IRI value.</summary>
    Uri,

    /// <summary>Literal value.</summary>
    Literal,

    /// <summary>Blank node.</summary>
    BlankNode
}

/// <summary>
/// Represents a single SPARQL result row (binding).
/// </summary>
public sealed class SparqlResultRow
{
    private readonly Dictionary<string, SparqlResultValue> _bindings;

    internal SparqlResultRow()
    {
        _bindings = new Dictionary<string, SparqlResultValue>(StringComparer.Ordinal);
    }

    internal void SetBinding(string variable, SparqlResultValue value)
    {
        _bindings[variable] = value;
    }

    internal void Clear()
    {
        _bindings.Clear();
    }

    /// <summary>
    /// Get the value bound to a variable.
    /// </summary>
    /// <param name="variable">Variable name (with or without leading ?).</param>
    /// <param name="value">The bound value.</param>
    /// <returns>True if the variable is bound.</returns>
    public bool TryGetValue(string variable, out SparqlResultValue value)
    {
        // Strip leading ? if present
        if (variable.StartsWith('?'))
            variable = variable.Substring(1);

        return _bindings.TryGetValue(variable, out value);
    }

    /// <summary>
    /// Get the value bound to a variable.
    /// </summary>
    /// <param name="variable">Variable name (with or without leading ?).</param>
    /// <returns>The bound value, or null if not bound.</returns>
    public SparqlResultValue? GetValue(string variable)
    {
        return TryGetValue(variable, out var value) ? value : null;
    }

    /// <summary>
    /// Check if a variable is bound.
    /// </summary>
    public bool IsBound(string variable)
    {
        if (variable.StartsWith('?'))
            variable = variable.Substring(1);
        return _bindings.ContainsKey(variable);
    }

    /// <summary>
    /// Get all bound variable names.
    /// </summary>
    public IEnumerable<string> BoundVariables => _bindings.Keys;

    /// <summary>
    /// Get the number of bound variables.
    /// </summary>
    public int Count => _bindings.Count;
}

/// <summary>
/// Parses SPARQL Query Results in JSON format.
/// Follows W3C SPARQL 1.1 Query Results JSON Format specification.
///
/// Input format:
/// {
///   "head": { "vars": ["s", "p", "o"] },
///   "results": {
///     "bindings": [
///       { "s": { "type": "uri", "value": "..." }, ... },
///       ...
///     ]
///   }
/// }
///
/// Or for ASK queries:
/// {
///   "head": {},
///   "boolean": true
/// }
/// </summary>
public sealed class SparqlJsonResultParser : IDisposable, IAsyncDisposable
{
    private readonly Stream _stream;
    private bool _isDisposed;

    // Parsed results
    private string[]? _variables;
    private List<SparqlResultRow>? _rows;
    private bool? _booleanResult;
    private bool _parsed;

    public SparqlJsonResultParser(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <summary>
    /// Gets the variable names from the result head.
    /// </summary>
    public IReadOnlyList<string> Variables
    {
        get
        {
            EnsureParsed();
            return _variables ?? Array.Empty<string>();
        }
    }

    /// <summary>
    /// Gets the boolean result for ASK queries.
    /// Returns null for SELECT queries.
    /// </summary>
    public bool? BooleanResult
    {
        get
        {
            EnsureParsed();
            return _booleanResult;
        }
    }

    /// <summary>
    /// Gets whether this is an ASK query result.
    /// </summary>
    public bool IsAskResult
    {
        get
        {
            EnsureParsed();
            return _booleanResult.HasValue;
        }
    }

    /// <summary>
    /// Gets the result rows.
    /// </summary>
    public IReadOnlyList<SparqlResultRow> Rows
    {
        get
        {
            EnsureParsed();
            return _rows ?? (IReadOnlyList<SparqlResultRow>)Array.Empty<SparqlResultRow>();
        }
    }

    /// <summary>
    /// Parse the results synchronously.
    /// </summary>
    public void Parse()
    {
        if (_parsed) return;

        using var ms = new MemoryStream();
        _stream.CopyTo(ms);
        var bytes = ms.ToArray();

        ParseJson(bytes);
        _parsed = true;
    }

    /// <summary>
    /// Parse the results asynchronously.
    /// </summary>
    public async Task ParseAsync(CancellationToken ct = default)
    {
        if (_parsed) return;

        using var ms = new MemoryStream();
        await _stream.CopyToAsync(ms, ct).ConfigureAwait(false);
        var bytes = ms.ToArray();

        ParseJson(bytes);
        _parsed = true;
    }

    private void EnsureParsed()
    {
        if (!_parsed)
            Parse();
    }

    private void ParseJson(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;

        // Parse head
        if (root.TryGetProperty("head", out var headElement))
        {
            if (headElement.TryGetProperty("vars", out var varsElement))
            {
                var vars = new List<string>();
                foreach (var v in varsElement.EnumerateArray())
                {
                    vars.Add(v.GetString() ?? "");
                }
                _variables = vars.ToArray();
            }
        }

        // Check for boolean result (ASK query)
        if (root.TryGetProperty("boolean", out var boolElement))
        {
            _booleanResult = boolElement.GetBoolean();
            return;
        }

        // Parse results
        if (root.TryGetProperty("results", out var resultsElement))
        {
            if (resultsElement.TryGetProperty("bindings", out var bindingsElement))
            {
                _rows = new List<SparqlResultRow>();

                foreach (var binding in bindingsElement.EnumerateArray())
                {
                    var row = new SparqlResultRow();

                    foreach (var prop in binding.EnumerateObject())
                    {
                        var varName = prop.Name;
                        var value = ParseValue(prop.Value);
                        row.SetBinding(varName, value);
                    }

                    _rows.Add(row);
                }
            }
        }
    }

    private static SparqlResultValue ParseValue(JsonElement element)
    {
        var type = SparqlValueType.Literal;
        var value = "";
        string? datatype = null;
        string? language = null;

        if (element.TryGetProperty("type", out var typeElement))
        {
            var typeStr = typeElement.GetString();
            type = typeStr switch
            {
                "uri" => SparqlValueType.Uri,
                "bnode" => SparqlValueType.BlankNode,
                "literal" => SparqlValueType.Literal,
                "typed-literal" => SparqlValueType.Literal,
                _ => SparqlValueType.Literal
            };
        }

        if (element.TryGetProperty("value", out var valueElement))
        {
            value = valueElement.GetString() ?? "";
        }

        if (element.TryGetProperty("datatype", out var datatypeElement))
        {
            datatype = datatypeElement.GetString();
        }

        if (element.TryGetProperty("xml:lang", out var langElement))
        {
            language = langElement.GetString();
        }

        return new SparqlResultValue(type, value, datatype, language);
    }

    /// <summary>
    /// Enumerate results using callback (streaming pattern).
    /// </summary>
    public void ForEach(Action<SparqlResultRow> handler)
    {
        EnsureParsed();
        if (_rows == null) return;

        foreach (var row in _rows)
        {
            handler(row);
        }
    }

    /// <summary>
    /// Enumerate results asynchronously.
    /// </summary>
    public async IAsyncEnumerable<SparqlResultRow> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await ParseAsync(ct).ConfigureAwait(false);

        if (_rows == null) yield break;

        foreach (var row in _rows)
        {
            yield return row;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
