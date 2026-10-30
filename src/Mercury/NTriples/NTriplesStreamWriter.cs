// NTriplesStreamWriter.cs
// Zero-GC streaming N-Triples writer: N-Quads in the default graph
// Writes canonical N-Triples (W3C RDF 1.2 N-Triples, Canonical N-Triples)
// No external dependencies, only BCL
// .NET 10 / C# 14

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.NQuads;
using SkyOmega.Mercury.Runtime.Buffers;

namespace SkyOmega.Mercury.NTriples;

/// <summary>
/// Zero-allocation streaming writer for RDF N-Triples format.
///
/// N-Triples grammar:
/// [1] triple ::= subject predicate object '.'
/// [2] subject ::= IRIREF | BLANK_NODE_LABEL
/// [3] predicate ::= IRIREF
/// [4] object ::= IRIREF | BLANK_NODE_LABEL | literal
///
/// An N-Triples statement is an N-Quads statement in the default graph, so this writer writes through
/// <see cref="NQuadsStreamWriter"/>, the one implementation of the terms and their canonical escaping. Before
/// 2026-10-30 the two were copies; the N-Quads one wrote '"' and '\' in a literal unescaped.
///
/// Usage:
///   await using var writer = new NTriplesStreamWriter(textWriter);
///   writer.WriteTriple(subject, predicate, obj);
///   // or async:
///   await writer.WriteTripleAsync(subject, predicate, obj);
/// </summary>
internal sealed class NTriplesStreamWriter : IDisposable, IAsyncDisposable
{
    private readonly NQuadsStreamWriter _statements;

    public NTriplesStreamWriter(TextWriter writer, int bufferSize = NQuadsStreamWriter.DefaultBufferSize, IBufferManager? bufferManager = null)
    {
        _statements = new NQuadsStreamWriter(writer, bufferSize, bufferManager);
    }

    /// <summary>
    /// Write a triple in N-Triples format (synchronous).
    /// Terms are in Mercury's term form:
    /// - IRIs: &lt;http://example.org/foo&gt; (with angle brackets)
    /// - Blank nodes: _:b0
    /// - Literals: "value", "value"@lang, "value"^^&lt;datatype&gt;, with the value's escapes decoded
    /// </summary>
    public void WriteTriple(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj)
        => _statements.WriteQuad(subject, predicate, obj);

    /// <summary>
    /// Write a triple in N-Triples format (asynchronous).
    /// Uses ReadOnlyMemory for async compatibility (spans can't cross await).
    /// </summary>
    public ValueTask WriteTripleAsync(ReadOnlyMemory<char> subject, ReadOnlyMemory<char> predicate,
        ReadOnlyMemory<char> obj, CancellationToken ct = default)
        => _statements.WriteQuadAsync(subject, predicate, obj, ReadOnlyMemory<char>.Empty, ct);

    /// <summary>
    /// Write a triple in N-Triples format (asynchronous, string overload).
    /// </summary>
    public ValueTask WriteTripleAsync(string subject, string predicate, string obj, CancellationToken ct = default)
        => _statements.WriteQuadAsync(subject.AsMemory(), predicate.AsMemory(), obj.AsMemory(), ReadOnlyMemory<char>.Empty, ct);

    /// <summary>
    /// Write a raw triple from CONSTRUCT/DESCRIBE results.
    /// The terms are written as-is - caller ensures proper formatting.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteRawTriple(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj)
        => _statements.WriteRawQuad(subject, predicate, obj);

    /// <summary>
    /// Flush any buffered content to the underlying writer.
    /// </summary>
    public void Flush() => _statements.Flush();

    /// <summary>
    /// Flush any buffered content to the underlying writer (async).
    /// </summary>
    public ValueTask FlushAsync(CancellationToken ct = default) => _statements.FlushAsync(ct);

    public void Dispose() => _statements.Dispose();

    public ValueTask DisposeAsync() => _statements.DisposeAsync();
}
