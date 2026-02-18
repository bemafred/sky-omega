// NTriplesStreamWriter.cs
// Zero-GC streaming N-Triples writer
// Based on W3C RDF 1.1 N-Triples specification
// No external dependencies, only BCL
// .NET 10 / C# 14

using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.Runtime.Buffers;

namespace SkyOmega.Mercury.NTriples;

/// <summary>
/// Zero-allocation streaming writer for RDF N-Triples format.
/// Implements W3C RDF 1.1 N-Triples specification.
///
/// N-Triples grammar:
/// [1] triple ::= subject predicate object '.'
/// [2] subject ::= IRIREF | BLANK_NODE_LABEL
/// [3] predicate ::= IRIREF
/// [4] object ::= IRIREF | BLANK_NODE_LABEL | literal
///
/// Usage:
///   await using var writer = new NTriplesStreamWriter(textWriter);
///   writer.WriteTriple(subject, predicate, obj);
///   // or async:
///   await writer.WriteTripleAsync(subject, predicate, obj);
/// </summary>
internal sealed class NTriplesStreamWriter : IDisposable, IAsyncDisposable
{
    private readonly TextWriter _writer;
    private readonly IBufferManager _bufferManager;
    private char[] _buffer;
    private int _bufferPos;
    private bool _isDisposed;

    private const int DefaultBufferSize = 4096;

    public NTriplesStreamWriter(TextWriter writer, int bufferSize = DefaultBufferSize, IBufferManager? bufferManager = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _bufferManager = bufferManager ?? PooledBufferManager.Shared;
        _buffer = _bufferManager.Rent<char>(bufferSize).Array!;
        _bufferPos = 0;
        _isDisposed = false;
    }

    /// <summary>
    /// Write a triple in N-Triples format (synchronous).
    /// Terms should be in their canonical form:
    /// - IRIs: &lt;http://example.org/foo&gt; (with angle brackets)
    /// - Blank nodes: _:b0
    /// - Literals: "value", "value"@lang, "value"^^&lt;datatype&gt;
    /// </summary>
    public void WriteTriple(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj)
    {
        WriteTerm(subject);
        WriteChar(' ');
        WriteTerm(predicate);
        WriteChar(' ');
        WriteTerm(obj);
        WriteChar(' ');
        WriteChar('.');
        WriteChar('\n');
        FlushBuffer();
    }

