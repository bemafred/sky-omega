// SparqlXmlResultParser.cs
// SPARQL Query Results XML Format parser
// Based on W3C SPARQL Query Results XML Format
// https://www.w3.org/TR/rdf-sparql-XMLres/
// No external dependencies, only BCL
// .NET 10 / C# 14

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Results;

/// <summary>
/// Parses SPARQL Query Results in XML format.
/// Follows W3C SPARQL Query Results XML Format specification.
///
/// Input format:
/// &lt;sparql xmlns="http://www.w3.org/2005/sparql-results#"&gt;
///   &lt;head&gt;
///     &lt;variable name="s"/&gt;
///     &lt;variable name="p"/&gt;
///   &lt;/head&gt;
///   &lt;results&gt;
///     &lt;result&gt;
///       &lt;binding name="s"&gt;&lt;uri&gt;...&lt;/uri&gt;&lt;/binding&gt;
///       ...
///     &lt;/result&gt;
///   &lt;/results&gt;
/// &lt;/sparql&gt;
///
/// Or for ASK queries:
/// &lt;sparql&gt;
///   &lt;head/&gt;
///   &lt;boolean&gt;true&lt;/boolean&gt;
/// &lt;/sparql&gt;
/// </summary>
internal sealed class SparqlXmlResultParser : IDisposable, IAsyncDisposable
{
    private readonly Stream _stream;
    private bool _isDisposed;

    // Parsed results
    private string[]? _variables;
    private List<SparqlResultRow>? _rows;
    private bool? _booleanResult;
    private bool _parsed;

    private const string SparqlNs = "http://www.w3.org/2005/sparql-results#";

    public SparqlXmlResultParser(Stream stream)
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

        ParseXml();
        _parsed = true;
    }

    /// <summary>
    /// Parse the results asynchronously.
    /// </summary>
    public async Task ParseAsync(CancellationToken ct = default)
    {
        if (_parsed) return;

        // XmlReader doesn't have great async support for full document parsing
        // Read stream to memory first
        using var ms = new MemoryStream();
        await _stream.CopyToAsync(ms, ct).ConfigureAwait(false);
        ms.Position = 0;

        ParseXmlFromStream(ms);
        _parsed = true;
    }

    private void EnsureParsed()
    {
        if (!_parsed)
            Parse();
    }

    private void ParseXml()
    {
        ParseXmlFromStream(_stream);
    }

    private void ParseXmlFromStream(Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            IgnoreWhitespace = true,
            IgnoreComments = true,
            DtdProcessing = DtdProcessing.Ignore
        };

        using var reader = XmlReader.Create(stream, settings);

        var variables = new List<string>();
        _rows = new List<SparqlResultRow>();

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
                continue;

            var localName = reader.LocalName;

            if (localName == "variable")
            {
                var name = reader.GetAttribute("name");
                if (!string.IsNullOrEmpty(name))
                    variables.Add(name);
            }
            else if (localName == "boolean")
            {
                var content = reader.ReadElementContentAsString();
                _booleanResult = content.Equals("true", StringComparison.OrdinalIgnoreCase);
                break;
            }
            else if (localName == "result")
            {
                var row = ParseResult(reader);
                _rows.Add(row);
            }
        }

        _variables = variables.ToArray();
    }

    private static SparqlResultRow ParseResult(XmlReader reader)
    {
        var row = new SparqlResultRow();
        var depth = reader.Depth;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                break;

            if (reader.NodeType != XmlNodeType.Element)
                continue;

            if (reader.LocalName == "binding")
            {
                var varName = reader.GetAttribute("name");
                if (!string.IsNullOrEmpty(varName))
                {
                    var value = ParseBinding(reader);
                    row.SetBinding(varName, value);
                }
            }
        }

        return row;
    }

    private static SparqlResultValue ParseBinding(XmlReader reader)
    {
        var bindingDepth = reader.Depth;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == bindingDepth)
                break;

            if (reader.NodeType != XmlNodeType.Element)
                continue;

            var localName = reader.LocalName;

            if (localName == "uri")
            {
                var value = reader.ReadElementContentAsString();
                return new SparqlResultValue(SparqlValueType.Uri, value);
            }
            else if (localName == "bnode")
            {
                var value = reader.ReadElementContentAsString();
                return new SparqlResultValue(SparqlValueType.BlankNode, value);
            }
            else if (localName == "literal")
            {
                var datatype = reader.GetAttribute("datatype");
                var language = reader.GetAttribute("lang", "http://www.w3.org/XML/1998/namespace");
                var value = reader.ReadElementContentAsString();
                return new SparqlResultValue(SparqlValueType.Literal, value, datatype, language);
            }
        }

        return new SparqlResultValue(SparqlValueType.Literal, "");
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
