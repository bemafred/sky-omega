// SparqlJsonResultWriter.cs
// SPARQL Query Results JSON Format writer
// Based on W3C SPARQL 1.1 Query Results JSON Format
// https://www.w3.org/TR/sparql11-results-json/
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
/// Writes SPARQL SELECT query results in JSON format.
/// Follows W3C SPARQL 1.1 Query Results JSON Format specification.
///
/// Output format:
/// {
///   "head": { "vars": ["s", "p", "o"] },
///   "results": {
///     "bindings": [
///       { "s": { "type": "uri", "value": "..." }, ... },
///       ...
///     ]
///   }
/// }
/// </summary>
internal sealed class SparqlJsonResultWriter : IDisposable, IAsyncDisposable
{
    private readonly TextWriter _writer;
    private readonly IBufferManager _bufferManager;
    private char[] _buffer;
    private int _bufferPos;
    private bool _isDisposed;
    private bool _headWritten;
    private bool _firstResult;
    private string[]? _variables;

    private const int DefaultBufferSize = 4096;

    public SparqlJsonResultWriter(TextWriter writer, int bufferSize = DefaultBufferSize, IBufferManager? bufferManager = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _bufferManager = bufferManager ?? PooledBufferManager.Shared;
        _buffer = _bufferManager.Rent<char>(bufferSize).Array!;
        _bufferPos = 0;
        _isDisposed = false;
        _headWritten = false;
        _firstResult = true;
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

        WriteString("{\n  \"head\": { \"vars\": [");

        for (int i = 0; i < variables.Length; i++)
        {
            if (i > 0) WriteChar(',');
            WriteChar('"');
            WriteJsonEscaped(variables[i].AsSpan());
            WriteChar('"');
        }

        WriteString("] },\n  \"results\": {\n    \"bindings\": [\n");
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

        if (!_firstResult)
        {
            WriteString(",\n");
        }
        _firstResult = false;

        WriteString("      {");

        bool first = true;
        foreach (var varName in _variables)
        {
            var varWithQ = varName.StartsWith("?") ? varName : "?" + varName;
            var idx = bindings.FindBinding(varWithQ.AsSpan());
            if (idx < 0) continue; // Unbound variable

            if (!first) WriteChar(',');
            first = false;

            WriteString("\n        \"");
            // Write variable name without leading ?
            var nameToWrite = varName.StartsWith("?") ? varName.Substring(1) : varName;
            WriteJsonEscaped(nameToWrite.AsSpan());
            WriteString("\": ");

            WriteBindingValue(ref bindings, idx);
        }

        WriteString("\n      }");
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

        WriteString("\n    ]\n  }\n}\n");
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
        WriteString("{\n  \"head\": { },\n  \"boolean\": ");
        WriteString(value ? "true" : "false");
        WriteString("\n}\n");
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

        WriteChar('{');

        switch (type)
        {
            case BindingValueType.Uri:
                WriteString("\"type\":\"uri\",\"value\":\"");
                // Strip angle brackets if present
                if (value.Length >= 2 && value[0] == '<' && value[^1] == '>')
                {
                    WriteJsonEscaped(value.Slice(1, value.Length - 2));
                }
                else
                {
                    WriteJsonEscaped(value);
                }
                WriteChar('"');
                break;

            case BindingValueType.String:
                // Check if it's a literal, URI, or blank node
                if (value.Length > 0 && value[0] == '<')
                {
                    // URI
                    WriteString("\"type\":\"uri\",\"value\":\"");
                    WriteJsonEscaped(value.Slice(1, value.Length - 2));
                    WriteChar('"');
                }
                else if (value.Length > 1 && value[0] == '_' && value[1] == ':')
                {
                    // Blank node
                    WriteString("\"type\":\"bnode\",\"value\":\"");
                    WriteJsonEscaped(value.Slice(2));
                    WriteChar('"');
                }
                else if (value.Length > 0 && value[0] == '"')
                {
                    // Literal - parse it
                    WriteLiteralBinding(value);
                }
                else
                {
                    // Plain string value
                    WriteString("\"type\":\"literal\",\"value\":\"");
                    WriteJsonEscaped(value);
                    WriteChar('"');
                }
                break;

            case BindingValueType.Integer:
                WriteString("\"type\":\"literal\",\"datatype\":\"http://www.w3.org/2001/XMLSchema#integer\",\"value\":\"");
                WriteSpan(value);
                WriteChar('"');
                break;

            case BindingValueType.Double:
                WriteString("\"type\":\"literal\",\"datatype\":\"http://www.w3.org/2001/XMLSchema#double\",\"value\":\"");
                WriteSpan(value);
                WriteChar('"');
                break;

            case BindingValueType.Boolean:
                WriteString("\"type\":\"literal\",\"datatype\":\"http://www.w3.org/2001/XMLSchema#boolean\",\"value\":\"");
                WriteSpan(value);
                WriteChar('"');
                break;

            default:
                WriteString("\"type\":\"literal\",\"value\":\"");
                WriteJsonEscaped(value);
                WriteChar('"');
                break;
        }

        WriteChar('}');
    }

