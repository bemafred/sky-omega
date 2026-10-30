// Licensed under the MIT License.

using System;
using System.IO;
using SkyOmega.Mercury.Abstractions;
using SkyOmega.Mercury.JsonLd;
using SkyOmega.Mercury.NQuads;
using SkyOmega.Mercury.NTriples;
using SkyOmega.Mercury.Rdf;
using SkyOmega.Mercury.Rdf.Turtle;
using SkyOmega.Mercury.RdfXml;
using SkyOmega.Mercury.TriG;

namespace SkyOmega.Mercury;

/// <summary>
/// An RDF document in one format, written statement by statement: the one map from <see cref="RdfFormat"/> to its
/// streaming writer, used by <see cref="RdfEngine.WriteTriples"/>, <see cref="RdfEngine.WriteQuads"/> and
/// <see cref="RdfEngine.ConvertAsync"/>. Disposing it completes the document (RDF/XML's end element, Turtle's last
/// subject, TriG's last graph block, the JSON-LD document) exactly once.
/// </summary>
/// <remarks>
/// N-Triples, Turtle and RDF/XML have no named graphs. A statement in a named graph written to one of them is an error,
/// not dropped and not merged into the default graph.
/// </remarks>
internal sealed class RdfDocumentWriter : IDisposable
{
    private readonly RdfFormat _format;
    private readonly NTriplesStreamWriter? _nTriples;
    private readonly NQuadsStreamWriter? _nQuads;
    private readonly TurtleStreamWriter? _turtle;
    private readonly TriGStreamWriter? _triG;
    private readonly RdfXmlStreamWriter? _rdfXml;
    private readonly JsonLdStreamWriter? _jsonLd;

    public RdfDocumentWriter(TextWriter writer, RdfFormat format)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _format = format;
        switch (format)
        {
            case RdfFormat.NTriples: _nTriples = new NTriplesStreamWriter(writer); break;
            case RdfFormat.NQuads: _nQuads = new NQuadsStreamWriter(writer); break;
            case RdfFormat.Turtle: _turtle = new TurtleStreamWriter(writer); break;
            case RdfFormat.TriG: _triG = new TriGStreamWriter(writer); break;
            case RdfFormat.RdfXml:
                _rdfXml = new RdfXmlStreamWriter(writer);
                _rdfXml.WriteStartDocument();
                break;
            case RdfFormat.JsonLd: _jsonLd = new JsonLdStreamWriter(writer); break;
            default: throw new NotSupportedException($"No writer for RDF format: {format}");
        }
    }

    /// <summary>True for the formats that hold named graphs: N-Quads, TriG and JSON-LD.</summary>
    public static bool HoldsNamedGraphs(RdfFormat format) =>
        format is RdfFormat.NQuads or RdfFormat.TriG or RdfFormat.JsonLd;

    /// <summary>
    /// Write one statement; <paramref name="graph"/> is empty for the default graph. Terms are in Mercury's term form.
    /// </summary>
    /// <exception cref="NotSupportedException">A named graph, written to a format that has none.</exception>
    public void Write(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj, ReadOnlySpan<char> graph)
    {
        switch (_format)
        {
            case RdfFormat.NQuads: _nQuads!.WriteQuad(subject, predicate, obj, graph); return;
            case RdfFormat.TriG: _triG!.WriteQuad(subject, predicate, obj, graph); return;
            case RdfFormat.JsonLd: _jsonLd!.WriteQuad(subject, predicate, obj, graph); return;
        }

        if (!graph.IsEmpty)
            throw new NotSupportedException(
                $"{_format} has no named graphs, so the statement in graph {graph.ToString()} cannot be written. Write N-Quads, TriG or JSON-LD to keep the graphs.");

        switch (_format)
        {
            case RdfFormat.NTriples: _nTriples!.WriteTriple(subject, predicate, obj); return;
            case RdfFormat.Turtle: _turtle!.WriteTriple(subject, predicate, obj); return;
            case RdfFormat.RdfXml: _rdfXml!.WriteTriple(subject, predicate, obj); return;
        }
    }

    /// <summary>Completes the document and returns the writers' buffers. The TextWriter stays open.</summary>
    public void Dispose()
    {
        _nTriples?.Dispose();
        _nQuads?.Dispose();
        _turtle?.Dispose();
        _triG?.Dispose();
        _rdfXml?.Dispose();
        _jsonLd?.Dispose();
    }
}