    /// <summary>
    /// Write a triple in N-Triples format (asynchronous).
    /// Uses ReadOnlyMemory for async compatibility (spans can't cross await).
    /// </summary>
    public async ValueTask WriteTripleAsync(ReadOnlyMemory<char> subject, ReadOnlyMemory<char> predicate,
        ReadOnlyMemory<char> obj, CancellationToken ct = default)
    {
        WriteTerm(subject.Span);
        WriteChar(' ');
        WriteTerm(predicate.Span);
        WriteChar(' ');
        WriteTerm(obj.Span);
        WriteChar(' ');
        WriteChar('.');
        WriteChar('\n');
        await FlushBufferAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Write a triple in N-Triples format (asynchronous, string overload).
    /// </summary>
    public ValueTask WriteTripleAsync(string subject, string predicate, string obj, CancellationToken ct = default)
    {
        return WriteTripleAsync(subject.AsMemory(), predicate.AsMemory(), obj.AsMemory(), ct);
    }

    /// <summary>
    /// Write a raw triple from CONSTRUCT/DESCRIBE results.
    /// The terms are written as-is - caller ensures proper formatting.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteRawTriple(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj)
    {
        WriteSpan(subject);
        WriteChar(' ');
        WriteSpan(predicate);
        WriteChar(' ');
        WriteSpan(obj);
        WriteChar(' ');
        WriteChar('.');
        WriteChar('\n');
        FlushBuffer();
    }

    /// <summary>
    /// Write a term, applying N-Triples escaping for literals.
    /// IRIs and blank nodes are written as-is.
    /// </summary>
    private void WriteTerm(ReadOnlySpan<char> term)
    {
        if (term.IsEmpty) return;

        // Check if it's a literal (starts with ")
        if (term[0] == '"')
        {
            WriteLiteral(term);
        }
        else
        {
            // IRI or blank node - write as-is
            WriteSpan(term);
        }
    }

    /// <summary>
    /// Write a literal with proper N-Triples escaping.
    /// Input format: "value", "value"@lang, or "value"^^<datatype>
    /// </summary>
    private void WriteLiteral(ReadOnlySpan<char> literal)
    {
        // Find the end of the quoted string
        int closeQuote = 1;
        bool inEscape = false;
        for (int i = 1; i < literal.Length; i++)
        {
            if (inEscape)
            {
                inEscape = false;
                continue;
            }
            if (literal[i] == '\\')
            {
                inEscape = true;
                continue;
            }
            if (literal[i] == '"')
            {
                closeQuote = i;
                break;
            }
        }

        // Write opening quote
        WriteChar('"');

        // Write escaped content
        var content = literal.Slice(1, closeQuote - 1);
        WriteEscapedString(content);

        // Write closing quote
        WriteChar('"');

        // Write suffix (language tag or datatype) as-is
        if (closeQuote + 1 < literal.Length)
        {
            var suffix = literal.Slice(closeQuote + 1);
            WriteSpan(suffix);
        }
    }

    /// <summary>
    /// Write a string with N-Triples escape sequences.
    /// Escapes: \t \n \r \" \\ and non-ASCII as \uXXXX or \UXXXXXXXX
    /// </summary>
    private void WriteEscapedString(ReadOnlySpan<char> str)
    {
        for (int i = 0; i < str.Length; i++)
        {
            char c = str[i];

            switch (c)
            {
                case '\t':
                    WriteChar('\\');
                    WriteChar('t');
                    break;
                case '\n':
                    WriteChar('\\');
                    WriteChar('n');
                    break;
                case '\r':
                    WriteChar('\\');
                    WriteChar('r');
                    break;
                case '"':
                    WriteChar('\\');
                    WriteChar('"');
                    break;
                case '\\':
                    WriteChar('\\');
                    WriteChar('\\');
                    break;
                default:
                    if (c < 0x20)
                    {
                        // Control character - escape as \uXXXX
                        WriteUnicodeEscape(c);
                    }
                    else if (c <= 0x7E)
                    {
                        // Printable ASCII - write directly
                        WriteChar(c);
                    }
                    else if (char.IsHighSurrogate(c) && i + 1 < str.Length && char.IsLowSurrogate(str[i + 1]))
                    {
                        // Surrogate pair - escape as \UXXXXXXXX
                        int codePoint = char.ConvertToUtf32(c, str[i + 1]);
                        WriteUnicodeEscape32(codePoint);
                        i++; // Skip low surrogate
                    }
                    else if (c > 0x7E)
                    {
                        // Non-ASCII BMP character - escape as \uXXXX
                        WriteUnicodeEscape(c);
                    }
                    else
                    {
                        WriteChar(c);
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Write \uXXXX escape for BMP characters.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteUnicodeEscape(char c)
    {
        WriteChar('\\');
        WriteChar('u');
        WriteHexDigit((c >> 12) & 0xF);
        WriteHexDigit((c >> 8) & 0xF);
        WriteHexDigit((c >> 4) & 0xF);
        WriteHexDigit(c & 0xF);
    }

    /// <summary>
    /// Write \UXXXXXXXX escape for supplementary characters.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteUnicodeEscape32(int codePoint)
    {
        WriteChar('\\');
        WriteChar('U');
        WriteHexDigit((codePoint >> 28) & 0xF);
        WriteHexDigit((codePoint >> 24) & 0xF);
        WriteHexDigit((codePoint >> 20) & 0xF);
        WriteHexDigit((codePoint >> 16) & 0xF);
        WriteHexDigit((codePoint >> 12) & 0xF);
        WriteHexDigit((codePoint >> 8) & 0xF);
        WriteHexDigit((codePoint >> 4) & 0xF);
        WriteHexDigit(codePoint & 0xF);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteHexDigit(int digit)
    {
        WriteChar(digit < 10 ? (char)('0' + digit) : (char)('A' + digit - 10));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteChar(char c)
    {
        EnsureCapacity(1);
        _buffer[_bufferPos++] = c;
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
                // Need a larger buffer
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
        FlushBuffer();
        _writer.Flush();
    }

    /// <summary>
    /// Flush any buffered content to the underlying writer (async).
    /// </summary>
    public async ValueTask FlushAsync(CancellationToken ct = default)
    {
        await FlushBufferAsync(ct).ConfigureAwait(false);
        await _writer.FlushAsync(ct).ConfigureAwait(false);
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
