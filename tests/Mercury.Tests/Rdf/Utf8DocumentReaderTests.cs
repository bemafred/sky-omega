using System.Text;
using SkyOmega.Mercury.Rdf;
using SkyOmega.Mercury.Runtime.Buffers;
using Xunit;

namespace SkyOmega.Mercury.Tests.Rdf;

/// <summary>
/// Utf8DocumentReader, the reading layer of the TriG, RDF/XML and N3 Patch parsers. Each test reads through
/// SlowReadStream at one byte per read, the case its lookahead exists for.
/// </summary>
public class Utf8DocumentReaderTests
{
    [Fact]
    public async Task Peek_OneBytePerRead_ReturnsEachCodePoint()
    {
        await using var stream = new SlowReadStream(new MemoryStream(Encoding.UTF8.GetBytes("aé€😀\nb")), 1);
        using var reader = new Utf8DocumentReader(stream, 16, PooledBufferManager.Shared, "Test");
        await reader.StartAsync(TestContext.Current.CancellationToken);

        var codePoints = new List<int>();
        while (reader.Peek() != -1)
        {
            codePoints.Add(reader.Peek());
            reader.Consume();
        }

        Assert.Equal(new[] { 'a', 0xE9, 0x20AC, 0x1F600, '\n', 'b' }, codePoints);
        Assert.Equal(2, reader.Line);
        Assert.Equal(2, reader.Column);
        Assert.True(reader.AtEnd);
    }

    [Fact]
    public async Task PeekAhead_PastTheBytesRead_ReadsMore()
    {
        await using var stream = new SlowReadStream(new MemoryStream("@prefix"u8.ToArray()), 1);
        using var reader = new Utf8DocumentReader(stream, 16, PooledBufferManager.Shared, "Test");
        await reader.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal('x', reader.PeekAhead(6));
        Assert.Equal(-1, reader.PeekAhead(7));
        Assert.Equal('@', reader.Peek());
    }

    [Fact]
    public async Task Peek_InvalidUtf8_IsAnError()
    {
        await using var stream = new SlowReadStream(new MemoryStream([(byte)'a', 0xFF]), 1);
        using var reader = new Utf8DocumentReader(stream, 16, PooledBufferManager.Shared, "Test");
        await reader.StartAsync(TestContext.Current.CancellationToken);
        reader.Consume();

        var error = Assert.Throws<InvalidDataException>(() => reader.Peek());

        Assert.Equal("Test parse error at line 1, column 2: Invalid UTF-8: byte 0xFF", error.Message);
    }

    [Fact]
    public async Task PeekAhead_FurtherThanTheBuffer_IsAnError()
    {
        // A lookahead the buffer cannot hold is an error, not a silent stop that a caller could loop on.
        await using var stream = new SlowReadStream(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 200))), 1);
        using var reader = new Utf8DocumentReader(stream, 16, PooledBufferManager.Shared, "Test");
        await reader.StartAsync(TestContext.Current.CancellationToken);

        var error = Assert.Throws<InvalidDataException>(() => reader.PeekAhead(100));

        Assert.Contains("Lookahead past the", error.Message);
    }
}
