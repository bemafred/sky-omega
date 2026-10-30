using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using SkyOmega.Mercury.Runtime.Buffers;

namespace SkyOmega.Mercury.Rdf;

/// <summary>
/// A UTF-8 document read as code points, with lookahead that refills itself. <see cref="Peek"/>,
/// <see cref="PeekAhead"/> and <see cref="PeekAheadCodePoint"/> read the stream until they hold the bytes they need, so
/// a term may cross the end of the buffer and the stream may return any number of bytes per read: a network body, a
/// pipe, a decompressor. Invalid UTF-8 is an error. A byte order mark at the start is skipped
/// (<see cref="Utf8ByteOrderMark"/>).
/// </summary>
/// <remarks>
/// <para>The reading layer of the TriG, RDF/XML and N3 Patch parsers. It is the Turtle parser's self-refilling
/// lookahead (1.7.4) with the N-Quads parser's UTF-8 validation; those two parsers still hold their own copies.</para>
/// <para>Reads inside a peek are synchronous, as in the Turtle and N-Quads parsers: a peek happens mid-term, where the
/// parsers have no await. <see cref="TopUpAsync"/> reads asynchronously between statements, so most reads are
/// asynchronous.</para>
/// </remarks>
internal sealed class Utf8DocumentReader : IDisposable
{
    private readonly Stream _stream;
    private readonly IBufferManager _bufferManager;
    private readonly string _documentFormat;
    private byte[] _buffer;
    private int _position;
    private int _length;
    private bool _endOfStream;

    /// <param name="stream">The document.</param>
    /// <param name="bufferSize">The input buffer's size in bytes; lookahead cannot reach further than this.</param>
    /// <param name="bufferManager">Where the buffer is rented from.</param>
    /// <param name="documentFormat">The format's name, which begins each error message ("TriG", "RDF/XML").</param>
    public Utf8DocumentReader(Stream stream, int bufferSize, IBufferManager bufferManager, string documentFormat)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _bufferManager = bufferManager;
        _documentFormat = documentFormat;
        _buffer = _bufferManager.Rent<byte>(bufferSize).Array!;
    }

    /// <summary>The line of the next code point, from 1.</summary>
    public int Line { get; private set; } = 1;

    /// <summary>The column of the next code point, from 1, counted in code points.</summary>
    public int Column { get; private set; } = 1;

    /// <summary>True when the whole document has been read.</summary>
    public bool AtEnd
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _endOfStream && _position >= _length;
    }

    /// <summary>
    /// The first read: at least <see cref="Utf8ByteOrderMark.Length"/> bytes, or the whole document if it is shorter, so
    /// a byte order mark at the start is recognized and skipped. Called once, before anything else.
    /// </summary>
    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        do
            await FillAsync(cancellationToken).ConfigureAwait(false);
        while (_length - _position < Utf8ByteOrderMark.Length && !_endOfStream && _length < _buffer.Length);

        _position += Utf8ByteOrderMark.LengthAt(_buffer.AsSpan(_position, _length - _position));
    }

    /// <summary>
    /// Reads ahead when less than a quarter of the buffer is unread. Called between statements, where the parser can
    /// await.
    /// </summary>
    public async ValueTask TopUpAsync(CancellationToken cancellationToken)
    {
        if (_length - _position < _buffer.Length / 4 && !_endOfStream)
            await FillAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The next code point, or -1 at the end of the document.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Peek()
    {
        if (_position < _length)
        {
            byte b = _buffer[_position];
            if (b < 0x80)
                return b;
        }
        return PeekNonAscii();
    }

    private int PeekNonAscii()
    {
        while (true)
        {
            if (_position >= _length)
            {
                if (_endOfStream)
                    return -1;
                Fill();
                continue;
            }

            byte b = _buffer[_position];
            if (b < 0x80)
                return b;

            var status = Rune.DecodeFromUtf8(_buffer.AsSpan(_position, _length - _position), out var rune, out _);
            if (status == OperationStatus.Done)
                return rune.Value;
            if (status == OperationStatus.NeedMoreData && !_endOfStream)
            {
                FillOrThrow();
                continue;
            }
            throw Error($"Invalid UTF-8: byte 0x{b:X2}");
        }
    }

    /// <summary>
    /// The byte <paramref name="offset"/> bytes after the next code point's first byte, or -1 past the end of the
    /// document. For ASCII lookahead such as keywords and '_:'.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PeekAhead(int offset)
    {
        while (_position + offset >= _length)
        {
            if (_endOfStream)
                return -1;
            FillOrThrow();
        }
        return _buffer[_position + offset];
    }

    /// <summary>
    /// The code point that starts <paramref name="byteOffset"/> bytes after the next code point's first byte, or -1 past
    /// the end of the document or at invalid UTF-8, which <see cref="Peek"/> reports when it gets there.
    /// </summary>
    public int PeekAheadCodePoint(int byteOffset)
    {
        int b = PeekAhead(byteOffset);
        if (b < 0x80)
            return b;
        while (true)
        {
            int from = _position + byteOffset;
            var status = Rune.DecodeFromUtf8(_buffer.AsSpan(from, _length - from), out var rune, out _);
            if (status == OperationStatus.Done)
                return rune.Value;
            if (status == OperationStatus.NeedMoreData && !_endOfStream)
            {
                FillOrThrow();
                continue;
            }
            return -1;
        }
    }

    /// <summary>Moves past the code point <see cref="Peek"/> returns. At the end of the document it does nothing.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Consume()
    {
        int codePoint = Peek();
        if (codePoint < 0)
            return;

        _position += codePoint < 0x80 ? 1 : new Rune(codePoint).Utf8SequenceLength;
        if (codePoint == '\n')
        {
            Line++;
            Column = 1;
        }
        else
        {
            Column++;
        }
    }

    /// <summary>An error at the next code point, in the form "{format} parse error at line L, column C: message".</summary>
    public InvalidDataException Error(string message)
        => new($"{_documentFormat} parse error at line {Line}, column {Column}: {message}");

    private async ValueTask FillAsync(CancellationToken cancellationToken)
    {
        if (_endOfStream)
            return;
        ShiftUnreadToStart();
        if (_length == _buffer.Length)
            return;

        int read = await _stream.ReadAsync(_buffer.AsMemory(_length, _buffer.Length - _length), cancellationToken)
            .ConfigureAwait(false);
        if (read == 0)
            _endOfStream = true;
        else
            _length += read;
    }

    private void Fill()
    {
        if (_endOfStream)
            return;
        ShiftUnreadToStart();
        if (_length == _buffer.Length)
            return;

        int read = _stream.Read(_buffer, _length, _buffer.Length - _length);
        if (read == 0)
            _endOfStream = true;
        else
            _length += read;
    }

    /// <summary>
    /// A read that must make progress: lookahead that needs more bytes than the buffer holds is an error, where a
    /// silent return would let a caller loop on the same position.
    /// </summary>
    private void FillOrThrow()
    {
        int unread = _length - _position;
        Fill();
        if (!_endOfStream && _length - _position == unread)
            throw Error($"Lookahead past the {_buffer.Length}-byte input buffer");
    }

    private void ShiftUnreadToStart()
    {
        if (_position >= _length)
        {
            _position = 0;
            _length = 0;
        }
        else if (_position > 0)
        {
            int unread = _length - _position;
            Array.Copy(_buffer, _position, _buffer, 0, unread);
            _length = unread;
            _position = 0;
        }
    }

    public void Dispose()
    {
        if (_buffer.Length == 0)
            return;
        _bufferManager.Return(_buffer);
        _buffer = [];
    }
}
