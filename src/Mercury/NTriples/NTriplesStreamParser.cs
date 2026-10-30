// NTriplesStreamParser.cs
// Zero-GC streaming N-Triples parser: the N-Quads grammar without graph labels
// Based on W3C RDF 1.1 N-Triples specification
// No external dependencies, only BCL
// .NET 10 / C# 14

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.NQuads;
using SkyOmega.Mercury.Rdf;
using SkyOmega.Mercury.Rdf.Turtle;
using SkyOmega.Mercury.Runtime.Buffers;

namespace SkyOmega.Mercury.NTriples;

/// <summary>
/// Zero-allocation streaming parser for RDF N-Triples format.
/// Implements W3C RDF 1.1 N-Triples specification.
///
/// N-Triples grammar (simplified):
/// [1] ntriplesDoc ::= triple? (EOL triple)* EOL?
/// [2] triple      ::= subject predicate object '.'
/// [3] subject     ::= IRIREF | BLANK_NODE_LABEL
/// [4] predicate   ::= IRIREF
/// [5] object      ::= IRIREF | BLANK_NODE_LABEL | literal
///
/// N-Triples is N-Quads without the graph label: every N-Triples document is an N-Quads document whose statements are
/// all in the default graph. So this parser reads through <see cref="NQuadsStreamParser"/>, the one implementation of
/// the terms, the UTF-8 decoding and the buffering, and rejects a graph label. Before 2026-10-30 it was a copy that had
/// drifted: it decoded UTF-8 as Latin-1, one character per byte.
///
/// For zero-GC parsing, use ParseAsync(TripleHandler).
/// The IAsyncEnumerable overload allocates strings for compatibility.
/// </summary>
/// <remarks>
/// <para><b>Thread Safety:</b> This class is NOT thread-safe. Each instance
/// maintains internal parsing state and buffers that cannot be shared across threads.
/// Create a separate instance per thread or serialize access with locking.</para>
/// <para><b>Usage Pattern:</b> Create one instance per stream. Dispose when done
/// to return pooled buffers.</para>
/// </remarks>
internal sealed class NTriplesStreamParser : IDisposable, IAsyncDisposable
{
    private readonly NQuadsStreamParser _statements;

    public NTriplesStreamParser(Stream stream, int bufferSize = NQuadsStreamParser.DefaultBufferSize, IBufferManager? bufferManager = null)
    {
        _statements = new NQuadsStreamParser(stream, bufferSize, bufferManager, "N-Triples");
    }

    /// <summary>
    /// Parse N-Triples document with zero allocations.
    /// Triples are emitted via the handler callback with spans valid only during the call.
    /// </summary>
    public Task ParseAsync(TripleHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);

        return _statements.ParseAsync((subject, predicate, obj, graph) =>
        {
            if (!graph.IsEmpty)
                throw _statements.ParserException($"N-Triples has no graph label: {graph.ToString()}");
            handler(subject, predicate, obj);
        }, cancellationToken);
    }

    /// <summary>
    /// Parse N-Triples document and yield RDF triples (allocates strings).
    /// </summary>
    public async IAsyncEnumerable<RdfTriple> ParseAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var quad in _statements.ParseAsync(cancellationToken))
        {
            if (quad.Graph is not null)
                throw _statements.ParserException($"N-Triples has no graph label: {quad.Graph}");
            yield return new RdfTriple(quad.Subject, quad.Predicate, quad.Object);
        }
    }

    public void Dispose() => _statements.Dispose();

    public ValueTask DisposeAsync() => _statements.DisposeAsync();
}
