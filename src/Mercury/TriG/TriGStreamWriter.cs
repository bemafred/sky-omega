// TriGStreamWriter.cs
// Zero-GC streaming TriG writer with prefix and graph support
// Based on W3C RDF 1.1 TriG specification
// No external dependencies, only BCL
// .NET 10 / C# 14

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.Runtime.Buffers;

namespace SkyOmega.Mercury.TriG;

/// <summary>
/// Zero-allocation streaming writer for RDF TriG format.
/// TriG extends Turtle with named graph support.
///
/// Features:
/// - Named graph blocks using GRAPH keyword or shorthand syntax
/// - All Turtle features: prefixes, subject grouping, 'a' shorthand
/// - Automatic graph grouping for consecutive quads
/// - Streaming output to any TextWriter
///
/// TriG syntax:
///   @prefix ex: &lt;http://example.org/&gt; .
///
///   # Default graph
///   ex:s ex:p ex:o .
///
///   # Named graph with GRAPH keyword
///   GRAPH &lt;http://example.org/g1&gt; {
///       ex:s ex:p ex:o .
///   }
///
///   # Named graph shorthand
///   &lt;http://example.org/g2&gt; {
///       ex:s ex:p ex:o .
///   }
///
/// Usage:
///   await using var writer = new TriGStreamWriter(textWriter);
///   writer.RegisterPrefix("ex", "http://example.org/");
///   writer.WritePrefixes();
///
///   // Default graph
///   writer.WriteQuad(subject, predicate, obj);
///
///   // Named graph
///   writer.WriteQuad(subject, predicate, obj, "&lt;http://example.org/graph&gt;");
///
///   writer.Flush();
/// </summary>
internal sealed class TriGStreamWriter : IDisposable, IAsyncDisposable
{
    private readonly TextWriter _writer;
    private readonly IBufferManager _bufferManager;
    private char[] _buffer;
    private int _bufferPos;
    private bool _isDisposed;

    // Prefix mappings: prefix -> namespace IRI (without angle brackets)
    private readonly Dictionary<string, string> _prefixes;
    private readonly Dictionary<string, string> _namespaceToPrefix; // Reverse lookup

    // Graph state
    private string? _currentGraph;      // Current graph IRI (null = default graph)
    private bool _graphBlockOpen;       // Whether we're inside a { } block
    private bool _useGraphKeyword;      // Whether to emit GRAPH keyword

    // Subject grouping state (within current graph)
    private string? _currentSubject;
    private string? _currentSubjectAbbrev;
    private bool _hasWrittenPredicateForSubject;

    // Common IRIs for shortcuts
    private const string RdfType = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";
    private const string RdfTypeWithBrackets = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>";

    private const int DefaultBufferSize = 4096;

    /// <summary>
    /// Create a TriG writer.
    /// </summary>
    /// <param name="writer">The TextWriter to output to.</param>
    /// <param name="useGraphKeyword">If true, emit "GRAPH" keyword; if false, use shorthand syntax.</param>
    /// <param name="bufferSize">Internal buffer size.</param>
    /// <param name="bufferManager">Optional buffer manager for pooling.</param>
    public TriGStreamWriter(TextWriter writer, bool useGraphKeyword = true, int bufferSize = DefaultBufferSize, IBufferManager? bufferManager = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _bufferManager = bufferManager ?? PooledBufferManager.Shared;
        _buffer = _bufferManager.Rent<char>(bufferSize).Array!;
        _bufferPos = 0;
        _isDisposed = false;
        _useGraphKeyword = useGraphKeyword;

        _prefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        _namespaceToPrefix = new Dictionary<string, string>(StringComparer.Ordinal);

        _currentGraph = null;
        _graphBlockOpen = false;
        _currentSubject = null;
        _currentSubjectAbbrev = null;
        _hasWrittenPredicateForSubject = false;

        // Register standard prefixes
        RegisterPrefix("rdf", "http://www.w3.org/1999/02/22-rdf-syntax-ns#");
        RegisterPrefix("rdfs", "http://www.w3.org/2000/01/rdf-schema#");
        RegisterPrefix("xsd", "http://www.w3.org/2001/XMLSchema#");
    }

    /// <summary>
    /// Register a prefix for IRI abbreviation.
    /// </summary>
    public void RegisterPrefix(string prefix, string namespaceIri)
    {
        _prefixes[prefix] = namespaceIri;
        _namespaceToPrefix[namespaceIri] = prefix;
    }

    /// <summary>
    /// Write all registered prefix declarations.
    /// Call this once at the start of output.
    /// </summary>
    public void WritePrefixes()
    {
        foreach (var kvp in _prefixes)
        {
            WriteString("@prefix ");
            WriteString(kvp.Key);
            WriteString(": <");
            WriteString(kvp.Value);
            WriteString("> .\n");
        }
        if (_prefixes.Count > 0)
        {
            WriteChar('\n');
        }
        FlushBuffer();
    }

