using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SkyOmega.Mercury.Abstractions;
using Xunit;

namespace SkyOmega.Mercury.Tests.Rdf;

/// <summary>
/// IRIREF ([18] in Turtle and TriG, [8] in N-Triples and N-Quads) is '&lt;' ([^#x00-#x20&lt;&gt;"{}|^`\] | UCHAR)* '&gt;',
/// and a UCHAR stands for its character, so an escape of an excluded character is no more an IRI character than the
/// character is (W3C turtle-syntax-bad-uri-escape-01 to -03, trig-syntax-bad-uri-escape-01 to -04). Through 1.8.32 no
/// reader checked an escaped character; N-Quads let a raw '&lt;' through, and TriG checked only whitespace, so a raw
/// '{', '|' or '^' was accepted. Every reader now asks RdfIri.IsIriRefCharacter, for a written and an escaped
/// character alike. The documents are verbatim strings, so each \u and \U is the document's escape.
/// </summary>
public class IriRefCharacterTests
{
    [Theory]
    [InlineData(RdfFormat.Turtle, @"<http://ex/s\u0020> <http://ex/p> <http://ex/o> .")]        // an escaped space
    [InlineData(RdfFormat.Turtle, @"<http://ex/s\u003C> <http://ex/p> <http://ex/o> .")]        // an escaped <
    [InlineData(RdfFormat.Turtle, @"<http://ex/s> <http://ex/p> <http://ex/o\u003E> .")]        // an escaped >, as object
    [InlineData(RdfFormat.Turtle, @"<http://ex/s> <http://ex/p> <http://ex/o\U0000007B> .")]    // an 8-digit escaped {
    [InlineData(RdfFormat.Turtle, @"<http://ex/s\u005C> <http://ex/p> <http://ex/o> .")]        // an escaped backslash
    [InlineData(RdfFormat.TriG, @"{ <http://ex/s\u0020> <http://ex/p> <http://ex/o> . }")]
    [InlineData(RdfFormat.TriG, @"{ <http://ex/s> <http://ex/p> <http://ex/o\U0000007C> . }")]
    [InlineData(RdfFormat.TriG, @"{ <http://ex/{abc}> <http://ex/p> <http://ex/o> . }")]          // a written {
    [InlineData(RdfFormat.TriG, @"<http://ex/s|> <http://ex/p> <http://ex/o> .")]                 // a written |
    [InlineData(RdfFormat.TriG, @"<http://ex/s^> <http://ex/p> <http://ex/o> .")]                 // a written ^
    [InlineData(RdfFormat.NTriples, @"<http://ex/s\u0020> <http://ex/p> <http://ex/o> .")]
    [InlineData(RdfFormat.NTriples, @"<http://ex/s> <http://ex/p> <http://ex/o\U00000022> .")]  // an escaped "
    [InlineData(RdfFormat.NQuads, @"<http://ex/s\u003C> <http://ex/p> <http://ex/o> .")]
    [InlineData(RdfFormat.NQuads, @"<http://ex/s> <http://ex/p> <http://ex/o> <http://ex/g\u0060> .")] // an escaped `, in a graph
    [InlineData(RdfFormat.NQuads, @"<http://ex/s<> <http://ex/p> <http://ex/o> .")]               // a written <
    public async Task IriRef_WithAnExcludedCharacter_IsASyntaxError(RdfFormat format, string document)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document));

        await Assert.ThrowsAnyAsync<Exception>(() => RdfEngine.ParseTriplesAsync(stream, format,
            ct: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(RdfFormat.Turtle, @"<http://ex/caf\u00E9> <http://ex/p> <http://ex/\U0001F600> .")]
    [InlineData(RdfFormat.TriG, @"{ <http://ex/caf\u00E9> <http://ex/p> <http://ex/\U0001F600> . }")]
    [InlineData(RdfFormat.NTriples, @"<http://ex/caf\u00E9> <http://ex/p> <http://ex/\U0001F600> .")]
    [InlineData(RdfFormat.NQuads, @"<http://ex/caf\u00E9> <http://ex/p> <http://ex/\U0001F600> .")]
    public async Task IriRef_WithAnEscapedIriCharacter_IsThatCharacter(RdfFormat format, string document)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document));

        var triples = await RdfEngine.ParseTriplesAsync(stream, format, ct: TestContext.Current.CancellationToken);

        var triple = Assert.Single(triples);
        Assert.Equal("<http://ex/caf\u00E9>", triple.Subject);       // a regular string: C# decodes the escape
        Assert.Equal("<http://ex/\U0001F600>", triple.Object);
    }
}
