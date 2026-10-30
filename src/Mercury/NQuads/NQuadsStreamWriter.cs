// NQuadsStreamWriter.cs
// Zero-GC streaming N-Quads writer, and through NTriplesStreamWriter the N-Triples writer
// Writes canonical N-Quads and N-Triples (W3C RDF 1.2 N-Triples, Canonical N-Triples)
// No external dependencies, only BCL
// .NET 10 / C# 14

using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.Runtime.Buffers;

namespace SkyOmega.Mercury.NQuads;

/// <summary>
/// Zero-allocation streaming writer for RDF N-Quads format, the one writer of N-Quads and of N-Triples, which is N-Quads
/// in the default graph (<see cref="NTriples.NTriplesStreamWriter"/> writes through this class).
///
/// N-Quads grammar:
/// [1] statement  ::= subject predicate object graphLabel? '.'
/// [2] subject    ::= IRIREF | BLANK_NODE_LABEL
/// [3] predicate  ::= IRIREF
/// [4] object     ::= IRIREF | BLANK_NODE_LABEL | literal
/// [5] graphLabel ::= IRIREF | BLANK_NODE_LABEL
///
/// Literals are written in canonical form (RDF 1.2 N-Triples, Canonical N-Triples): BS, HT, LF, FF, CR, '"' and '\'
/// as ECHAR; U+0000-U+0007, VT, U+000E-U+001F, DEL and the characters outside XML 1.1's Char (U+FFFE, U+FFFF, lone
/// surrogates) as \uXXXX with uppercase hex; every other character as itself. A language tag is written in lowercase,
/// and an xsd:string literal without its datatype.
///
/// Usage:
///   await using var writer = new NQuadsStreamWriter(textWriter);
///   writer.WriteQuad(subject, predicate, obj, graph);
///   // or for default graph:
///   writer.WriteQuad(subject, predicate, obj);
/// </summary>
internal sealed class NQuadsStreamWriter : IDisposable, IAsyncDisposable
{
    private readonly TextWriter _writer;
    private readonly IBufferManager _bufferManager;
    private char[] _buffer;
    private int _bufferPos;
    private bool _isDisposed;

    internal const int DefaultBufferSize = 4096;

    private const string XsdStringSuffix = "^^<http://www.w3.org/2001/XMLSchema#string>";

    public NQuadsStreamWriter(TextWriter writer, int bufferSize = DefaultBufferSize, IBufferManager? bufferManager = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _bufferManager = bufferManager ?? PooledBufferManager.Shared;
        _buffer = _bufferManager.Rent<char>(bufferSize).Array!;
        _bufferPos = 0;
        _isDisposed = false;
    }

    /// <summary>
    /// Write a quad in N-Quads format (synchronous).
    /// Terms are in Mercury's term form:
    /// - IRIs: &lt;http://example.org/foo&gt; (with angle brackets)
    /// - Blank nodes: _:b0
    /// - Literals: "value", "value"@lang, "value"^^&lt;datatype&gt;, with the value's escapes decoded
    /// Graph is optional; if empty or default, writes a triple.
    /// </summary>
    public void WriteQuad(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj, ReadOnlySpan<char> graph = default)
    {
        WriteStatement(subject, predicate, obj, graph);
        FlushBuffer();
    }

