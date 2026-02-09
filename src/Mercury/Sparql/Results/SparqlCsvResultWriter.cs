// SparqlCsvResultWriter.cs
// SPARQL Query Results CSV Format writer
// Based on W3C SPARQL 1.1 Query Results CSV and TSV Formats
// https://www.w3.org/TR/sparql11-results-csv-tsv/
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
/// Writes SPARQL SELECT query results in CSV or TSV format.
/// Follows W3C SPARQL 1.1 Query Results CSV and TSV Formats specification.
///
/// CSV format:
/// s,p,o
/// http://example.org/s,http://example.org/p,value
///
/// TSV format:
/// ?s	?p	?o
/// &lt;http://example.org/s&gt;	&lt;http://example.org/p&gt;	"value"
/// </summary>
public sealed class SparqlCsvResultWriter : IDisposable, IAsyncDisposable
{
    private readonly TextWriter _writer;
    private readonly IBufferManager _bufferManager;
    private char[] _buffer;
    private int _bufferPos;
    private bool _isDisposed;
    private bool _headWritten;
    private string[]? _variables;
    private readonly bool _useTsv;
    private readonly char _separator;

    private const int DefaultBufferSize = 4096;

    /// <summary>
    /// Create a CSV result writer.
    /// </summary>
    public SparqlCsvResultWriter(TextWriter writer, int bufferSize = DefaultBufferSize, IBufferManager? bufferManager = null)
        : this(writer, useTsv: false, bufferSize, bufferManager)
    {
    }

    /// <summary>
    /// Create a CSV or TSV result writer.
    /// </summary>
    public SparqlCsvResultWriter(TextWriter writer, bool useTsv, int bufferSize = DefaultBufferSize, IBufferManager? bufferManager = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _bufferManager = bufferManager ?? PooledBufferManager.Shared;
        _buffer = _bufferManager.Rent<char>(bufferSize).Array!;
        _bufferPos = 0;
        _isDisposed = false;
        _headWritten = false;
        _variables = null;
        _useTsv = useTsv;
        _separator = useTsv ? '\t' : ',';
    }

    /// <summary>
    /// Write the header row with variable names.
    /// Must be called before writing any results.
    /// </summary>
    public void WriteHead(ReadOnlySpan<string> variables)
    {
        if (_headWritten)
            throw new InvalidOperationException("Head already written");

        _variables = variables.ToArray();
        _headWritten = true;

        for (int i = 0; i < variables.Length; i++)
        {
            if (i > 0) WriteChar(_separator);

            var name = variables[i];
            if (_useTsv)
            {
                // TSV uses ?var format
                if (!name.StartsWith("?"))
                    WriteChar('?');
                WriteSpan(name.AsSpan());
            }
            else
            {
                // CSV strips the ?
                var nameToWrite = name.StartsWith("?") ? name.Substring(1) : name;
                WriteCsvField(nameToWrite.AsSpan());
            }
        }

        WriteChar('\n');
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

        for (int i = 0; i < _variables.Length; i++)
        {
            if (i > 0) WriteChar(_separator);

            var varName = _variables[i];
            var varWithQ = varName.StartsWith("?") ? varName : "?" + varName;
            var idx = bindings.FindBinding(varWithQ.AsSpan());

            if (idx >= 0)
            {
                WriteBindingValue(ref bindings, idx);
            }
            // Unbound variables produce empty field
        }

        WriteChar('\n');
    }

