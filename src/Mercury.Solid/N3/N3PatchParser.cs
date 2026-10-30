// N3PatchParser.cs
// Parser for Solid N3 Patch format.
// Based on W3C Solid N3 Patch specification.
// https://solidproject.org/TR/n3-patch
// No external dependencies, only BCL.
// .NET 10 / C# 14

using SkyOmega.Mercury.Rdf;
using SkyOmega.Mercury.Runtime.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace SkyOmega.Mercury.Solid.N3;

/// <summary>
/// Parses Solid N3 Patch documents.
/// N3 Patch is a subset of N3 used for PATCH operations.
/// </summary>
/// <remarks>
/// Supports:
/// - solid:InsertDeletePatch type
/// - solid:where, solid:deletes, solid:inserts clauses
/// - Variables (?x) in formula patterns
/// - { ... } formula blocks
/// </remarks>
public sealed class N3PatchParser : IDisposable
{
    // Solid N3 Patch namespace
    private const string SolidNs = "http://www.w3.org/ns/solid/terms#";
    private const string SolidInsertDeletePatch = SolidNs + "InsertDeletePatch";
    private const string SolidWhere = SolidNs + "where";
    private const string SolidDeletes = SolidNs + "deletes";
    private const string SolidInserts = SolidNs + "inserts";
    private const string RdfType = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";

    private readonly string? _baseUri;

    // The patch, read as code points with lookahead that refills itself
    private readonly Utf8DocumentReader _input;
    private bool _disposed;

    private Dictionary<string, string> _prefixes = new();

    private const int DefaultBufferSize = 8192;

    public N3PatchParser(Stream stream, string? baseUri = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _baseUri = baseUri;
        _input = new Utf8DocumentReader(stream, DefaultBufferSize, PooledBufferManager.Shared, "N3 Patch");
    }

    /// <summary>
    /// Parses the N3 Patch document.
    /// </summary>
    public async Task<N3Patch> ParseAsync(CancellationToken ct = default)
    {
        await _input.StartAsync(ct);

        var patchNode = (string?)null;
        var whereFormula = (N3Formula?)null;
        var deletesFormula = (N3Formula?)null;
        var insertsFormula = (N3Formula?)null;

        while (true)
        {
            await _input.TopUpAsync(ct);
            SkipWhitespaceAndComments();

            var c = Peek();
            if (c == -1)
                break;

            // Parse @prefix or @base directives
            if (c == '@')
            {
                ParseDirective();
                continue;
            }

            // Parse PREFIX or BASE (SPARQL style)
            if (TryParseSparqlDirective())
                continue;

            // Parse a statement
            var subject = ParseTerm();
            SkipWhitespaceAndComments();

            // Parse predicate-object pairs
            while (true)
            {
                var predicate = ParseTerm();
                SkipWhitespaceAndComments();

                var obj = ParseTermOrFormula();
                SkipWhitespaceAndComments();

                // Check if this is a patch type declaration
                var predicateIri = ResolveTerm(predicate);
                var objResolved = obj as string ?? (obj as N3Formula != null ? null : obj?.ToString());

                if (predicateIri == RdfType &&
                    objResolved != null && ResolveTerm(objResolved) == SolidInsertDeletePatch)
                {
                    patchNode = subject;
                }
                else if (patchNode != null && subject == patchNode)
                {
                    if (predicateIri == SolidWhere && obj is N3Formula wf)
                    {
                        whereFormula = wf;
                    }
                    else if (predicateIri == SolidDeletes && obj is N3Formula df)
                    {
                        deletesFormula = df;
                    }
                    else if (predicateIri == SolidInserts && obj is N3Formula inf)
                    {
                        insertsFormula = inf;
                    }
                }

                c = Peek();
                if (c == ';')
                {
                    Consume();
                    SkipWhitespaceAndComments();
                    continue;
                }
                if (c == '.')
                    Consume();
                break;
            }
        }

        return new N3Patch(whereFormula, deletesFormula, insertsFormula);
    }