    /// <summary>
    /// Write a quad in N-Quads format (asynchronous).
    /// Uses ReadOnlyMemory for async compatibility (spans can't cross await).
    /// </summary>
    public async ValueTask WriteQuadAsync(ReadOnlyMemory<char> subject, ReadOnlyMemory<char> predicate,
        ReadOnlyMemory<char> obj, ReadOnlyMemory<char> graph = default, CancellationToken ct = default)
    {
        WriteStatement(subject.Span, predicate.Span, obj.Span, graph.Span);
        await FlushBufferAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Write a quad in N-Quads format (asynchronous, string overload).
    /// </summary>
    public ValueTask WriteQuadAsync(string subject, string predicate, string obj,
        string? graph = null, CancellationToken ct = default)
    {
        return WriteQuadAsync(
            subject.AsMemory(),
            predicate.AsMemory(),
            obj.AsMemory(),
            graph?.AsMemory() ?? ReadOnlyMemory<char>.Empty,
            ct);
    }

    /// <summary>
    /// Write a raw quad from query results.
    /// The terms are written as-is - caller ensures proper formatting.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteRawQuad(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj, ReadOnlySpan<char> graph = default)
    {
        WriteSpan(subject);
        WriteChar(' ');
        WriteSpan(predicate);
        WriteChar(' ');
        WriteSpan(obj);
        WriteChar(' ');

        if (!graph.IsEmpty)
        {
            WriteSpan(graph);
            WriteChar(' ');
        }

        WriteChar('.');
        WriteChar('\n');
        FlushBuffer();
    }

    /// <summary>
    /// Write a triple (default graph) in N-Quads format.
    /// Convenience method for triples without named graph.
    /// </summary>
    public void WriteTriple(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj)
    {
        WriteQuad(subject, predicate, obj, ReadOnlySpan<char>.Empty);
    }

    /// <summary>
    /// subject predicate object graphLabel? '.' and LF, each term followed by a single space (Canonical N-Triples).
    /// </summary>
    private void WriteStatement(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj, ReadOnlySpan<char> graph)
    {
        WriteTerm(subject);
        WriteChar(' ');
        WriteTerm(predicate);
        WriteChar(' ');
        WriteTerm(obj);
        WriteChar(' ');

        if (!graph.IsEmpty)
        {
            WriteTerm(graph);
            WriteChar(' ');
        }

        WriteChar('.');
        WriteChar('\n');
    }

    /// <summary>
    /// Write a term, applying N-Quads escaping for literals.
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
    /// Write a literal in canonical form.
    /// Input format: "value", "value"@lang, or "value"^^&lt;datatype&gt;
    /// </summary>
    /// <remarks>
    /// The parsers decode a literal's escapes in memory: the term holds the value, not the wire form. So a forward scan
    /// with backslash tracking cannot find the closing quote; an internal quote and the closing one look the same.
    /// Instead, the closing quote is found from the suffix's shape, scanning from the end:
    ///
    /// - ends with <c>&gt;</c> → datatype form, find <c>"^^&lt;</c> backward
    /// - ends with lang-tag chars + <c>@</c> → lang form, find <c>"@</c> backward
    /// - ends with <c>"</c> → plain form, last char is the close quote
    ///
    /// The parsers always write a datatype IRI in <c>&lt;...&gt;</c> form. See
    /// docs/validations/bulk-load-gradient-2026-04-17.md for the failure mode this replaces (this writer's own forward
    /// scan kept it until 2026-10-30).
    /// </remarks>
    private void WriteLiteral(ReadOnlySpan<char> literal)
    {
        if (literal.Length < 2)
        {
            // Degenerate — write as-is and bail
            WriteSpan(literal);
            return;
        }

        int closeQuote;
        int suffixStart;

        char last = literal[literal.Length - 1];

        if (last == '>')
        {
            // Datatype form: "lexical"^^<iri>
            // Find the `^^<` marker by scanning backward from the end.
            var idx = literal.LastIndexOf("^^<".AsSpan());
            if (idx >= 1 && literal[idx - 1] == '"')
            {
                closeQuote = idx - 1;
                suffixStart = closeQuote + 1;
            }
            else
            {
                // Malformed input — fall back to plain treatment
                closeQuote = literal.Length - 1;
                suffixStart = literal.Length;
            }
        }
        else if (last == '"')
        {
            // Plain form: "lexical"
            closeQuote = literal.Length - 1;
            suffixStart = literal.Length;
        }
        else
        {
            // Lang-tag form: "lexical"@lang[-subtags][--dir]
            // Walk back over valid BCP-47 lang-tag chars, then require '@'
            // preceded by '"'.
            int i = literal.Length - 1;
            while (i > 0 && IsLangChar(literal[i])) i--;
            if (i >= 2 && literal[i] == '@' && literal[i - 1] == '"')
            {
                closeQuote = i - 1;
                suffixStart = closeQuote + 1;
            }
            else
            {
                // Malformed input — fall back: use last '"' as close if present.
                var lastQuote = literal.LastIndexOf('"');
                closeQuote = lastQuote > 0 ? lastQuote : literal.Length - 1;
                suffixStart = closeQuote + 1;
            }
        }

        WriteChar('"');
        WriteEscapedString(literal.Slice(1, closeQuote - 1));
        WriteChar('"');

        var suffix = literal.Slice(suffixStart);
        if (suffix.IsEmpty || suffix.SequenceEqual(XsdStringSuffix))
            return;   // Canonical N-Triples: an xsd:string literal is written without its datatype

        if (suffix[0] == '@')
        {
            // Canonical N-Triples: a language tag in lowercase letters
            foreach (var c in suffix)
                WriteChar(c >= 'A' && c <= 'Z' ? (char)(c + ('a' - 'A')) : c);
        }
        else
        {
            WriteSpan(suffix);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsLangChar(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-';

    /// <summary>
    /// Write a literal's value in canonical form: ECHAR for BS, HT, LF, FF, CR, '"' and '\'; \uXXXX for U+0000-U+0007,
    /// VT, U+000E-U+001F, DEL and characters outside XML 1.1's Char; every other character, a surrogate pair included,
    /// as itself.
    /// </summary>
    private void WriteEscapedString(ReadOnlySpan<char> str)
    {
        for (int i = 0; i < str.Length; i++)
        {
            char c = str[i];

            switch (c)
            {
                case '\b': WriteChar('\\'); WriteChar('b'); break;
                case '\t': WriteChar('\\'); WriteChar('t'); break;
                case '\n': WriteChar('\\'); WriteChar('n'); break;
                case '\f': WriteChar('\\'); WriteChar('f'); break;
                case '\r': WriteChar('\\'); WriteChar('r'); break;
                case '"': WriteChar('\\'); WriteChar('"'); break;
                case '\\': WriteChar('\\'); WriteChar('\\'); break;
                default:
                    if (c < 0x20 || c == 0x7F || c == '￾' || c == '￿')
                    {
                        WriteUnicodeEscape(c);
                    }
                    else if (char.IsHighSurrogate(c) && i + 1 < str.Length && char.IsLowSurrogate(str[i + 1]))
                    {
                        // A supplementary character, written as itself
                        WriteChar(c);
                        WriteChar(str[i + 1]);
                        i++;
                    }
                    else if (char.IsSurrogate(c))
                    {
                        // A lone surrogate is not a character (XML 1.1 Char excludes it)
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
    /// Write \uXXXX: a lowercase u and four uppercase hex digits (Canonical N-Triples).
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