    /// <summary>
    /// Write all registered prefix declarations (async).
    /// </summary>
    public async ValueTask WritePrefixesAsync(CancellationToken ct = default)
    {
        foreach (var kvp in _prefixes)
        {
            WriteString("@prefix ");
            WriteString(kvp.Key);
            WriteString(": <");
            WriteString(kvp.Value);
            WriteString("> .\n");
        }
        if (_prefixes.Count > 0)
        {
            WriteChar('\n');
        }
        await FlushBufferAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Write a quad with optional named graph.
    /// Consecutive quads in the same graph and with the same subject are grouped.
    /// </summary>
    /// <param name="subject">Subject IRI or blank node.</param>
    /// <param name="predicate">Predicate IRI.</param>
    /// <param name="obj">Object (IRI, blank node, or literal).</param>
    /// <param name="graph">Graph IRI or blank node; empty for default graph.</param>
    public void WriteQuad(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj, ReadOnlySpan<char> graph = default)
    {
        var graphStr = graph.IsEmpty ? null : graph.ToString();

        // Check if we need to switch graphs
        if (!string.Equals(_currentGraph, graphStr, StringComparison.Ordinal))
        {
            SwitchGraph(graphStr);
        }

        // Now write the triple within the current graph context
        WriteTripleInCurrentGraph(subject, predicate, obj);
    }

    /// <summary>
    /// Write a quad using string parameters.
    /// </summary>
    public void WriteQuad(string subject, string predicate, string obj, string? graph = null)
    {
        if (graph != null)
        {
            WriteQuad(subject.AsSpan(), predicate.AsSpan(), obj.AsSpan(), graph.AsSpan());
        }
        else
        {
            WriteQuad(subject.AsSpan(), predicate.AsSpan(), obj.AsSpan(), ReadOnlySpan<char>.Empty);
        }
    }

    /// <summary>
    /// Write a quad (async version).
    /// </summary>
    public async ValueTask WriteQuadAsync(string subject, string predicate, string obj,
        string? graph = null, CancellationToken ct = default)
    {
        if (graph != null)
        {
            WriteQuad(subject.AsSpan(), predicate.AsSpan(), obj.AsSpan(), graph.AsSpan());
        }
        else
        {
            WriteQuad(subject.AsSpan(), predicate.AsSpan(), obj.AsSpan(), ReadOnlySpan<char>.Empty);
        }
        await FlushBufferAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Write a triple to the default graph.
    /// </summary>
    public void WriteTriple(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj)
    {
        WriteQuad(subject, predicate, obj, ReadOnlySpan<char>.Empty);
    }

    /// <summary>
    /// Write a raw quad without any formatting.
    /// Terms are written as-is - caller ensures proper formatting.
    /// </summary>
    public void WriteRawQuad(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj, ReadOnlySpan<char> graph = default)
    {
        var graphStr = graph.IsEmpty ? null : graph.ToString();

        if (!string.Equals(_currentGraph, graphStr, StringComparison.Ordinal))
        {
            SwitchGraph(graphStr);
        }

        // Write without abbreviation
        if (_graphBlockOpen)
        {
            WriteString("    "); // Indent inside graph block
        }
        WriteSpan(subject);
        WriteChar(' ');
        WriteSpan(predicate);
        WriteChar(' ');
        WriteSpan(obj);
        WriteString(" .\n");
    }

    /// <summary>
    /// Switch to a different graph context.
    /// Closes current graph block if open, opens new one if needed.
    /// </summary>
    private void SwitchGraph(string? newGraph)
    {
        // Close current subject grouping
        FinishCurrentSubject();

        // Close current graph block if open
        if (_graphBlockOpen)
        {
            WriteString("}\n\n");
            _graphBlockOpen = false;
        }

        _currentGraph = newGraph;

        // Open new graph block if not default graph
        if (newGraph != null)
        {
            if (_useGraphKeyword)
            {
                WriteString("GRAPH ");
            }
            WriteSpan(TryAbbreviate(newGraph.AsSpan()));
            WriteString(" {\n");
            _graphBlockOpen = true;
        }
    }

    /// <summary>
    /// Write a triple within the current graph context (with subject grouping).
    /// </summary>
    private void WriteTripleInCurrentGraph(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj)
    {
        var subjectStr = subject.ToString();
        var subjectAbbrev = TryAbbreviate(subject);

        var indent = _graphBlockOpen ? "    " : "";
        var contIndent = _graphBlockOpen ? "        " : "    ";

        if (_currentSubject != null && _currentSubject == subjectStr)
        {
            // Same subject - continue with semicolon
            WriteString(" ;\n");
            WriteString(contIndent);
        }
        else
        {
            // New subject - finish previous if any
            if (_currentSubject != null && _hasWrittenPredicateForSubject)
            {
                WriteString(" .\n");
            }

            // Write new subject
            WriteString(indent);
            WriteSpan(subjectAbbrev);
            WriteChar(' ');
            _currentSubject = subjectStr;
            _currentSubjectAbbrev = subjectAbbrev.ToString();
        }

        // Write predicate
        WritePredicate(predicate);
        WriteChar(' ');

        // Write object
        WriteObject(obj);

        _hasWrittenPredicateForSubject = true;
    }

    /// <summary>
    /// Finish the current subject group.
    /// </summary>
    public void FinishCurrentSubject()
    {
        if (_currentSubject != null && _hasWrittenPredicateForSubject)
        {
            WriteString(" .\n");
            FlushBuffer();
        }
        _currentSubject = null;
        _currentSubjectAbbrev = null;
        _hasWrittenPredicateForSubject = false;
    }

    /// <summary>
    /// Write predicate, using 'a' shorthand for rdf:type.
    /// </summary>
    private void WritePredicate(ReadOnlySpan<char> predicate)
    {
        if (predicate.Equals(RdfTypeWithBrackets.AsSpan(), StringComparison.Ordinal))
        {
            WriteChar('a');
            return;
        }

        if (predicate.Length > 2 && predicate[0] == '<' && predicate[^1] == '>')
        {
            var iri = predicate.Slice(1, predicate.Length - 2);
            if (iri.Equals(RdfType.AsSpan(), StringComparison.Ordinal))
            {
                WriteChar('a');
                return;
            }
        }

        WriteSpan(TryAbbreviate(predicate));
    }

    /// <summary>
    /// Write object, abbreviating IRIs but preserving literals as-is.
    /// </summary>
    private void WriteObject(ReadOnlySpan<char> obj)
    {
        if (obj.Length == 0) return;

        if (obj[0] == '<')
        {
            WriteSpan(TryAbbreviate(obj));
        }
        else if (obj[0] == '"')
        {
            WriteSpan(obj);
        }
        else if (obj[0] == '_')
        {
            WriteSpan(obj);
        }
        else
        {
            WriteSpan(obj);
        }
    }

    /// <summary>
    /// Try to abbreviate an IRI using registered prefixes.
    /// </summary>
    private ReadOnlySpan<char> TryAbbreviate(ReadOnlySpan<char> term)
    {
        if (term.Length < 3 || term[0] != '<' || term[^1] != '>')
            return term;

        var iri = term.Slice(1, term.Length - 2);

        foreach (var kvp in _namespaceToPrefix)
        {
            var ns = kvp.Key.AsSpan();
            if (iri.StartsWith(ns, StringComparison.Ordinal))
            {
                var localName = iri.Slice(ns.Length);
                if (IsValidLocalName(localName))
                {
                    var prefix = kvp.Value;
                    var result = $"{prefix}:{localName.ToString()}";
                    return result.AsSpan();
                }
            }
        }

        return term;
    }

    /// <summary>
    /// Check if a local name is valid for Turtle prefix abbreviation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsValidLocalName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty) return false;

        var first = name[0];
        if (!char.IsLetter(first) && first != '_')
            return false;

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity(int needed)
    {
        if (_bufferPos + needed > _buffer.Length)
        {
            FlushBuffer();
            if (needed > _buffer.Length)
            {
                _bufferManager.Return(_buffer);
                _buffer = _bufferManager.Rent<char>(needed * 2).Array!;
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
    /// Flush all buffered content, closing any open graph blocks.
    /// </summary>
    public void Flush()
    {
        FinishCurrentSubject();
        if (_graphBlockOpen)
        {
            WriteString("}\n");
            _graphBlockOpen = false;
        }
        FlushBuffer();
        _writer.Flush();
    }

    /// <summary>
    /// Flush all buffered content (async).
    /// </summary>
    public async ValueTask FlushAsync(CancellationToken ct = default)
    {
        FinishCurrentSubject();
        if (_graphBlockOpen)
        {
            WriteString("}\n");
            _graphBlockOpen = false;
        }
        await FlushBufferAsync(ct).ConfigureAwait(false);
        await _writer.FlushAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        FinishCurrentSubject();
        if (_graphBlockOpen)
        {
            WriteString("}\n");
            _graphBlockOpen = false;
        }
        FlushBuffer();
        _bufferManager.Return(_buffer);
        _buffer = null!;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        FinishCurrentSubject();
        if (_graphBlockOpen)
        {
            WriteString("}\n");
            _graphBlockOpen = false;
        }
        await FlushBufferAsync(default).ConfigureAwait(false);
        _bufferManager.Return(_buffer);
        _buffer = null!;
    }
}
