using System;

namespace SkyOmega.Mercury.Rdf;

/// <summary>
/// A scope of blank nodes as the store holds them. RDF scopes a blank-node label to its document (RDF 1.1 Concepts §3.4),
/// and SPARQL Update to an operation's data or one solution of a template, so two scopes that both write <c>_:b</c> mean
/// two nodes. Every scope has a token, a version-7 GUID (its first 48 bits are the time the scope began, so tokens sort
/// by it), and stores the label <c>_:x</c> as <c>_:g{token}x</c>: the same label within a scope is the same node, with no
/// lookup. Every blank node the store receives from a document, an Update or BNODE() is labeled here.
/// </summary>
internal sealed class BlankNodeScope
{
    // 64 characters in ASCII order, each valid in a blank-node label: a token sorts as its GUID does
    private const string Alphabet = "-0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz";

    private readonly string _prefix; // "_:g" + the token
    private char[] _subject = new char[64];
    private char[] _object = new char[64];
    private char[] _graph = new char[64];

    /// <summary>A new scope, with a new version-7 GUID.</summary>
    public BlankNodeScope() : this(Guid.CreateVersion7()) { }

    internal BlankNodeScope(Guid id) => _prefix = "_:g" + Token(id);

    /// <summary>The scope for the process: BNODE() mints its nodes here.</summary>
    public static BlankNodeScope Process { get; } = new();

    /// <summary>The token: the GUID's 128 bits, most significant first, six at a time (22 characters).</summary>
    internal static string Token(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        Span<char> token = stackalloc char[22];
        int bit = 0;
        for (int i = 0; i < 22; i++, bit += 6)
        {
            int value = 0;
            for (int b = 0; b < 6; b++)
            {
                int at = bit + b;
                value <<= 1;
                if (at < 128 && (bytes[at >> 3] & (0x80 >> (at & 7))) != 0)
                    value |= 1;
            }
            token[i] = Alphabet[value];
        }
        return new string(token);
    }

    /// <summary>A subject as stored: a blank node in this scope; any other term unchanged.</summary>
    public ReadOnlySpan<char> Subject(ReadOnlySpan<char> term) => Scope(term, ref _subject);

    /// <summary>An object as stored: a blank node in this scope; any other term unchanged.</summary>
    public ReadOnlySpan<char> Object(ReadOnlySpan<char> term) => Scope(term, ref _object);

    /// <summary>A graph name as stored: a blank node in this scope (N-Quads, TriG); any other term unchanged.</summary>
    public ReadOnlySpan<char> Graph(ReadOnlySpan<char> term) => Scope(term, ref _graph);

    /// <summary>The label <c>_:{label}</c> in this scope, as a string (Update's labels and template nodes).</summary>
    public string Label(ReadOnlySpan<char> label) =>
        label.Length > 2 && label[0] == '_' && label[1] == ':'
            ? string.Concat(_prefix, label[2..])
            : string.Concat(_prefix, label);

    private ReadOnlySpan<char> Scope(ReadOnlySpan<char> term, ref char[] buffer)
    {
        if (term.Length < 3 || term[0] != '_' || term[1] != ':')
            return term;
        int length = _prefix.Length + term.Length - 2;
        if (buffer.Length < length)
            buffer = new char[Math.Max(length, 2 * buffer.Length)];
        _prefix.CopyTo(buffer);
        term[2..].CopyTo(buffer.AsSpan(_prefix.Length));
        return buffer.AsSpan(0, length);
    }
}
