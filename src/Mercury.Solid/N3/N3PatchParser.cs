// N3PatchParser.cs
// Parser for Solid N3 Patch format.
// Based on W3C Solid N3 Patch specification.
// https://solidproject.org/TR/n3-patch
// No external dependencies, only BCL.
// .NET 10 / C# 14

using System.Buffers;
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

    private readonly Stream _stream;
    private readonly string? _baseUri;
    private byte[] _buffer;
    private char[] _charBuffer;
    private int _bufferPosition;
    private int _bufferLength;
    private bool _endOfStream;
    private bool _disposed;

    private Dictionary<string, string> _prefixes = new();
    private int _line = 1;
#pragma warning disable CS0414 // Field is assigned but its value is never used
    private int _column = 1;
#pragma warning restore CS0414

    private const int DefaultBufferSize = 8192;

    public N3PatchParser(Stream stream, string? baseUri = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _baseUri = baseUri;
        _buffer = ArrayPool<byte>.Shared.Rent(DefaultBufferSize);
        _charBuffer = ArrayPool<char>.Shared.Rent(DefaultBufferSize);
    }

    /// <summary>
    /// Parses the N3 Patch document and returns the patch operations.
    /// </summary>
    public async Task<N3Patch> ParseAsync(CancellationToken ct = default)
    {
        await FillBufferAsync(ct);

        var patchNode = (string?)null;
        var whereFormula = (N3Formula?)null;
        var deletesFormula = (N3Formula?)null;
        var insertsFormula = (N3Formula?)null;

        // Parse prefixes and find the patch
        SkipWhitespaceAndComments();

        while (!_endOfStream || _bufferPosition < _bufferLength)
        {
            if (_bufferPosition >= _bufferLength && !_endOfStream)
            {
                await FillBufferAsync(ct);
            }

            if (_bufferPosition >= _bufferLength)
                break;

            SkipWhitespaceAndComments();

            if (_bufferPosition >= _bufferLength)
                break;

            var c = (char)_buffer[_bufferPosition];

            // Parse @prefix or @base directives
            if (c == '@')
            {
                ParseDirective();
                continue;
            }

            // Parse PREFIX or BASE (SPARQL style)
            if (c == 'P' || c == 'p' || c == 'B' || c == 'b')
            {
                if (TryParseSparqlDirective())
                    continue;
            }

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

                if (predicateIri == "http://www.w3.org/1999/02/22-rdf-syntax-ns#type" &&
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

                if (_bufferPosition >= _bufferLength)
                    break;

                c = (char)_buffer[_bufferPosition];
                if (c == ';')
                {
                    _bufferPosition++;
                    SkipWhitespaceAndComments();
                    continue;
                }
                if (c == '.' || c == '}')
                {
                    if (c == '.')
                        _bufferPosition++;
                    break;
                }

                break;
            }

            SkipWhitespaceAndComments();
        }

        return new N3Patch(whereFormula, deletesFormula, insertsFormula);
    }

    private void ParseDirective()
    {
        _bufferPosition++; // Skip @
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
            var iri = ReadIri();
            SkipWhitespace();
            ExpectChar('.');
            // Base URI handling would go here
        }
    }

    private bool TryParseSparqlDirective()
    {
        var start = _bufferPosition;
        var keyword = ReadWord();

        if (keyword.Equals("PREFIX", StringComparison.OrdinalIgnoreCase))
        {
            SkipWhitespace();
            var prefix = ReadPrefix();
            SkipWhitespace();
            var iri = ReadIri();
            _prefixes[prefix] = iri;
            return true;
        }
        else if (keyword.Equals("BASE", StringComparison.OrdinalIgnoreCase))
        {
            SkipWhitespace();
            var iri = ReadIri();
            return true;
        }

        // Rewind if not a directive
        _bufferPosition = start;
        return false;
    }

    private string ParseTerm()
    {
        SkipWhitespaceAndComments();

        if (_bufferPosition >= _bufferLength)
            throw ParserException("Unexpected end of input");

        var c = (char)_buffer[_bufferPosition];

        // IRI
        if (c == '<')
        {
            return ReadIri();
        }

        // Variable
        if (c == '?')
        {
            _bufferPosition++;
            return "?" + ReadWord();
        }

        // Blank node
        if (c == '_' && _bufferPosition + 1 < _bufferLength && _buffer[_bufferPosition + 1] == ':')
        {
            _bufferPosition += 2;
            return "_:" + ReadWord();
        }

        // Prefixed name
        if (char.IsLetter(c) || c == ':')
        {
            return ReadPrefixedName();
        }

        // Literal
        if (c == '"' || c == '\'')
        {
            return ReadLiteral();
        }

        // 'a' shorthand for rdf:type
        if (c == 'a' && (_bufferPosition + 1 >= _bufferLength || !char.IsLetterOrDigit((char)_buffer[_bufferPosition + 1])))
        {
            _bufferPosition++;
            return "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";
        }

        // Keyword or prefixed name starting with letter
        return ReadPrefixedName();
    }

    private object ParseTermOrFormula()
    {
        SkipWhitespaceAndComments();

        if (_bufferPosition >= _bufferLength)
            throw ParserException("Unexpected end of input");

        var c = (char)_buffer[_bufferPosition];

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

        while (_bufferPosition < _bufferLength && (char)_buffer[_bufferPosition] != '}')
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
            if (_bufferPosition < _bufferLength)
            {
                var c = (char)_buffer[_bufferPosition];
                if (c == '.')
                {
                    _bufferPosition++;
                    SkipWhitespaceAndComments();
                }
                else if (c == ';')
                {
                    // Predicate-object list within formula
                    while ((char)_buffer[_bufferPosition] == ';')
                    {
                        _bufferPosition++;
                        SkipWhitespaceAndComments();
                        if (_bufferPosition < _bufferLength && (char)_buffer[_bufferPosition] != '}')
                        {
                            predicate = ParseN3Term();
                            SkipWhitespaceAndComments();
                            obj = ParseN3Term();
                            SkipWhitespaceAndComments();
                            patterns.Add(new N3TriplePattern(subject, predicate, obj));
                        }
                    }
                    if (_bufferPosition < _bufferLength && (char)_buffer[_bufferPosition] == '.')
                    {
                        _bufferPosition++;
                        SkipWhitespaceAndComments();
                    }
                }
            }
        }

        ExpectChar('}');
        return new N3Formula(patterns);
    }

    private N3Term ParseN3Term()
    {
        SkipWhitespaceAndComments();

        if (_bufferPosition >= _bufferLength)
            throw ParserException("Unexpected end of input");

        var c = (char)_buffer[_bufferPosition];

        // Variable
        if (c == '?')
        {
            _bufferPosition++;
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
        if (c == '_' && _bufferPosition + 1 < _bufferLength && _buffer[_bufferPosition + 1] == ':')
        {
            _bufferPosition += 2;
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
        if (c == 'a' && (_bufferPosition + 1 >= _bufferLength || !char.IsLetterOrDigit((char)_buffer[_bufferPosition + 1])))
        {
            _bufferPosition++;
            return N3Term.Iri("http://www.w3.org/1999/02/22-rdf-syntax-ns#type");
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

    private string ReadWord()
    {
        var sb = new StringBuilder();
        while (_bufferPosition < _bufferLength)
        {
            var c = (char)_buffer[_bufferPosition];
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
            {
                sb.Append(c);
                _bufferPosition++;
            }
            else
            {
                break;
            }
        }
        return sb.ToString();
    }

    private string ReadPrefix()
    {
        var sb = new StringBuilder();
        while (_bufferPosition < _bufferLength)
        {
            var c = (char)_buffer[_bufferPosition];
            if (c == ':')
            {
                _bufferPosition++;
                break;
            }
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
            {
                sb.Append(c);
                _bufferPosition++;
            }
            else
            {
                break;
            }
        }
        return sb.ToString();
    }

    private string ReadIri()
    {
        if ((char)_buffer[_bufferPosition] != '<')
            throw ParserException("Expected '<'");

        _bufferPosition++;
        var sb = new StringBuilder();
        sb.Append('<');

        while (_bufferPosition < _bufferLength)
        {
            var c = (char)_buffer[_bufferPosition];
            _bufferPosition++;

            if (c == '>')
            {
                sb.Append(c);
                break;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private string ReadPrefixedName()
    {
        var sb = new StringBuilder();

        // Read prefix part
        while (_bufferPosition < _bufferLength)
        {
            var c = (char)_buffer[_bufferPosition];
            if (c == ':')
            {
                sb.Append(c);
                _bufferPosition++;
                break;
            }
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
            {
                sb.Append(c);
                _bufferPosition++;
            }
            else
            {
                break;
            }
        }

        // Read local part
        while (_bufferPosition < _bufferLength)
        {
            var c = (char)_buffer[_bufferPosition];
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == '/')
            {
                sb.Append(c);
                _bufferPosition++;
            }
            else
            {
                break;
            }
        }

        return sb.ToString();
    }

    private string ReadLiteral()
    {
        var quote = (char)_buffer[_bufferPosition];
        _bufferPosition++;

        var sb = new StringBuilder();
        sb.Append(quote);

        bool escaped = false;
        while (_bufferPosition < _bufferLength)
        {
            var c = (char)_buffer[_bufferPosition];
            _bufferPosition++;
            sb.Append(c);

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
                break;
            }
        }

        // Check for language tag or datatype
        if (_bufferPosition < _bufferLength)
        {
            var c = (char)_buffer[_bufferPosition];
            if (c == '@')
            {
                sb.Append(c);
                _bufferPosition++;
                // Read language tag
                while (_bufferPosition < _bufferLength)
                {
                    c = (char)_buffer[_bufferPosition];
                    if (char.IsLetterOrDigit(c) || c == '-')
                    {
                        sb.Append(c);
                        _bufferPosition++;
                    }
                    else
                    {
                        break;
                    }
                }
            }
            else if (c == '^' && _bufferPosition + 1 < _bufferLength && _buffer[_bufferPosition + 1] == '^')
            {
                sb.Append("^^");
                _bufferPosition += 2;
                SkipWhitespace();
                // Read datatype IRI
                if (_bufferPosition < _bufferLength && (char)_buffer[_bufferPosition] == '<')
                {
                    sb.Append(ReadIri());
                }
                else
                {
                    sb.Append(ReadPrefixedName());
                }
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
        while (_bufferPosition < _bufferLength)
        {
            var c = (char)_buffer[_bufferPosition];
            if (c == ' ' || c == '\t' || c == '\r')
            {
                _bufferPosition++;
            }
            else if (c == '\n')
            {
                _bufferPosition++;
                _line++;
                _column = 1;
            }
            else
            {
                break;
            }
        }
    }

    private void SkipWhitespaceAndComments()
    {
        while (_bufferPosition < _bufferLength)
        {
            var c = (char)_buffer[_bufferPosition];
            if (c == ' ' || c == '\t' || c == '\r')
            {
                _bufferPosition++;
            }
            else if (c == '\n')
            {
                _bufferPosition++;
                _line++;
                _column = 1;
            }
            else if (c == '#')
            {
                // Skip comment
                while (_bufferPosition < _bufferLength && (char)_buffer[_bufferPosition] != '\n')
                {
                    _bufferPosition++;
                }
            }
            else
            {
                break;
            }
        }
    }

    private void ExpectChar(char expected)
    {
        if (_bufferPosition >= _bufferLength || (char)_buffer[_bufferPosition] != expected)
        {
            throw ParserException($"Expected '{expected}'");
        }
        _bufferPosition++;
    }

    private async Task FillBufferAsync(CancellationToken ct)
    {
        if (_endOfStream)
            return;

        // Shift remaining data to start
        if (_bufferPosition > 0 && _bufferPosition < _bufferLength)
        {
            var remaining = _bufferLength - _bufferPosition;
            Buffer.BlockCopy(_buffer, _bufferPosition, _buffer, 0, remaining);
            _bufferLength = remaining;
            _bufferPosition = 0;
        }
        else if (_bufferPosition >= _bufferLength)
        {
            _bufferPosition = 0;
            _bufferLength = 0;
        }

        // Read more data
        var spaceAvailable = _buffer.Length - _bufferLength;
        var bytesRead = await _stream.ReadAsync(_buffer.AsMemory(_bufferLength, spaceAvailable), ct);

        if (bytesRead == 0)
        {
            _endOfStream = true;
        }
        else
        {
            _bufferLength += bytesRead;
        }
    }

    private Exception ParserException(string message)
    {
        return new InvalidDataException($"N3 Patch parse error at line {_line}: {message}");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        ArrayPool<byte>.Shared.Return(_buffer);
        ArrayPool<char>.Shared.Return(_charBuffer);
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
