// TurtleStreamWriter.cs
// Zero-GC streaming Turtle writer with prefix support
// Based on W3C RDF 1.1 Turtle specification
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

namespace SkyOmega.Mercury.Rdf.Turtle;

/// <summary>
/// Zero-allocation streaming writer for RDF Turtle format.
/// Supports prefix declarations for compact IRI output.
///
/// Features:
/// - Prefix registration and IRI abbreviation
/// - Subject grouping with semicolon separators
/// - 'a' shorthand for rdf:type
/// - Streaming output to any TextWriter
///
/// Usage:
///   await using var writer = new TurtleStreamWriter(textWriter);
///   writer.RegisterPrefix("ex", "http://example.org/");
///   writer.WritePrefixes();
///   writer.WriteTriple(subject, predicate, obj);
///   writer.Flush(); // Writes any pending grouped triples
/// </summary>
public sealed class TurtleStreamWriter : IDisposable, IAsyncDisposable
{
    private readonly TextWriter _writer;
    private readonly IBufferManager _bufferManager;
    private char[] _buffer;
    private int _bufferPos;
    private bool _isDisposed;

    // Prefix mappings: prefix -> namespace IRI (without angle brackets)
    private readonly Dictionary<string, string> _prefixes;
    private readonly Dictionary<string, string> _namespaceToPrefix; // Reverse lookup

    // Subject grouping state
    private string? _currentSubject;
    private string? _currentSubjectAbbrev;
    private bool _hasWrittenPredicateForSubject;

    // Common IRIs for shortcuts
    private const string RdfType = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";
    private const string RdfTypeWithBrackets = "<http://www.w3.org/1999/02/22-rdf-syntax-ns#type>";

    private const int DefaultBufferSize = 4096;

    public TurtleStreamWriter(TextWriter writer, int bufferSize = DefaultBufferSize, IBufferManager? bufferManager = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _bufferManager = bufferManager ?? PooledBufferManager.Shared;
        _buffer = _bufferManager.Rent<char>(bufferSize).Array!;
        _bufferPos = 0;
        _isDisposed = false;
        _prefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        _namespaceToPrefix = new Dictionary<string, string>(StringComparer.Ordinal);
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
            WriteChar('\n'); // Blank line after prefixes
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
    /// Write a triple with subject grouping.
    /// Consecutive triples with the same subject are grouped using semicolons.
    /// </summary>
    public void WriteTriple(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj)
    {
        var subjectStr = subject.ToString();
        var subjectAbbrev = TryAbbreviate(subject);

        if (_currentSubject != null && _currentSubject == subjectStr)
        {
            // Same subject - continue with semicolon
            WriteString(" ;\n    ");
        }
        else
        {
            // New subject - finish previous if any
            if (_currentSubject != null && _hasWrittenPredicateForSubject)
            {
                WriteString(" .\n");
            }

            // Write new subject
            WriteSpan(subjectAbbrev);
            WriteChar(' ');
            _currentSubject = subjectStr;
            _currentSubjectAbbrev = subjectAbbrev.ToString();
        }

        // Write predicate (with 'a' shorthand for rdf:type)
        WritePredicate(predicate);
        WriteChar(' ');

        // Write object
        WriteObject(obj);

        _hasWrittenPredicateForSubject = true;
    }

    /// <summary>
    /// Write a triple without subject grouping (each on its own line).
    /// </summary>
    public void WriteTripleUngrouped(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj)
    {
        // Finish any pending grouped subject first
        FinishCurrentSubject();

        WriteSpan(TryAbbreviate(subject));
        WriteChar(' ');
        WritePredicate(predicate);
        WriteChar(' ');
        WriteObject(obj);
        WriteString(" .\n");
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

    /// <summary>
    /// Finish the current subject group and start fresh.
    /// Call this when done with a batch or before switching contexts.
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
        // Check for rdf:type
        if (predicate.Equals(RdfTypeWithBrackets.AsSpan(), StringComparison.Ordinal))
        {
            WriteChar('a');
            return;
        }

        // Check if it's the bare IRI
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
            // IRI - try to abbreviate
            WriteSpan(TryAbbreviate(obj));
        }
        else if (obj[0] == '"')
        {
            // Literal - write as-is (already properly formatted)
            WriteSpan(obj);
        }
        else if (obj[0] == '_')
        {
            // Blank node - write as-is
            WriteSpan(obj);
        }
        else
        {
            // Unknown format - write as-is
            WriteSpan(obj);
        }
    }

    /// <summary>
    /// Try to abbreviate an IRI using registered prefixes.
    /// Returns the original if no prefix matches.
    /// </summary>
    private ReadOnlySpan<char> TryAbbreviate(ReadOnlySpan<char> term)
    {
        if (term.Length < 3 || term[0] != '<' || term[^1] != '>')
            return term;

        var iri = term.Slice(1, term.Length - 2);

        // Try each registered namespace
        foreach (var kvp in _namespaceToPrefix)
        {
            var ns = kvp.Key.AsSpan();
            if (iri.StartsWith(ns, StringComparison.Ordinal))
            {
                var localName = iri.Slice(ns.Length);
                // Verify local name is valid (alphanumeric, underscore, hyphen)
                if (IsValidLocalName(localName))
                {
                    // Build prefixed name: prefix:localName
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
    /// Flush any buffered content to the underlying writer.
    /// </summary>
    public void Flush()
    {
        FinishCurrentSubject();
        FlushBuffer();
        _writer.Flush();
    }

    /// <summary>
    /// Flush any buffered content (async).
    /// </summary>
    public async ValueTask FlushAsync(CancellationToken ct = default)
    {
        FinishCurrentSubject();
        await FlushBufferAsync(ct).ConfigureAwait(false);
        await _writer.FlushAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        FinishCurrentSubject();
        FlushBuffer();
        _bufferManager.Return(_buffer);
        _buffer = null!;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        FinishCurrentSubject();
        await FlushBufferAsync(default).ConfigureAwait(false);
        _bufferManager.Return(_buffer);
        _buffer = null!;
    }
}
