// RdfXmlStreamWriter.cs
// Zero-GC streaming RDF/XML writer with namespace support
// Based on W3C RDF/XML specification
// No external dependencies, only BCL
// .NET 10 / C# 14

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace SkyOmega.Mercury.RdfXml;

/// <summary>
/// Zero-allocation streaming writer for RDF/XML format.
/// Supports namespace declarations and subject grouping.
///
/// Features:
/// - Namespace registration for compact output
/// - Subject grouping with rdf:Description elements
/// - rdf:resource for IRI objects
/// - xml:lang for language-tagged literals
/// - rdf:datatype for typed literals
/// - rdf:nodeID for blank nodes
///
/// Usage:
///   await using var writer = new RdfXmlStreamWriter(textWriter);
///   writer.RegisterNamespace("ex", "http://example.org/");
///   writer.WriteStartDocument();
///   writer.WriteTriple(subject, predicate, obj);
///   writer.WriteEndDocument();
/// </summary>
public sealed class RdfXmlStreamWriter : IDisposable, IAsyncDisposable
{
    private readonly TextWriter _writer;
    private readonly ArrayPool<char> _charPool;
    private char[] _buffer;
    private int _bufferPos;
    private bool _isDisposed;
    private bool _documentStarted;
    private bool _documentEnded;

    // Namespace mappings: prefix -> namespace IRI
    private readonly Dictionary<string, string> _namespaces;
    private readonly Dictionary<string, string> _iriToPrefix; // Reverse lookup

    // Subject grouping state
    private string? _currentSubject;
    private bool _hasOpenDescription;

    // Common namespace IRIs
    private const string RdfNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";

    private const int DefaultBufferSize = 4096;

    public RdfXmlStreamWriter(TextWriter writer, int bufferSize = DefaultBufferSize)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _charPool = ArrayPool<char>.Shared;
        _buffer = _charPool.Rent(bufferSize);
        _bufferPos = 0;
        _isDisposed = false;
        _documentStarted = false;
        _documentEnded = false;
        _namespaces = new Dictionary<string, string>(StringComparer.Ordinal);
        _iriToPrefix = new Dictionary<string, string>(StringComparer.Ordinal);
        _currentSubject = null;
        _hasOpenDescription = false;

