// SparqlXmlResultWriter.cs
// SPARQL Query Results XML Format writer
// Based on W3C SPARQL Query Results XML Format
// https://www.w3.org/TR/rdf-sparql-XMLres/
// No external dependencies, only BCL
// .NET 10 / C# 14

using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.Runtime.Buffers;
using SkyOmega.Mercury.Sparql.Types;
using SkyOmega.Mercury.Rdf;

namespace SkyOmega.Mercury.Sparql.Results;

/// <summary>
/// Writes SPARQL SELECT query results in XML format.
/// Follows W3C SPARQL Query Results XML Format specification.
///
/// Output format:
/// &lt;?xml version="1.0"?&gt;
/// &lt;sparql xmlns="http://www.w3.org/2005/sparql-results#"&gt;
///   &lt;head&gt;
///     &lt;variable name="s"/&gt;
///   &lt;/head&gt;
///   &lt;results&gt;
///     &lt;result&gt;
///       &lt;binding name="s"&gt;
///         &lt;uri&gt;http://example.org/s&lt;/uri&gt;
///       &lt;/binding&gt;
///     &lt;/result&gt;
///   &lt;/results&gt;
/// &lt;/sparql&gt;
/// </summary>
internal sealed class SparqlXmlResultWriter : IDisposable, IAsyncDisposable
{
    private readonly TextWriter _writer;
    private readonly IBufferManager _bufferManager;
    private char[] _buffer;
    private int _bufferPos;
    private bool _isDisposed;
    private bool _headWritten;
    private bool _resultsStarted;
    private string[]? _variables;

    private const int DefaultBufferSize = 4096;
    private const string SparqlResultsNamespace = "http://www.w3.org/2005/sparql-results#";

    public SparqlXmlResultWriter(TextWriter writer, int bufferSize = DefaultBufferSize, IBufferManager? bufferManager = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _bufferManager = bufferManager ?? PooledBufferManager.Shared;
        _buffer = _bufferManager.Rent<char>(bufferSize).Array!;
        _bufferPos = 0;
        _isDisposed = false;
        _headWritten = false;
        _resultsStarted = false;
        _variables = null;
    }

    /// <summary>
    /// Write the header with variable names.
    /// Must be called before writing any results.
    /// </summary>
    public void WriteHead(ReadOnlySpan<string> variables)
    {
        if (_headWritten)
            throw new InvalidOperationException("Head already written");

        _variables = variables.ToArray();
        _headWritten = true;

        WriteString("<?xml version=\"1.0\"?>\n");
        WriteString("<sparql xmlns=\"");
        WriteString(SparqlResultsNamespace);
        WriteString("\">\n");
        WriteString("  <head>\n");

        foreach (var v in variables)
        {
            WriteString("    <variable name=\"");
            WriteXmlEscaped(VariableName.WithoutSigil(v.AsSpan()));
            WriteString("\"/>\n");
        }

        WriteString("  </head>\n");
        FlushBuffer();
    }