    private void ParseDirective()
    {
        Consume(); // @
        var keyword = ReadWord();

        if (keyword.Equals("prefix", StringComparison.OrdinalIgnoreCase))
        {
            SkipWhitespace();
            var prefix = ReadPrefix();
            SkipWhitespace();
            var iri = ReadIri();
            SkipWhitespace();
            ExpectChar('.');

            _prefixes[prefix] = iri;
        }
        else if (keyword.Equals("base", StringComparison.OrdinalIgnoreCase))
        {
            SkipWhitespace();
            ReadIri();
            SkipWhitespace();
            ExpectChar('.');
            // Base URI handling would go here
        }
    }

    /// <summary>
    /// A SPARQL-style PREFIX or BASE directive, recognized by lookahead so nothing is read when there is none.
    /// </summary>
    private bool TryParseSparqlDirective()
    {
        if (MatchKeyword("PREFIX"))
        {
            SkipWhitespace();
            var prefix = ReadPrefix();
            SkipWhitespace();
            var iri = ReadIri();
            _prefixes[prefix] = iri;
            return true;
        }
        if (MatchKeyword("BASE"))
        {
            SkipWhitespace();
            ReadIri();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Reads past <paramref name="keyword"/>, in any case, when it is next and not the start of a longer word.
    /// </summary>
    private bool MatchKeyword(string keyword)
    {
        for (int i = 0; i < keyword.Length; i++)
        {
            int b = _input.PeekAhead(i);
            if (b == -1 || char.ToUpperInvariant((char)b) != keyword[i])
                return false;
        }
        int next = _input.PeekAhead(keyword.Length);
        if (next != -1 && (char.IsLetterOrDigit((char)next) || next == '_' || next == ':'))
            return false;

        for (int i = 0; i < keyword.Length; i++)
            Consume();
        return true;
    }

    private string ParseTerm()
    {
        SkipWhitespaceAndComments();

        var c = Peek();
        if (c == -1)
            throw ParserException("Unexpected end of input");

        // IRI
        if (c == '<')
        {
            return ReadIri();
        }

        // Variable
        if (c == '?')
        {
            Consume();
            return "?" + ReadWord();
        }

        // Blank node
        if (c == '_' && _input.PeekAhead(1) == ':')
        {
            Consume();
            Consume();
            return "_:" + ReadWord();
        }

        // 'a' for rdf:type, before the prefixed-name rule, which would read it as a name
        if (c == 'a' && !IsWordChar(_input.PeekAheadCodePoint(1)))
        {
            Consume();
            return RdfType;
        }

        // Literal
        if (c == '"' || c == '\'')
        {
            return ReadLiteral();
        }

        // Prefixed name; anything else is an error there
        return ReadPrefixedName();
    }

    private object ParseTermOrFormula()
    {
        SkipWhitespaceAndComments();

        var c = Peek();
        if (c == -1)
            throw ParserException("Unexpected end of input");

        // Formula
        if (c == '{')
        {
            return ParseFormula();
        }

        return ParseTerm();
    }

    private N3Formula ParseFormula()
    {
        ExpectChar('{');
        SkipWhitespaceAndComments();

        var patterns = new List<N3TriplePattern>();

        while (Peek() != -1 && Peek() != '}')
        {
            // Parse triple pattern
            var subject = ParseN3Term();
            SkipWhitespaceAndComments();
            var predicate = ParseN3Term();
            SkipWhitespaceAndComments();
            var obj = ParseN3Term();
            SkipWhitespaceAndComments();

            patterns.Add(new N3TriplePattern(subject, predicate, obj));

            // Expect . or ; or }
            var c = Peek();
            if (c == '.')
            {
                Consume();
                SkipWhitespaceAndComments();
            }
            else if (c == ';')
            {
                // Predicate-object list within formula
                while (Peek() == ';')
                {
                    Consume();
                    SkipWhitespaceAndComments();
                    if (Peek() != -1 && Peek() != '}')
                    {
                        predicate = ParseN3Term();
                        SkipWhitespaceAndComments();
                        obj = ParseN3Term();
                        SkipWhitespaceAndComments();
                        patterns.Add(new N3TriplePattern(subject, predicate, obj));
                    }
                }
                if (Peek() == '.')
                {
                    Consume();
                    SkipWhitespaceAndComments();
                }
            }
        }

        ExpectChar('}');
        return new N3Formula(patterns);
    }

    private N3Term ParseN3Term()
    {
        SkipWhitespaceAndComments();

        var c = Peek();
        if (c == -1)
            throw ParserException("Unexpected end of input");

        // Variable
        if (c == '?')
        {
            Consume();
            var name = ReadWord();
            return N3Term.Variable(name);
        }

        // IRI
        if (c == '<')
        {
            var iri = ReadIri();
            // Strip angle brackets
            if (iri.StartsWith("<") && iri.EndsWith(">"))
                iri = iri.Substring(1, iri.Length - 2);
            return N3Term.Iri(iri);
        }

        // Blank node
        if (c == '_' && _input.PeekAhead(1) == ':')
        {
            Consume();
            Consume();
            var id = ReadWord();
            return N3Term.BlankNode(id);
        }

        // Literal
        if (c == '"' || c == '\'')
        {
            var literal = ReadLiteral();
            return ParseLiteralToTerm(literal);
        }

        // 'a' shorthand
        if (c == 'a' && !IsWordChar(_input.PeekAheadCodePoint(1)))
        {
            Consume();
            return N3Term.Iri(RdfType);
        }

        // Prefixed name
        var prefixedName = ReadPrefixedName();
        var resolved = ResolveTerm(prefixedName);
        return N3Term.Iri(resolved);
    }

    private N3Term ParseLiteralToTerm(string literal)
    {
        // Parse literal format: "value" or "value"@lang or "value"^^<type>
        if (!literal.StartsWith("\""))
            return N3Term.Literal(literal);

        var endQuote = literal.LastIndexOf('"');
        if (endQuote <= 0)
            return N3Term.Literal(literal);

        var value = literal.Substring(1, endQuote - 1);
        var suffix = literal.Substring(endQuote + 1);

        if (suffix.StartsWith("@"))
        {
            return N3Term.Literal(value, language: suffix.Substring(1));
        }
        else if (suffix.StartsWith("^^"))
        {
            var datatype = suffix.Substring(2);
            if (datatype.StartsWith("<") && datatype.EndsWith(">"))
                datatype = datatype.Substring(1, datatype.Length - 2);
            return N3Term.Literal(value, datatype: datatype);
        }

        return N3Term.Literal(value);
    }

    /// <summary>A letter, digit, '_' or '-': the characters of a variable, blank node label or keyword.</summary>
    private static bool IsWordChar(int c)
        => c == '_' || c == '-' || (c >= 0 && Rune.IsValid(c) && Rune.IsLetterOrDigit(new Rune(c)));

    private string ReadWord()
    {
        var sb = new StringBuilder();
        while (IsWordChar(Peek()))
        {
            RdfEscape.AppendUtf16(sb, Peek());
            Consume();
        }
        return sb.ToString();
    }

    private string ReadPrefix()
    {
        var sb = new StringBuilder();
        while (true)
        {
            var c = Peek();
            if (c == ':')
            {
                Consume();
                break;
            }
            if (!IsWordChar(c))
                break;
            RdfEscape.AppendUtf16(sb, c);
            Consume();
        }
        return sb.ToString();
    }

    private string ReadIri()
    {
        if (Peek() != '<')
            throw ParserException("Expected '<'");

        Consume();
        var sb = new StringBuilder();
        sb.Append('<');

        while (true)
        {
            var c = Peek();
            if (c == -1)
                throw ParserException("Unterminated IRI");
            Consume();
            RdfEscape.AppendUtf16(sb, c);
            if (c == '>')
                return sb.ToString();
        }
    }

    private string ReadPrefixedName()
    {
        var sb = new StringBuilder();

        // Read prefix part
        while (true)
        {
            var c = Peek();
            if (c == ':')
            {
                sb.Append(':');
                Consume();
                break;
            }
            if (!IsWordChar(c))
                break;
            RdfEscape.AppendUtf16(sb, c);
            Consume();
        }

        // Read local part
        while (true)
        {
            var c = Peek();
            if (!IsWordChar(c) && c != '.' && c != '/')
                break;
            RdfEscape.AppendUtf16(sb, c);
            Consume();
        }

        // Nothing read: the character starts no term. Returning an empty name here made the statement loop retry
        // the same character forever.
        if (sb.Length == 0)
        {
            var c = Peek();
            if (c == -1)
                throw ParserException("Unexpected end of input");
            throw ParserException($"Unexpected character U+{c:X4}");
        }

        return sb.ToString();
    }

    private string ReadLiteral()
    {
        var quote = Peek();
        Consume();

        var sb = new StringBuilder();
        sb.Append((char)quote);

        bool escaped = false;
        bool terminated = false;
        while (true)
        {
            var c = Peek();
            if (c == -1)
                break;
            Consume();
            RdfEscape.AppendUtf16(sb, c);

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == quote)
            {
                terminated = true;
                break;
            }
        }

        if (!terminated)
            throw ParserException("Unterminated literal");

        // Check for language tag or datatype
        if (Peek() == '@')
        {
            sb.Append('@');
            Consume();
            // Read language tag
            while (true)
            {
                var c = Peek();
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-'))
                    break;
                sb.Append((char)c);
                Consume();
            }
        }
        else if (Peek() == '^' && _input.PeekAhead(1) == '^')
        {
            sb.Append("^^");
            Consume();
            Consume();
            SkipWhitespace();
            // Read datatype IRI
            if (Peek() == '<')
            {
                sb.Append(ReadIri());
            }
            else
            {
                sb.Append(ReadPrefixedName());
            }
        }

        return sb.ToString();
    }

    private string ResolveTerm(string term)
    {
        if (term.StartsWith("<") && term.EndsWith(">"))
        {
            return term.Substring(1, term.Length - 2);
        }

        var colonIndex = term.IndexOf(':');
        if (colonIndex > 0 || (colonIndex == 0 && term.Length > 1))
        {
            var prefix = colonIndex > 0 ? term.Substring(0, colonIndex) : "";
            var local = term.Substring(colonIndex + 1);

            if (_prefixes.TryGetValue(prefix, out var ns))
            {
                // Strip angle brackets from namespace
                if (ns.StartsWith("<") && ns.EndsWith(">"))
                    ns = ns.Substring(1, ns.Length - 2);
                return ns + local;
            }
        }

        return term;
    }

    private void SkipWhitespace()
    {
        while (Peek() is ' ' or '\t' or '\r' or '\n')
            Consume();
    }

    private void SkipWhitespaceAndComments()
    {
        while (true)
        {
            var c = Peek();
            if (c is ' ' or '\t' or '\r' or '\n')
            {
                Consume();
            }
            else if (c == '#')
            {
                // Skip comment
                while (Peek() != -1 && Peek() != '\n')
                    Consume();
            }
            else
            {
                break;
            }
        }
    }

    private void ExpectChar(char expected)
    {
        if (Peek() != expected)
            throw ParserException($"Expected '{expected}'");
        Consume();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Peek() => _input.Peek();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Consume() => _input.Consume();

    private Exception ParserException(string message) => _input.Error(message);

    public void Dispose()
    {
        if (_disposed)
            return;

        _input.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// Represents a parsed N3 Patch document.
/// </summary>
public sealed class N3Patch
{
    /// <summary>
    /// The WHERE clause (patterns to match).
    /// </summary>
    public N3Formula? Where { get; }

    /// <summary>
    /// The DELETES clause (triples to remove).
    /// </summary>
    public N3Formula? Deletes { get; }

    /// <summary>
    /// The INSERTS clause (triples to add).
    /// </summary>
    public N3Formula? Inserts { get; }

    public N3Patch(N3Formula? where, N3Formula? deletes, N3Formula? inserts)
    {
        Where = where;
        Deletes = deletes;
        Inserts = inserts;
    }

    /// <summary>
    /// Whether this patch has any operations.
    /// </summary>
    public bool IsEmpty => (Deletes == null || Deletes.Patterns.Count == 0) &&
                           (Inserts == null || Inserts.Patterns.Count == 0);

    /// <summary>
    /// Gets all variables used in the patch.
    /// </summary>
    public IEnumerable<string> GetVariables()
    {
        var vars = new HashSet<string>();
        if (Where != null)
        {
            foreach (var v in Where.Variables)
                vars.Add(v);
        }
        if (Deletes != null)
        {
            foreach (var v in Deletes.Variables)
                vars.Add(v);
        }
        if (Inserts != null)
        {
            foreach (var v in Inserts.Variables)
                vars.Add(v);
        }
        return vars;
    }
}