    private void WriteLiteralBinding(ReadOnlySpan<char> literal)
    {
        // Parse: "value"@lang or "value"^^<datatype> or "value"
        if (literal.Length < 2 || literal[0] != '"')
        {
            WriteString("\"type\":\"literal\",\"value\":\"");
            WriteJsonEscaped(literal);
            WriteChar('"');
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
            WriteString("\"type\":\"literal\",\"value\":\"");
            WriteJsonEscaped(literal);
            WriteChar('"');
            return;
        }

        var value = literal.Slice(1, closeQuote - 1);
        var suffix = literal.Slice(closeQuote + 1);

        WriteString("\"type\":\"literal\",\"value\":\"");
        WriteJsonEscapedLiteral(value);
        WriteChar('"');

        if (suffix.StartsWith("@".AsSpan()))
        {
            // Language tag
            WriteString(",\"xml:lang\":\"");
            WriteSpan(suffix.Slice(1));
            WriteChar('"');
        }
        else if (suffix.StartsWith("^^".AsSpan()))
        {
            // Datatype
            var datatype = suffix.Slice(2);
            if (datatype.Length >= 2 && datatype[0] == '<' && datatype[^1] == '>')
            {
                datatype = datatype.Slice(1, datatype.Length - 2);
            }
            WriteString(",\"datatype\":\"");
            WriteJsonEscaped(datatype);
            WriteChar('"');
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

    private void WriteJsonEscaped(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            switch (c)
            {
                case '"':
                    WriteString("\\\"");
                    break;
                case '\\':
                    WriteString("\\\\");
                    break;
                case '\n':
                    WriteString("\\n");
                    break;
                case '\r':
                    WriteString("\\r");
                    break;
                case '\t':
                    WriteString("\\t");
                    break;
                default:
                    if (c < 32)
                    {
                        WriteString("\\u");
                        WriteHex4((ushort)c);
                    }
                    else
                    {
                        WriteChar(c);
                    }
                    break;
            }
        }
    }

    private void WriteJsonEscapedLiteral(ReadOnlySpan<char> text)
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
                        WriteString("\\n");
                        i++;
                        continue;
                    case 't':
                        WriteString("\\t");
                        i++;
                        continue;
                    case 'r':
                        WriteString("\\r");
                        i++;
                        continue;
                    case '\\':
                        WriteString("\\\\");
                        i++;
                        continue;
                    case '"':
                        WriteString("\\\"");
                        i++;
                        continue;
                }
            }

            switch (c)
            {
                case '"':
                    WriteString("\\\"");
                    break;
                case '\\':
                    WriteString("\\\\");
                    break;
                case '\n':
                    WriteString("\\n");
                    break;
                case '\r':
                    WriteString("\\r");
                    break;
                case '\t':
                    WriteString("\\t");
                    break;
                default:
                    if (c < 32)
                    {
                        WriteString("\\u");
                        WriteHex4((ushort)c);
                    }
                    else
                    {
                        WriteChar(c);
                    }
                    break;
            }
        }
    }

    private void WriteHex4(ushort value)
    {
        const string hex = "0123456789abcdef";
        WriteChar(hex[(value >> 12) & 0xF]);
        WriteChar(hex[(value >> 8) & 0xF]);
        WriteChar(hex[(value >> 4) & 0xF]);
        WriteChar(hex[value & 0xF]);
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