    /// <summary>
    /// Write the header (async).
    /// </summary>
    public async ValueTask WriteHeadAsync(string[] variables, CancellationToken ct = default)
    {
        WriteHead(variables);
        await FlushBufferAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Write a result row from a BindingTable.
    /// </summary>
    public void WriteResult(scoped ref BindingTable bindings)
    {
        if (!_headWritten)
            throw new InvalidOperationException("Must call WriteHead first");
        if (_variables == null)
            throw new InvalidOperationException("No variables defined");

        if (!_resultsStarted)
        {
            WriteString("  <results>\n");
            _resultsStarted = true;
        }

        WriteString("    <result>\n");

        foreach (var varName in _variables)
        {
            var idx = bindings.FindBinding(varName.AsSpan());
            if (idx < 0) continue; // Unbound variable

            WriteString("      <binding name=\"");
            WriteXmlEscaped(VariableName.WithoutSigil(varName.AsSpan()));
            WriteString("\">\n");

            WriteBindingValue(ref bindings, idx);

            WriteString("      </binding>\n");
        }

        WriteString("    </result>\n");
    }

    /// <summary>
    /// Write a result row of terms as the store holds them (an IRI in angle brackets, <c>_:label</c> or a stored
    /// literal), keyed by variable name without '?'. A head variable with no term is unbound.
    /// </summary>
    public void WriteResult(IReadOnlyDictionary<string, string> row)
    {
        if (!_headWritten)
            throw new InvalidOperationException("Must call WriteHead first");
        if (_variables == null)
            throw new InvalidOperationException("No variables defined");

        if (!_resultsStarted)
        {
            WriteString("  <results>\n");
            _resultsStarted = true;
        }

        WriteString("    <result>\n");

        foreach (var varName in _variables)
        {
            var name = VariableName.WithoutSigil(varName.AsSpan()).ToString();
            if (!row.TryGetValue(name, out var term))
                continue; // Unbound variable

            WriteString("      <binding name=\"");
            WriteXmlEscaped(name);
            WriteString("\">\n");

            WriteTerm(term);

            WriteString("      </binding>\n");
        }

        WriteString("    </result>\n");
    }

    /// <summary>
    /// Flush buffered content (call after WriteResult if using async pattern).
    /// </summary>
    public async ValueTask FlushResultsAsync(CancellationToken ct = default)
    {
        await FlushBufferAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Write the closing structure.
    /// </summary>
    public void WriteEnd()
    {
        if (!_headWritten)
            throw new InvalidOperationException("Must call WriteHead first");

        if (!_resultsStarted)
        {
            WriteString("  <results>\n");
        }

        WriteString("  </results>\n");
        WriteString("</sparql>\n");
        FlushBuffer();
    }

    /// <summary>
    /// Write the closing structure (async).
    /// </summary>
    public async ValueTask WriteEndAsync(CancellationToken ct = default)
    {
        WriteEnd();
        await FlushBufferAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Write a boolean result (for ASK queries).
    /// </summary>
    public void WriteBooleanResult(bool value)
    {
        WriteString("<?xml version=\"1.0\"?>\n");
        WriteString("<sparql xmlns=\"");
        WriteString(SparqlResultsNamespace);
        WriteString("\">\n");
        WriteString("  <head/>\n");
        WriteString("  <boolean>");
        WriteString(value ? "true" : "false");
        WriteString("</boolean>\n");
        WriteString("</sparql>\n");
        FlushBuffer();
    }

    /// <summary>
    /// Write a boolean result (async).
    /// </summary>
    public async ValueTask WriteBooleanResultAsync(bool value, CancellationToken ct = default)
    {
        WriteBooleanResult(value);
        await FlushBufferAsync(ct).ConfigureAwait(false);
    }

    private void WriteBindingValue(scoped ref BindingTable bindings, int idx)
    {
        var type = bindings.GetType(idx);
        var value = bindings.GetString(idx);

        switch (type)
        {
            case BindingValueType.Uri:
                WriteString("        <uri>");
                // Strip angle brackets if present
                if (value.Length >= 2 && value[0] == '<' && value[^1] == '>')
                {
                    WriteXmlEscaped(value.Slice(1, value.Length - 2));
                }
                else
                {
                    WriteXmlEscaped(value);
                }
                WriteString("</uri>\n");
                break;

            case BindingValueType.String:
                WriteTerm(value);
                break;

            case BindingValueType.Integer:
                WriteString("        <literal datatype=\"http://www.w3.org/2001/XMLSchema#integer\">");
                WriteSpan(value);
                WriteString("</literal>\n");
                break;

            case BindingValueType.Double:
                WriteString("        <literal datatype=\"http://www.w3.org/2001/XMLSchema#double\">");
                WriteSpan(value);
                WriteString("</literal>\n");
                break;

            case BindingValueType.Boolean:
                WriteString("        <literal datatype=\"http://www.w3.org/2001/XMLSchema#boolean\">");
                WriteSpan(value);
                WriteString("</literal>\n");
                break;

            default:
                WriteString("        <literal>");
                WriteXmlEscaped(value);
                WriteString("</literal>\n");
                break;
        }
    }

    /// <summary>
    /// A term as the store holds it, as a binding's element: an IRI in angle brackets, <c>_:label</c>, a stored literal
    /// (its lexical form decoded, <see cref="StoredLiteral"/>), or a bare value, written as a simple literal.
    /// </summary>
    private void WriteTerm(ReadOnlySpan<char> term)
    {
        if (term.Length >= 2 && term[0] == '<' && term[^1] == '>')
        {
            WriteString("        <uri>");
            WriteXmlEscaped(term.Slice(1, term.Length - 2));
            WriteString("</uri>\n");
            return;
        }

        if (term.Length > 1 && term[0] == '_' && term[1] == ':')
        {
            WriteString("        <bnode>");
            WriteXmlEscaped(term.Slice(2));
            WriteString("</bnode>\n");
            return;
        }

        if (!StoredLiteral.TrySplit(term, out var lexical, out var suffix))
        {
            WriteString("        <literal>");
            WriteXmlEscaped(term);
            WriteString("</literal>\n");
            return;
        }

        if (suffix.StartsWith("@".AsSpan()))
        {
            // Language tag
            WriteString("        <literal xml:lang=\"");
            WriteXmlEscaped(suffix.Slice(1));
            WriteString("\">");
        }
        else if (suffix.StartsWith("^^".AsSpan()))
        {
            // Datatype
            var datatype = suffix.Slice(2);
            if (datatype.Length >= 2 && datatype[0] == '<' && datatype[^1] == '>')
            {
                datatype = datatype.Slice(1, datatype.Length - 2);
            }
            WriteString("        <literal datatype=\"");
            WriteXmlEscaped(datatype);
            WriteString("\">");
        }
        else
        {
            WriteString("        <literal>");
        }
        WriteXmlEscaped(lexical);
        WriteString("</literal>\n");
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
                case '\'':
                    WriteString("&apos;");
                    break;
                case '\r':
                    // A raw CR is read back as LF (XML 1.0 §2.11)
                    WriteString("&#xD;");
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

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        FlushBuffer();
        _bufferManager.Return(_buffer);
        _buffer = null!;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        await FlushBufferAsync(default).ConfigureAwait(false);
        _bufferManager.Return(_buffer);
        _buffer = null!;
    }
}