    /// <summary>
    /// Flush buffered content (call after WriteResult if using async pattern).
    /// </summary>
    public async ValueTask FlushResultsAsync(CancellationToken ct = default)
    {
        await FlushBufferAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Flush and complete the output.
    /// </summary>
    public void WriteEnd()
    {
        FlushBuffer();
    }

    /// <summary>
    /// Flush and complete (async).
    /// </summary>
    public async ValueTask WriteEndAsync(CancellationToken ct = default)
    {
        await FlushBufferAsync(ct).ConfigureAwait(false);
    }

    private void WriteBindingValue(scoped ref BindingTable bindings, int idx)
    {
        var type = bindings.GetType(idx);
        var value = bindings.GetString(idx);

        if (_useTsv)
        {
            WriteTsvValue(type, value);
        }
        else
        {
            WriteCsvValue(type, value);
        }
    }

    private void WriteCsvValue(BindingValueType type, ReadOnlySpan<char> value)
    {
        switch (type)
        {
            case BindingValueType.Uri:
                // Strip angle brackets for CSV
                if (value.Length >= 2 && value[0] == '<' && value[^1] == '>')
                {
                    WriteCsvField(value.Slice(1, value.Length - 2));
                }
                else
                {
                    WriteCsvField(value);
                }
                break;

            case BindingValueType.String:
                if (value.Length > 0 && value[0] == '<')
                {
                    // URI - strip brackets
                    WriteCsvField(value.Slice(1, value.Length - 2));
                }
                else if (value.Length > 1 && value[0] == '_' && value[1] == ':')
                {
                    // Blank node
                    WriteCsvField(value);
                }
                else if (value.Length > 0 && value[0] == '"')
                {
                    // Literal - extract value
                    WriteCsvLiteral(value);
                }
                else
                {
                    WriteCsvField(value);
                }
                break;

            default:
                WriteCsvField(value);
                break;
        }
    }

    private void WriteTsvValue(BindingValueType type, ReadOnlySpan<char> value)
    {
        switch (type)
        {
            case BindingValueType.Uri:
                // TSV keeps angle brackets for URIs
                if (value.Length >= 2 && value[0] == '<' && value[^1] == '>')
                {
                    WriteTsvField(value);
                }
                else
                {
                    WriteChar('<');
                    WriteTsvField(value);
                    WriteChar('>');
                }
                break;

            case BindingValueType.String:
                if (value.Length > 0 && value[0] == '<')
                {
                    // URI with brackets
                    WriteTsvField(value);
                }
                else if (value.Length > 1 && value[0] == '_' && value[1] == ':')
                {
                    // Blank node
                    WriteTsvField(value);
                }
                else if (value.Length > 0 && value[0] == '"')
                {
                    // Literal - keep as-is for TSV
                    WriteTsvField(value);
                }
                else
                {
                    // Plain value - quote it
                    WriteChar('"');
                    WriteTsvField(value);
                    WriteChar('"');
                }
                break;

            case BindingValueType.Integer:
            case BindingValueType.Double:
            case BindingValueType.Boolean:
                // Numeric/boolean values as-is
                WriteTsvField(value);
                break;

            default:
                WriteTsvField(value);
                break;
        }
    }

    private void WriteCsvLiteral(ReadOnlySpan<char> literal)
    {
        // Parse: "value"@lang or "value"^^<datatype> or "value"
        if (literal.Length < 2 || literal[0] != '"')
        {
            WriteCsvField(literal);
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
            WriteCsvField(literal);
            return;
        }

        var value = literal.Slice(1, closeQuote - 1);
        // For CSV, we just output the plain value (losing lang/datatype)
        WriteCsvFieldLiteral(value);
    }

    private void WriteCsvField(ReadOnlySpan<char> field)
    {
        // Check if quoting is needed
        bool needsQuoting = false;
        foreach (var c in field)
        {
            if (c == ',' || c == '"' || c == '\n' || c == '\r')
            {
                needsQuoting = true;
                break;
            }
        }

        if (needsQuoting)
        {
            WriteChar('"');
            foreach (var c in field)
            {
                if (c == '"')
                {
                    WriteString("\"\""); // Escape quotes by doubling
                }
                else
                {
                    WriteChar(c);
                }
            }
            WriteChar('"');
        }
        else
        {
            WriteSpan(field);
        }
    }

    private void WriteCsvFieldLiteral(ReadOnlySpan<char> field)
    {
        // Handle escape sequences and check if quoting is needed
        bool needsQuoting = false;
        foreach (var c in field)
        {
            if (c == ',' || c == '"' || c == '\n' || c == '\r' || c == '\\')
            {
                needsQuoting = true;
                break;
            }
        }

        if (needsQuoting)
        {
            WriteChar('"');
            for (int i = 0; i < field.Length; i++)
            {
                var c = field[i];
                if (c == '\\' && i + 1 < field.Length)
                {
                    var next = field[i + 1];
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
                            WriteString("\"\"");
                            i++;
                            continue;
                    }
                }
                if (c == '"')
                {
                    WriteString("\"\"");
                }
                else
                {
                    WriteChar(c);
                }
            }
            WriteChar('"');
        }
        else
        {
            WriteSpan(field);
        }
    }

    private void WriteTsvField(ReadOnlySpan<char> field)
    {
        // TSV escapes tabs and newlines
        foreach (var c in field)
        {
            switch (c)
            {
                case '\t':
                    WriteString("\\t");
                    break;
                case '\n':
                    WriteString("\\n");
                    break;
                case '\r':
                    WriteString("\\r");
                    break;
                default:
                    WriteChar(c);
                    break;
            }
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
