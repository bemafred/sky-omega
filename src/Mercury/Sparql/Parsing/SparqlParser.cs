using System;
using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkyOmega.Mercury.Sparql;

namespace SkyOmega.Mercury.Sparql.Parsing;

/// <summary>
/// Zero-allocation SPARQL 1.1 parser using stack-based parsing and Span&lt;T&gt;
/// Based on SPARQL 1.1 Query Language EBNF grammar.
/// </summary>
/// <remarks>
/// <para><b>Thread Safety:</b> This is a ref struct that exists only on the stack.
/// It cannot be shared between threads or captured in closures/async methods.
/// Each thread must create its own parser instance.</para>
/// <para><b>Usage Pattern:</b> Create a new instance for each query to parse.
/// The parser is designed for single-use: parse one query, then discard.</para>
/// </remarks>
public ref partial struct SparqlParser
{
    private ReadOnlySpan<char> _source;
    private int _position;
    private int _quotedTripleCounter; // Counter for generating synthetic reifier variables
    private int _seqVarCounter; // Counter for generating synthetic sequence intermediate variables
    private int _blankNodePropListCounter; // Counter for generating synthetic blank nodes for [ ] property lists
    private int _currentDepth; // Current recursion depth for subqueries, paths, quoted triples
    private readonly int _maxDepth; // Maximum allowed recursion depth

    // Default maximum query depth (subqueries, property paths, quoted triples)
    public const int DefaultMaxDepth = 10;

    // RDF namespace constants for reification expansion (offsets into synthetic buffer)
    // These are appended to the source during query execution
    private const int RdfTypeOffset = -1;
    private const int RdfStatementOffset = -2;
    private const int RdfSubjectOffset = -3;
    private const int RdfPredicateOffset = -4;
    private const int RdfObjectOffset = -5;

    public SparqlParser(ReadOnlySpan<char> source)
        : this(source, DefaultMaxDepth)
    {
    }

    public SparqlParser(ReadOnlySpan<char> source, int maxDepth)
    {
        if (maxDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDepth), "Max depth must be positive");

        _source = source;
        _position = 0;
        _quotedTripleCounter = 0;
        _seqVarCounter = 0;
        _blankNodePropListCounter = 0;
        _currentDepth = 0;
        _maxDepth = maxDepth;
    }

    /// <summary>
    /// Checks depth limit and increments current depth. Must be paired with DecrementDepth.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void IncrementDepth(string context)
    {
        if (_currentDepth >= _maxDepth)
            throw new SparqlParseException($"{context} nesting exceeds maximum depth of {_maxDepth}");
        _currentDepth++;
    }

    /// <summary>
    /// Decrements current depth after recursive operation completes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DecrementDepth()
    {
        _currentDepth--;
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
        // Return what's available, even if less than requested
        return remaining <= 0 ? ReadOnlySpan<char>.Empty : _source.Slice(_position, Math.Min(length, remaining));
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

        // syn-bad-pname-05: Validate prefix name starts with letter (or is empty for default prefix)
        // SPARQL grammar: PNAME_NS ::= PN_PREFIX? ':'
        // PN_PREFIX ::= PN_CHARS_BASE ((PN_CHARS | '.')* PN_CHARS)?
        // PN_CHARS_BASE ::= [A-Z] | [a-z] | [#x00C0-#x00D6] | ...
        if (!IsAtEnd() && Peek() != ':')
        {
            var firstChar = Peek();
            if (!IsLetter(firstChar) && firstChar != '_')
            {
                throw new SparqlParseException($"PREFIX name must start with a letter, not '{firstChar}'");
            }
        }

        // Track prefix position
        var prefixStart = _position;
        ParsePnameNs();
        var prefixLength = _position - prefixStart;

        SkipWhitespace();

        // Track IRI position
        var iriStart = _position;
        ParseIriRef();
        var iriLength = _position - iriStart;

        prologue.AddPrefixRange(prefixStart, prefixLength, iriStart, iriLength);
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
    /// Handles semicolon-separated predicate-object lists: ?s :p1 ?o1; :p2 ?o2
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

        // syn-bad-pname-08/09: Check for incomplete triple pattern (subject only)
        // If we hit '.' or '}' immediately after subject, the pattern is incomplete
        // EXCEPTION: Blank node property lists [ :p :o ] are complete patterns on their own
        if (Peek() == '.' || Peek() == '}')
        {
            // Blank node property lists starting with '[' ARE complete patterns
            // They contain their own predicate-object pairs within the brackets
            if (subject.IsBlankNode && _source[subject.Start] == '[')
            {
                // This is a valid standalone blank node property list pattern
                // The patterns inside are not expanded here - they're captured in the term
                pattern.AddPattern(new TriplePattern
                {
                    Subject = subject,
                    Predicate = default,
                    Object = default,
                    Path = default
                });
                return true;
            }
            throw new SparqlParseException("Incomplete triple pattern - expected predicate and object after subject");
        }

        // Parse predicate - may be a property path
        var (predicate, path) = ParsePredicateOrPath();

        // Check that we got a valid predicate
        if (predicate.Length == 0 && path.Type == PathType.None)
        {
            throw new SparqlParseException("Incomplete triple pattern - expected predicate");
        }

        SkipWhitespace();

        // Check for incomplete triple pattern (subject and predicate only)
        if (Peek() == '.' || Peek() == '}')
        {
            throw new SparqlParseException("Incomplete triple pattern - expected object after predicate");
        }

        var obj = ParseTerm();

        // Check that we got a valid object
        if (obj.Length == 0)
        {
            throw new SparqlParseException("Incomplete triple pattern - expected object");
        }

        // Add the first pattern
        AddTriplePatternOrExpand(ref pattern, subject, predicate, obj, path);

        // Handle semicolon-separated predicate-object lists (same subject)
        // Grammar: PropertyListNotEmpty ::= Verb ObjectList ( ';' ( Verb ObjectList )? )*
        SkipWhitespace();
        while (Peek() == ';')
        {
            Advance(); // Skip ';'
            SkipWhitespace();

            // Check for empty predicate-object after semicolon (valid: "?s :p ?o ;")
            if (IsAtEnd() || Peek() == '}' || Peek() == '.')
                break;

            // Check for keywords that indicate end of property list
            var span = PeekSpan(8);
            if ((span.Length >= 6 && span[..6].Equals("FILTER", StringComparison.OrdinalIgnoreCase)) ||
                (span.Length >= 8 && span[..8].Equals("OPTIONAL", StringComparison.OrdinalIgnoreCase)) ||
                (span.Length >= 5 && span[..5].Equals("MINUS", StringComparison.OrdinalIgnoreCase)) ||
                (span.Length >= 4 && span[..4].Equals("BIND", StringComparison.OrdinalIgnoreCase)) ||
                (span.Length >= 6 && span[..6].Equals("VALUES", StringComparison.OrdinalIgnoreCase)) ||
                (span.Length >= 7 && span[..7].Equals("SERVICE", StringComparison.OrdinalIgnoreCase)) ||
                (span.Length >= 5 && span[..5].Equals("GRAPH", StringComparison.OrdinalIgnoreCase)))
                break;

            // Parse next predicate-object pair with same subject
            var (nextPredicate, nextPath) = ParsePredicateOrPath();
            SkipWhitespace();
            var nextObj = ParseTerm();

            AddTriplePatternOrExpand(ref pattern, subject, nextPredicate, nextObj, nextPath);

            SkipWhitespace();
        }

        return true;
    }

    /// <summary>
    /// Add a triple pattern, handling quoted triples, blank node property lists, and sequence paths.
    /// </summary>
    private void AddTriplePatternOrExpand(ref GraphPattern pattern, Term subject, Term predicate, Term obj, PropertyPath path)
    {
        // Check if subject or object is a quoted triple - if so, expand to reification patterns
        if (subject.IsQuotedTriple || obj.IsQuotedTriple)
        {
            ExpandQuotedTriplePattern(ref pattern, subject, predicate, obj, path);
            return;
        }

        // Check if subject or object is a blank node property list [ :p :o ]
        // These need to be expanded to multiple patterns with a generated blank node
        Term actualSubject = subject;
        Term actualObject = obj;

        if (IsBlankNodePropertyList(subject))
        {
            actualSubject = ExpandBlankNodePropertyList(ref pattern, subject);
        }

        if (IsBlankNodePropertyList(obj))
        {
            actualObject = ExpandBlankNodePropertyList(ref pattern, obj);
        }

        // Check for sequence path - expand to multiple patterns with intermediate variables
        if (path.Type == PathType.Sequence)
        {
            ExpandSequencePath(ref pattern, actualSubject, actualObject, path);
            return;
        }

        pattern.AddPattern(new TriplePattern
        {
            Subject = actualSubject,
            Predicate = predicate,
            Object = actualObject,
            Path = path
        });
    }

    /// <summary>
    /// Check if a term is a blank node property list (starts with '[').
    /// </summary>
    private bool IsBlankNodePropertyList(Term term)
    {
        if (!term.IsBlankNode || term.Length < 2)
            return false;

        // Check if it starts with '[' - if so, it's a property list, not a named blank node like _:b1
        return term.Start >= 0 && term.Start < _source.Length && _source[term.Start] == '[';
    }

    /// <summary>
    /// Expand a blank node property list into multiple patterns.
    /// For: ?s :p [ :q :r ; :t :u ]
    /// Generates a synthetic variable ?_bn{N} and patterns:
    ///   ?_bn{N} :q :r .
    ///   ?_bn{N} :t :u .
    /// Returns the synthetic variable term to be used in the main pattern.
    /// Uses synthetic variables (not blank nodes) so patterns join properly.
    /// </summary>
    private Term ExpandBlankNodePropertyList(ref GraphPattern pattern, Term blankNodePropList)
    {
        // Generate a synthetic variable for joining (not a blank node, which would be a wildcard)
        // We use negative offsets with a specific range to indicate synthetic blank node property list vars
        var blankNodeIndex = _blankNodePropListCounter++;
        // Use negative start in range -300 to -331 to indicate synthetic blank node property list variable
        var syntheticVar = Term.Variable(-(blankNodeIndex + 300), 0);

        // Parse the property list inside the brackets
        // Save current position and temporarily move to parse the inner content
        var savedPosition = _position;
        _position = blankNodePropList.Start + 1; // Skip the opening '['
        SkipWhitespace();

        // Parse predicate-object pairs until we hit the closing ']'
        while (!IsAtEnd() && Peek() != ']')
        {
            SkipWhitespace();

            // Parse predicate
            var (innerPredicate, innerPath) = ParsePredicateOrPath();
            if (innerPredicate.Length == 0 && innerPath.Type == PathType.None)
                break;

            SkipWhitespace();

            // Parse object
            var innerObject = ParseTerm();
            if (innerObject.Length == 0)
                break;

            // Check if inner object is itself a blank node property list (nested)
            Term actualInnerObject = innerObject;
            if (IsBlankNodePropertyList(innerObject))
            {
                actualInnerObject = ExpandBlankNodePropertyList(ref pattern, innerObject);
            }

            // Add the pattern: syntheticVar innerPredicate innerObject
            if (innerPath.Type == PathType.Sequence)
            {
                ExpandSequencePath(ref pattern, syntheticVar, actualInnerObject, innerPath);
            }
            else
            {
                pattern.AddPattern(new TriplePattern
                {
                    Subject = syntheticVar,
                    Predicate = innerPredicate,
                    Object = actualInnerObject,
                    Path = innerPath
                });
            }

            SkipWhitespace();

            // Handle comma-separated objects (same predicate): [ :p :o1, :o2 ]
            while (Peek() == ',')
            {
                Advance(); // Skip ','
                SkipWhitespace();

                innerObject = ParseTerm();
                if (innerObject.Length == 0)
                    break;

                actualInnerObject = innerObject;
                if (IsBlankNodePropertyList(innerObject))
                {
                    actualInnerObject = ExpandBlankNodePropertyList(ref pattern, innerObject);
                }

                if (innerPath.Type == PathType.Sequence)
                {
                    ExpandSequencePath(ref pattern, syntheticVar, actualInnerObject, innerPath);
                }
                else
                {
                    pattern.AddPattern(new TriplePattern
                    {
                        Subject = syntheticVar,
                        Predicate = innerPredicate,
                        Object = actualInnerObject,
                        Path = innerPath
                    });
                }

                SkipWhitespace();
            }

            // Handle semicolon-separated predicate-object pairs: [ :p :o ; :q :r ]
            if (Peek() == ';')
            {
                Advance(); // Skip ';'
                SkipWhitespace();
                // Continue to next predicate-object pair
            }
            else if (Peek() == '.')
            {
                Advance(); // Skip '.'
                SkipWhitespace();
            }
            else
            {
                // No more pairs
                break;
            }
        }

        // Restore position (we've parsed the property list, caller will skip past the ']')
        _position = savedPosition;

        return syntheticVar;
    }

    /// <summary>
    /// Expand a blank node property list into multiple patterns for a SubSelect.
    /// Overload that works with SubSelect instead of GraphPattern.
    /// </summary>
    private Term ExpandBlankNodePropertyListForSubSelect(ref SubSelect subSelect, Term blankNodePropList)
    {
        // Generate a synthetic variable for joining
        var blankNodeIndex = _blankNodePropListCounter++;
        var syntheticVar = Term.Variable(-(blankNodeIndex + 300), 0);

        // Parse the property list inside the brackets
        var savedPosition = _position;
        _position = blankNodePropList.Start + 1; // Skip the opening '['
        SkipWhitespace();

        // Parse predicate-object pairs until we hit the closing ']'
        while (!IsAtEnd() && Peek() != ']')
        {
            SkipWhitespace();

            // Parse predicate (simplified - no path support in subselect version for now)
            var innerPredicate = ParseTerm();
            if (innerPredicate.Length == 0)
                break;

            SkipWhitespace();

            // Parse object
            var innerObject = ParseTerm();
            if (innerObject.Length == 0)
                break;

            // Check if inner object is itself a blank node property list (nested)
            Term actualInnerObject = innerObject;
            if (IsBlankNodePropertyList(innerObject))
            {
                actualInnerObject = ExpandBlankNodePropertyListForSubSelect(ref subSelect, innerObject);
            }

            // Add the pattern: syntheticVar innerPredicate innerObject
            subSelect.AddPattern(new TriplePattern
            {
                Subject = syntheticVar,
                Predicate = innerPredicate,
                Object = actualInnerObject
            });

            SkipWhitespace();

            // Handle comma-separated objects (same predicate)
            while (Peek() == ',')
            {
                Advance();
                SkipWhitespace();

                innerObject = ParseTerm();
                if (innerObject.Length == 0)
                    break;

                actualInnerObject = innerObject;
                if (IsBlankNodePropertyList(innerObject))
                {
                    actualInnerObject = ExpandBlankNodePropertyListForSubSelect(ref subSelect, innerObject);
                }

                subSelect.AddPattern(new TriplePattern
                {
                    Subject = syntheticVar,
                    Predicate = innerPredicate,
                    Object = actualInnerObject
                });

                SkipWhitespace();
            }

            // Handle semicolon-separated predicate-object pairs
            if (Peek() == ';')
            {
                Advance();
                SkipWhitespace();
            }
            else if (Peek() == '.')
            {
                Advance();
                SkipWhitespace();
            }
            else
            {
                break;
            }
        }

        _position = savedPosition;
        return syntheticVar;
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
        IncrementDepth("Quoted triple");
        try
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
        finally
        {
            DecrementDepth();
        }
    }

    /// <summary>
    /// Expand a sequence path (p1/p2) into multiple triple patterns with synthetic intermediate variables.
    /// For: ?s &lt;p1&gt;/&lt;p2&gt; ?o
    /// Generates:
    ///   ?s &lt;p1&gt; ?_seq0 .
    ///   ?_seq0 &lt;p2&gt; ?o .
    /// Handles nested sequences recursively: ?s &lt;p1&gt;/&lt;p2&gt;/&lt;p3&gt; ?o becomes 3 patterns.
    /// </summary>
    private bool ExpandSequencePath(ref GraphPattern pattern, Term subject, Term obj, PropertyPath path)
    {
        IncrementDepth("Property path");
        try
        {
            // Generate synthetic intermediate variable: ?_seq{N}
            var seqIndex = _seqVarCounter++;
            var intermediateTerm = Term.Variable(-(seqIndex + 200), 0); // Synthetic sequence variable marker

            // Re-parse left and right path segments from source offsets
            var (leftPredicate, leftPath) = ParsePathSegment(path.LeftStart, path.LeftLength);
            var (rightPredicate, rightPath) = ParsePathSegment(path.RightStart, path.RightLength);

            // Add left pattern: subject -> intermediate
            if (leftPath.Type == PathType.Sequence)
            {
                // Recursively expand nested sequence on left side
                ExpandSequencePath(ref pattern, subject, intermediateTerm, leftPath);
            }
            else
            {
                pattern.AddPattern(new TriplePattern
                {
                    Subject = subject,
                    Predicate = leftPredicate,
                    Object = intermediateTerm,
                    Path = leftPath
                });
            }

            // Add right pattern: intermediate -> object
            if (rightPath.Type == PathType.Sequence)
            {
                // Recursively expand nested sequence on right side
                ExpandSequencePath(ref pattern, intermediateTerm, obj, rightPath);
            }
            else
            {
                pattern.AddPattern(new TriplePattern
                {
                    Subject = intermediateTerm,
                    Predicate = rightPredicate,
                    Object = obj,
                    Path = rightPath
                });
            }

            return true;
        }
        finally
        {
            DecrementDepth();
        }
    }

    /// <summary>
    /// Re-parse a path segment from source offsets.
    /// Handles nested sequences/alternatives within the segment bounds.
    /// </summary>
    private (Term predicate, PropertyPath path) ParsePathSegment(int start, int length)
    {
        // Save current parser position
        var savedPosition = _position;
        var segmentEnd = start + length;

        // Temporarily set position to the segment location
        _position = start;

        SkipWhitespace();
        var ch = Peek();

        Term term;
        PropertyPath resultPath = default;

        // Check for inverse path: ^predicate
        if (ch == '^')
        {
            Advance(); // Skip '^'
            SkipWhitespace();
            term = ParseTerm();
            resultPath = PropertyPath.Inverse(term);
        }
        else
        {
            // Parse the term
            term = ParseTerm();

            // Check for modifier after the term
            SkipWhitespace();
            ch = Peek();

            if (ch == '*')
            {
                Advance();
                resultPath = PropertyPath.ZeroOrMore(term);
            }
            else if (ch == '+')
            {
                Advance();
                resultPath = PropertyPath.OneOrMore(term);
            }
            else if (ch == '?')
            {
                // Need to distinguish from variable - '?' followed by letter is variable
                var next = PeekAt(1);
                if (!IsLetter(next) && next != '_')
                {
                    Advance();
                    resultPath = PropertyPath.ZeroOrOne(term);
                }
            }
            // Check for sequence or alternative within segment bounds
            else if (ch == '/' && _position < segmentEnd)
            {
                var leftStart = term.Start;
                var leftLength = term.Length;
                Advance(); // Skip '/'
                SkipWhitespace();
                var rightStart = _position;
                var rightLength = segmentEnd - rightStart;
                resultPath = PropertyPath.Sequence(leftStart, leftLength, rightStart, rightLength);
            }
            else if (ch == '|' && _position < segmentEnd)
            {
                var leftStart = term.Start;
                var leftLength = term.Length;
                Advance(); // Skip '|'
                SkipWhitespace();
                var rightStart = _position;
                var rightLength = segmentEnd - rightStart;
                resultPath = PropertyPath.Alternative(leftStart, leftLength, rightStart, rightLength);
            }
        }

        // Restore original position
        _position = savedPosition;

        return (term, resultPath);
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

        // Check for negated property set: !(iri1|iri2|...) or !iri
        if (ch == '!')
        {
            Advance(); // Skip '!'
            SkipWhitespace();
            ch = Peek();

            if (ch == '(')
            {
                // Parse !(iri1|iri2|...)
                Advance(); // Skip '('
                SkipWhitespace();

                // Record start of negated set content
                var contentStart = _position;

                // Parse first IRI
                var firstIri = ParseTerm();

                // Parse additional IRIs separated by |
                while (true)
                {
                    SkipWhitespace();
                    ch = Peek();
                    if (ch == ')')
                    {
                        var contentLength = _position - contentStart;
                        Advance(); // Skip ')'
                        return (firstIri, PropertyPath.NegatedSet(contentStart, contentLength));
                    }
                    if (ch == '|')
                    {
                        Advance(); // Skip '|'
                        SkipWhitespace();
                        ParseTerm(); // Parse next IRI (we just need to advance past it)
                    }
                    else
                    {
                        throw new SparqlParseException("Expected '|' or ')' in negated property set");
                    }
                }
            }
            else
            {
                // Parse !iri (single negated predicate)
                var contentStart = _position;
                var iri = ParseTerm();
                var contentLength = _position - contentStart;
                return (iri, PropertyPath.NegatedSet(contentStart, contentLength));
            }
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

        // Check for sequence: iri1/iri2/iri3... (parse entire right side span for nested sequences)
        if (ch == '/')
        {
            var leftStart = term.Start;
            var leftLength = term.Length;
            Advance(); // Skip '/'
            SkipWhitespace();
            var rightStart = _position;
            ParseTerm();  // Parse first right term
            var rightEnd = _position;  // Track end before whitespace

            // Continue parsing while we see more '/' to capture the entire span
            // This allows a/b/c to capture right = "b/c" for recursive expansion
            while (true)
            {
                SkipWhitespace();
                if (Peek() != '/')
                    break;
                Advance(); // Skip '/'
                SkipWhitespace();
                ParseTerm();  // Parse next term
                rightEnd = _position;  // Update end position after each term
            }

            var rightLength = rightEnd - rightStart;  // Use end position, not current position
            return (term, PropertyPath.Sequence(leftStart, leftLength, rightStart, rightLength));
        }

        // Check for alternative: iri1|iri2|iri3... (parse entire right side span for nested alternatives)
        if (ch == '|')
        {
            var leftStart = term.Start;
            var leftLength = term.Length;
            Advance(); // Skip '|'
            SkipWhitespace();
            var rightStart = _position;
            ParseTerm();  // Parse first right term
            var rightEnd = _position;  // Track end before whitespace

            // Continue parsing while we see more '|' to capture the entire span
            while (true)
            {
                SkipWhitespace();
                if (Peek() != '|')
                    break;
                Advance(); // Skip '|'
                SkipWhitespace();
                ParseTerm();  // Parse next term
                rightEnd = _position;  // Update end position after each term
            }

            var rightLength = rightEnd - rightStart;  // Use end position, not current position
            return (term, PropertyPath.Alternative(leftStart, leftLength, rightStart, rightLength));
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

        // Prefixed name: prefix:local or :local (empty prefix)
        if (IsLetter(ch))
        {
            return ParsePrefixedNameOrKeyword();
        }

        // Empty prefix: :local (starts with colon)
        if (ch == ':')
        {
            return ParseEmptyPrefixName();
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

        // Blank node property list: [ :p :o ; :q :r ]
        // Generates an anonymous blank node with properties
        // For now, we capture the whole [...] as a blank node term for later expansion
        if (ch == '[')
        {
            return ParseBlankNodePropertyList();
        }

        // Collection (RDF list): ( item1 item2 ... )
        // Generates rdf:first/rdf:rest structure
        // For now, we capture the whole (...) as a special term for later expansion
        if (ch == '(')
        {
            return ParseCollection();
        }

        return default;
    }

    /// <summary>
    /// Parse a blank node property list: [ predicate object ; ... ]
    /// Returns a blank node term. The actual property list is captured for later expansion.
    /// Supports property paths in predicates (e.g., [:p*/:q 123]).
    /// </summary>
    private Term ParseBlankNodePropertyList()
    {
        var start = _position;
        Advance(); // Skip '['
        SkipWhitespace();

        // Handle empty blank node []
        if (Peek() == ']')
        {
            Advance();
            return Term.BlankNode(start, _position - start);
        }

        // Parse property list inside brackets
        // We just skip to the closing ']', counting nested brackets
        int depth = 1;
        while (!IsAtEnd() && depth > 0)
        {
            var ch = Peek();
            if (ch == '[') depth++;
            else if (ch == ']') depth--;

            if (depth > 0)
                Advance();
        }

        if (Peek() == ']')
            Advance();

        return Term.BlankNode(start, _position - start);
    }

    /// <summary>
    /// Parse a collection (RDF list): ( item1 item2 ... )
    /// Returns a blank node term representing the list head.
    /// Supports blank node property lists with property paths inside.
    /// </summary>
    private Term ParseCollection()
    {
        var start = _position;
        Advance(); // Skip '('
        SkipWhitespace();

        // Handle empty collection ()
        if (Peek() == ')')
        {
            Advance();
            // Empty list is rdf:nil - represent as IRI
            return Term.Iri(start, _position - start);
        }

        // Parse items, counting nested parens and brackets
        int depth = 1;
        while (!IsAtEnd() && depth > 0)
        {
            var ch = Peek();
            if (ch == '(' || ch == '[') depth++;
            else if (ch == ')' || ch == ']') depth--;

            if (depth > 0)
                Advance();
        }

        if (Peek() == ')')
            Advance();

        // Collections are represented as blank nodes for now
        // Full RDF list expansion would require generating rdf:first/rdf:rest triples
        return Term.BlankNode(start, _position - start);
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

        // syn-bad-pname-10/11/12: Variables cannot be followed by ':' (e.g., ?x:a is invalid)
        if (Peek() == ':')
        {
            var varName = _source.Slice(start, _position - start).ToString();
            throw new SparqlParseException($"Variable {varName} cannot be followed by colon - invalid syntax");
        }

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

            // Read local part - per SPARQL grammar PN_LOCAL can contain ':' after first char
            // Also handle PLX escapes (backslash escapes and percent escapes)
            while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_' || Peek() == '-' || Peek() == '.' || Peek() == ':' || Peek() == '\\' || Peek() == '%'))
            {
                // Don't include trailing dot
                if (Peek() == '.' && !IsLetterOrDigit(PeekAt(1)) && PeekAt(1) != ':')
                    break;

                // syn-bad-pname-06: Validate backslash escapes in prefixed names
                if (Peek() == '\\')
                {
                    var nextChar = PeekAt(1);
                    if (!IsValidPnLocalEsc(nextChar))
                    {
                        throw new SparqlParseException($"Invalid escape sequence '\\{nextChar}' in prefixed name local part");
                    }
                    Advance(); // Skip '\'
                    Advance(); // Skip escaped char
                    continue;
                }

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
    /// Parse a prefixed name with empty prefix: :local
    /// </summary>
    private Term ParseEmptyPrefixName()
    {
        var start = _position;

        // Skip ':'
        Advance();

        // Read local part - per SPARQL grammar PN_LOCAL can contain ':' after first char
        // Also handle PLX escapes (backslash escapes and percent escapes)
        while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_' || Peek() == '-' || Peek() == '.' || Peek() == ':' || Peek() == '\\' || Peek() == '%'))
        {
            // Don't include trailing dot
            if (Peek() == '.' && !IsLetterOrDigit(PeekAt(1)) && PeekAt(1) != ':')
                break;

            // syn-bad-pname-06: Validate backslash escapes in prefixed names
            // PN_LOCAL_ESC ::= '\' ( '_' | '~' | '.' | '-' | '!' | '$' | '&' | "'" | '(' | ')' | '*' | '+' | ',' | ';' | '=' | '/' | '?' | '#' | '@' | '%' )
            if (Peek() == '\\')
            {
                var nextChar = PeekAt(1);
                if (!IsValidPnLocalEsc(nextChar))
                {
                    throw new SparqlParseException($"Invalid escape sequence '\\{nextChar}' in prefixed name local part");
                }
                Advance(); // Skip '\'
                Advance(); // Skip escaped char
                continue;
            }

            Advance();
        }

        return Term.Iri(start, _position - start);
    }

    /// <summary>
    /// Checks if a character is valid after '\' in PN_LOCAL_ESC.
    /// Valid chars: _ ~ . - ! $ &amp; ' ( ) * + , ; = / ? # @ %
    /// </summary>
    private static bool IsValidPnLocalEsc(char c)
    {
        return c == '_' || c == '~' || c == '.' || c == '-' || c == '!' ||
               c == '$' || c == '&' || c == '\'' || c == '(' || c == ')' ||
               c == '*' || c == '+' || c == ',' || c == ';' || c == '=' ||
               c == '/' || c == '?' || c == '#' || c == '@' || c == '%';
    }

    /// <summary>
    /// Validates a \uXXXX Unicode escape sequence for invalid codepoints.
    /// Throws if the codepoint is in the surrogate range (U+D800 to U+DFFF).
    /// </summary>
    private void ValidateUnicodeEscape4(int literalStart)
    {
        // Position is at '\', PeekAt(1) is 'u', PeekAt(2-5) are hex digits
        if (_position + 6 > _source.Length)
            return; // Not enough chars for \uXXXX

        var hexSpan = _source.Slice(_position + 2, 4);
        if (TryParseHex4(hexSpan, out int codepoint))
        {
            // Check for surrogate range (D800-DFFF)
            if (codepoint >= 0xD800 && codepoint <= 0xDFFF)
            {
                throw new SparqlParseException($"Invalid Unicode codepoint U+{codepoint:X4} - surrogate codepoints are not allowed in strings");
            }
        }
    }

    /// <summary>
    /// Validates a \UXXXXXXXX Unicode escape sequence for invalid codepoints.
    /// Throws if the codepoint is in the surrogate range (U+D800 to U+DFFF).
    /// </summary>
    private void ValidateUnicodeEscape8(int literalStart)
    {
        // Position is at '\', PeekAt(1) is 'U', PeekAt(2-9) are hex digits
        if (_position + 10 > _source.Length)
            return; // Not enough chars for \UXXXXXXXX

        var hexSpan = _source.Slice(_position + 2, 8);
        if (TryParseHex8(hexSpan, out int codepoint))
        {
            // Check for surrogate range (D800-DFFF)
            if (codepoint >= 0xD800 && codepoint <= 0xDFFF)
            {
                throw new SparqlParseException($"Invalid Unicode codepoint U+{codepoint:X4} - surrogate codepoints are not allowed in strings");
            }
        }
    }

    private static bool TryParseHex4(ReadOnlySpan<char> hex, out int value)
    {
        value = 0;
        if (hex.Length != 4)
            return false;

        for (int i = 0; i < 4; i++)
        {
            var c = hex[i];
            int digit;
            if (c >= '0' && c <= '9')
                digit = c - '0';
            else if (c >= 'a' && c <= 'f')
                digit = 10 + (c - 'a');
            else if (c >= 'A' && c <= 'F')
                digit = 10 + (c - 'A');
            else
                return false;
            value = (value << 4) | digit;
        }
        return true;
    }

    private static bool TryParseHex8(ReadOnlySpan<char> hex, out int value)
    {
        value = 0;
        if (hex.Length != 8)
            return false;

        for (int i = 0; i < 8; i++)
        {
            var c = hex[i];
            int digit;
            if (c >= '0' && c <= '9')
                digit = c - '0';
            else if (c >= 'a' && c <= 'f')
                digit = 10 + (c - 'a');
            else if (c >= 'A' && c <= 'F')
                digit = 10 + (c - 'A');
            else
                return false;
            value = (value << 4) | digit;
        }
        return true;
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
                {
                    var escapeChar = PeekAt(1);

                    // Validate Unicode escapes for invalid codepoints (surrogates)
                    if (escapeChar == 'u')
                    {
                        ValidateUnicodeEscape4(start);
                    }
                    else if (escapeChar == 'U')
                    {
                        ValidateUnicodeEscape8(start);
                    }

                    Advance(); // Skip '\'
                }
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

        // syn-bad-pname-13: Blank nodes cannot be followed by ':' (e.g., _:az:b is invalid)
        if (Peek() == ':')
        {
            var bnodeName = _source.Slice(start, _position - start).ToString();
            throw new SparqlParseException($"Blank node {bnodeName} cannot be followed by colon - invalid syntax");
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
        // W3C Grammar: HavingClause ::= 'HAVING' HavingCondition+
        // Multiple conditions are ANDed together
        SkipWhitespace();
        span = PeekSpan(6);
        if (span.Length >= 6 && span[..6].Equals("HAVING", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("HAVING");
            SkipWhitespace();

            // HAVING can have multiple conditions: (cond1) (cond2) ...
            // Capture the entire span of all conditions
            if (Peek() == '(')
            {
                var start = _position;  // Start includes first '('

                // Parse all HAVING conditions
                while (Peek() == '(')
                {
                    Advance(); // Skip '('

                    // Find matching closing paren
                    int depth = 1;
                    while (!IsAtEnd() && depth > 0)
                    {
                        var ch = Peek();
                        if (ch == '(') depth++;
                        else if (ch == ')') depth--;
                        if (depth > 0) Advance();
                    }

                    if (Peek() == ')')
                        Advance(); // Skip ')'

                    SkipWhitespace();
                }

                var length = _position - start;
                // Trim trailing whitespace from length
                while (length > 0 && char.IsWhiteSpace(_source[start + length - 1]))
                    length--;

                modifier.Having = new HavingClause { ExpressionStart = start, ExpressionLength = length };
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

        // Check for deprecated BINDINGS clause and validate cardinality
        SkipWhitespace();
        span = PeekSpan(8);
        if (span.Length >= 8 && span[..8].Equals("BINDINGS", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeKeyword("BINDINGS");
            SkipWhitespace();

            // Count variables
            int varCount = 0;
            while (!IsAtEnd() && Peek() == '?')
            {
                Advance(); // Skip '?'
                while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                    Advance();
                varCount++;
                SkipWhitespace();
            }

            // Expect '{'
            if (Peek() == '{')
            {
                Advance();
                SkipWhitespace();

                // Parse rows and validate cardinality
                while (!IsAtEnd() && Peek() != '}')
                {
                    if (Peek() == '(')
                    {
                        Advance(); // Skip '('
                        SkipWhitespace();

                        int rowValueCount = 0;
                        while (!IsAtEnd() && Peek() != ')')
                        {
                            // Parse value
                            var ch = Peek();
                            if (ch == '"' || ch == '<' || IsDigit(ch) || ch == '-' || ch == '+' || IsLetter(ch))
                            {
                                // Skip value
                                while (!IsAtEnd() && !char.IsWhiteSpace(Peek()) && Peek() != ')' && Peek() != '(')
                                    Advance();
                                rowValueCount++;
                            }
                            else if (ch == ')')
                            {
                                break;
                            }
                            else
                            {
                                Advance();
                            }
                            SkipWhitespace();
                        }

                        // Validate cardinality
                        if (rowValueCount != varCount)
                        {
                            if (rowValueCount < varCount)
                                throw new SparqlParseException($"BINDINGS row has {rowValueCount} values but {varCount} variables declared");
                            else
                                throw new SparqlParseException($"BINDINGS row has {rowValueCount} values but only {varCount} variables declared");
                        }

                        if (Peek() == ')')
                            Advance();
                        SkipWhitespace();
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        return modifier;
    }

    private TemporalClause ParseTemporalClause()
    {
        var clause = new TemporalClause { Mode = TemporalQueryMode.Current };

        var span = PeekSpan(12);  // "ALL VERSIONS" is longest

        // Check for "AS OF" - must be followed by whitespace and "OF"
        // Note: Don't confuse with SPARQL "AS" keyword in expressions like (expr AS ?var)
        if (span.Length >= 5 && span[..2].Equals("AS", StringComparison.OrdinalIgnoreCase) &&
            char.IsWhiteSpace(span[2]) && span[3..5].Equals("OF", StringComparison.OrdinalIgnoreCase))
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

        // Parse one or more grouping variables or expressions
        while (!IsAtEnd() && clause.Count < GroupByClause.MaxVariables)
        {
            SkipWhitespace();

            // Check for expression with alias: (expr AS ?var)
            if (Peek() == '(')
            {
                var exprStart = _position;
                Advance(); // Skip opening '('
                SkipWhitespace();

                // Find the matching closing paren and AS keyword
                // The expression is everything between the opening ( and AS
                int depth = 1;
                int asPosition = -1;

                // Scan for AS keyword at depth 1
                while (!IsAtEnd() && depth > 0)
                {
                    var c = Peek();
                    if (c == '(')
                    {
                        depth++;
                        Advance();
                    }
                    else if (c == ')')
                    {
                        depth--;
                        if (depth > 0) Advance();
                    }
                    else if (depth == 1 && (c == 'A' || c == 'a'))
                    {
                        // Check for AS keyword
                        var peek = PeekSpan(3);
                        if (peek.Length >= 2 &&
                            (peek[0] == 'A' || peek[0] == 'a') &&
                            (peek[1] == 'S' || peek[1] == 's') &&
                            (peek.Length < 3 || !IsLetterOrDigit(peek[2])))
                        {
                            asPosition = _position;
                            ConsumeKeyword("AS");
                            break;
                        }
                        else
                        {
                            Advance();
                        }
                    }
                    else
                    {
                        Advance();
                    }
                }

                if (asPosition < 0)
                {
                    throw new SparqlParseException("Expected AS in GROUP BY expression");
                }

                // The inner expression is from after ( to before AS (trim whitespace)
                var innerExprStart = exprStart + 1;
                var innerExprEnd = asPosition;
                while (innerExprEnd > innerExprStart && char.IsWhiteSpace(_source[innerExprEnd - 1]))
                    innerExprEnd--;
                while (innerExprStart < innerExprEnd && char.IsWhiteSpace(_source[innerExprStart]))
                    innerExprStart++;

                var innerExprLength = innerExprEnd - innerExprStart;

                // Now parse the alias variable
                SkipWhitespace();
                if (Peek() != '?')
                {
                    throw new SparqlParseException("Expected variable after AS in GROUP BY expression");
                }

                var aliasStart = _position;
                Advance(); // Skip '?'

                while (!IsAtEnd() && (IsLetterOrDigit(Peek()) || Peek() == '_'))
                    Advance();

                var aliasLength = _position - aliasStart;

                SkipWhitespace();

                // Expect closing )
                if (Peek() != ')')
                {
                    throw new SparqlParseException("Expected closing ) in GROUP BY expression");
                }
                Advance(); // Skip closing ')'

                clause.AddExpression(aliasStart, aliasLength, innerExprStart, innerExprLength);
            }
            // Check for simple variable
            else if (Peek() == '?')
            {
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
            }
            else
            {
                break;
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
        return int.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;
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
