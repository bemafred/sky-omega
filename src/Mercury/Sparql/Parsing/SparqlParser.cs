using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkyOmega.Mercury.Sparql;

namespace SkyOmega.Mercury.Sparql.Parsing;

/// <summary>
/// Zero-allocation SPARQL 1.1 parser using stack-based parsing and Span&lt;T&gt;
/// Based on SPARQL 1.1 Query Language EBNF grammar
/// </summary>
public ref partial struct SparqlParser
{
    private ReadOnlySpan<char> _source;
    private int _position;
    private int _quotedTripleCounter; // Counter for generating synthetic reifier variables

    // RDF namespace constants for reification expansion (offsets into synthetic buffer)
    // These are appended to the source during query execution
    private const int RdfTypeOffset = -1;
    private const int RdfStatementOffset = -2;
    private const int RdfSubjectOffset = -3;
    private const int RdfPredicateOffset = -4;
    private const int RdfObjectOffset = -5;

    public SparqlParser(ReadOnlySpan<char> source)
    {
        _source = source;
        _position = 0;
        _quotedTripleCounter = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsAtEnd() => _position >= _source.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char Peek() => IsAtEnd() ? '\0' : _source[_position];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char Advance() => IsAtEnd() ? '\0' : _source[_position++];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> PeekSpan(int length)
    {
        var remaining = _source.Length - _position;
        return remaining < length ? ReadOnlySpan<char>.Empty : _source.Slice(_position, length);
    }

    private void SkipWhitespace()
    {
        while (!IsAtEnd())
        {
            var ch = Peek();
            if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n')
            {
                Advance();
            }
            else if (ch == '#') // Comment
            {
                while (!IsAtEnd() && Peek() != '\n')
                    Advance();
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>
    /// [1] QueryUnit ::= Query
    /// </summary>
    public Query ParseQuery()
    {
        SkipWhitespace();
        return ParseQueryInternal();
    }

    /// <summary>
    /// Parse a SPARQL Update operation.
    /// [29] Update ::= Prologue ( Update1 ( ';' Update )? )?
    /// </summary>
    public UpdateOperation ParseUpdate()
    {
        SkipWhitespace();
        var prologue = ParsePrologue();
        SkipWhitespace();

        var updateType = DetermineUpdateType();
        return updateType switch
        {
            QueryType.InsertData => ParseInsertData(prologue),
            QueryType.DeleteData => ParseDeleteData(prologue),
            QueryType.DeleteWhere => ParseDeleteWhere(prologue),
            QueryType.Modify => ParseModify(prologue),
            QueryType.Load => ParseLoad(prologue),
            QueryType.Clear => ParseClear(prologue),
            QueryType.Create => ParseCreate(prologue),
            QueryType.Drop => ParseDrop(prologue),
            QueryType.Copy => ParseCopy(prologue),
            QueryType.Move => ParseMove(prologue),
            QueryType.Add => ParseAdd(prologue),
            _ => throw new SparqlParseException("Expected INSERT, DELETE, LOAD, CLEAR, CREATE, DROP, COPY, MOVE, or ADD")
        };
    }

    private QueryType DetermineUpdateType()
    {
        // Use remaining length (up to 12) to handle short inputs like "CLEAR ALL"
        var remaining = _source.Length - _position;
        var span = remaining > 0 ? _source.Slice(_position, Math.Min(remaining, 12)) : ReadOnlySpan<char>.Empty;

        // INSERT DATA or INSERT (with WHERE)
        if (span.Length >= 6 && span[..6].Equals("INSERT", StringComparison.OrdinalIgnoreCase))
        {
            // Check if it's INSERT DATA
            var savedPos = _position;
            ConsumeKeyword("INSERT");
            SkipWhitespace();
            var next = PeekSpan(4);
            _position = savedPos; // Restore position
            if (next.Length >= 4 && next[..4].Equals("DATA", StringComparison.OrdinalIgnoreCase))
                return QueryType.InsertData;
            return QueryType.Modify; // INSERT { } WHERE
        }

        // DELETE DATA, DELETE WHERE, or DELETE (with INSERT)
        if (span.Length >= 6 && span[..6].Equals("DELETE", StringComparison.OrdinalIgnoreCase))
        {
            var savedPos = _position;
            ConsumeKeyword("DELETE");
            SkipWhitespace();
            var next = PeekSpan(5);
            _position = savedPos;
            if (next.Length >= 4 && next[..4].Equals("DATA", StringComparison.OrdinalIgnoreCase))
                return QueryType.DeleteData;
            if (next.Length >= 5 && next[..5].Equals("WHERE", StringComparison.OrdinalIgnoreCase))
                return QueryType.DeleteWhere;
            return QueryType.Modify; // DELETE { } INSERT { } WHERE or DELETE { } WHERE
        }

        // WITH ... DELETE/INSERT (Modify operation)
        if (span.Length >= 4 && span[..4].Equals("WITH", StringComparison.OrdinalIgnoreCase))
            return QueryType.Modify;

        if (span.Length >= 4 && span[..4].Equals("LOAD", StringComparison.OrdinalIgnoreCase))
            return QueryType.Load;
        if (span.Length >= 5 && span[..5].Equals("CLEAR", StringComparison.OrdinalIgnoreCase))
            return QueryType.Clear;
        if (span.Length >= 6 && span[..6].Equals("CREATE", StringComparison.OrdinalIgnoreCase))
            return QueryType.Create;
        if (span.Length >= 4 && span[..4].Equals("DROP", StringComparison.OrdinalIgnoreCase))
            return QueryType.Drop;
        if (span.Length >= 4 && span[..4].Equals("COPY", StringComparison.OrdinalIgnoreCase))
            return QueryType.Copy;
        if (span.Length >= 4 && span[..4].Equals("MOVE", StringComparison.OrdinalIgnoreCase))
            return QueryType.Move;
        if (span.Length >= 3 && span[..3].Equals("ADD", StringComparison.OrdinalIgnoreCase))
            return QueryType.Add;

        return QueryType.Unknown;
    }

    /// <summary>
    /// [2] Query ::= Prologue ( SelectQuery | ConstructQuery | DescribeQuery | AskQuery ) ValuesClause
    /// </summary>
    private Query ParseQueryInternal()
    {
        var prologue = ParsePrologue();
        
        SkipWhitespace();
        var queryType = DetermineQueryType();
        
        return queryType switch
        {
            QueryType.Select => ParseSelectQuery(prologue),
            QueryType.Construct => ParseConstructQuery(prologue),
            QueryType.Describe => ParseDescribeQuery(prologue),
            QueryType.Ask => ParseAskQuery(prologue),
            _ => throw new SparqlParseException("Expected SELECT, CONSTRUCT, DESCRIBE, or ASK")
        };
    }

    private QueryType DetermineQueryType()
    {
        var span = PeekSpan(10);
        if (span.Length >= 6 && span[..6].Equals("SELECT", StringComparison.OrdinalIgnoreCase))
            return QueryType.Select;
        if (span.Length >= 9 && span[..9].Equals("CONSTRUCT", StringComparison.OrdinalIgnoreCase))
            return QueryType.Construct;
        if (span.Length >= 8 && span[..8].Equals("DESCRIBE", StringComparison.OrdinalIgnoreCase))
            return QueryType.Describe;
        if (span.Length >= 3 && span[..3].Equals("ASK", StringComparison.OrdinalIgnoreCase))
            return QueryType.Ask;
        
        return QueryType.Unknown;
    }

    /// <summary>
    /// [4] Prologue ::= ( BaseDecl | PrefixDecl )*
    /// </summary>
    private Prologue ParsePrologue()
    {
        var prologue = new Prologue();
        
        while (true)
        {
            SkipWhitespace();
            var span = PeekSpan(7);
            
            if (span.Length >= 4 && span[..4].Equals("BASE", StringComparison.OrdinalIgnoreCase))
            {
                ParseBaseDecl(ref prologue);
            }
            else if (span.Length >= 6 && span[..6].Equals("PREFIX", StringComparison.OrdinalIgnoreCase))
            {
                ParsePrefixDecl(ref prologue);
            }
            else
            {
                break;
            }
        }
        
        return prologue;
    }

    private void ParseBaseDecl(ref Prologue prologue)
    {
        ConsumeKeyword("BASE");
        SkipWhitespace();
        var start = _position;
        ParseIriRef();
        prologue.BaseUriStart = start;
        prologue.BaseUriLength = _position - start;
    }

    private void ParsePrefixDecl(ref Prologue prologue)
    {
        ConsumeKeyword("PREFIX");
        SkipWhitespace();
        var prefix = ParsePnameNs();
        SkipWhitespace();
        var iri = ParseIriRef();
        prologue.AddPrefix(prefix, iri);
    }

    /// <summary>
    /// Parse a triple pattern and add it as optional.
    /// </summary>
    private bool TryParseOptionalTriplePattern(ref GraphPattern pattern)
    {
        SkipWhitespace();

        if (IsAtEnd() || Peek() == '}')
            return false;

        var subject = ParseTerm();
        if (subject.Type == TermType.Variable && subject.Length == 0)
            return false;

        SkipWhitespace();
        var predicate = ParseTerm();

        SkipWhitespace();
        var obj = ParseTerm();

        pattern.AddOptionalPattern(new TriplePattern
        {
            Subject = subject,
            Predicate = predicate,
            Object = obj
        });

        return true;
    }

    /// <summary>
    /// Try to parse a triple pattern (subject predicate object)
    /// Supports property paths in the predicate position.
    /// Expands SPARQL-star quoted triples to reification patterns.
    /// </summary>
    private bool TryParseTriplePattern(ref GraphPattern pattern)
    {
        SkipWhitespace();

        // Check if we're at end of pattern
        if (IsAtEnd() || Peek() == '}')
            return false;

        var subject = ParseTerm();
        if (subject.Type == TermType.Variable && subject.Length == 0)
            return false;

        SkipWhitespace();

        // Parse predicate - may be a property path
        var (predicate, path) = ParsePredicateOrPath();

        SkipWhitespace();
        var obj = ParseTerm();

        // Check if subject or object is a quoted triple - if so, expand to reification patterns
        if (subject.IsQuotedTriple || obj.IsQuotedTriple)
        {
            return ExpandQuotedTriplePattern(ref pattern, subject, predicate, obj, path);
        }

        pattern.AddPattern(new TriplePattern
        {
            Subject = subject,
            Predicate = predicate,
            Object = obj,
            Path = path
        });

        return true;
    }

    /// <summary>
    /// Expand a pattern containing quoted triples into reification patterns.
    /// For: &lt;&lt; s p o &gt;&gt; pred obj
    /// Generates:
    ///   ?_qt{N} rdf:type rdf:Statement .
    ///   ?_qt{N} rdf:subject s .
    ///   ?_qt{N} rdf:predicate p .
    ///   ?_qt{N} rdf:object o .
    ///   ?_qt{N} pred obj .
    /// </summary>
    private bool ExpandQuotedTriplePattern(ref GraphPattern pattern, Term subject, Term predicate, Term obj, PropertyPath path)
    {
        Term actualSubject = subject;
        Term actualObject = obj;

        // Expand subject if it's a quoted triple
        if (subject.IsQuotedTriple)
        {
            actualSubject = ExpandQuotedTriple(ref pattern, subject);
        }

        // Expand object if it's a quoted triple
        if (obj.IsQuotedTriple)
        {
            actualObject = ExpandQuotedTriple(ref pattern, obj);
        }

        // Add the main pattern with reifier(s) as subject/object
        pattern.AddPattern(new TriplePattern
        {
            Subject = actualSubject,
            Predicate = predicate,
            Object = actualObject,
            Path = path
        });

        return true;
    }

    /// <summary>
    /// Expand a single quoted triple into reification patterns.
    /// Returns a synthetic variable Term representing the reifier.
    /// </summary>
    private Term ExpandQuotedTriple(ref GraphPattern pattern, Term quotedTriple)
    {
        // Generate synthetic reifier variable: ?_qt{N}
        // We use negative offsets to indicate synthetic terms
        var reifierIndex = _quotedTripleCounter++;
        var reifierTerm = Term.Variable(-(reifierIndex + 100), 0); // Synthetic variable marker

        // Re-parse the quoted triple content to extract nested terms
        var (innerSubject, innerPredicate, innerObject) =
            ParseQuotedTripleContent(quotedTriple.Start, quotedTriple.Length);

        // Handle nested quoted triples recursively
        if (innerSubject.IsQuotedTriple)
        {
            innerSubject = ExpandQuotedTriple(ref pattern, innerSubject);
        }
        if (innerObject.IsQuotedTriple)
        {
            innerObject = ExpandQuotedTriple(ref pattern, innerObject);
        }

        // Add reification patterns:
        // ?_qt{N} rdf:type rdf:Statement
        pattern.AddPattern(new TriplePattern
        {
            Subject = reifierTerm,
            Predicate = Term.Iri(RdfTypeOffset, 0),
            Object = Term.Iri(RdfStatementOffset, 0)
        });

        // ?_qt{N} rdf:subject innerSubject
        pattern.AddPattern(new TriplePattern
        {
            Subject = reifierTerm,
            Predicate = Term.Iri(RdfSubjectOffset, 0),
            Object = innerSubject
        });

        // ?_qt{N} rdf:predicate innerPredicate
        pattern.AddPattern(new TriplePattern
        {
            Subject = reifierTerm,
            Predicate = Term.Iri(RdfPredicateOffset, 0),
            Object = innerPredicate
        });

        // ?_qt{N} rdf:object innerObject
        pattern.AddPattern(new TriplePattern
        {
            Subject = reifierTerm,
            Predicate = Term.Iri(RdfObjectOffset, 0),
            Object = innerObject
        });

        return reifierTerm;
    }

    /// <summary>
    /// Parse a predicate which may be a property path expression.
    /// Returns both the simple Term (for backwards compatibility) and a PropertyPath.
    /// </summary>
    private (Term predicate, PropertyPath path) ParsePredicateOrPath()
    {
        SkipWhitespace();
        var ch = Peek();

        // Check for inverse path: ^predicate
        if (ch == '^')
        {
            Advance(); // Skip '^'
            SkipWhitespace();
            var iri = ParseTerm();
            return (iri, PropertyPath.Inverse(iri));
        }

        // Parse the base term
        var term = ParseTerm();

        // Check for path modifier after the term
        SkipWhitespace();
        ch = Peek();

        if (ch == '*')
        {
            Advance();
            return (term, PropertyPath.ZeroOrMore(term));
        }

        if (ch == '+')
        {
            Advance();
            return (term, PropertyPath.OneOrMore(term));
        }

        if (ch == '?')
        {
            // Need to distinguish from variable - '?' followed by letter is variable
            var next = PeekAt(1);
            if (!IsLetter(next) && next != '_')
            {
                Advance();
                return (term, PropertyPath.ZeroOrOne(term));
            }
        }

        // Check for sequence or alternative (simple case: iri1/iri2 or iri1|iri2)
        if (ch == '/')
        {
            var leftStart = term.Start;
            var leftLength = term.Length;
            Advance(); // Skip '/'
            SkipWhitespace();
            var right = ParseTerm();
            return (term, PropertyPath.Sequence(leftStart, leftLength, right.Start, right.Length));
        }

        if (ch == '|')
        {
            var leftStart = term.Start;
            var leftLength = term.Length;
            Advance(); // Skip '|'
            SkipWhitespace();
            var right = ParseTerm();
            return (term, PropertyPath.Alternative(leftStart, leftLength, right.Start, right.Length));
        }

        // Simple predicate - no path
        return (term, default);
    }

    /// <summary>
    /// Parse a term (variable, IRI, literal, or quoted triple)
    /// </summary>
    private Term ParseTerm()
    {
        SkipWhitespace();
        var ch = Peek();

        // Variable: ?name or $name
        if (ch == '?' || ch == '$')
        {
            return ParseVariable();
        }

        // Quoted triple (SPARQL-star): << s p o >>
        if (ch == '<' && PeekAt(1) == '<')
        {
            return ParseQuotedTriple();
        }

        // IRI: <uri>
        if (ch == '<')
        {
            return ParseTermIriRef();
        }

        // Prefixed name: prefix:local
        if (IsLetter(ch))
        {
            return ParsePrefixedNameOrKeyword();
        }

        // Literal: "string" or 'string'
        if (ch == '"' || ch == '\'')
        {
            return ParseLiteral();
        }

        // Numeric literal
        if (IsDigit(ch) || ch == '-' || ch == '+')
        {
            return ParseNumericLiteral();
        }

        // Blank node: _:name
        if (ch == '_')
        {
            return ParseBlankNode();
        }

        // 'a' as shorthand for rdf:type
        if (ch == 'a' && !IsLetterOrDigit(PeekAt(1)))
        {
            Advance();
            return Term.Iri(_position - 1, 1); // 'a' shorthand
        }

        return default;
    }

    /// <summary>
    /// Parse a variable: ?name or $name
    /// </summary>
    private Term ParseVariable()
    {
        var start = _position;
        Advance(); // Skip ? or $

        while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
            Advance();

        return Term.Variable(start, _position - start);
    }

    /// <summary>
    /// Parse a quoted triple (SPARQL-star): &lt;&lt; subject predicate object &gt;&gt;
    /// Records the range; nested terms are re-parsed on demand during expansion.
    /// </summary>
    private Term ParseQuotedTriple()
    {
        var start = _position;

        // Consume '<<'
        Advance(); // '<'
        Advance(); // '<'
        SkipWhitespace();

        // Parse subject (can be IRI, blank node, variable, or nested quoted triple)
        ParseTerm(); // We don't store this; re-parse on demand
        SkipWhitespace();

        // Parse predicate (IRI, variable, or 'a')
        ParseTerm();
        SkipWhitespace();

        // Parse object (can be IRI, blank node, literal, variable, or nested quoted triple)
        ParseTerm();
        SkipWhitespace();

        // Consume '>>'
        if (Peek() != '>' || PeekAt(1) != '>')
            throw new InvalidOperationException("Expected '>>' to close quoted triple");
        Advance(); // '>'
        Advance(); // '>'

        return Term.QuotedTriple(start, _position - start);
    }

    /// <summary>
    /// Parse the content of a quoted triple, returning the three nested terms.
    /// This is used during pattern expansion to extract subject, predicate, object.
    /// </summary>
    private (Term subject, Term predicate, Term obj) ParseQuotedTripleContent(int start, int length)
    {
        // Save and restore position
        var savedPos = _position;
        _position = start + 2; // Skip '<<'
        SkipWhitespace();

        var subject = ParseTerm();
        SkipWhitespace();
        var predicate = ParseTerm();
        SkipWhitespace();
        var obj = ParseTerm();

        _position = savedPos;
        return (subject, predicate, obj);
    }

    /// <summary>
    /// Parse an IRI reference: &lt;uri&gt; (returns Term)
    /// Includes angle brackets in the term for matching against stored IRIs.
    /// </summary>
    private Term ParseTermIriRef()
    {
        // Include angle brackets in the term for matching against stored IRIs
        var start = _position;
        Advance(); // Skip '<'

        while (!IsAtEnd() && Peek() != '>')
            Advance();

        if (Peek() == '>')
            Advance(); // Include '>'

        var length = _position - start;
        return Term.Iri(start, length);
    }

    /// <summary>
    /// Parse a prefixed name or check for keyword
    /// </summary>
    private Term ParsePrefixedNameOrKeyword()
    {
        var start = _position;

        // Read prefix part
        while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
            Advance();

        // Check for colon (prefixed name)
        if (Peek() == ':')
        {
            Advance(); // Skip ':'

            // Read local part
            while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_' || Peek() == '-' || Peek() == '.'))
            {
                // Don't include trailing dot
                if (Peek() == '.' && !IsLetterOrDigit(PeekAt(1)))
                    break;
                Advance();
            }

            return Term.Iri(start, _position - start);
        }

        // Not a prefixed name - might be a keyword, reset
        // Check if this looks like a keyword we should skip
        var word = _source.Slice(start, _position - start);
        if (word.Equals("FILTER", StringComparison.OrdinalIgnoreCase) ||
            word.Equals("OPTIONAL", StringComparison.OrdinalIgnoreCase) ||
            word.Equals("UNION", StringComparison.OrdinalIgnoreCase))
        {
            _position = start; // Reset
            return default;
        }

        // Treat as bare word (error in strict parsing, but we'll be lenient)
        return Term.Iri(start, _position - start);
    }

    /// <summary>
    /// Parse a string literal
    /// </summary>
    private Term ParseLiteral()
    {
        var quote = Peek();
        var start = _position;

        Advance(); // Skip opening quote

        // Check for long string (""" or ''')
        if (Peek() == quote && PeekAt(1) == quote)
        {
            Advance();
            Advance();
            // Long string - read until closing """
            while (!IsAtEnd())
            {
                if (Peek() == quote && PeekAt(1) == quote && PeekAt(2) == quote)
                {
                    Advance();
                    Advance();
                    Advance();
                    break;
                }
                Advance();
            }
        }
        else
        {
            // Short string - read until closing quote
            while (!IsAtEnd() && Peek() != quote)
            {
                if (Peek() == '\\')
                    Advance(); // Skip escape
                Advance();
            }

            if (Peek() == quote)
                Advance(); // Skip closing quote
        }

        // Check for language tag @en or datatype ^^<type>
        if (Peek() == '@')
        {
            Advance();
            while (!IsAtEnd() && (IsLetter(Peek()) || Peek() == '-'))
                Advance();
        }
        else if (Peek() == '^' && PeekAt(1) == '^')
        {
            Advance();
            Advance();
            ParseTerm(); // Parse datatype IRI
        }

        return Term.Literal(start, _position - start);
    }

    /// <summary>
    /// Parse a numeric literal
    /// </summary>
    private Term ParseNumericLiteral()
    {
        var start = _position;

        if (Peek() == '-' || Peek() == '+')
            Advance();

        while (!IsAtEnd() && IsDigit(Peek()))
            Advance();

        // Decimal or double
        if (Peek() == '.')
        {
            Advance();
            while (!IsAtEnd() && IsDigit(Peek()))
                Advance();
        }

        // Exponent
        if (Peek() == 'e' || Peek() == 'E')
        {
            Advance();
            if (Peek() == '-' || Peek() == '+')
                Advance();
            while (!IsAtEnd() && IsDigit(Peek()))
                Advance();
        }

        return Term.Literal(start, _position - start);
    }

    /// <summary>
    /// Parse a blank node: _:name
    /// </summary>
    private Term ParseBlankNode()
    {
        var start = _position;
        Advance(); // Skip '_'

        if (Peek() == ':')
        {
            Advance(); // Skip ':'
            while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_' || Peek() == '-' || Peek() == '.'))
            {
                if (Peek() == '.' && !IsLetterOrDigit(PeekAt(1)))
                    break;
                Advance();
            }
        }

        return Term.BlankNode(start, _position - start);
    }

    /// <summary>
    /// Skip until closing brace (for unsupported constructs)
    /// </summary>
    private void SkipUntilClosingBrace()
    {
        var depth = 0;
        while (!IsAtEnd())
        {
            var ch = Advance();
            if (ch == '{') depth++;
            else if (ch == '}')
            {
                if (depth == 0) break;
                depth--;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private char PeekAt(int offset)
    {
        var pos = _position + offset;
        return pos >= _source.Length ? '\0' : _source[pos];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsLetter(char ch) => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDigit(char ch) => ch >= '0' && ch <= '9';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsLetterOrDigit(char ch) => IsLetter(ch) || IsDigit(ch);

    private SolutionModifier ParseSolutionModifier()
    {
        var modifier = new SolutionModifier();
        SkipWhitespace();

        // Parse GROUP BY (must come before ORDER BY)
        var span = PeekSpan(8);
        if (span.Length >= 5 && span[..5].Equals("GROUP", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("GROUP");
            SkipWhitespace();
            ConsumeKeyword("BY");
            SkipWhitespace();

            modifier.GroupBy = ParseGroupByClause();
        }

        // Parse HAVING (must come after GROUP BY, before ORDER BY)
        SkipWhitespace();
        span = PeekSpan(6);
        if (span.Length >= 6 && span[..6].Equals("HAVING", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("HAVING");
            SkipWhitespace();

            // HAVING expression is in parentheses
            if (Peek() == '(')
            {
                Advance(); // Skip '('
                var start = _position;

                // Find matching closing paren
                int depth = 1;
                while (!IsAtEnd() && depth > 0)
                {
                    var ch = Peek();
                    if (ch == '(') depth++;
                    else if (ch == ')') depth--;
                    if (depth > 0) Advance();
                }

                var length = _position - start;
                modifier.Having = new HavingClause { ExpressionStart = start, ExpressionLength = length };

                if (Peek() == ')')
                    Advance(); // Skip ')'
            }
        }

        // Parse ORDER BY
        SkipWhitespace();
        span = PeekSpan(8);
        if (span.Length >= 5 && span[..5].Equals("ORDER", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("ORDER");
            SkipWhitespace();
            ConsumeKeyword("BY");
            SkipWhitespace();
            
            modifier.OrderBy = ParseOrderByClause();
        }
        
        // Parse LIMIT
        SkipWhitespace();
        span = PeekSpan(5);
        if (span.Length >= 5 && span[..5].Equals("LIMIT", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("LIMIT");
            SkipWhitespace();
            modifier.Limit = ParseInteger();
        }
        
        // Parse OFFSET
        SkipWhitespace();
        span = PeekSpan(6);
        if (span.Length >= 6 && span[..6].Equals("OFFSET", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("OFFSET");
            SkipWhitespace();
            modifier.Offset = ParseInteger();
        }

        // Parse temporal clauses (AS OF, DURING, ALL VERSIONS)
        SkipWhitespace();
        modifier.Temporal = ParseTemporalClause();

        return modifier;
    }

    private TemporalClause ParseTemporalClause()
    {
        var clause = new TemporalClause { Mode = TemporalQueryMode.Current };

        var span = PeekSpan(12);  // "ALL VERSIONS" is longest

        // Check for "AS OF"
        if (span.Length >= 2 && span[..2].Equals("AS", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("AS");
            SkipWhitespace();
            ConsumeKeyword("OF");
            SkipWhitespace();

            clause.Mode = TemporalQueryMode.AsOf;
            (clause.TimeStartStart, clause.TimeStartLength) = ParseTemporalLiteral();
        }
        // Check for "DURING"
        else if (span.Length >= 6 && span[..6].Equals("DURING", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("DURING");
            SkipWhitespace();

            clause.Mode = TemporalQueryMode.During;

            // Parse [start, end] bracket syntax
            if (Peek() == '[')
            {
                Advance(); // Skip '['
                SkipWhitespace();
                (clause.TimeStartStart, clause.TimeStartLength) = ParseTemporalLiteral();
                SkipWhitespace();
                if (Peek() == ',')
                    Advance(); // Skip ','
                SkipWhitespace();
                (clause.TimeEndStart, clause.TimeEndLength) = ParseTemporalLiteral();
                SkipWhitespace();
                if (Peek() == ']')
                    Advance(); // Skip ']'
            }
        }
        // Check for "ALL VERSIONS"
        else if (span.Length >= 3 && span[..3].Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("ALL");
            SkipWhitespace();
            ConsumeKeyword("VERSIONS");

            clause.Mode = TemporalQueryMode.AllVersions;
        }

        return clause;
    }

    private (int Start, int Length) ParseTemporalLiteral()
    {
        // Parse "value"^^xsd:date or "value"^^xsd:dateTime or just "value"
        int start = _position;

        if (Peek() != '"')
            return (0, 0);

        Advance(); // Skip opening '"'

        // Read until closing quote
        while (!IsAtEnd() && Peek() != '"')
            Advance();

        if (Peek() == '"')
            Advance(); // Skip closing '"'

        // Check for optional datatype suffix
        if (_position + 1 < _source.Length && _source[_position] == '^' && _source[_position + 1] == '^')
        {
            _position += 2; // Skip ^^

            // Skip datatype IRI (xsd:date, xsd:dateTime, <uri>, etc.)
            if (Peek() == '<')
            {
                // Full IRI: <http://...>
                Advance();
                while (!IsAtEnd() && Peek() != '>')
                    Advance();
                if (Peek() == '>')
                    Advance();
            }
            else
            {
                // Prefixed name: xsd:date
                while (!IsAtEnd() && !char.IsWhiteSpace(Peek())
                       && Peek() != ',' && Peek() != ']' && Peek() != ')')
                    Advance();
            }
        }

        return (start, _position - start);
    }

    private GroupByClause ParseGroupByClause()
    {
        var clause = new GroupByClause();

        // Parse one or more grouping variables
        while (!IsAtEnd() && clause.Count < GroupByClause.MaxVariables)
        {
            SkipWhitespace();

            // Check for variable
            if (Peek() != '?')
                break;

            var start = _position;
            Advance(); // Skip '?'

            // Parse variable name
            while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                Advance();

            var length = _position - start;
            if (length > 1) // Must have at least one character after '?'
            {
                clause.AddVariable(start, length);
            }

            SkipWhitespace();
        }

        return clause;
    }

    private OrderByClause ParseOrderByClause()
    {
        var clause = new OrderByClause();

        // Parse one or more order conditions
        while (!IsAtEnd() && clause.Count < 4)
        {
            SkipWhitespace();

            var direction = OrderDirection.Ascending;
            bool hasDirectionKeyword = false;

            // Check for ASC/DESC keyword with parenthesis: ASC(?var) or DESC(?var)
            var span = PeekSpan(4);
            if (span.Length >= 3 && span[..3].Equals("ASC", StringComparison.OrdinalIgnoreCase))
            {
                ConsumeKeyword("ASC");
                SkipWhitespace();
                hasDirectionKeyword = true;
            }
            else if (span.Length >= 4 && span[..4].Equals("DESC", StringComparison.OrdinalIgnoreCase))
            {
                ConsumeKeyword("DESC");
                SkipWhitespace();
                direction = OrderDirection.Descending;
                hasDirectionKeyword = true;
            }

            // If we had ASC/DESC, expect parenthesis around variable
            if (hasDirectionKeyword)
            {
                if (Peek() == '(')
                {
                    Advance(); // Skip '('
                    SkipWhitespace();
                }
            }

            // Parse variable
            if (Peek() == '?')
            {
                var varStart = _position;
                Advance(); // Skip '?'
                while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                    Advance();
                var varLength = _position - varStart;

                clause.AddCondition(varStart, varLength, direction);

                // Skip closing parenthesis if we had direction keyword
                if (hasDirectionKeyword)
                {
                    SkipWhitespace();
                    if (Peek() == ')')
                        Advance();
                }
            }
            else
            {
                // Not a variable - stop parsing order conditions
                break;
            }

            SkipWhitespace();

            // Check if next token is LIMIT, OFFSET, or end - if so, stop
            span = PeekSpan(6);
            if (span.Length >= 5 && span[..5].Equals("LIMIT", StringComparison.OrdinalIgnoreCase))
                break;
            if (span.Length >= 6 && span[..6].Equals("OFFSET", StringComparison.OrdinalIgnoreCase))
                break;

            // Check for ASC/DESC or variable to continue
            if (span.Length >= 3 && span[..3].Equals("ASC", StringComparison.OrdinalIgnoreCase))
                continue;
            if (span.Length >= 4 && span[..4].Equals("DESC", StringComparison.OrdinalIgnoreCase))
                continue;
            if (Peek() == '?')
                continue;

            break;
        }

        return clause;
    }

    private int ParseInteger()
    {
        var start = _position;
        
        while (!IsAtEnd() && char.IsDigit(Peek()))
            Advance();
        
        var span = _source.Slice(start, _position - start);
        return int.TryParse(span, out var result) ? result : 0;
    }

    private ReadOnlySpan<char> ParseIriRef()
    {
        if (Peek() != '<')
            throw new SparqlParseException("Expected '<' for IRI reference");
        
        var start = _position;
        Advance(); // Skip '<'
        
        while (!IsAtEnd() && Peek() != '>')
            Advance();
        
        if (IsAtEnd())
            throw new SparqlParseException("Unterminated IRI reference");
        
        var end = _position;
        Advance(); // Skip '>'
        
        return _source.Slice(start, end - start + 1);
    }

    private ReadOnlySpan<char> ParsePnameNs()
    {
        var start = _position;
        
        while (!IsAtEnd() && Peek() != ':')
            Advance();
        
        if (IsAtEnd())
            throw new SparqlParseException("Expected ':' in prefix name");
        
        Advance(); // Skip ':'
        return _source.Slice(start, _position - start);
    }

    private void ConsumeKeyword(ReadOnlySpan<char> keyword)
    {
        var span = PeekSpan(keyword.Length);
        if (!span.Equals(keyword, StringComparison.OrdinalIgnoreCase))
            throw new SparqlParseException($"Expected keyword '{keyword.ToString()}'");
        
        _position += keyword.Length;
    }
}
