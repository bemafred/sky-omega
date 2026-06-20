using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SkyOmega.Mercury.NTriples;
using SkyOmega.Mercury.NQuads;
using SkyOmega.Mercury.TriG;
using SkyOmega.Mercury.Rdf.Turtle;
using Xunit;

namespace SkyOmega.Mercury.Tests.Rdf;

/// <summary>
/// Cross-format escape-decode identity (docs/divergence S1a). The N-Triples, N-Quads, Turtle, and TriG
/// streaming parsers all route string-escape decoding through the single shared <c>RdfEscape</c>, so the
/// SAME lexical literal MUST decode to the SAME bytes regardless of which format ingested it. If it did not,
/// one logical triple would store as two different atoms depending on the input format — the latent
/// catastrophe the divergence audit flagged. These tests are the dogfood lock on that convergence.
/// </summary>
public class RdfEscapeConvergenceTests
{
    [Theory]
    // Simple ECHAR — the \n / \t family every parser shares.
    [InlineData("\"line\\nbreak\"", "\"line\nbreak\"")]
    // BMP UCHAR — \u within the basic plane (é = U+00E9).
    [InlineData("\"caf\\u00E9\"", "\"café\"")]
    // Supplementary UCHAR — \U above U+FFFF (😀 = U+1F600) exercises surrogate-pair encoding, the case a
    // char-truncating decoder gets wrong and a code-point decoder gets right. All four must agree.
    [InlineData("\"emoji\\U0001F600end\"", "\"emoji\U0001F600end\"")]
    public async Task Escapes_DecodeIdenticallyAcrossFormats(string escapedObject, string expectedDecoded)
    {
        const string subj = "<http://ex.org/s>";
        const string pred = "<http://ex.org/p>";
        string triple = $"{subj} {pred} {escapedObject} .";

        string ntDecoded = null!;
        await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(triple)))
            await new NTriplesStreamParser(stream).ParseAsync((_, _, o) => ntDecoded = o.ToString());

        string nqDecoded = null!;
        await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(triple)))
            await new NQuadsStreamParser(stream).ParseAsync((_, _, o, _) => nqDecoded = o.ToString());

        string ttlDecoded = null!;
        await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(triple)))
            await new TurtleStreamParser(stream).ParseAsync((_, _, o) => ttlDecoded = o.ToString());

        string trigDecoded = null!;
        await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(triple)))
            await new TriGStreamParser(stream).ParseAsync((_, _, o, _) => trigDecoded = o.ToString());

        // Each format decodes to the expected form...
        Assert.Equal(expectedDecoded, ntDecoded);
        Assert.Equal(expectedDecoded, nqDecoded);
        Assert.Equal(expectedDecoded, ttlDecoded);
        Assert.Equal(expectedDecoded, trigDecoded);
        // ...which means all four agree with each other (the atom-identity invariant).
        Assert.Equal(ntDecoded, nqDecoded);
        Assert.Equal(ntDecoded, ttlDecoded);
        Assert.Equal(ntDecoded, trigDecoded);
    }

    [Fact]
    public async Task OutOfRangeUnicodeEscape_RejectedByAllFormats()
    {
        // U+110000 is one past the Unicode maximum (U+10FFFF). The shared RdfEscape rejects it everywhere;
        // before convergence, N-Quads and TriG lacked the upper-bound guard and let it through to throw a
        // raw exception downstream. Every format must now reject it at decode time.
        const string triple = "<http://ex.org/s> <http://ex.org/p> \"bad\\U00110000\" .";

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(triple));
            await new NTriplesStreamParser(stream).ParseAsync((_, _, _) => { });
        });

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(triple));
            await new NQuadsStreamParser(stream).ParseAsync((_, _, _, _) => { });
        });

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(triple));
            await new TurtleStreamParser(stream).ParseAsync((_, _, _) => { });
        });

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(triple));
            await new TriGStreamParser(stream).ParseAsync((_, _, _, _) => { });
        });
    }
}
