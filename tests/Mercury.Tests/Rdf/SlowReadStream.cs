namespace SkyOmega.Mercury.Tests.Rdf;

// Also compiled into Mercury.Solid.Tests (linked in its project file), so the N3 Patch parser is read the same way.

/// <summary>
/// Stream wrapper that limits each Read to at most N bytes, simulating
/// network/disk reads that return partial data. Forces the parser's
/// FillBuffer path to handle small refills.
/// </summary>
internal sealed class SlowReadStream : Stream
{
    private readonly Stream _inner;
    private readonly int _maxBytesPerRead;

    public SlowReadStream(Stream inner, int maxBytesPerRead)
    {
        _inner = inner;
        _maxBytesPerRead = maxBytesPerRead;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => _inner.Read(buffer, offset, Math.Min(count, _maxBytesPerRead));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => _inner.ReadAsync(buffer, offset, Math.Min(count, _maxBytesPerRead), ct);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        => _inner.ReadAsync(buffer.Slice(0, Math.Min(buffer.Length, _maxBytesPerRead)), ct);

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
