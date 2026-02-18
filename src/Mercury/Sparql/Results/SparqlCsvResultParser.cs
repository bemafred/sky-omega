// SparqlCsvResultParser.cs
// SPARQL Query Results CSV/TSV Format parser
// Based on W3C SPARQL 1.1 Query Results CSV and TSV Formats
// https://www.w3.org/TR/sparql11-results-csv-tsv/
// No external dependencies, only BCL
// .NET 10 / C# 14

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Results;

/// <summary>
/// Parses SPARQL Query Results in CSV or TSV format.
/// Follows W3C SPARQL 1.1 Query Results CSV and TSV Formats specification.
///
/// CSV format:
/// - First line contains variable names
/// - Values are comma-separated
/// - Quoted strings for values containing commas/quotes
/// - URIs are bare (no angle brackets)
/// - Blank nodes are _:label
/// - Literals are bare strings (no type info preserved in CSV)
///
/// TSV format:
/// - First line contains ?variable names (with ? prefix)
/// - Values are tab-separated
/// - URIs in angle brackets: &lt;uri&gt;
/// - Typed literals: "value"^^&lt;datatype&gt;
/// - Language literals: "value"@lang
/// </summary>
internal sealed class SparqlCsvResultParser : IDisposable, IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly bool _isTsv;
    private readonly char _delimiter;
    private bool _isDisposed;

    // Parsed results
    private string[]? _variables;
    private List<SparqlResultRow>? _rows;
    private bool _parsed;

    public SparqlCsvResultParser(Stream stream, bool isTsv = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _isTsv = isTsv;
        _delimiter = isTsv ? '\t' : ',';
    }

    /// <summary>
    /// Gets the variable names from the header row.
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

        using var reader = new StreamReader(_stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        ParseFromReader(reader);
        _parsed = true;
    }

    /// <summary>
    /// Parse the results asynchronously.
    /// </summary>
    public async Task ParseAsync(CancellationToken ct = default)
    {
        if (_parsed) return;

        using var reader = new StreamReader(_stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        await ParseFromReaderAsync(reader, ct).ConfigureAwait(false);
        _parsed = true;
    }

    private void EnsureParsed()
    {
        if (!_parsed)
            Parse();
    }

    private void ParseFromReader(StreamReader reader)
    {
        // Read header line
        var headerLine = reader.ReadLine();
        if (string.IsNullOrEmpty(headerLine))
        {
            _variables = Array.Empty<string>();
            _rows = new List<SparqlResultRow>();
            return;
        }

        _variables = ParseHeaderLine(headerLine);
        _rows = new List<SparqlResultRow>();

        // Read data lines
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var row = ParseDataLine(line, _variables);
            _rows.Add(row);
        }
    }

    private async Task ParseFromReaderAsync(StreamReader reader, CancellationToken ct)
    {
        // Read header line
        var headerLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(headerLine))
        {
            _variables = Array.Empty<string>();
            _rows = new List<SparqlResultRow>();
            return;
        }

        _variables = ParseHeaderLine(headerLine);
        _rows = new List<SparqlResultRow>();

        // Read data lines
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var row = ParseDataLine(line, _variables);
            _rows.Add(row);
        }
    }

    private string[] ParseHeaderLine(string line)
    {
        var fields = ParseLine(line);
        var vars = new string[fields.Count];

        for (int i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            // TSV has ?variable prefix, CSV does not
            if (field.StartsWith('?'))
                field = field.Substring(1);
            vars[i] = field;
        }

        return vars;
    }

    private SparqlResultRow ParseDataLine(string line, string[] variables)
    {
        var fields = ParseLine(line);
        var row = new SparqlResultRow();

        for (int i = 0; i < Math.Min(fields.Count, variables.Length); i++)
        {
            var field = fields[i];
            if (string.IsNullOrEmpty(field))
                continue; // Unbound variable

            var value = ParseValue(field);
            row.SetBinding(variables[i], value);
        }

        return row;
    }

    private List<string> ParseLine(string line)
    {
        // TSV: Simple tab split, no quote handling (quotes are part of SPARQL value syntax)
        if (_isTsv)
        {
            return new List<string>(line.Split('\t'));
        }

        // CSV: Handle quoted fields
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        int i = 0;

        while (i < line.Length)
        {
            char c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // Check for escaped quote
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i += 2;
                    }
                    else
                    {
                        inQuotes = false;
                        i++;
                    }
                }
                else
                {
                    current.Append(c);
                    i++;
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                    i++;
                }
                else if (c == _delimiter)
                {
                    fields.Add(current.ToString());
                    current.Clear();
                    i++;
                }
                else
                {
                    current.Append(c);
                    i++;
                }
            }
        }

        fields.Add(current.ToString());
        return fields;
    }

    private SparqlResultValue ParseValue(string field)
    {
        if (string.IsNullOrEmpty(field))
            return new SparqlResultValue(SparqlValueType.Literal, "");

        // TSV format preserves type information
        if (_isTsv)
        {
            return ParseTsvValue(field);
        }

        // CSV format - limited type information
        return ParseCsvValue(field);
    }

    private static SparqlResultValue ParseTsvValue(string field)
    {
        // URI: <http://...>
        if (field.StartsWith('<') && field.EndsWith('>'))
        {
            return new SparqlResultValue(SparqlValueType.Uri, field.Substring(1, field.Length - 2));
        }

        // Blank node: _:label
        if (field.StartsWith("_:"))
        {
            return new SparqlResultValue(SparqlValueType.BlankNode, field.Substring(2));
        }

        // Typed literal: "value"^^<datatype>
        if (field.StartsWith('"'))
        {
            return ParseQuotedLiteral(field);
        }

        // Plain value - treat as literal
        return new SparqlResultValue(SparqlValueType.Literal, field);
    }

    private static SparqlResultValue ParseCsvValue(string field)
    {
        // Blank node: _:label
        if (field.StartsWith("_:"))
        {
            return new SparqlResultValue(SparqlValueType.BlankNode, field.Substring(2));
        }

        // URI heuristic: starts with http:// or similar
        if (field.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            field.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            field.StartsWith("urn:", StringComparison.OrdinalIgnoreCase))
        {
            return new SparqlResultValue(SparqlValueType.Uri, field);
        }

        // Everything else is a literal in CSV
        return new SparqlResultValue(SparqlValueType.Literal, field);
    }

    private static SparqlResultValue ParseQuotedLiteral(string field)
    {
        // Find the closing quote
        int closeQuote = 1;
        var value = new StringBuilder();

        while (closeQuote < field.Length)
        {
            if (field[closeQuote] == '"')
            {
                // Check for escaped quote
                if (closeQuote + 1 < field.Length && field[closeQuote + 1] == '"')
                {
                    value.Append('"');
                    closeQuote += 2;
                }
                else
                {
                    closeQuote++;
                    break;
                }
            }
            else if (field[closeQuote] == '\\' && closeQuote + 1 < field.Length)
            {
                // Handle escape sequences
                var next = field[closeQuote + 1];
                switch (next)
                {
                    case 'n': value.Append('\n'); break;
                    case 'r': value.Append('\r'); break;
                    case 't': value.Append('\t'); break;
                    case '\\': value.Append('\\'); break;
                    case '"': value.Append('"'); break;
                    default: value.Append(next); break;
                }
                closeQuote += 2;
            }
            else
            {
                value.Append(field[closeQuote]);
                closeQuote++;
            }
        }

        var valueStr = value.ToString();
        string? datatype = null;
        string? language = null;

        // Check for language tag: "value"@lang
        if (closeQuote < field.Length && field[closeQuote] == '@')
        {
            language = field.Substring(closeQuote + 1);
        }
        // Check for datatype: "value"^^<datatype>
        else if (closeQuote + 1 < field.Length && field[closeQuote] == '^' && field[closeQuote + 1] == '^')
        {
            var dtPart = field.Substring(closeQuote + 2);
            if (dtPart.StartsWith('<') && dtPart.EndsWith('>'))
            {
                datatype = dtPart.Substring(1, dtPart.Length - 2);
            }
            else
            {
                datatype = dtPart;
            }
        }

        return new SparqlResultValue(SparqlValueType.Literal, valueStr, datatype, language);
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
