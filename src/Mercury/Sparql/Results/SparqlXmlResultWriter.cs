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
public sealed class SparqlXmlResultWriter : IDisposable, IAsyncDisposable
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
            var name = v.StartsWith("?") ? v.Substring(1) : v;
            WriteXmlEscaped(name.AsSpan());
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
            var varWithQ = varName.StartsWith("?") ? varName : "?" + varName;
            var idx = bindings.FindBinding(varWithQ.AsSpan());
            if (idx < 0) continue; // Unbound variable

            WriteString("      <binding name=\"");
            var nameToWrite = varName.StartsWith("?") ? varName.Substring(1) : varName;
            WriteXmlEscaped(nameToWrite.AsSpan());
            WriteString("\">\n");

            WriteBindingValue(ref bindings, idx);

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
                // Check if it's a literal, URI, or blank node
                if (value.Length > 0 && value[0] == '<')
                {
                    // URI
                    WriteString("        <uri>");
                    WriteXmlEscaped(value.Slice(1, value.Length - 2));
                    WriteString("</uri>\n");
                }
                else if (value.Length > 1 && value[0] == '_' && value[1] == ':')
                {
                    // Blank node
                    WriteString("        <bnode>");
                    WriteXmlEscaped(value.Slice(2));
                    WriteString("</bnode>\n");
                }
                else if (value.Length > 0 && value[0] == '"')
                {
                    // Literal - parse it
                    WriteLiteralBinding(value);
                }
                else
                {
                    // Plain string value
                    WriteString("        <literal>");
                    WriteXmlEscaped(value);
                    WriteString("</literal>\n");
                }
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

    private void WriteLiteralBinding(ReadOnlySpan<char> literal)
    {
        // Parse: "value"@lang or "value"^^<datatype> or "value"
        if (literal.Length < 2 || literal[0] != '"')
        {
            WriteString("        <literal>");
            WriteXmlEscaped(literal);
            WriteString("</literal>\n");
            return;
        }

        // Find closing quote
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
            WriteString("        <literal>");
            WriteXmlEscaped(literal);
            WriteString("</literal>\n");
            return;
        }

        var value = literal.Slice(1, closeQuote - 1);
        var suffix = literal.Slice(closeQuote + 1);

        if (suffix.StartsWith("@".AsSpan()))
        {
            // Language tag
            WriteString("        <literal xml:lang=\"");
            WriteSpan(suffix.Slice(1));
            WriteString("\">");
            WriteXmlEscapedLiteral(value);
            WriteString("</literal>\n");
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
            WriteXmlEscapedLiteral(value);
            WriteString("</literal>\n");
        }
        else
        {
            // Plain literal
            WriteString("        <literal>");
            WriteXmlEscapedLiteral(value);
            WriteString("</literal>\n");
        }
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