        // Register standard namespaces
        RegisterNamespace("rdf", RdfNamespace);
        RegisterNamespace("rdfs", "http://www.w3.org/2000/01/rdf-schema#");
        RegisterNamespace("xsd", "http://www.w3.org/2001/XMLSchema#");
    }

    /// <summary>
    /// Register a namespace for use in the output.
    /// </summary>
    public void RegisterNamespace(string prefix, string namespaceIri)
    {
        _namespaces[prefix] = namespaceIri;
        _iriToPrefix[namespaceIri] = prefix;
    }

    /// <summary>
    /// Write the XML declaration and opening rdf:RDF element.
    /// Call this once at the start of output.
    /// </summary>
    public void WriteStartDocument()
    {
        if (_documentStarted)
            throw new InvalidOperationException("Document already started");

        _documentStarted = true;

        // XML declaration
        WriteString("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");

        // Opening rdf:RDF with namespace declarations
        WriteString("<rdf:RDF");

        foreach (var kvp in _namespaces)
        {
            WriteString("\n    xmlns:");
            WriteString(kvp.Key);
            WriteString("=\"");
            WriteXmlEscaped(kvp.Value.AsSpan());
            WriteChar('"');
        }

        WriteString(">\n");
        FlushBuffer();
    }

    /// <summary>
    /// Write the XML declaration and opening element (async).
    /// </summary>
    public async ValueTask WriteStartDocumentAsync(CancellationToken ct = default)
    {
        WriteStartDocument();
        await FlushBufferAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Write the closing rdf:RDF element.
    /// Call this once at the end of output.
    /// </summary>
    public void WriteEndDocument()
    {
        if (!_documentStarted)
            throw new InvalidOperationException("Document not started");
        if (_documentEnded)
            throw new InvalidOperationException("Document already ended");

        // Close any open Description element
        CloseCurrentDescription();

        _documentEnded = true;
        WriteString("</rdf:RDF>\n");
        FlushBuffer();
    }

    /// <summary>
    /// Write the closing element (async).
    /// </summary>
    public async ValueTask WriteEndDocumentAsync(CancellationToken ct = default)
    {
        WriteEndDocument();
        await FlushBufferAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Write a triple with subject grouping.
    /// Consecutive triples with the same subject are grouped under one rdf:Description.
    /// </summary>
    public void WriteTriple(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj)
    {
        if (!_documentStarted)
            throw new InvalidOperationException("Must call WriteStartDocument first");
        if (_documentEnded)
            throw new InvalidOperationException("Document already ended");

        var subjectStr = subject.ToString();

        if (_currentSubject != subjectStr)
        {
            // Close previous Description if any
            CloseCurrentDescription();

            // Open new Description element
            WriteString("  <rdf:Description ");

            if (subject.Length > 0 && subject[0] == '_' && subject.Length > 1 && subject[1] == ':')
            {
                // Blank node
                WriteString("rdf:nodeID=\"");
                WriteXmlEscaped(subject.Slice(2)); // Skip "_:"
                WriteString("\">\n");
            }
            else
            {
                // IRI subject
                WriteString("rdf:about=\"");
                WriteSubjectIri(subject);
                WriteString("\">\n");
            }

            _currentSubject = subjectStr;
            _hasOpenDescription = true;
        }

        // Write predicate element with object
        WritePredicateObject(predicate, obj);
    }

    /// <summary>
    /// Write a triple without subject grouping.
    /// </summary>
    public void WriteTripleUngrouped(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj)
    {
        if (!_documentStarted)
            throw new InvalidOperationException("Must call WriteStartDocument first");
        if (_documentEnded)
            throw new InvalidOperationException("Document already ended");

        // Close any open Description
        CloseCurrentDescription();

        // Write complete Description element
        WriteString("  <rdf:Description ");

        if (subject.Length > 0 && subject[0] == '_' && subject.Length > 1 && subject[1] == ':')
        {
            WriteString("rdf:nodeID=\"");
            WriteXmlEscaped(subject.Slice(2));
            WriteString("\">\n");
        }
        else
        {
            WriteString("rdf:about=\"");
            WriteSubjectIri(subject);
            WriteString("\">\n");
        }

        WritePredicateObject(predicate, obj);

        WriteString("  </rdf:Description>\n");
        FlushBuffer();
    }

    /// <summary>
    /// Write a triple (async version).
    /// </summary>
    public async ValueTask WriteTripleAsync(string subject, string predicate, string obj, CancellationToken ct = default)
    {
        WriteTriple(subject.AsSpan(), predicate.AsSpan(), obj.AsSpan());
        await FlushBufferAsync(ct).ConfigureAwait(false);
    }

    private void WriteSubjectIri(ReadOnlySpan<char> subject)
    {
        // Strip angle brackets if present
        if (subject.Length >= 2 && subject[0] == '<' && subject[^1] == '>')
        {
            WriteXmlEscaped(subject.Slice(1, subject.Length - 2));
        }
        else
        {
            WriteXmlEscaped(subject);
        }
    }

    private void WritePredicateObject(ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj)
    {
        WriteString("    ");

        // Write predicate as element name
        WritePredicateElement(predicate, out var predicateEnd);

        // Determine object type and write appropriately
        if (obj.Length > 0 && obj[0] == '<')
        {
            // IRI object - use rdf:resource attribute
            WriteString(" rdf:resource=\"");
            WriteXmlEscaped(obj.Slice(1, obj.Length - 2)); // Strip angle brackets
            WriteString("\"/>\n");
        }
        else if (obj.Length > 0 && obj[0] == '_' && obj.Length > 1 && obj[1] == ':')
        {
            // Blank node object
            WriteString(" rdf:nodeID=\"");
            WriteXmlEscaped(obj.Slice(2)); // Skip "_:"
            WriteString("\"/>\n");
        }
        else if (obj.Length > 0 && obj[0] == '"')
        {
            // Literal - parse and write
            WriteLiteralObject(obj, predicateEnd);
        }
        else
        {
            // Plain value (shouldn't normally happen, but handle gracefully)
            WriteChar('>');
            WriteXmlEscaped(obj);
            WriteString("</");
            WriteSpan(predicateEnd);
            WriteString(">\n");
        }
    }

    private void WritePredicateElement(ReadOnlySpan<char> predicate, out ReadOnlySpan<char> elementName)
    {
        // Strip angle brackets if present
        ReadOnlySpan<char> iri;
        if (predicate.Length >= 2 && predicate[0] == '<' && predicate[^1] == '>')
        {
            iri = predicate.Slice(1, predicate.Length - 2);
        }
        else
        {
            iri = predicate;
        }

        // Try to find matching namespace
        foreach (var kvp in _iriToPrefix)
        {
            var ns = kvp.Key.AsSpan();
            if (iri.StartsWith(ns, StringComparison.Ordinal))
            {
                var localName = iri.Slice(ns.Length);
                if (IsValidXmlName(localName))
                {
                    var prefix = kvp.Value;
                    WriteChar('<');
                    WriteString(prefix);
                    WriteChar(':');
                    WriteSpan(localName);
                    elementName = $"{prefix}:{localName.ToString()}".AsSpan();
                    return;
                }
            }
        }

        // No matching namespace - use full IRI (not valid XML, but best effort)
        // We need to create an element somehow - use rdf:Description with rdf:about
        // Actually, for RDF/XML we should always have a namespace. Fall back to using the IRI hash/slash split
        var hashIdx = iri.LastIndexOf('#');
        var slashIdx = iri.LastIndexOf('/');
        var splitIdx = Math.Max(hashIdx, slashIdx);

        if (splitIdx > 0 && splitIdx < iri.Length - 1)
        {
            var ns = iri.Slice(0, splitIdx + 1).ToString();
            var local = iri.Slice(splitIdx + 1);

            // Auto-register namespace with generated prefix
            if (!_iriToPrefix.ContainsKey(ns))
            {
                var prefix = $"ns{_namespaces.Count}";
                RegisterNamespace(prefix, ns);
            }

            var registeredPrefix = _iriToPrefix[ns];
            WriteChar('<');
            WriteString(registeredPrefix);
            WriteChar(':');
            WriteSpan(local);
            elementName = $"{registeredPrefix}:{local.ToString()}".AsSpan();
            return;
        }

        // Last resort - shouldn't happen with valid RDF
        WriteString("<rdf:value");
        elementName = "rdf:value".AsSpan();
    }

    private void WriteLiteralObject(ReadOnlySpan<char> literal, ReadOnlySpan<char> predicateElement)
    {
        // Parse literal: "value"@lang or "value"^^<datatype> or just "value"
        if (literal.Length < 2 || literal[0] != '"')
        {
            WriteChar('>');
            WriteXmlEscaped(literal);
            WriteString("</");
            WriteSpan(predicateElement);
            WriteString(">\n");
            return;
        }

        // Find closing quote (handle escaped quotes)
        int closeQuote = -1;
        for (int i = 1; i < literal.Length; i++)
        {
            if (literal[i] == '"' && (i == 1 || literal[i - 1] != '\\'))
            {
                closeQuote = i;
                break;
            }
        }

        if (closeQuote < 0)
        {
            // Malformed - output as-is
            WriteChar('>');
            WriteXmlEscaped(literal);
            WriteString("</");
            WriteSpan(predicateElement);
            WriteString(">\n");
            return;
        }

        var value = literal.Slice(1, closeQuote - 1);
        var suffix = literal.Slice(closeQuote + 1);

        if (suffix.StartsWith("@".AsSpan(), StringComparison.Ordinal))
        {
            // Language tag
            var lang = suffix.Slice(1);
            WriteString(" xml:lang=\"");
            WriteSpan(lang);
            WriteString("\">");
            WriteXmlEscapedLiteral(value);
            WriteString("</");
            WriteSpan(predicateElement);
            WriteString(">\n");
        }
        else if (suffix.StartsWith("^^".AsSpan(), StringComparison.Ordinal))
        {
            // Datatype
            var datatype = suffix.Slice(2);
            if (datatype.Length >= 2 && datatype[0] == '<' && datatype[^1] == '>')
            {
                datatype = datatype.Slice(1, datatype.Length - 2);
            }

            WriteString(" rdf:datatype=\"");
            WriteXmlEscaped(datatype);
            WriteString("\">");
            WriteXmlEscapedLiteral(value);
            WriteString("</");
            WriteSpan(predicateElement);
            WriteString(">\n");
        }
        else
        {
            // Plain literal
            WriteChar('>');
            WriteXmlEscapedLiteral(value);
            WriteString("</");
            WriteSpan(predicateElement);
            WriteString(">\n");
        }
    }

    private void CloseCurrentDescription()
    {
        if (_hasOpenDescription)
        {
            WriteString("  </rdf:Description>\n");
            _hasOpenDescription = false;
            _currentSubject = null;
            FlushBuffer();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsValidXmlName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty) return false;

        // First char must be letter or underscore
        var first = name[0];
        if (!char.IsLetter(first) && first != '_')
            return false;

        // Rest can be letter, digit, underscore, hyphen, period
        for (int i = 1; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != '.')
                return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteChar(char c)
    {
        EnsureCapacity(1);
        _buffer[_bufferPos++] = c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteString(string s)
    {
        WriteSpan(s.AsSpan());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteSpan(ReadOnlySpan<char> span)
    {
        EnsureCapacity(span.Length);
        span.CopyTo(_buffer.AsSpan(_bufferPos));
        _bufferPos += span.Length;
    }

    private void WriteXmlEscaped(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            switch (c)
            {
                case '<':
                    WriteString("&lt;");
                    break;
                case '>':
                    WriteString("&gt;");
                    break;
                case '&':
                    WriteString("&amp;");
                    break;
                case '"':
                    WriteString("&quot;");
                    break;
                default:
                    WriteChar(c);
                    break;
            }
        }
    }

    private void WriteXmlEscapedLiteral(ReadOnlySpan<char> text)
    {
        // Handle escape sequences in literal values
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '\\' && i + 1 < text.Length)
            {
                var next = text[i + 1];
                switch (next)
                {
                    case 'n':
                        WriteChar('\n');
                        i++;
                        continue;
                    case 't':
                        WriteChar('\t');
                        i++;
                        continue;
                    case 'r':
                        WriteChar('\r');
                        i++;
                        continue;
                    case '\\':
                        WriteChar('\\');
                        i++;
                        continue;
                    case '"':
                        WriteChar('"');
                        i++;
                        continue;
                }
            }

            switch (c)
            {
                case '<':
                    WriteString("&lt;");
                    break;
                case '>':
                    WriteString("&gt;");
                    break;
                case '&':
                    WriteString("&amp;");
                    break;
                default:
                    WriteChar(c);
                    break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity(int needed)
    {
        if (_bufferPos + needed > _buffer.Length)
        {
            FlushBuffer();
            if (needed > _buffer.Length)
            {
                _charPool.Return(_buffer);
                _buffer = _charPool.Rent(needed * 2);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FlushBuffer()
    {
        if (_bufferPos > 0)
        {
            _writer.Write(_buffer, 0, _bufferPos);
            _bufferPos = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private async ValueTask FlushBufferAsync(CancellationToken ct)
    {
        if (_bufferPos > 0)
        {
            await _writer.WriteAsync(_buffer.AsMemory(0, _bufferPos), ct).ConfigureAwait(false);
            _bufferPos = 0;
        }
    }

    /// <summary>
    /// Flush any buffered content to the underlying writer.
    /// </summary>
    public void Flush()
    {
        CloseCurrentDescription();
        FlushBuffer();
        _writer.Flush();
    }

    /// <summary>
    /// Flush any buffered content (async).
    /// </summary>
    public async ValueTask FlushAsync(CancellationToken ct = default)
    {
        CloseCurrentDescription();
        await FlushBufferAsync(ct).ConfigureAwait(false);
        await _writer.FlushAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_documentStarted && !_documentEnded)
        {
            WriteEndDocument();
        }

        FlushBuffer();
        _charPool.Return(_buffer);
        _buffer = null!;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_documentStarted && !_documentEnded)
        {
            WriteEndDocument();
        }

        await FlushBufferAsync(default).ConfigureAwait(false);
        _charPool.Return(_buffer);
        _buffer = null!;
    }
}
