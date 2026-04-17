using System.Text;
using SkyOmega.Mercury.Rdf.Turtle;
using Xunit;
using Xunit.Abstractions;

namespace SkyOmega.Mercury.Tests.Rdf;

/// <summary>
/// Differential boundary tests: parse the same input with a reference (large)
/// buffer and a tiny buffer, asserting identical triple sequences.
///
/// A correct sliding-buffer parser must produce identical output regardless of
/// buffer size, as long as the buffer is at least large enough to hold the
/// longest atomic token (the longest fixed lookahead is "@version" = 8 bytes).
///
/// These tests sweep prefix padding from 0..bufferSize so that the critical
/// token lands at every offset relative to the buffer boundary. They are
/// designed to expose lookahead and rewind bugs in the current implementation
/// without requiring the full Wikidata dataset.
/// </summary>
public class ParserBoundaryDifferentialTests
{
    private readonly ITestOutputHelper _output;

    public ParserBoundaryDifferentialTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private const int ReferenceBufferSize = 1 << 20; // 1 MB — boundary effects vanish

    private static async Task<List<(string S, string P, string O)>> ParseAllAsync(
        string turtle, int bufferSize, int? slowReadChunk = null)
    {
        Stream stream = new MemoryStream(Encoding.UTF8.GetBytes(turtle));
        if (slowReadChunk is { } chunk)
            stream = new SlowReadStream(stream, chunk);

        var triples = new List<(string, string, string)>();
        using var parser = new TurtleStreamParser(stream, bufferSize: bufferSize);
        await parser.ParseAsync((s, p, o) =>
            triples.Add((s.ToString(), p.ToString(), o.ToString())));
        return triples;
    }

    private async Task AssertEquivalent(string turtle, int bufferSize, string context, int? slowReadChunk = null)
    {
        var reference = await ParseAllAsync(turtle, ReferenceBufferSize);

        List<(string S, string P, string O)> actual;
        try
        {
            actual = await ParseAllAsync(turtle, bufferSize, slowReadChunk);
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException(
                $"Parsing threw with bufferSize={bufferSize} ({context}): {ex.GetType().Name}: {ex.Message}");
        }

        if (reference.Count != actual.Count)
        {
            throw new Xunit.Sdk.XunitException(
                $"Triple count mismatch with bufferSize={bufferSize} ({context}): " +
                $"reference={reference.Count}, actual={actual.Count}");
        }

        for (int i = 0; i < reference.Count; i++)
        {
            if (reference[i] != actual[i])
            {
                throw new Xunit.Sdk.XunitException(
                    $"Triple[{i}] mismatch with bufferSize={bufferSize} ({context}):\n" +
                    $"  reference: {reference[i]}\n" +
                    $"  actual:    {actual[i]}");
            }
        }
    }

    // -------------------------------------------------------------------------
    // Discrepancy 1: PeekString never refills — false negative across boundary
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(64)]
    public async Task PrefixDirective_AcrossBoundary_Differential(int bufferSize)
    {
        // Sweep so "@prefix" / "PREFIX" land at every offset relative to the
        // buffer boundary. PeekString does not trigger refill — when the
        // 7-byte "@prefix" straddles the boundary it returns false.
        for (int pad = 0; pad <= bufferSize + 8; pad++)
        {
            var input = new string(' ', pad) +
                "@prefix ex: <http://example.org/> .\nex:a ex:b ex:c .\n";
            await AssertEquivalent(input, bufferSize, $"pad={pad}");
        }
    }

    // -------------------------------------------------------------------------
    // Discrepancy 2: triple-quote opener at boundary
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(96)]
    public async Task LongStringLiteral_AcrossBoundary_Differential(int bufferSize)
    {
        // The PeekAhead(1)/PeekAhead(2) check for """ does not refill.
        // When the 3-byte opener straddles the boundary, the parser falls
        // through to short-string parsing and corrupts the literal.
        var preamble = "@prefix ex: <http://example.org/> .\n";
        for (int pad = 0; pad <= bufferSize + 8; pad++)
        {
            var input = preamble + new string(' ', pad) +
                "ex:s ex:p \"\"\"multi\nline\nliteral\"\"\" .\n";
            await AssertEquivalent(input, bufferSize, $"pad={pad}");
        }
    }

    // -------------------------------------------------------------------------
    // Discrepancy 3: "<<" reified-triple opener at boundary
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(96)]
    public async Task ReifiedTriple_AcrossBoundary_Differential(int bufferSize)
    {
        // PeekString("<<") returns false when "<<" straddles the boundary.
        // Parser then treats "<" as the start of an IRIREF, mis-parsing the
        // entire reified triple as a malformed IRI.
        var preamble = "@prefix ex: <http://example.org/> .\n";
        for (int pad = 0; pad <= bufferSize + 8; pad++)
        {
            var input = preamble + new string(' ', pad) +
                "<< ex:s ex:p ex:o >> ex:reifies ex:thing .\n";
            await AssertEquivalent(input, bufferSize, $"pad={pad}");
        }
    }

    // -------------------------------------------------------------------------
    // Discrepancy 4: multi-byte UTF-8 codepoint straddling buffer boundary
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(48)]
    [InlineData(64)]
    public async Task MultiByteUtf8InLiteral_AcrossBoundary_Differential(int bufferSize)
    {
        // PeekUtf8CodePoint returns the leader byte raw with byteLength=1
        // when the trailing continuation bytes are not yet in the buffer.
        // Consume() then advances by 1 byte, leaving the parser pointing
        // mid-codepoint. Use a 3-byte CJK character (中 = E4 B8 AD).
        var preamble = "@prefix ex: <http://example.org/> .\n";
        for (int pad = 0; pad <= bufferSize + 8; pad++)
        {
            var input = preamble + new string(' ', pad) +
                "ex:s ex:p \"中文测试\" .\n";
            await AssertEquivalent(input, bufferSize, $"pad={pad}");
        }
    }

    // -------------------------------------------------------------------------
    // Discrepancy 5: cumulative state — many statements with tiny buffer
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(128)]
    public async Task ManyStatements_TinyBuffer_Differential(int bufferSize)
    {
        // Reproduces the cumulative degradation pattern: many statements
        // all crossing buffer boundaries, exercising the rewind path
        // (`_bufferPosition = _statementStartPos`) repeatedly. The saved
        // `_statementStartPos` is invalidated by FillBufferSync's shift.
        var sb = new StringBuilder();
        sb.AppendLine("@prefix ex: <http://example.org/> .");
        sb.AppendLine("@prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .");
        for (int i = 0; i < 200; i++)
        {
            sb.AppendLine($"ex:s{i} ex:p \"value-{i}\" ;");
            sb.AppendLine($"        rdfs:label \"Label {i}\"@en ;");
            sb.AppendLine($"        ex:related ex:o{i} .");
        }

        await AssertEquivalent(sb.ToString(), bufferSize, "many-statements");
    }

    // -------------------------------------------------------------------------
    // Worst case: tiny buffer + slow stream (1 byte per Read)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(64, 1)]
    [InlineData(128, 1)]
    [InlineData(64, 3)]
    public async Task MixedTurtle_TinyBuffer_SlowStream_Differential(int bufferSize, int chunkBytes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@prefix ex: <http://example.org/> .");
        sb.AppendLine("@prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .");
        for (int i = 0; i < 50; i++)
        {
            sb.AppendLine($"ex:s{i} ex:p \"value-{i}\"@en ;");
            sb.AppendLine($"        rdfs:label \"Label {i}\" ;");
            sb.AppendLine($"        ex:related ex:o{i} .");
        }

        await AssertEquivalent(sb.ToString(), bufferSize, $"chunk={chunkBytes}", slowReadChunk: chunkBytes);
    }

    // -------------------------------------------------------------------------
    // Targeted: `_:` blank-node detection across boundary
    // -------------------------------------------------------------------------
    // `Peek() == '_' && PeekAhead(1) == ':'` — when '_' is the last byte in the
    // buffer, PeekAhead(1) returns -1 (no refill). Parser falls through to IRI
    // parsing; ParsePrefixedNameSpan sees '_' (not PN_CHARS_BASE), returns
    // empty. Subject is empty → ParseTriplesZeroGC returns false → rewind path
    // (`_bufferPosition = _statementStartPos`). The rewind itself may walk into
    // a position invalidated by the FillBufferSync that happened during Peek.

    [Theory]
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(96)]
    public async Task BlankNode_AcrossBoundary_Differential(int bufferSize)
    {
        var preamble = "@prefix ex: <http://example.org/> .\nex:s ex:p ex:o .\n";
        for (int pad = 0; pad <= bufferSize + 8; pad++)
        {
            var input = preamble + new string(' ', pad) +
                "_:blank ex:p ex:o .\n";
            await AssertEquivalent(input, bufferSize, $"pad={pad}");
        }
    }

    // -------------------------------------------------------------------------
    // Targeted: `@version` directive (8-byte PeekString — longest fixed lookahead)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(48)]
    [InlineData(64)]
    public async Task VersionDirective_AcrossBoundary_Differential(int bufferSize)
    {
        var preamble = "@prefix ex: <http://example.org/> .\nex:s ex:p ex:o .\n";
        for (int pad = 0; pad <= bufferSize + 8; pad++)
        {
            var input = preamble + new string(' ', pad) +
                "@version \"1.2\" .\n";
            await AssertEquivalent(input, bufferSize, $"pad={pad}");
        }
    }

    // -------------------------------------------------------------------------
    // Targeted: PN_LOCAL with embedded dot run across boundary
    // -------------------------------------------------------------------------
    // `while (PeekAhead(dotCount) == '.') dotCount++;` walks past buffer end
    // silently (returns -1, loop exits). Then `PeekAhead(dotCount)` for the
    // continuation check sees -1 → treats it as terminator → trailing dots
    // get dropped or mis-classified.

    [Theory]
    [InlineData(48)]
    [InlineData(64)]
    public async Task PnLocalDotRun_AcrossBoundary_Differential(int bufferSize)
    {
        var preamble = "@prefix ex: <http://example.org/> .\n";
        for (int pad = 0; pad <= bufferSize + 8; pad++)
        {
            var input = preamble + new string(' ', pad) +
                "ex:foo.bar.baz ex:p ex:o .\n";
            await AssertEquivalent(input, bufferSize, $"pad={pad}");
        }
    }

    // -------------------------------------------------------------------------
    // Targeted: `<<` reified-triple opener in OBJECT position
    // -------------------------------------------------------------------------
    // The original test placed `<<` at subject position, where the outer
    // refill-on-statement-boundary keeps `<<` together. In object position,
    // the boundary can land between the two `<` chars after substantial
    // intra-statement parsing has happened.

    [Theory]
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(96)]
    public async Task ReifiedTripleAsObject_AcrossBoundary_Differential(int bufferSize)
    {
        var preamble = "@prefix ex: <http://example.org/> .\n";
        for (int pad = 0; pad <= bufferSize + 8; pad++)
        {
            var input = preamble + new string(' ', pad) +
                "ex:s ex:p << ex:a ex:b ex:c >> .\n";
            await AssertEquivalent(input, bufferSize, $"pad={pad}");
        }
    }

    // -------------------------------------------------------------------------
    // Worst-case combo: rich constructs (`<<`, `"""`, multi-byte) under
    // a 1-byte-per-Read stream — every PeekString/PeekAhead chain runs
    // against a buffer with at most a few bytes ahead of the cursor.
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(64, 1)]
    [InlineData(128, 1)]
    [InlineData(64, 2)]
    public async Task RichConstructs_TinyBufferSlowStream_Differential(int bufferSize, int chunkBytes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@prefix ex: <http://example.org/> .");
        sb.AppendLine("@prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .");
        for (int i = 0; i < 20; i++)
        {
            sb.AppendLine($"ex:s{i} ex:p \"中文-{i}\" .");
            sb.AppendLine($"_:b{i} ex:p << ex:a ex:b ex:c >> .");
            sb.AppendLine($"ex:s{i} rdfs:label \"\"\"long");
            sb.AppendLine($"multiline");
            sb.AppendLine($"literal {i}\"\"\" .");
        }

        await AssertEquivalent(sb.ToString(), bufferSize, $"chunk={chunkBytes}", slowReadChunk: chunkBytes);
    }
}

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
